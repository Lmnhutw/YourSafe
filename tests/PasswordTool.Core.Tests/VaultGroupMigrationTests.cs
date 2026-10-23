using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using PasswordTool.Core.Models;
using PasswordTool.Core.Services;

namespace PasswordTool.Core.Tests;

public sealed class VaultGroupMigrationTests : IDisposable
{
    private const string Password = "correct horse battery staple";
    private readonly string directory = Path.Combine(Path.GetTempPath(), "PasswordTool.Core.Tests", Guid.NewGuid().ToString("N"));
    private readonly EncryptionService encryption = new();
    private readonly TotpService totp = new();

    [Fact]
    public void New_vault_and_unassigned_entries_stay_without_groups_after_reads_saves_and_reopen()
    {
        var storage = new VaultStorageService(directory);
        var secret = totp.GenerateSecret();
        using var vault = new VaultService(storage, encryption, totp);
        vault.InitializeNewVault(Password, secret, Code(secret), RecoveryKeyService.Generate(), true);
        Assert.Empty(vault.GetGroups());
        var item = vault.AddItem(new VaultItem { Title = "All", Password = "synthetic password" });
        vault.AddGroup("Work");
        Assert.Null(item.GroupId);
        vault.ClearSession();
        Assert.True(vault.TryUnlockMasterPassword(Password, out _));
        Assert.True(vault.VerifyTotpForSession(Code(secret)));
        var payload = storage.LoadVaultPayload();
        Assert.Null(Assert.Single(vault.GetItems()).GroupId);
        Assert.Equal("Work", Assert.Single(vault.GetGroups()).Name);
        Assert.Equal(payload, storage.LoadVaultPayload());
        Assert.Equal(3, ReadData(storage).Version);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Migration_preserves_active_and_trash_data_and_runs_only_once_after_authentication(int version)
    {
        var storage = new VaultStorageService(directory);
        var secret = totp.GenerateSecret();
        using var vault = new VaultService(storage, encryption, totp);
        vault.InitializeNewVault(Password, secret, Code(secret), RecoveryKeyService.Generate(), true);
        var removed = vault.AddGroup("Ungrouped");
        var renamed = vault.AddGroup("Inbox");
        var active = vault.AddItem(new VaultItem { Title = "Active", Username = "user", Password = "first password", GroupId = removed.Id });
        var edited = vault.GetItemForEditing(active.Id, "");
        edited.Password = "second password";
        vault.UpdateItem(edited);
        var deleted = vault.AddItem(new VaultItem { Title = "Trash", Password = "trash password", GroupId = removed.Id });
        vault.DeleteItem(deleted.Id);
        vault.AddItem(new VaultItem { Title = "Renamed group", Password = "kept password", GroupId = renamed.Id });
        vault.AddItem(new VaultItem { Title = "All", Password = "unassigned password" });
        var expected = ReadData(storage);
        foreach (var item in expected.Items.Where(item => item.GroupId == removed.Id)) item.GroupId = null;
        expected.Groups.RemoveAll(group => group.Id == removed.Id);
        expected.Version = 3;
        expected.Items.Single(item => item.Title == "All").PasswordChangedAt = null;
        var futureDate = DateTimeOffset.UtcNow.AddDays(10);
        expected.Items.Single(item => item.Id == deleted.Id).PasswordChangedAt = futureDate;
        WriteLegacy(storage, version, node =>
        {
            foreach (var item in node["Items"]!.AsArray())
            {
                item!["Tags"] = new JsonObject { ["ignored"] = true };
                if (item["Title"]!.GetValue<string>() == "All") item["PasswordChangedAt"] = null;
                if (item["Id"]!.GetValue<Guid>() == deleted.Id) item["PasswordChangedAt"] = JsonValue.Create(futureDate);
            }
        });
        var originalPayload = storage.LoadVaultPayload();

        vault.ClearSession();
        Assert.False(vault.TryUnlockMasterPassword("incorrect password", out _));
        Assert.Equal(originalPayload, storage.LoadVaultPayload());
        Assert.True(vault.TryUnlockMasterPassword(Password, out _));
        var invalid = Code(secret) == "000000" ? "111111" : "000000";
        Assert.False(vault.VerifyTotpForSession(invalid));
        Assert.Equal(originalPayload, storage.LoadVaultPayload());
        Assert.True(vault.VerifyTotpForSession(Code(secret)));
        Assert.Equal(JsonSerializer.Serialize(expected), JsonSerializer.Serialize(ReadData(storage)));
        Assert.Equal(renamed.Id, Assert.Single(vault.GetGroups()).Id);
        Assert.Single(vault.GetDeletedItems());
        Assert.Single(vault.GetPasswordHistory(active.Id, ""));

        var ordinary = vault.AddGroup("Ungrouped");
        var migratedPayload = storage.LoadVaultPayload();
        vault.ClearSession();
        Assert.True(vault.TryUnlockMasterPassword(Password, out _));
        Assert.True(vault.VerifyTotpForSession(Code(secret)));
        Assert.Contains(vault.GetGroups(), group => group.Id == ordinary.Id);
        Assert.Equal(migratedPayload, storage.LoadVaultPayload());
        Assert.Equal(JsonSerializer.Serialize(expected.Items), JsonSerializer.Serialize(ReadData(storage).Items));
        Assert.DoesNotContain("\"Tags\"", JsonSerializer.Serialize(ReadData(storage)));
    }

    [Theory]
    [InlineData("staged")]
    [InlineData("config-replaced")]
    [InlineData("vault-replaced")]
    public void Failed_migration_rolls_back_membership_and_persistence(string checkpoint)
    {
        var fail = false;
        var storage = new VaultStorageService(directory, stage =>
        {
            if (fail && stage == checkpoint) throw new IOException("Synthetic migration failure.");
        });
        var secret = totp.GenerateSecret();
        using var vault = new VaultService(storage, encryption, totp);
        vault.InitializeNewVault(Password, secret, Code(secret), RecoveryKeyService.Generate(), true);
        var group = vault.AddGroup("Ungrouped");
        vault.AddItem(new VaultItem { Title = "Preserved", Password = "synthetic password", GroupId = group.Id });
        WriteLegacy(storage, 2);
        var payload = storage.LoadVaultPayload();
        var config = File.ReadAllText(storage.ConfigPath);
        vault.ClearSession();
        Assert.True(vault.TryUnlockMasterPassword(Password, out _));
        fail = true;
        Assert.Throws<IOException>(() => vault.VerifyTotpForSession(Code(secret)));
        Assert.Equal(group.Id, Assert.Single(vault.GetGroups()).Id);
        Assert.Equal(group.Id, Assert.Single(vault.GetItems()).GroupId);
        Assert.Equal(payload, storage.LoadVaultPayload());
        Assert.Equal(config, File.ReadAllText(storage.ConfigPath));
        fail = false;
        vault.ClearSession();
        Assert.True(vault.TryUnlockMasterPassword(Password, out _));
        Assert.True(vault.VerifyTotpForSession(Code(secret)));
        Assert.Empty(vault.GetGroups());
        Assert.Null(Assert.Single(vault.GetItems()).GroupId);
    }

    [Fact]
    public void Invalid_legacy_item_prevents_migration_write()
    {
        var storage = new VaultStorageService(directory);
        var secret = totp.GenerateSecret();
        using var vault = new VaultService(storage, encryption, totp);
        vault.InitializeNewVault(Password, secret, Code(secret), RecoveryKeyService.Generate(), true);
        vault.AddItem(new VaultItem { Title = "Invalid", Password = "synthetic password" });
        WriteLegacy(storage, 2, node => node["Items"]![0]!["Title"] = "");
        var payload = storage.LoadVaultPayload();
        vault.ClearSession();
        Assert.True(vault.TryUnlockMasterPassword(Password, out _));
        Assert.Throws<InvalidDataException>(() => vault.VerifyTotpForSession(Code(secret)));
        Assert.Equal(payload, storage.LoadVaultPayload());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Migration_also_runs_after_trusted_sign_in_and_active_session_reunlock(bool trusted)
    {
        var storage = new VaultStorageService(directory);
        var secret = totp.GenerateSecret();
        using var vault = new VaultService(storage, encryption, totp);
        vault.InitializeNewVault(Password, secret, Code(secret), RecoveryKeyService.Generate(), true);
        var group = vault.AddGroup("Ungrouped");
        vault.AddItem(new VaultItem { Title = "Preserved", Password = "synthetic password", GroupId = group.Id });
        WriteLegacy(storage, 2);
        if (trusted)
        {
            vault.ClearSession();
            Assert.True(vault.TryUnlockWithGoogleAuthenticator(Code(secret), out var error), error);
        }
        else
        {
            vault.LockVault();
            Assert.True(vault.TryUnlockMasterPassword(Password, out var error), error);
        }
        Assert.Empty(vault.GetGroups());
        Assert.Null(Assert.Single(vault.GetItems()).GroupId);
        Assert.Equal(3, ReadData(storage).Version);
    }

    [Fact]
    public void Recovery_setup_migrates_in_the_verified_write_and_rolls_back_if_it_fails()
    {
        var fail = false;
        var storage = new VaultStorageService(directory, stage =>
        {
            if (fail && stage == "vault-replaced") throw new IOException("Synthetic migration failure.");
        });
        var secret = totp.GenerateSecret();
        using var vault = new VaultService(storage, encryption, totp);
        vault.InitializeNewVault(Password, secret, Code(secret), RecoveryKeyService.Generate(), true);
        var group = vault.AddGroup("Ungrouped");
        vault.AddItem(new VaultItem { Title = "Preserved", Password = "synthetic password", GroupId = group.Id });
        WriteLegacy(storage, 2);
        var legacyConfig = storage.LoadConfig();
        legacyConfig.RecoveryKeySlot = null;
        storage.SaveConfig(legacyConfig);
        var payload = storage.LoadVaultPayload();
        var config = File.ReadAllText(storage.ConfigPath);
        vault.ClearSession();
        Assert.True(vault.TryUnlockMasterPassword(Password, out _));
        Assert.True(vault.VerifyTotpForSession(Code(secret)));
        Assert.Equal(payload, storage.LoadVaultPayload());
        Assert.Throws<UnauthorizedAccessException>(() => vault.SaveRecoveryKey("wrong password", RecoveryKeyService.Generate(), true));
        Assert.Equal(payload, storage.LoadVaultPayload());
        fail = true;
        Assert.Throws<IOException>(() => vault.SaveRecoveryKey(Password, RecoveryKeyService.Generate(), true));
        Assert.Equal(payload, storage.LoadVaultPayload());
        Assert.Equal(config, File.ReadAllText(storage.ConfigPath));
        Assert.True(vault.NeedsRecoveryKey);
        fail = false;
        vault.SaveRecoveryKey(Password, RecoveryKeyService.Generate(), true);
        Assert.False(vault.NeedsRecoveryKey);
        Assert.Empty(vault.GetGroups());
        Assert.Null(Assert.Single(vault.GetItems()).GroupId);
        Assert.Equal(3, ReadData(storage).Version);
    }

    [Fact]
    public void Migration_preserves_large_existing_vaults_while_backups_keep_their_item_limit()
    {
        var storage = new VaultStorageService(directory);
        var secret = totp.GenerateSecret();
        using var vault = new VaultService(storage, encryption, totp);
        vault.InitializeNewVault(Password, secret, Code(secret), RecoveryKeyService.Generate(), true);
        var group = vault.AddGroup("Ungrouped");
        var items = Enumerable.Range(0, 10_001).Select(index => new VaultItem
        {
            Title = $"Synthetic {index}", Password = "synthetic password", GroupId = group.Id
        }).ToList();
        Assert.Throws<InvalidDataException>(() => VaultBackupService.ValidateItems(items));
        WriteLegacy(storage, 2, node => node["Items"] = JsonSerializer.SerializeToNode(items));
        vault.ClearSession();
        Assert.True(vault.TryUnlockMasterPassword(Password, out var error), error);
        Assert.True(vault.VerifyTotpForSession(Code(secret)));
        var migrated = ReadData(storage);
        Assert.Equal(3, migrated.Version);
        Assert.Empty(migrated.Groups);
        Assert.Equal(items.Count, migrated.Items.Count);
        Assert.All(migrated.Items, item => Assert.Null(item.GroupId));
        Assert.Equal(items.Select(item => item.Id), migrated.Items.Select(item => item.Id));
    }

    private string Code(string secret) => totp.GetCurrentCode(secret).Code;

    private VaultData ReadData(VaultStorageService storage)
    {
        Assert.True(new MasterPasswordService(encryption).TryUnlockConfig(Password, storage.LoadConfig(), out var key, out _));
        try { return encryption.DecryptObject<VaultData>(storage.LoadVaultPayload(), key, MasterPasswordService.VaultContext); }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    private void WriteLegacy(VaultStorageService storage, int version, Action<JsonObject>? update = null)
    {
        Assert.True(new MasterPasswordService(encryption).TryUnlockConfig(Password, storage.LoadConfig(), out var key, out _));
        try
        {
            var data = encryption.DecryptObject<JsonObject>(storage.LoadVaultPayload(), key, MasterPasswordService.VaultContext);
            data["Version"] = version;
            update?.Invoke(data);
            storage.SaveVaultPayload(encryption.EncryptObject(data, key, MasterPasswordService.VaultContext));
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }
}
