using System.Security.Cryptography;
using System.Text.Json;
using OtpNet;
using PasswordTool.Core.Models;
using PasswordTool.Core.Services;

namespace PasswordTool.Core.Tests;

public sealed class VaultTotpTests : IDisposable
{
    private const string MasterPassword = "correct horse battery staple";
    private const string BackupPassword = "correct backup horse battery staple";
    private readonly string directory = Path.Combine(Path.GetTempPath(), "YourSafe.Totp.Tests", Guid.NewGuid().ToString("N"));
    private static readonly TotpConfiguration Configuration = new("JBSWY3DPEHPK3PXP", "Example", "user@example.com", TotpAlgorithm.Sha512, 8, 60);

    [Fact]
    public void Discovery_includes_totp_only_credentials_and_native_reads_recheck_current_url_and_eligibility()
    {
        var service = new TotpService();
        var secret = service.GenerateSecret();
        using var vault = CreateVault(new VaultStorageService(directory), service, secret);
        var withPassword = vault.AddItem(new VaultItem { Title = "Password", Password = "secret", Url = "https://example.com" });
        var withTotp = new VaultItem { Title = "TOTP", Username = "user", Url = "https://example.com" };
        withTotp.SetTotpConfiguration(Configuration);
        var totpId = vault.AddItem(withTotp).Id;
        vault.AddItem(new VaultItem { Title = "Recovery", RecoveryCodes = ["alpha-1234", "beta-5678"] });

        var discovery = vault.GetAutofillCredentials();
        Assert.Equal(2, discovery.Count);
        Assert.True(Assert.Single(discovery, item => item.Id == withPassword.Id).HasPassword);
        Assert.False(Assert.Single(discovery, item => item.Id == withPassword.Id).HasTotp);
        var totp = Assert.Single(discovery, item => item.Id == totpId);
        Assert.False(totp.HasPassword);
        Assert.True(totp.HasTotp);
        Assert.DoesNotContain(Configuration.Secret, JsonSerializer.Serialize(discovery));
        Assert.Equal(8, vault.GetWebsiteTotpCode(totpId).Code.Length);
        Assert.Equal(60, vault.GetAutofillTotp(totpId, totp.Url).PeriodSeconds);
        Assert.Throws<UnauthorizedAccessException>(() => vault.GetAutofillSecret(totpId, totp.Url));
        Assert.Throws<UnauthorizedAccessException>(() => vault.GetAutofillTotp(totpId, "https://other.example"));

        var edited = vault.GetItemForEditing(totpId, string.Empty);
        edited.Url = "https://changed.example";
        vault.UpdateItem(edited);
        Assert.Throws<UnauthorizedAccessException>(() => vault.GetAutofillTotp(totpId, totp.Url));
        edited.Url = string.Empty;
        vault.UpdateItem(edited);
        Assert.Equal(8, vault.GetAutofillTotp(totpId, string.Empty).Code.Length);
        vault.DeleteItem(totpId);
        Assert.Single(vault.GetAutofillCredentials());
        Assert.Throws<InvalidOperationException>(() => vault.GetAutofillTotp(totpId, string.Empty));
        vault.LockVault();
        Assert.Throws<InvalidOperationException>(() => vault.GetWebsiteTotpCode(withPassword.Id));
    }

    [Fact]
    public void Complete_configuration_survives_encrypted_save_reopen_backup_import_and_removal()
    {
        var service = new TotpService();
        var secret = service.GenerateSecret();
        var storage = new VaultStorageService(Path.Combine(directory, "source"));
        Guid id;
        using (var vault = CreateVault(storage, service, secret))
        {
            var item = new VaultItem { Title = "Example", Password = "preserved-password", RecoveryCodes = ["alpha-1234", "beta-5678"] };
            item.SetTotpConfiguration(Configuration);
            id = vault.AddItem(item).Id;
            var listed = Assert.Single(vault.GetItems());
            Assert.True(listed.HasTotp);
            Assert.Null(listed.GetTotpConfiguration());
        }

        using var reopened = new VaultService(storage, new EncryptionService(), service);
        Assert.True(reopened.UnlockWithMasterPassword(MasterPassword).Success);
        Assert.True(reopened.VerifyTotpForSession(ComputeCode(secret)));
        Assert.Equal(Configuration, reopened.GetItemForEditing(id, string.Empty).GetTotpConfiguration());
        var backup = reopened.ExportBackupJson(BackupPassword, string.Empty);
        Assert.DoesNotContain(Configuration.Secret, backup);
        Assert.DoesNotContain(Configuration.Secret, File.ReadAllText(storage.VaultPath));

        using var destination = CreateVault(new VaultStorageService(Path.Combine(directory, "destination")), service, service.GenerateSecret());
        Assert.Equal(1, destination.ImportBackupJson(backup, BackupPassword, string.Empty));
        Assert.Equal(Configuration, destination.GetItemForEditing(id, string.Empty).GetTotpConfiguration());
        Assert.Equal(0, destination.ImportBackupJson(backup, BackupPassword, string.Empty));
        var edited = destination.GetItemForEditing(id, string.Empty);
        edited.SetTotpConfiguration(null);
        destination.UpdateItem(edited);
        Assert.Equal("preserved-password", destination.GetPassword(id, string.Empty));
        Assert.Equal(["alpha-1234", "beta-5678"], destination.GetRecoveryCodes(id, string.Empty));
        Assert.False(Assert.Single(destination.GetItems()).HasTotp);
        Assert.Throws<InvalidOperationException>(() => destination.GetWebsiteTotpCode(id));
    }

    [Fact]
    public void Old_encrypted_vault_without_metadata_loads_default_configuration()
    {
        var encryption = new EncryptionService();
        var service = new TotpService();
        var secret = service.GenerateSecret();
        var storage = new VaultStorageService(directory);
        using (var vault = CreateVault(storage, service, secret))
            vault.AddItem(new VaultItem { Title = "Old TOTP", TotpSecretBase32 = Configuration.Secret });
        var master = new MasterPasswordService(encryption);
        Assert.True(master.TryUnlockConfig(MasterPassword, storage.LoadConfig(), out var key, out _));
        try
        {
            var data = encryption.DecryptObject<VaultData>(storage.LoadVaultPayload(), key, MasterPasswordService.VaultContext);
            var json = JsonSerializer.SerializeToNode(data)!;
            var item = json["Items"]![0]!.AsObject();
            foreach (var property in new[] { "TotpIssuer", "TotpAccountName", "TotpAlgorithm", "TotpDigits", "TotpPeriod" }) item.Remove(property);
            storage.SaveVaultPayload(encryption.EncryptObject(json, key, MasterPasswordService.VaultContext));
        }
        finally { CryptographicOperations.ZeroMemory(key); }
        using var reopened = new VaultService(storage, encryption, service);
        Assert.True(reopened.UnlockWithMasterPassword(MasterPassword).Success);
        Assert.True(reopened.VerifyTotpForSession(ComputeCode(secret)));
        var old = Assert.Single(reopened.GetItems());
        Assert.Equal(new TotpConfiguration(Configuration.Secret), reopened.GetItemForEditing(old.Id, string.Empty).GetTotpConfiguration());
        Assert.Equal(6, reopened.GetWebsiteTotpCode(old.Id).Code.Length);
    }

    [Fact]
    public void Failed_update_and_add_restore_memory_before_later_successful_save()
    {
        var fail = false;
        var storage = new VaultStorageService(directory, checkpoint =>
        {
            if (fail && checkpoint == "staged") throw new IOException("Synthetic write failure.");
        });
        var service = new TotpService();
        var secret = service.GenerateSecret();
        using var vault = CreateVault(storage, service, secret);
        var item = new VaultItem { Title = "Original", Password = "original-password" };
        item.SetTotpConfiguration(Configuration);
        var id = vault.AddItem(item).Id;
        var original = vault.GetItemForEditing(id, string.Empty);
        var edited = vault.GetItemForEditing(id, string.Empty);
        edited.Title = "Failed title";
        edited.Password = "failed-password";
        edited.SetTotpConfiguration(Configuration with { Algorithm = TotpAlgorithm.Sha256, Period = 120 });
        fail = true;
        Assert.Throws<IOException>(() => vault.UpdateItem(edited));
        Assert.Throws<IOException>(() => vault.AddItem(new VaultItem { Title = "Failed add", Password = "failed-add" }));
        var restored = vault.GetItemForEditing(id, string.Empty);
        Assert.Equal(original.Title, restored.Title);
        Assert.Equal(original.Password, restored.Password);
        Assert.Equal(original.UpdatedAt, restored.UpdatedAt);
        Assert.Equal(Configuration, restored.GetTotpConfiguration());
        Assert.Empty(restored.PasswordHistory);
        Assert.Single(vault.GetItems());
        fail = false;
        vault.AddItem(new VaultItem { Title = "Successful save", Password = "other-password" });
        using var reopened = new VaultService(storage, new EncryptionService(), service);
        Assert.True(reopened.UnlockWithMasterPassword(MasterPassword).Success);
        Assert.True(reopened.VerifyTotpForSession(ComputeCode(secret)));
        Assert.Equal(2, reopened.GetItems().Count);
        Assert.Equal(Configuration, reopened.GetItemForEditing(id, string.Empty).GetTotpConfiguration());
        Assert.Equal("original-password", reopened.GetPassword(id, string.Empty));
        Assert.Empty(reopened.GetPasswordHistory(id, string.Empty));
    }

    private static VaultService CreateVault(VaultStorageService storage, TotpService service, string secret)
    {
        var vault = new VaultService(storage, new EncryptionService(), service);
        vault.InitializeNewVault(MasterPassword, secret, ComputeCode(secret), RecoveryKeyService.Generate(), true);
        return vault;
    }

    [Fact]
    public void Expiration_during_generation_discards_the_code_and_locks_the_vault()
    {
        var service = new TotpService();
        var secret = service.GenerateSecret();
        var now = DateTimeOffset.UtcNow;
        var expire = false;
        var reads = 0;
        using var vault = new VaultService(new VaultStorageService(directory), new EncryptionService(), service,
            utcNow: () => expire && reads++ > 0 ? now.AddMinutes(1) : now);
        vault.InitializeNewVault(MasterPassword, secret, ComputeCode(secret), RecoveryKeyService.Generate(), true);
        var item = new VaultItem { Title = "Expiration check" };
        item.SetTotpConfiguration(Configuration);
        var id = vault.AddItem(item).Id;
        expire = true;
        Assert.Throws<InvalidOperationException>(() => vault.GetWebsiteTotpCode(id));
        Assert.False(vault.IsVaultUnlocked);
    }

    private static string ComputeCode(string secret) => new Totp(Base32Encoding.ToBytes(secret)).ComputeTotp();

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }
}
