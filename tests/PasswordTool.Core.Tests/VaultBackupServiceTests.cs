using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using PasswordTool.Core.Models;
using PasswordTool.Core.Services;

namespace PasswordTool.Core.Tests;

public sealed class VaultBackupServiceTests
{
    private const string BackupPassphrase = "correct backup horse battery staple";

    [Fact]
    public void Backup_is_encrypted_and_round_trips_typed_items()
    {
        var service = new VaultBackupService();
        var items = CreateItems();

        var json = service.CreateBackup(items, BackupPassphrase, DateTimeOffset.UtcNow);
        var restored = service.ReadBackup(json, BackupPassphrase);

        Assert.DoesNotContain("account-password", json);
        Assert.DoesNotContain("JBSWY3DPEHPK3PXP", json);
        Assert.DoesNotContain("abcd-1234", json);
        Assert.Equal(2, restored.Count);
        Assert.Equal("account-password", restored[0].Password);
        Assert.Equal("JBSWY3DPEHPK3PXP", restored[0].TotpSecretBase32);
        Assert.True(restored[0].IsFavorite);
        Assert.Equal("Personal", restored[0].LegacyFolder);
        Assert.Equal(["email", "important"], restored[0].Tags);
        Assert.Equal(VaultItemType.RecoveryCodes, restored[1].Type);
        Assert.Equal(["abcd-1234", "efgh-5678"], restored[1].RecoveryCodes);
    }

    [Fact]
    public void Backup_rejects_wrong_passphrase_and_modified_ciphertext()
    {
        var service = new VaultBackupService();
        var json = service.CreateBackup(CreateItems(), BackupPassphrase, DateTimeOffset.UtcNow);

        Assert.Throws<CryptographicException>(() => service.ReadBackup(json, "incorrect backup passphrase"));

        var document = JsonNode.Parse(json)!.AsObject();
        var encryptedPayload = document["EncryptedPayload"]!.AsObject();
        var ciphertext = encryptedPayload["CipherTextBase64"]!.GetValue<string>();
        encryptedPayload["CipherTextBase64"] = (ciphertext[0] == 'A' ? "B" : "A") + ciphertext[1..];
        var tamperedJson = document.ToJsonString(new JsonSerializerOptions { WriteIndented = true });

        Assert.Throws<CryptographicException>(() => service.ReadBackup(tamperedJson, BackupPassphrase));
    }

    [Fact]
    public void Inspection_returns_only_safe_authenticated_metadata()
    {
        var service = new VaultBackupService();
        var items = CreateItems();
        items[1].IsDeleted = true;
        items[1].DeletedAt = DateTimeOffset.UtcNow;
        var createdAt = new DateTimeOffset(2026, 9, 17, 8, 30, 0, TimeSpan.Zero);
        var json = service.CreateBackup(items, BackupPassphrase, createdAt);

        var inspection = service.InspectBackup(json, BackupPassphrase);

        Assert.Equal("PasswordToolBackup", inspection.Format);
        Assert.Equal(1, inspection.Version);
        Assert.Equal(createdAt, inspection.CreatedAt);
        Assert.Equal(2, inspection.TotalItemCount);
        Assert.Equal(1, inspection.PasswordItemCount);
        Assert.Equal(1, inspection.RecoveryCodeItemCount);
        Assert.Equal(1, inspection.ActiveItemCount);
        Assert.Equal(1, inspection.TrashItemCount);
        Assert.DoesNotContain("account-password", inspection.ToString());
        Assert.DoesNotContain("abcd-1234", inspection.ToString());
        Assert.DoesNotContain(BackupPassphrase, inspection.ToString());
    }

    [Fact]
    public void Inspection_uses_the_same_generic_authentication_error_for_wrong_or_tampered_backups()
    {
        var service = new VaultBackupService();
        var json = service.CreateBackup(CreateItems(), BackupPassphrase, DateTimeOffset.UtcNow);
        var wrongPassphrase = Assert.Throws<CryptographicException>(
            () => service.InspectBackup(json, "incorrect backup passphrase"));

        var document = JsonNode.Parse(json)!.AsObject();
        var encryptedPayload = document["EncryptedPayload"]!.AsObject();
        var ciphertext = encryptedPayload["CipherTextBase64"]!.GetValue<string>();
        encryptedPayload["CipherTextBase64"] = (ciphertext[0] == 'A' ? "B" : "A") + ciphertext[1..];
        var tampered = Assert.Throws<CryptographicException>(
            () => service.InspectBackup(document.ToJsonString(), BackupPassphrase));

        Assert.Equal(wrongPassphrase.Message, tampered.Message);
        Assert.DoesNotContain(BackupPassphrase, wrongPassphrase.ToString());
    }

    [Fact]
    public void Import_plan_separates_new_duplicate_and_conflicting_ids()
    {
        var service = new VaultBackupService();
        var existing = CreateItems();
        var duplicate = Clone(existing[0]);
        var conflict = Clone(existing[1]);
        conflict.Title = "Changed title";
        var newItem = new VaultItem { Title = "New account", Password = "new-password" };

        var plan = service.CreateImportPlan([duplicate, conflict, newItem], existing);

        Assert.Equal(1, plan.NewItemCount);
        Assert.Equal(1, plan.DuplicateCount);
        Assert.Equal(1, plan.ConflictCount);
    }

    [Fact]
    public void Backup_rejects_legacy_recovery_items_that_also_contain_passwords()
    {
        var service = new VaultBackupService();
        var invalid = new VaultItem
        {
            Type = VaultItemType.RecoveryCodes,
            Title = "Mixed item",
            Password = "must-not-be-here",
            RecoveryCodes = ["abcd-1234", "efgh-5678"]
        };

        Assert.Throws<InvalidDataException>(() => service.CreateBackup([invalid], BackupPassphrase, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Backup_round_trips_password_and_recovery_codes_in_one_credential()
    {
        var service = new VaultBackupService();
        var item = new VaultItem
        {
            Title = "GitHub",
            Username = "person@example.com",
            Password = "password-value",
            RecoveryCodes = ["abcd-1234", "efgh-5678"]
        };

        var backup = service.CreateBackup([item], BackupPassphrase, DateTimeOffset.UtcNow);
        var restored = Assert.Single(service.ReadBackup(backup, BackupPassphrase));

        Assert.Equal("password-value", restored.Password);
        Assert.Equal(["abcd-1234", "efgh-5678"], restored.RecoveryCodes);
    }

    private static List<VaultItem> CreateItems()
    {
        return
        [
            new VaultItem
            {
                Title = "Account",
                Username = "person@example.com",
                Password = "account-password",
                TotpSecretBase32 = "JBSWY3DPEHPK3PXP",
                IsFavorite = true,
                LegacyFolder = "Personal",
                Tags = ["email", "important"]
            },
            new VaultItem
            {
                Type = VaultItemType.RecoveryCodes,
                Title = "Account recovery",
                Password = string.Empty,
                RecoveryCodes = ["abcd-1234", "efgh-5678"]
            }
        ];
    }

    [Fact]
    public void Backup_retains_all_totp_metadata_and_detects_configuration_conflicts()
    {
        var service = new VaultBackupService();
        var item = new VaultItem { Title = "TOTP-only account" };
        var configuration = new TotpConfiguration("JBSWY3DPEHPK3PXP", "Example", "user", TotpAlgorithm.Sha512, 8, 60);
        item.SetTotpConfiguration(configuration);
        var restored = Assert.Single(service.ReadBackup(service.CreateBackup([item], BackupPassphrase, DateTimeOffset.UtcNow), BackupPassphrase));
        Assert.Equal(configuration, restored.GetTotpConfiguration());
        Assert.Equal(1, service.CreateImportPlan([restored], [item]).DuplicateCount);
        foreach (var changed in new[]
        {
            configuration with { Issuer = "Other" },
            configuration with { AccountName = "Other" },
            configuration with { Algorithm = TotpAlgorithm.Sha256 },
            configuration with { Digits = 6 },
            configuration with { Period = 30 }
        })
        {
            restored.SetTotpConfiguration(changed);
            Assert.Equal(1, service.CreateImportPlan([restored], [item]).ConflictCount);
        }
    }

    private static VaultItem Clone(VaultItem item)
    {
        return new VaultItem
        {
            Id = item.Id,
            Type = item.Type,
            Title = item.Title,
            Username = item.Username,
            Password = item.Password,
            TotpSecretBase32 = item.TotpSecretBase32,
            TotpIssuer = item.TotpIssuer,
            TotpAccountName = item.TotpAccountName,
            TotpAlgorithm = item.TotpAlgorithm,
            TotpDigits = item.TotpDigits,
            TotpPeriod = item.TotpPeriod,
            RecoveryCodes = [.. item.RecoveryCodes],
            Url = item.Url,
            HideUrl = item.HideUrl,
            Notes = item.Notes,
            HideNotes = item.HideNotes,
            IsFavorite = item.IsFavorite,
            LegacyFolder = item.LegacyFolder,
            Tags = [.. item.Tags],
            CreatedAt = item.CreatedAt,
            UpdatedAt = item.UpdatedAt
        };
    }
}
