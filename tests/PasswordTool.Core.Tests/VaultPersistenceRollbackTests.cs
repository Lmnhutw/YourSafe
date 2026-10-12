using System.Text.Json;
using PasswordTool.Core.Models;
using PasswordTool.Core.Services;

namespace PasswordTool.Core.Tests;

public sealed class VaultPersistenceRollbackTests : IDisposable
{
    private const string Password = "synthetic strong master password";
    private readonly string directory = Path.Combine(Path.GetTempPath(), "PasswordTool.Rollback.Tests", Guid.NewGuid().ToString("N"));
    private readonly TotpService totp = new();

    public static IEnumerable<object[]> FailedOperations()
    {
        foreach (var operation in new[] { "delete", "restore", "permanent", "add-group", "update-group", "delete-group", "csv", "purge" })
            foreach (var checkpoint in new[] { "staged", "config-replaced", "vault-replaced" })
                yield return [operation, checkpoint];
    }

    [Theory]
    [MemberData(nameof(FailedOperations))]
    public void Failed_mutation_is_rolled_back_and_cannot_leak_into_a_later_successful_save(string operation, string checkpoint)
    {
        var fail = false;
        var now = DateTimeOffset.UtcNow;
        var storage = new VaultStorageService(directory, stage =>
        {
            if (fail && stage == checkpoint) throw new IOException("Synthetic persistence failure.");
        });
        var secret = totp.GenerateSecret();
        using var vault = new VaultService(storage, new EncryptionService(), totp, utcNow: () => now);
        vault.InitializeNewVault(Password, secret, totp.GetCurrentCode(secret).Code, RecoveryKeyService.Generate(), true);
        var group = vault.AddGroup("Original group", "#336699");
        if (operation == "purge") now = now.AddDays(-31);
        var item = vault.AddItem(new VaultItem { Title = "Original", Password = "synthetic password", GroupId = group.Id });
        if (operation is "restore" or "permanent" or "purge") vault.DeleteItem(item.Id);
        if (operation == "purge") now = now.AddDays(31);
        var expected = Capture(vault);
        var oldConfig = File.ReadAllText(storage.ConfigPath);
        var oldPayload = storage.LoadVaultPayload();
        var version = vault.MutationVersion;
        now = now.AddSeconds(1);
        fail = true;

        Assert.Throws<IOException>(() =>
        {
            switch (operation)
            {
                case "delete": vault.DeleteItem(item.Id); break;
                case "restore": vault.RestoreDeletedItem(item.Id); break;
                case "permanent": vault.PermanentlyDeleteItem(item.Id, ""); break;
                case "add-group": vault.AddGroup("Failed group"); break;
                case "update-group": vault.UpdateGroup(group.Id, "Failed rename", "#112233"); break;
                case "delete-group": vault.DeleteGroup(group.Id, "Confirm delete all data in \"Original group\"", ""); break;
                case "csv": vault.ImportCsv("folder,type,name,login_username,login_password\nFailed CSV group,login,Imported,user,synthetic password", ""); break;
                case "purge": vault.VerifyTotpForSession(totp.GetCurrentCode(secret).Code); break;
                default: throw new ArgumentException("Unknown operation.");
            }
        });

        Assert.Equal(expected, Capture(vault));
        Assert.Equal(oldConfig, File.ReadAllText(storage.ConfigPath));
        Assert.Equal(oldPayload, storage.LoadVaultPayload());
        Assert.True(vault.MutationVersion > version);
        fail = false;
        vault.AddItem(new VaultItem { Title = "Later successful save", Password = "another synthetic password", GroupId = group.Id });
        vault.ClearSession();
        // Keep the expired Trash item within retention while checking persistence after reopen.
        if (operation == "purge") now = now.AddDays(-31);
        Assert.True(vault.TryUnlockMasterPassword(Password, out var error), error);
        Assert.True(vault.VerifyTotpForSession(totp.GetCurrentCode(secret).Code));
        Assert.Single(vault.GetItems(), candidate => candidate.Title == "Later successful save");
        var later = vault.GetItems().Single(candidate => candidate.Title == "Later successful save");
        vault.DeleteItem(later.Id);
        vault.PermanentlyDeleteItem(later.Id, "");
        Assert.Equal(expected, Capture(vault));
    }

    [Fact]
    public void Failed_first_item_save_does_not_leave_a_normalization_group()
    {
        var fail = false;
        var storage = new VaultStorageService(directory, checkpoint =>
        {
            if (fail && checkpoint == "staged") throw new IOException("Synthetic persistence failure.");
        });
        var secret = totp.GenerateSecret();
        using var vault = new VaultService(storage, new EncryptionService(), totp);
        vault.InitializeNewVault(Password, secret, totp.GetCurrentCode(secret).Code, RecoveryKeyService.Generate(), true);
        var initial = Assert.Single(vault.GetGroups());
        vault.DeleteGroup(initial.Id, "Confirm delete all data in \"Ungrouped\"", "");
        fail = true;
        Assert.Throws<IOException>(() => vault.AddItem(new VaultItem { Title = "Failed", Password = "synthetic password" }));
        Assert.Empty(vault.GetItems());
        Assert.Empty(vault.GetGroups());
        fail = false;
        var saved = vault.AddGroup("Actual group");
        vault.ClearSession();
        Assert.True(vault.TryUnlockMasterPassword(Password, out var error), error);
        Assert.True(vault.VerifyTotpForSession(totp.GetCurrentCode(secret).Code));
        Assert.Equal(saved.Id, Assert.Single(vault.GetGroups()).Id);
        Assert.Empty(vault.GetItems());
    }

    [Theory]
    [InlineData("delete")]
    [InlineData("restore")]
    [InlineData("permanent")]
    [InlineData("update")]
    public void Lock_during_write_does_not_restore_a_session_or_secrets(string operation)
    {
        var lockDuringWrite = false;
        VaultService? vault = null;
        var storage = new VaultStorageService(directory, checkpoint =>
        {
            if (lockDuringWrite && checkpoint == "staged") vault!.LockVault();
        });
        var secret = totp.GenerateSecret();
        using (vault = new VaultService(storage, new EncryptionService(), totp))
        {
            vault.InitializeNewVault(Password, secret, totp.GetCurrentCode(secret).Code, RecoveryKeyService.Generate(), true);
            var item = vault.AddItem(new VaultItem { Title = "Original", Password = "synthetic password" });
            if (operation is "restore" or "permanent") vault.DeleteItem(item.Id);
            var editable = operation == "update" ? vault.GetItemForEditing(item.Id, "") : null;
            var payload = storage.LoadVaultPayload();
            lockDuringWrite = true;
            Assert.Throws<InvalidOperationException>(() =>
            {
                switch (operation)
                {
                    case "delete": vault.DeleteItem(item.Id); break;
                    case "restore": vault.RestoreDeletedItem(item.Id); break;
                    case "permanent": vault.PermanentlyDeleteItem(item.Id, ""); break;
                    case "update": editable!.Password = "replacement synthetic password"; vault.UpdateItem(editable); break;
                }
            });
            Assert.False(vault.IsVaultUnlocked);
            Assert.Throws<InvalidOperationException>(() => vault.GetPassword(item.Id, ""));
            Assert.Equal(payload, storage.LoadVaultPayload());
        }
    }

    private static string Capture(VaultService vault) => JsonSerializer.Serialize(new
    {
        Groups = vault.GetGroups(),
        Items = vault.GetItems().Select(item => vault.GetItemForEditing(item.Id, "")).ToList(),
        Trash = vault.GetDeletedItems()
    });

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }
}
