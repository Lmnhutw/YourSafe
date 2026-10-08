using System.Text.Json;
using OtpNet;
using PasswordTool.Core.Models;
using PasswordTool.Core.Services;

namespace PasswordTool.Core.Tests;

public sealed class VaultRecoveryTests : IDisposable
{
    private const string BackupPassphrase = "correct backup horse battery staple";
    private const string NewMasterPassword = "new master password for recovery";
    private readonly string tempDirectory = Path.Combine(
        Path.GetTempPath(),
        "PasswordTool.Core.Tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void New_machine_recovery_preserves_supported_fields_and_creates_fresh_configuration()
    {
        var now = new DateTimeOffset(2026, 9, 17, 9, 0, 0, TimeSpan.Zero);
        var encryption = new EncryptionService();
        var totp = new TotpService();
        var oldConfig = new MasterPasswordService(encryption).CreateConfig(
            "old master password value",
            totp.GenerateSecret());
        var item = CreateCompletePasswordItem(now);
        var recoveryCodes = new VaultItem
        {
            Type = VaultItemType.RecoveryCodes,
            Title = "Recovery codes",
            RecoveryCodes = ["abcd-1234", "efgh-5678"],
            Notes = "private recovery note",
            LegacyFolder = "Archive",
            Tags = ["recovery"],
            IsDeleted = true,
            DeletedAt = now.AddDays(-1),
            CreatedAt = now.AddYears(-1),
            UpdatedAt = now.AddDays(-1)
        };
        var backupJson = new VaultBackupService(encryption).CreateBackup(
            [item, recoveryCodes], BackupPassphrase, now.AddHours(-1));
        var storage = new VaultStorageService(tempDirectory);
        var secret = totp.GenerateSecret();
        var code = ComputeTotp(secret);

        using var vault = new VaultService(storage, encryption, totp, utcNow: () => now);
        vault.RecoverFromBackup(new VaultRecoveryRequest
        {
            BackupJson = backupJson,
            BackupPassphrase = BackupPassphrase,
            NewMasterPassword = NewMasterPassword,
            NewTotpSecretBase32 = secret,
            TotpConfirmationCode = code,
            RecoveryKey = RecoveryKeyService.Generate(), RecoveryKeySaved = true
        });

        Assert.True(vault.IsInitialized);
        var config = storage.LoadConfig();
        Assert.Equal(4, config.Version);
        Assert.NotNull(config.MasterKeySlot);
        Assert.Equal(MasterPasswordService.Argon2idAlgorithm, config.MasterKeySlot!.KdfAlgorithm);
        Assert.NotEqual(oldConfig.SaltBase64, config.SaltBase64);
        Assert.NotEqual(oldConfig.EncryptedTotpSecret, config.EncryptedTotpSecret);
        Assert.Equal(now, config.LastVerifiedBackupAt);
        Assert.Null(config.LastExternalBackupAt);
        var listed = Assert.Single(vault.GetItems());
        var restored = vault.GetItemForEditing(listed.Id, code);
        Assert.Equal(item.Id, restored.Id);
        Assert.Equal(item.Username, restored.Username);
        Assert.Equal(item.Url, restored.Url);
        Assert.Equal(item.HideUrl, restored.HideUrl);
        Assert.Equal(item.Notes, restored.Notes);
        Assert.Equal(item.HideNotes, restored.HideNotes);
        Assert.Equal(item.IsFavorite, restored.IsFavorite);
        var restoredGroup = Assert.Single(vault.GetGroups(), group => group.Name == "Personal");
        Assert.Equal(restoredGroup.Id, restored.GroupId);
        Assert.Equal(item.Tags, restored.Tags);
        Assert.Equal(item.Password, vault.GetPassword(item.Id, code));
        Assert.Single(vault.GetPasswordHistory(item.Id, code));
        Assert.Equal(item.PasswordHistory[0].Password, vault.GetPasswordHistory(item.Id, code)[0].Password);
        Assert.Equal(item.TotpSecretBase32, restored.TotpSecretBase32);
        Assert.Equal(item.GetTotpConfiguration(), restored.GetTotpConfiguration());
        var deleted = Assert.Single(vault.GetDeletedItems());
        Assert.Equal(recoveryCodes.Id, deleted.Id);
        Assert.Equal(recoveryCodes.DeletedAt, deleted.DeletedAt);
        vault.RestoreDeletedItem(recoveryCodes.Id);
        Assert.Equal(recoveryCodes.RecoveryCodes, vault.GetRecoveryCodes(recoveryCodes.Id, code));

        var configText = File.ReadAllText(storage.ConfigPath);
        var vaultText = File.ReadAllText(storage.VaultPath);
        Assert.DoesNotContain(BackupPassphrase, configText + vaultText);
        Assert.DoesNotContain(NewMasterPassword, configText + vaultText);
        Assert.DoesNotContain(item.Password, configText + vaultText);
        Assert.DoesNotContain(item.TotpSecretBase32, configText + vaultText);
        Assert.DoesNotContain(recoveryCodes.RecoveryCodes[0], configText + vaultText);
    }

    [Fact]
    public void Invalid_recovery_inputs_leave_storage_uninitialized()
    {
        var encryption = new EncryptionService();
        var totp = new TotpService();
        var secret = totp.GenerateSecret();
        var backupJson = new VaultBackupService(encryption).CreateBackup(
            [new VaultItem { Title = "Account", Password = "secret-password" }],
            BackupPassphrase,
            DateTimeOffset.UtcNow);
        var storage = new VaultStorageService(tempDirectory);
        using var vault = new VaultService(storage, encryption, totp);

        Assert.Throws<UnauthorizedAccessException>(() => vault.RecoverFromBackup(new VaultRecoveryRequest
        {
            BackupJson = backupJson,
            BackupPassphrase = BackupPassphrase,
            NewMasterPassword = NewMasterPassword,
            NewTotpSecretBase32 = secret,
            TotpConfirmationCode = GetInvalidTotpCode(secret, totp),
            RecoveryKey = RecoveryKeyService.Generate(), RecoveryKeySaved = true
        }));
        AssertNoVaultPair(storage);

        Assert.Throws<ArgumentException>(() => vault.RecoverFromBackup(new VaultRecoveryRequest
        {
            BackupJson = backupJson,
            BackupPassphrase = BackupPassphrase,
            NewMasterPassword = "short",
            NewTotpSecretBase32 = secret,
            TotpConfirmationCode = ComputeTotp(secret),
            RecoveryKey = RecoveryKeyService.Generate(), RecoveryKeySaved = true
        }));
        AssertNoVaultPair(storage);

        Assert.Throws<System.Security.Cryptography.CryptographicException>(() => vault.RecoverFromBackup(new VaultRecoveryRequest
        {
            BackupJson = backupJson,
            BackupPassphrase = "incorrect backup passphrase",
            NewMasterPassword = NewMasterPassword,
            NewTotpSecretBase32 = secret,
            TotpConfirmationCode = ComputeTotp(secret),
            RecoveryKey = RecoveryKeyService.Generate(), RecoveryKeySaved = true
        }));
        AssertNoVaultPair(storage);
    }

    [Fact]
    public void Inspection_does_not_initialize_or_modify_storage()
    {
        var encryption = new EncryptionService();
        var backupJson = new VaultBackupService(encryption).CreateBackup(
            [new VaultItem { Title = "Account", Password = "secret-password" }],
            BackupPassphrase,
            DateTimeOffset.UtcNow);
        var storage = new VaultStorageService(tempDirectory);
        using var vault = new VaultService(storage, encryption, new TotpService());

        var inspection = vault.InspectBackupJson(backupJson, BackupPassphrase);

        Assert.Equal(1, inspection.TotalItemCount);
        AssertNoVaultPair(storage);
    }

    [Fact]
    public void Recovery_write_failure_leaves_no_partial_pair()
    {
        Directory.CreateDirectory(tempDirectory);
        var blockedDirectory = Path.Combine(tempDirectory, "not-a-directory");
        File.WriteAllText(blockedDirectory, "block storage creation");
        var encryption = new EncryptionService();
        var totp = new TotpService();
        var secret = totp.GenerateSecret();
        var backupJson = new VaultBackupService(encryption).CreateBackup(
            [new VaultItem { Title = "Account", Password = "secret-password" }],
            BackupPassphrase,
            DateTimeOffset.UtcNow);
        var storage = new VaultStorageService(blockedDirectory);
        using var vault = new VaultService(storage, encryption, totp);

        Assert.ThrowsAny<IOException>(() => vault.RecoverFromBackup(new VaultRecoveryRequest
        {
            BackupJson = backupJson,
            BackupPassphrase = BackupPassphrase,
            NewMasterPassword = NewMasterPassword,
            NewTotpSecretBase32 = secret,
            TotpConfirmationCode = ComputeTotp(secret),
            RecoveryKey = RecoveryKeyService.Generate(), RecoveryKeySaved = true
        }));
        AssertNoVaultPair(storage);
    }

    [Fact]
    public void Existing_or_partial_storage_refuses_recovery_without_overwriting_files()
    {
        Directory.CreateDirectory(tempDirectory);
        var configPath = Path.Combine(tempDirectory, ".config");
        File.WriteAllText(configPath, "existing-config");
        var storage = new VaultStorageService(tempDirectory);
        using var vault = new VaultService(storage, new EncryptionService(), new TotpService());

        Assert.Throws<InvalidOperationException>(() => vault.RecoverFromBackup(new VaultRecoveryRequest
        {
            BackupJson = "{}",
            BackupPassphrase = BackupPassphrase,
            NewMasterPassword = NewMasterPassword,
            NewTotpSecretBase32 = "ignored",
            TotpConfirmationCode = "000000",
            RecoveryKey = RecoveryKeyService.Generate(), RecoveryKeySaved = true
        }));
        Assert.Equal("existing-config", File.ReadAllText(configPath));
        Assert.False(File.Exists(storage.VaultPath));
    }

    [Fact]
    public void Interrupted_paired_write_is_completed_consistently_on_next_start()
    {
        Directory.CreateDirectory(tempDirectory);
        var configJson = JsonSerializer.Serialize(new AppConfig { SaltBase64 = Convert.ToBase64String(new byte[32]) });
        const string vaultJson = "{\"CipherTextBase64\":\"pending\"}";
        File.WriteAllText(
            Path.Combine(tempDirectory, ".state-transaction"),
            JsonSerializer.Serialize(new { ConfigJson = configJson, VaultJson = vaultJson }));

        var storage = new VaultStorageService(tempDirectory);

        Assert.True(storage.IsInitialized);
        Assert.Equal(configJson, File.ReadAllText(storage.ConfigPath));
        Assert.Equal(vaultJson, File.ReadAllText(storage.VaultPath));
        Assert.False(File.Exists(Path.Combine(tempDirectory, ".state-transaction")));
    }

    [Fact]
    public void Legacy_config_has_null_backup_health_timestamps()
    {
        Directory.CreateDirectory(tempDirectory);
        File.WriteAllText(Path.Combine(tempDirectory, ".config"), "{\"Version\":1,\"SaltBase64\":\"\"}");

        var config = new VaultStorageService(tempDirectory).LoadConfig();

        Assert.Null(config.LastExternalBackupAt);
        Assert.Null(config.LastVerifiedBackupAt);
    }

    [Fact]
    public void Backup_health_changes_only_after_successful_create_or_verification()
    {
        var now = new DateTimeOffset(2026, 9, 17, 10, 0, 0, TimeSpan.Zero);
        var storage = new VaultStorageService(tempDirectory);
        var totp = new TotpService();
        var secret = totp.GenerateSecret();
        var code = ComputeTotp(secret);
        using var vault = new VaultService(storage, new EncryptionService(), totp, utcNow: () => now);
        vault.InitializeNewVault(NewMasterPassword, secret, code, RecoveryKeyService.Generate(), true);
        Assert.Null(vault.LastExternalBackupAt);
        Assert.Null(vault.LastVerifiedBackupAt);

        Assert.Throws<DirectoryNotFoundException>(() => vault.CreateExternalBackupFile(
            Path.Combine(tempDirectory, "missing", "backup.json"), BackupPassphrase, code));
        Assert.Null(vault.LastExternalBackupAt);

        var path = Path.Combine(tempDirectory, "backup.json");
        vault.CreateExternalBackupFile(path, BackupPassphrase, code);
        Assert.Equal(now, vault.LastExternalBackupAt);
        Assert.Equal(now, vault.LastVerifiedBackupAt);
        var createdAt = now;
        now = now.AddSeconds(30);

        Assert.Throws<System.Security.Cryptography.CryptographicException>(() =>
            vault.VerifyExternalBackupFile(path, "incorrect backup passphrase"));
        Assert.Equal(createdAt, vault.LastVerifiedBackupAt);
        var inspection = vault.VerifyExternalBackupFile(path, BackupPassphrase);
        Assert.Equal(0, inspection.TotalItemCount);
        Assert.Equal(now, vault.LastVerifiedBackupAt);
        Assert.Equal(createdAt, vault.LastExternalBackupAt);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Verification_failure_reports_the_stage_and_preserves_previous_file_until_replacement(bool destinationFailure)
    {
        Directory.CreateDirectory(tempDirectory);
        var path = Path.Combine(tempDirectory, "backup.json");
        File.WriteAllText(path, "previous backup");
        var backup = new VaultBackupService();
        var json = backup.CreateBackup([], BackupPassphrase, DateTimeOffset.UtcNow);
        var calls = 0;
        var error = Assert.Throws<BackupOperationException>(() =>
            VaultService.WriteExternalBackupFile(path, json, candidate =>
            {
                calls++;
                if (destinationFailure == (candidate == path)) File.WriteAllText(candidate, "corrupted");
                backup.InspectBackup(File.ReadAllText(candidate), BackupPassphrase);
            }));
        Assert.Equal(destinationFailure ? 2 : 1, calls);
        Assert.Contains(destinationFailure ? "was written" : "previous backup was preserved", error.Message);
        Assert.Equal(destinationFailure ? "corrupted" : "previous backup", File.ReadAllText(path));
        Assert.Empty(Directory.GetFiles(tempDirectory, "*.tmp"));
    }

    [Fact]
    public void Replacement_failure_preserves_existing_backup_and_cleans_temporary_file()
    {
        Directory.CreateDirectory(tempDirectory);
        var path = Path.Combine(tempDirectory, "backup.json");
        File.WriteAllText(path, "previous backup");
        using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var error = Record.Exception(() => VaultService.WriteExternalBackupFile(path, "new backup", _ => { }));
            Assert.True(error is IOException or UnauthorizedAccessException);
        }
        Assert.Equal("previous backup", File.ReadAllText(path));
        Assert.Empty(Directory.GetFiles(tempDirectory, "*.tmp"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Verified_backup_updates_both_timestamps_in_one_save_or_reports_metadata_failure(bool failMetadata)
    {
        var now = DateTimeOffset.UtcNow;
        var saves = 0;
        var fail = false;
        var storage = new VaultStorageService(tempDirectory, stage =>
        {
            if (stage != "staged") return;
            saves++;
            if (fail) throw new IOException("simulated configuration write failure");
        });
        var totp = new TotpService();
        var secret = totp.GenerateSecret();
        using var vault = new VaultService(storage, new EncryptionService(), totp, utcNow: () => now);
        vault.InitializeNewVault(NewMasterPassword, secret, ComputeTotp(secret), RecoveryKeyService.Generate(), true);
        saves = 0;
        fail = failMetadata;
        var path = Path.Combine(tempDirectory, "backup.json");
        if (failMetadata)
        {
            var error = Assert.Throws<BackupOperationException>(() =>
                vault.CreateExternalBackupFile(path, BackupPassphrase, ComputeTotp(secret)));
            Assert.Contains("created and verified", error.Message);
            Assert.Contains("status could not be updated", error.Message);
        }
        else vault.CreateExternalBackupFile(path, BackupPassphrase, ComputeTotp(secret));
        Assert.Equal(1, saves);
        Assert.Equal(0, new VaultBackupService().InspectBackup(File.ReadAllText(path), BackupPassphrase).TotalItemCount);
        Assert.Equal(failMetadata ? (DateTimeOffset?)null : now, vault.LastExternalBackupAt);
        Assert.Equal(failMetadata ? (DateTimeOffset?)null : now, vault.LastVerifiedBackupAt);
    }

    public void Dispose()
    {
        if (Directory.Exists(tempDirectory)) Directory.Delete(tempDirectory, recursive: true);
    }

    private static VaultItem CreateCompletePasswordItem(DateTimeOffset now) => new()
    {
        Title = "Account",
        Username = "person@example.com",
        Password = "current-password",
        PasswordHistory = [new PasswordHistoryEntry { Password = "previous-password", ChangedAt = now.AddMonths(-1) }],
        TotpSecretBase32 = "JBSWY3DPEHPK3PXP",
        TotpIssuer = "Example",
        TotpAccountName = "person@example.com",
        TotpAlgorithm = TotpAlgorithm.Sha256,
        TotpDigits = 8,
        TotpPeriod = 60,
        Url = "https://example.com",
        HideUrl = true,
        Notes = "private note",
        HideNotes = true,
        IsFavorite = true,
        LegacyFolder = "Personal",
        Tags = ["email", "important"],
        CreatedAt = now.AddYears(-1),
        UpdatedAt = now
    };

    private static string ComputeTotp(string secret)
    {
        var totp = new Totp(Base32Encoding.ToBytes(secret));
        return totp.ComputeTotp(DateTime.UtcNow);
    }

    private static string GetInvalidTotpCode(string secret, TotpService totpService)
    {
        var value = (int.Parse(ComputeTotp(secret), System.Globalization.CultureInfo.InvariantCulture) + 1) % 1_000_000;
        var candidate = value.ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
        while (totpService.VerifyCode(secret, candidate))
        {
            value = (value + 1) % 1_000_000;
            candidate = value.ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
        }
        return candidate;
    }

    private static void AssertNoVaultPair(VaultStorageService storage)
    {
        Assert.False(File.Exists(storage.ConfigPath));
        Assert.False(File.Exists(storage.VaultPath));
        Assert.False(File.Exists(storage.TrustedUnlockTokenPath));
    }
}
