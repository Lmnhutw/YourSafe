using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using PasswordTool.Core.Models;
using PasswordTool.Core.Services;

namespace PasswordTool.Core.Tests;

public sealed class VaultSnapshotVerificationTests : IDisposable
{
    private const string Password = "synthetic original master password";
    private const string NewPassword = "synthetic replacement master password";
    private readonly string directory = Path.Combine(Path.GetTempPath(), "PasswordTool.Snapshot.Tests", Guid.NewGuid().ToString("N"));
    private readonly TotpService totp = new();

    [Theory]
    [InlineData("ciphertext")]
    [InlineData("tag")]
    [InlineData("wrapped-key")]
    [InlineData("authenticator")]
    [InlineData("unrelated-vault")]
    [InlineData("null-items")]
    [InlineData("null-item")]
    [InlineData("null-group")]
    public void Invalid_snapshot_preserves_the_live_pair_and_session(string corruption)
    {
        var encryption = new EncryptionService();
        var storage = new VaultStorageService(directory);
        var secret = totp.GenerateSecret();
        using var vault = OpenNewVault(storage, encryption, secret);
        vault.AddItem(new VaultItem { Title = "Snapshot item", Password = "synthetic snapshot password" });
        vault.AddItem(new VaultItem { Title = "Live item", Password = "synthetic live password" });
        var snapshot = vault.GetSnapshots().First();
        var snapshotDirectory = Path.Combine(storage.SnapshotsDirectory, snapshot.Id);
        var configPath = Path.Combine(snapshotDirectory, ".config");
        var payloadPath = Path.Combine(snapshotDirectory, ".storage");
        var config = JsonNode.Parse(File.ReadAllText(configPath))!.AsObject();
        switch (corruption)
        {
            case "ciphertext": case "tag":
                WriteFixture(payloadPath, CorruptPayload(File.ReadAllText(payloadPath), corruption == "tag" ? "TagBase64" : "CipherTextBase64"));
                break;
            case "wrapped-key":
                var slot = config["MasterKeySlot"]!;
                slot["WrappedVaultKey"] = CorruptPayload(slot["WrappedVaultKey"]!.GetValue<string>(), "TagBase64");
                WriteFixture(configPath, config.ToJsonString());
                break;
            case "authenticator":
                config["EncryptedTotpSecret"] = CorruptPayload(config["EncryptedTotpSecret"]!.GetValue<string>(), "TagBase64");
                WriteFixture(configPath, config.ToJsonString());
                break;
            case "unrelated-vault":
                var unrelatedKey = RandomNumberGenerator.GetBytes(32);
                try { WriteFixture(payloadPath, encryption.EncryptObject(new VaultData(), unrelatedKey, MasterPasswordService.VaultContext)); }
                finally { CryptographicOperations.ZeroMemory(unrelatedKey); }
                break;
            case "null-items": case "null-item": case "null-group":
                var snapshotConfig = JsonSerializer.Deserialize<AppConfig>(config.ToJsonString())!;
                Assert.True(new MasterPasswordService(encryption).TryUnlockConfig(Password, snapshotConfig, out var key, out _));
                var invalidData = corruption switch
                {
                    "null-items" => "{\"Items\":null}",
                    "null-item" => "{\"Items\":[null]}",
                    _ => "{\"Items\":[],\"Groups\":[null]}"
                };
                try { WriteFixture(payloadPath, encryption.EncryptString(invalidData, key, MasterPasswordService.VaultContext)); }
                finally { CryptographicOperations.ZeroMemory(key); }
                break;
        }
        var liveConfig = File.ReadAllText(storage.ConfigPath);
        var livePayload = storage.LoadVaultPayload();
        Assert.False(vault.TryRestoreSnapshot(snapshot.Id, Password, out _));
        Assert.Equal(liveConfig, File.ReadAllText(storage.ConfigPath));
        Assert.Equal(livePayload, storage.LoadVaultPayload());
        Assert.True(vault.IsVaultUnlocked);
        Assert.Equal(2, vault.GetItems().Count);
        vault.ClearSession();
        Assert.True(vault.TryUnlockMasterPassword(Password, out var error), error);
        Assert.True(vault.VerifyTotpForSession(totp.GetCurrentCode(secret).Code));
        Assert.Equal(2, vault.GetItems().Count);
    }

    [Fact]
    public void Historical_password_is_required_to_verify_snapshot_but_cannot_authorize_live_replacement()
    {
        var storage = new VaultStorageService(directory);
        var secret = totp.GenerateSecret();
        using var vault = OpenNewVault(storage, new EncryptionService(), secret);
        vault.AddItem(new VaultItem { Title = "Historical item", Password = "synthetic password" });
        Assert.True(vault.TryChangeMasterPassword(Password, NewPassword, out var error), error);
        var snapshot = vault.GetSnapshots().First();
        Assert.True(snapshot.HasOldSecurityState);
        var liveConfig = File.ReadAllText(storage.ConfigPath);
        var livePayload = storage.LoadVaultPayload();
        Assert.False(vault.TryRestoreSnapshot(snapshot.Id, Password, out _, Password));
        Assert.False(vault.TryRestoreSnapshot(snapshot.Id, NewPassword, out var oldPasswordError));
        Assert.Contains("snapshot's Master Password", oldPasswordError);
        Assert.False(vault.TryRestoreSnapshot(snapshot.Id, NewPassword, out _, "incorrect historical password"));
        Assert.Equal(liveConfig, File.ReadAllText(storage.ConfigPath));
        Assert.Equal(livePayload, storage.LoadVaultPayload());
        Assert.True(vault.TryRestoreSnapshot(snapshot.Id, NewPassword, out error, Password), error);
        Assert.False(vault.IsSignInSessionActive);
        Assert.False(vault.TryUnlockMasterPassword(NewPassword, out _));
        Assert.True(vault.TryUnlockMasterPassword(Password, out error), error);
        Assert.True(vault.VerifyTotpForSession(totp.GetCurrentCode(secret).Code));
        Assert.Equal("Historical item", Assert.Single(vault.GetItems()).Title);
    }

    [Fact]
    public void Historical_authenticator_is_verified_and_restored_without_rejecting_revision_change()
    {
        var storage = new VaultStorageService(directory);
        var oldSecret = totp.GenerateSecret();
        var newSecret = totp.GenerateSecret();
        using var vault = OpenNewVault(storage, new EncryptionService(), oldSecret);
        vault.AddItem(new VaultItem { Title = "Historical item", Password = "synthetic password" });
        Assert.True(vault.TryResetAuthenticator(Password, newSecret, totp.GetCurrentCode(newSecret).Code, out var error), error);
        var snapshot = vault.GetSnapshots().First();
        Assert.True(snapshot.HasOldSecurityState);
        var oldConfig = File.ReadAllText(Path.Combine(storage.SnapshotsDirectory, snapshot.Id, ".config"));
        Assert.True(vault.TryRestoreSnapshot(snapshot.Id, Password, out error), error);
        Assert.Equal(oldConfig, File.ReadAllText(storage.ConfigPath));
        Assert.False(vault.IsSignInSessionActive);
        Assert.True(vault.TryUnlockMasterPassword(Password, out error), error);
        Assert.True(vault.VerifyTotpForSession(totp.GetCurrentCode(oldSecret).Code));
        Assert.Equal("Historical item", Assert.Single(vault.GetItems()).Title);
    }

    [Theory]
    [InlineData("lock")]
    [InlineData("expire")]
    [InlineData("tamper-staged")]
    [InlineData("config-replaced")]
    [InlineData("vault-replaced")]
    public void Staged_validation_lifecycle_and_write_failure_prevent_invalid_restore(string failure)
    {
        var armed = false;
        var now = DateTimeOffset.UtcNow;
        VaultService? vault = null;
        var storage = new VaultStorageService(directory, checkpoint =>
        {
            if (!armed) return;
            if (checkpoint == "verified" && failure == "lock") vault!.LockVault();
            if (checkpoint == "verified" && failure == "expire") now = now.AddHours(5);
            if (checkpoint == "staged" && failure == "tamper-staged")
            {
                var stagedPayload = Directory.GetFiles(directory, ".storage.*.tmp").Single();
                WriteFixture(stagedPayload, CorruptPayload(File.ReadAllText(stagedPayload), "TagBase64"));
            }
            if (checkpoint == failure) throw new IOException("Synthetic restore failure.");
        });
        var secret = totp.GenerateSecret();
        using (vault = new VaultService(storage, new EncryptionService(), totp, utcNow: () => now))
        {
            vault.InitializeNewVault(Password, secret, totp.GetCurrentCode(secret).Code, RecoveryKeyService.Generate(), true);
            vault.AddItem(new VaultItem { Title = "Snapshot item", Password = "synthetic password" });
            vault.AddItem(new VaultItem { Title = "Live item", Password = "synthetic password" });
            var snapshot = vault.GetSnapshots().First();
            var oldConfig = File.ReadAllText(storage.ConfigPath);
            var oldPayload = storage.LoadVaultPayload();
            armed = true;
            Assert.False(vault.TryRestoreSnapshot(snapshot.Id, Password, out _));
            Assert.Equal(oldConfig, File.ReadAllText(storage.ConfigPath));
            Assert.Equal(oldPayload, storage.LoadVaultPayload());
            if (failure is "lock" or "expire")
            {
                Assert.False(vault.IsVaultUnlocked);
                Assert.Throws<InvalidOperationException>(() => vault.GetItems());
            }
            else
            {
                Assert.True(vault.IsVaultUnlocked);
                Assert.Equal(2, vault.GetItems().Count);
            }
        }
    }

    private VaultService OpenNewVault(VaultStorageService storage, EncryptionService encryption, string secret)
    {
        var vault = new VaultService(storage, encryption, totp);
        vault.InitializeNewVault(Password, secret, totp.GetCurrentCode(secret).Code, RecoveryKeyService.Generate(), true);
        return vault;
    }

    [Fact]
    public void Token_revocation_failure_occurs_before_live_snapshot_replacement()
    {
        if (!OperatingSystem.IsWindows()) return;
        var storage = new VaultStorageService(directory);
        var secret = totp.GenerateSecret();
        using var vault = OpenNewVault(storage, new EncryptionService(), secret);
        vault.AddItem(new VaultItem { Title = "Snapshot item", Password = "synthetic password" });
        vault.AddItem(new VaultItem { Title = "Live item", Password = "synthetic password" });
        var snapshot = vault.GetSnapshots().First();
        var oldConfig = File.ReadAllText(storage.ConfigPath);
        var oldPayload = storage.LoadVaultPayload();
        using var tokenLock = new FileStream(storage.TrustedUnlockTokenPath, FileMode.Open, FileAccess.Read, FileShare.None);
        Assert.False(vault.TryRestoreSnapshot(snapshot.Id, Password, out _));
        Assert.Equal(oldConfig, File.ReadAllText(storage.ConfigPath));
        Assert.Equal(oldPayload, storage.LoadVaultPayload());
        Assert.True(vault.IsVaultUnlocked);
        Assert.Equal(2, vault.GetItems().Count);
    }

    private static string CorruptPayload(string json, string property)
    {
        var payload = JsonNode.Parse(json)!.AsObject();
        var bytes = Convert.FromBase64String(payload[property]!.GetValue<string>());
        bytes[0] ^= 1;
        payload[property] = Convert.ToBase64String(bytes);
        return payload.ToJsonString();
    }

    private static void WriteFixture(string path, string text)
    {
        File.SetAttributes(path, FileAttributes.Normal);
        File.WriteAllText(path, text);
    }

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }
}
