using System.Security.Cryptography;
using System.Text.Json;
using OtpNet;
using PasswordTool.Core.Models;
using PasswordTool.Core.Services;

namespace PasswordTool.Core.Tests;

public sealed class VaultEnvelopeEncryptionTests : IDisposable
{
    private readonly string tempDirectory = Path.Combine(Path.GetTempPath(), "PasswordTool.Envelope.Tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void New_vault_uses_v4_envelope_format_and_purpose_bound_ciphertexts()
    {
        var storage = new VaultStorageService(tempDirectory);
        var encryption = new EncryptionService();
        var totp = new TotpService();
        var secret = totp.GenerateSecret();
        using var vault = new VaultService(storage, encryption, totp);
        vault.InitializeNewVault("correct horse battery staple", secret, ComputeTotp(secret), RecoveryKeyService.Generate(), true);
        vault.AddItem(new VaultItem { Title = "Email", Password = "envelope-only-secret" });

        var config = storage.LoadConfig();
        Assert.Equal(4, config.Version);
        Assert.NotNull(config.MasterKeySlot);
        Assert.False(string.IsNullOrWhiteSpace(config.MasterKeySlot!.WrappedVaultKey));
        Assert.DoesNotContain(secret, File.ReadAllText(storage.ConfigPath));
        Assert.DoesNotContain("envelope-only-secret", File.ReadAllText(storage.VaultPath));

        Assert.True(new MasterPasswordService(encryption).TryUnlockConfig(
            "correct horse battery staple", config, out var vaultKey, out var decryptedSecret));
        try
        {
            Assert.Equal(secret, decryptedSecret);
            Assert.ThrowsAny<CryptographicException>(() =>
                encryption.DecryptString(config.EncryptedTotpSecret, vaultKey, MasterPasswordService.VaultContext));
        }
        finally { CryptographicOperations.ZeroMemory(vaultKey); }
    }

    [Fact]
    public void Legacy_master_unlock_migrates_atomically_and_preserves_a_v2_snapshot()
    {
        const string password = "correct horse battery staple";
        var encryption = new EncryptionService();
        var totp = new TotpService();
        var secret = totp.GenerateSecret();
        var storage = CreateLegacyVault(tempDirectory, password, secret, encryption);
        var oldConfig = File.ReadAllText(storage.ConfigPath);

        using var vault = new VaultService(storage, encryption, totp);
        var result = vault.UnlockWithMasterPassword(password);

        Assert.Equal(VaultUnlockStatus.UnlockedMigrationDeferred, result.Status);
        Assert.Equal(oldConfig, File.ReadAllText(storage.ConfigPath));
        Assert.True(vault.VerifyTotpForSession(ComputeTotp(secret)));
        vault.SaveRecoveryKey(password, RecoveryKeyService.Generate(), true);
        Assert.Equal(4, storage.LoadConfig().Version);
        Assert.Equal("legacy password", vault.GetPassword(vault.GetItems().Single().Id, ComputeTotp(secret)));
        var snapshot = Assert.Single(storage.GetSnapshots());
        Assert.Equal(oldConfig, File.ReadAllText(Path.Combine(storage.SnapshotsDirectory, snapshot.Id, ".config")));

        Assert.True(vault.TryRestoreSnapshot(snapshot.Id, password, out var restoreError), restoreError);
        var remigration = vault.UnlockWithMasterPassword(password);
        Assert.Equal(VaultUnlockStatus.UnlockedMigrationDeferred, remigration.Status);
        Assert.True(vault.VerifyTotpForSession(ComputeTotp(secret)));
        vault.SaveRecoveryKey(password, RecoveryKeyService.Generate(), true);
        Assert.Equal(4, storage.LoadConfig().Version);
    }

    [Theory]
    [InlineData("staged")]
    [InlineData("verified")]
    [InlineData("config-replaced")]
    [InlineData("vault-replaced")]
    public void Migration_failure_preserves_the_legacy_pair_and_opens_a_deferred_session(string failingCheckpoint)
    {
        const string password = "correct horse battery staple";
        var encryption = new EncryptionService();
        var totp = new TotpService();
        var secret = totp.GenerateSecret();
        var initial = CreateLegacyVault(tempDirectory, password, secret, encryption);
        var oldConfig = File.ReadAllText(initial.ConfigPath);
        var oldVault = File.ReadAllText(initial.VaultPath);
        var faultingStorage = new VaultStorageService(tempDirectory, checkpoint =>
        {
            if (checkpoint == failingCheckpoint) throw new IOException("Injected state-write failure.");
        });

        using var vault = new VaultService(faultingStorage, encryption, totp);
        var result = vault.UnlockWithMasterPassword(password);

        Assert.Equal(VaultUnlockStatus.UnlockedMigrationDeferred, result.Status);
        Assert.True(vault.VerifyTotpForSession(ComputeTotp(secret)));
        Assert.Throws<IOException>(() => vault.SaveRecoveryKey(password, RecoveryKeyService.Generate(), true));
        Assert.Equal(oldConfig, File.ReadAllText(initial.ConfigPath));
        Assert.Equal(oldVault, File.ReadAllText(initial.VaultPath));
        Assert.Throws<InvalidOperationException>(() => vault.GetItems());
    }

    [Fact]
    public void Kdf_upgrade_rewraps_the_dek_without_changing_vault_ciphertext()
    {
        const string password = "correct horse battery staple";
        var storage = new VaultStorageService(tempDirectory);
        var encryption = new EncryptionService();
        var master = new MasterPasswordService(encryption);
        var totp = new TotpService();
        var secret = totp.GenerateSecret();
        using var vault = new VaultService(storage, encryption, totp);
        vault.InitializeNewVault(password, secret, ComputeTotp(secret), RecoveryKeyService.Generate(), true);
        vault.AddItem(new VaultItem { Title = "KDF", Password = "kdf secret" });

        var config = storage.LoadConfig();
        Assert.True(master.TryUnlockConfig(password, config, out var vaultKey, out _));
        try
        {
            config.MasterKeySlot!.KdfIterations = 2;
            config.MasterKeySlot.SaltBase64 = Convert.ToBase64String(RandomNumberGenerator.GetBytes(MasterPasswordService.SaltSizeBytes));
            var weakKek = master.DeriveKey(password, config.MasterKeySlot);
            try
            {
                config.MasterKeySlot.WrappedVaultKey = encryption.EncryptKey(vaultKey, weakKek, MasterPasswordService.MasterKeyContext);
            }
            finally { CryptographicOperations.ZeroMemory(weakKek); }
            storage.SaveConfig(config);
        }
        finally { CryptographicOperations.ZeroMemory(vaultKey); }

        vault.ClearSession();
        Assert.True(vault.TryUnlockMasterPassword(password, out var unlockError), unlockError);
        Assert.True(vault.VerifyTotpForSession(ComputeTotp(secret)));
        Assert.True(vault.NeedsKdfUpgrade);
        var vaultCiphertext = File.ReadAllText(storage.VaultPath);
        Assert.True(vault.TryUpgradeKdf(password, out var upgradeError), upgradeError);
        Assert.Equal(vaultCiphertext, File.ReadAllText(storage.VaultPath));
        Assert.False(vault.NeedsKdfUpgrade);
    }
    [Fact]
    public void Trusted_unlock_checks_totp_before_unprotecting_the_vault_key_blob()
    {
        var storage = new VaultStorageService(tempDirectory);
        var encryption = new EncryptionService();
        var totp = new TotpService();
        var secret = totp.GenerateSecret();
        var code = ComputeTotp(secret);
        using (var vault = new VaultService(storage, encryption, totp))
            vault.InitializeNewVault("correct horse battery staple", secret, code, RecoveryKeyService.Generate(), true);

        var token = storage.LoadTrustedUnlockToken();
        Assert.Equal(3, token.Version);
        Assert.False(string.IsNullOrWhiteSpace(token.ProtectedAuthenticatorSecretBase64));
        Assert.False(string.IsNullOrWhiteSpace(token.ProtectedVaultKeyBase64));
        token.ProtectedVaultKeyBase64 = "not-base64";
        storage.SaveTrustedUnlockToken(token);

        using var reopened = new VaultService(storage, encryption, totp);
        Assert.False(reopened.TryUnlockWithGoogleAuthenticator(GetInvalidTotpCode(secret, totp), out var invalidError));
        Assert.Contains("Invalid", invalidError, StringComparison.OrdinalIgnoreCase);
        Assert.False(reopened.TryUnlockWithGoogleAuthenticator(code, out var keyError));
        Assert.Contains("token", keyError, StringComparison.OrdinalIgnoreCase);
    }

    private static VaultStorageService CreateLegacyVault(string directory, string password, string secret, EncryptionService encryption)
    {
        var storage = new VaultStorageService(directory);
        var master = new MasterPasswordService(encryption);
        var config = master.CreateConfig(password, secret);
        var key = master.DeriveKey(password, config);
        try
        {
            var data = new VaultData { Items = [new VaultItem { Title = "Legacy", Password = "legacy password" }] };
            storage.SaveState(config, encryption.EncryptObject(data, key));
        }
        finally { CryptographicOperations.ZeroMemory(key); }
        return storage;
    }

    private static string ComputeTotp(string secret) => new Totp(Base32Encoding.ToBytes(secret)).ComputeTotp();
    private static string GetInvalidTotpCode(string secret, TotpService service)
    {
        var valid = ComputeTotp(secret);
        for (var value = 0; value < 1_000_000; value++)
        {
            var candidate = value.ToString("D6");
            if (candidate != valid && !service.VerifyCode(secret, candidate)) return candidate;
        }
        throw new InvalidOperationException("Unable to produce an invalid TOTP code.");
    }

    public void Dispose()
    {
        if (Directory.Exists(tempDirectory)) Directory.Delete(tempDirectory, recursive: true);
    }
}
