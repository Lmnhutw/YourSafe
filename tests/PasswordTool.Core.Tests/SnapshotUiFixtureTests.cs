using System.Security.Cryptography;
using System.Text.Json;
using PasswordTool.Core.Models;
using PasswordTool.Core.Services;

namespace PasswordTool.Core.Tests;

public sealed class SnapshotUiFixtureTests
{
    [Fact]
    public void Create_opt_in_disposable_snapshot_ui_vault()
    {
        var target = Environment.GetEnvironmentVariable("PASSWORDTOOL_SNAPSHOT_UI_TEST_DIRECTORY");
        if (string.IsNullOrEmpty(target)) return;
        target = Path.GetFullPath(target);
        Assert.False(Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any(),
            "Snapshot UI fixtures require a fresh empty directory.");
        var storage = new VaultStorageService(target);
        var totp = new TotpService();
        using var vault = new VaultService(storage, new EncryptionService(), totp);
        const string historical = "synthetic original master password";
        const string current = "synthetic replacement master password";
        const string secret = "JBSWY3DPEHPK3PXP"; // Public synthetic fixture secret only.
        vault.InitializeNewVault(historical, secret, totp.GetCurrentCode(secret).Code, RecoveryKeyService.Generate(), true);
        Assert.True(vault.TryUpdateSettings(historical, VaultLoginMode.Hybrid, new VaultSecuritySettings(10, 5, 30), out _));
        vault.AddItem(new VaultItem { Title = "Historical snapshot account", Username = "history@example.test", Password = "synthetic historical item password" });
        Assert.True(vault.TryChangeMasterPassword(historical, current, out var error), error);
        var snapshot = vault.GetSnapshots().First();
        Assert.True(snapshot.HasOldSecurityState);
        vault.AddItem(new VaultItem { Title = "Live-only snapshot account", Password = "synthetic live item password" });
        vault.ClearSession();
        Assert.False(vault.TryUnlockMasterPassword(historical, out _));
        Assert.True(vault.TryUnlockMasterPassword(current, out error), error);
        Assert.True(vault.VerifyTotpForSession(totp.GetCurrentCode(secret).Code));
        Assert.Equal(2, vault.GetItems().Count);
        var snapshots = vault.GetSnapshots().ToList();
        var snapshotDirectory = Path.Combine(storage.SnapshotsDirectory, snapshot.Id);
        File.WriteAllText(Path.Combine(target, "snapshot-ui-fixture.json"), JsonSerializer.Serialize(new
        {
            fixture = "YourSafe historical snapshot UI test v1",
            snapshotId = snapshot.Id,
            snapshotIndex = snapshots.FindIndex(value => value.Id == snapshot.Id),
            currentConfigHash = Hash(storage.ConfigPath),
            currentVaultHash = Hash(storage.VaultPath),
            snapshotConfigHash = Hash(Path.Combine(snapshotDirectory, ".config")),
            snapshotVaultHash = Hash(Path.Combine(snapshotDirectory, ".storage"))
        }));
    }

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
}
