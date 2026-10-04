using OtpNet;
using PasswordTool.Core.Models;
using PasswordTool.Core.Services;
using System.Security.Cryptography;

namespace PasswordTool.Core.Tests;

public sealed class VaultServiceTests : IDisposable
{
    private readonly string tempDirectory = Path.Combine(
        Path.GetTempPath(),
        "PasswordTool.Core.Tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void Group_colors_validate_before_mutation_and_invalid_legacy_colors_do_not_block_unlock()
    {
        const string password = "correct horse battery staple";
        var storage = new VaultStorageService(tempDirectory);
        var encryption = new EncryptionService();
        var totp = new TotpService();
        var secret = totp.GenerateSecret();
        using var vault = new VaultService(storage, encryption, totp);
        vault.InitializeNewVault(password, secret, ComputeTotp(secret), RecoveryKeyService.Generate(), true);
        var group = vault.AddGroup("Database", " \t#ab12Ef \r\n");
        Assert.Equal("#AB12EF", group.AccentColor);

        foreach (var reset in new string?[] { null, string.Empty, " \t " })
        {
            vault.UpdateGroup(group.Id, group.Name, reset);
            Assert.Null(Assert.Single(vault.GetGroups()).AccentColor);
            vault.UpdateGroup(group.Id, group.Name, " #0033ff ");
            Assert.Equal("#0033FF", Assert.Single(vault.GetGroups()).AccentColor);
        }

        var original = Assert.Single(vault.GetGroups());
        var config = File.ReadAllText(storage.ConfigPath);
        var payload = storage.LoadVaultPayload();
        foreach (var invalid in new[] { "# FFFFF", "#\tFFFFF", "#FFF FF", "#12345G", "#12345", "#1234567", "123456" })
        {
            Assert.Throws<ArgumentException>(() => vault.AddGroup("Invalid", invalid));
            Assert.Throws<ArgumentException>(() => vault.UpdateGroup(group.Id, "Changed", invalid));
            var unchanged = Assert.Single(vault.GetGroups());
            Assert.Equal(original.Name, unchanged.Name);
            Assert.Equal(original.AccentColor, unchanged.AccentColor);
            Assert.Equal(original.UpdatedAt, unchanged.UpdatedAt);
            Assert.Equal(config, File.ReadAllText(storage.ConfigPath));
            Assert.Equal(payload, storage.LoadVaultPayload());
        }

        var item = vault.AddItem(new VaultItem { Title = "Preserved", Password = "secret", GroupId = group.Id });
        vault.ClearSession();
        Assert.True(vault.TryUnlockMasterPassword(password, out var error), error);
        Assert.True(vault.VerifyTotpForSession(ComputeTotp(secret)));
        Assert.Equal(original.Name, Assert.Single(vault.GetGroups()).Name);
        Assert.Equal(original.AccentColor, Assert.Single(vault.GetGroups()).AccentColor);

        Assert.True(new MasterPasswordService(encryption).TryUnlockConfig(password, storage.LoadConfig(), out var key, out _));
        try
        {
            var legacy = encryption.DecryptObject<VaultData>(storage.LoadVaultPayload(), key, MasterPasswordService.VaultContext);
            Assert.Single(legacy.Groups).AccentColor = "# FFFFF";
            legacy.Groups.Add(new VaultGroup { Name = "Invalid old color", AccentColor = "#GGGGGG" });
            storage.SaveVaultPayload(encryption.EncryptObject(legacy, key, MasterPasswordService.VaultContext));
        }
        finally { CryptographicOperations.ZeroMemory(key); }

        vault.ClearSession();
        Assert.True(vault.TryUnlockWithGoogleAuthenticator(ComputeTotp(secret), out error), error);
        Assert.Equal(2, vault.GetGroups().Count);
        Assert.All(vault.GetGroups(), loaded => Assert.Null(loaded.AccentColor));
        Assert.Equal(item.Id, Assert.Single(vault.GetItems()).Id);
        vault.ClearSession();
        Assert.True(vault.TryUnlockMasterPassword(password, out error), error);
        Assert.True(vault.VerifyTotpForSession(ComputeTotp(secret)));
        Assert.Equal(2, vault.GetGroups().Count);
        Assert.All(vault.GetGroups(), loaded => Assert.Null(loaded.AccentColor));
        Assert.Equal(item.Id, Assert.Single(vault.GetItems()).Id);
    }

    [Fact]
    public void Group_deletion_requires_exact_confirmation_and_fresh_totp_and_removes_only_group_data()
    {
        var totpService = new TotpService();
        var secret = totpService.GenerateSecret();
        using var vault = new VaultService(new VaultStorageService(tempDirectory), new EncryptionService(), totpService);
        vault.InitializeNewVault("correct horse battery staple", secret, ComputeTotp(secret), RecoveryKeyService.Generate(), true);
        var group = vault.AddGroup("Database", "#336699");
        var item = vault.AddItem(new VaultItem { Title = "PostgreSQL", Password = "secret", GroupId = group.Id });
        var other = vault.AddItem(new VaultItem { Title = "Other", Password = "keep" });
        var trashed = vault.AddItem(new VaultItem { Title = "Old", Password = "old", GroupId = group.Id });
        vault.DeleteItem(trashed.Id);

        Assert.Equal(group.Id, item.GroupId);
        Assert.Single(vault.GetGroups());

        var confirmation = "Confirm delete all data in \"Database\"";
        Assert.Throws<ArgumentException>(() => vault.DeleteGroup(group.Id, "Delete Database", ComputeTotp(secret)));
        Assert.Throws<UnauthorizedAccessException>(() => vault.DeleteGroup(group.Id, confirmation, GetInvalidTotpCode(secret, totpService)));
        Assert.Equal(2, vault.GetItems().Count);
        Assert.Single(vault.GetGroups());
        Assert.Single(vault.GetDeletedItems());
        vault.DeleteGroup(group.Id, confirmation, ComputeTotp(secret));

        Assert.Equal(other.Id, Assert.Single(vault.GetItems()).Id);
        Assert.Empty(vault.GetDeletedItems());
        Assert.Empty(vault.GetGroups());
        vault.ClearSession();
        Assert.True(vault.TryUnlockMasterPassword("correct horse battery staple", out _));
        Assert.True(vault.VerifyTotpForSession(ComputeTotp(secret)));
        Assert.Equal(other.Id, Assert.Single(vault.GetItems()).Id);
        Assert.Empty(vault.GetDeletedItems());
        Assert.Empty(vault.GetGroups());
    }

    [Fact]
    public void Password_precheck_does_not_open_a_session_or_change_storage()
    {
        var totp = new TotpService();
        var secret = totp.GenerateSecret();
        var storage = new VaultStorageService(tempDirectory);
        using var vault = new VaultService(storage, new EncryptionService(), totp);
        vault.InitializeNewVault("correct horse battery staple", secret, ComputeTotp(secret), RecoveryKeyService.Generate(), true);
        vault.ClearSession();
        var config = File.ReadAllText(storage.ConfigPath);
        var payload = File.ReadAllText(storage.VaultPath);

        Assert.False(vault.ValidateMasterPassword("wrong password"));
        Assert.False(vault.ValidateMasterPassword(""));
        Assert.True(vault.ValidateMasterPassword("correct horse battery staple"));
        Assert.Throws<InvalidOperationException>(() => vault.GetItems());
        Assert.Throws<InvalidOperationException>(() => vault.VerifyTotpForSession(ComputeTotp(secret)));
        Assert.Equal(config, File.ReadAllText(storage.ConfigPath));
        Assert.Equal(payload, File.ReadAllText(storage.VaultPath));
    }

    [Fact]
    public void Vault_storage_is_encrypted_and_supports_master_password_and_trusted_totp_login()
    {
        var storage = new VaultStorageService(tempDirectory);
        var encryption = new EncryptionService();
        var totpService = new TotpService();
        var vaultService = new VaultService(storage, encryption, totpService);
        var secret = totpService.GenerateSecret();
        var code = ComputeTotp(secret);
        var invalidCode = GetInvalidTotpCode(secret, totpService);

        vaultService.InitializeNewVault("correct horse battery staple", secret, code, RecoveryKeyService.Generate(), true);
        var addedItem = vaultService.AddItem(new VaultItem
        {
            Title = "Email",
            Username = "person@example.com",
            Password = "super-secret-value",
            Url = "https://example.com",
            Notes = "private note"
        });

        var encryptedVaultFile = File.ReadAllText(storage.VaultPath);
        var configFile = File.ReadAllText(storage.ConfigPath);

        Assert.DoesNotContain("super-secret-value", encryptedVaultFile);
        Assert.DoesNotContain("person@example.com", encryptedVaultFile);
        Assert.DoesNotContain(secret, configFile);

        vaultService.ClearSession();

        using var reopenedVault = new VaultService(storage, encryption, totpService);
        Assert.True(reopenedVault.CanUnlockWithGoogleAuthenticatorToken);
        Assert.False(reopenedVault.TryUnlockWithGoogleAuthenticator(invalidCode, out _));
        Assert.Throws<InvalidOperationException>(() => reopenedVault.GetItems());
        Assert.True(reopenedVault.TryUnlockWithGoogleAuthenticator(code, out _));
        Assert.Single(reopenedVault.GetItems());
        Assert.Equal("super-secret-value", reopenedVault.GetPassword(addedItem.Id, invalidCode));
        Assert.Equal("super-secret-value", reopenedVault.GetPassword(addedItem.Id, code));

        reopenedVault.ClearSession();
        Assert.False(reopenedVault.TryUnlockMasterPassword("wrong master password", out _));
        Assert.Throws<InvalidOperationException>(() => reopenedVault.VerifyTotpForSession(code));
        Assert.True(reopenedVault.TryUnlockMasterPassword("correct horse battery staple", out _));
        Assert.Throws<InvalidOperationException>(() => reopenedVault.GetItems());
        Assert.True(reopenedVault.VerifyTotpForSession(code));
        Assert.True(reopenedVault.IsGoogleAuthenticatorConfigured);
        Assert.Single(reopenedVault.GetItems());
        Assert.Equal("super-secret-value", reopenedVault.GetPassword(addedItem.Id, invalidCode));
        Assert.Equal("super-secret-value", reopenedVault.GetPassword(addedItem.Id, code));
    }

    [Fact]
    public void Google_authenticator_login_token_expires_after_one_day()
    {
        var now = new DateTimeOffset(2026, 1, 1, 8, 0, 0, TimeSpan.Zero);
        var storage = new VaultStorageService(tempDirectory);
        var encryption = new EncryptionService();
        var totpService = new TotpService();
        var secret = totpService.GenerateSecret();
        var code = ComputeTotp(secret);

        using (var vaultService = new VaultService(storage, encryption, totpService, utcNow: () => now))
        {
            vaultService.InitializeNewVault("correct horse battery staple", secret, code, RecoveryKeyService.Generate(), true);
            vaultService.AddItem(new VaultItem
            {
                Title = "Email",
                Password = "super-secret-value"
            });
        }

        using (var withinTokenLifetime = new VaultService(storage, encryption, totpService, utcNow: () => now.AddHours(23)))
        {
            Assert.True(withinTokenLifetime.CanUnlockWithGoogleAuthenticatorToken);
            Assert.True(withinTokenLifetime.TryUnlockWithGoogleAuthenticator(code, out _));
            Assert.Single(withinTokenLifetime.GetItems());
        }

        using var afterTokenExpiration = new VaultService(storage, encryption, totpService, utcNow: () => now.AddDays(1).AddSeconds(1));
        Assert.False(afterTokenExpiration.CanUnlockWithGoogleAuthenticatorToken);
        Assert.False(afterTokenExpiration.TryUnlockWithGoogleAuthenticator(code, out var errorMessage));
        Assert.Contains("expired", errorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Throws<InvalidOperationException>(() => afterTokenExpiration.GetItems());
    }

    [Fact]
    public void Legacy_vault_without_totp_secret_cannot_complete_the_required_login()
    {
        var storage = new VaultStorageService(tempDirectory);
        var encryption = new EncryptionService();
        var totpService = new TotpService();
        var masterPasswordService = new MasterPasswordService(encryption);
        const string masterPassword = "correct horse battery staple";
        var config = masterPasswordService.CreateConfig(masterPassword, totpService.GenerateSecret());
        config.EncryptedTotpSecret = string.Empty;
        var key = masterPasswordService.DeriveKey(masterPassword, config);

        try
        {
            storage.SaveConfig(config);
            var legacyItemId = Guid.NewGuid();
            var legacyJson = $$"""
                {
                  "Version": 1,
                  "Items": [
                    {
                      "Id": "{{legacyItemId}}",
                      "Title": "Legacy",
                      "Username": "",
                      "Password": "legacy-secret",
                      "RecoveryCodes": [],
                      "Url": "",
                      "HideUrl": false,
                      "Notes": "",
                      "HideNotes": false
                    }
                  ]
                }
                """;
            storage.SaveVaultPayload(encryption.EncryptString(legacyJson, key));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }

        using var vaultService = new VaultService(storage, encryption, totpService);

        Assert.False(vaultService.TryUnlockMasterPassword("wrong master password", out _));
        Assert.True(vaultService.TryUnlockMasterPassword(masterPassword, out _));
        Assert.False(vaultService.IsGoogleAuthenticatorConfigured);
        Assert.False(vaultService.CanUnlockWithGoogleAuthenticatorToken);
        Assert.False(vaultService.VerifyTotpForSession("000000"));
        Assert.Throws<UnauthorizedAccessException>(() => vaultService.GetItems());
    }

    [Fact]
    public void Recovery_codes_and_encrypted_json_backup_round_trip_without_overwriting_existing_items()
    {
        var sourceDirectory = Path.Combine(tempDirectory, "source");
        var destinationDirectory = Path.Combine(tempDirectory, "destination");
        var encryption = new EncryptionService();
        var totpService = new TotpService();
        var sourceSecret = totpService.GenerateSecret();
        var sourceCode = ComputeTotp(sourceSecret);

        string backupJson;
        using (var source = new VaultService(new VaultStorageService(sourceDirectory), encryption, totpService))
        {
            source.InitializeNewVault("source master password", sourceSecret, sourceCode, RecoveryKeyService.Generate(), true);
            source.AddItem(new VaultItem { Title = "Email", Password = "source-password" });
            source.AddItem(new VaultItem
            {
                Type = VaultItemType.RecoveryCodes,
                Title = "Email recovery",
                RecoveryCodes = ["abcd-1234", "efgh-5678"]
            });
            backupJson = source.ExportBackupJson("correct backup passphrase", sourceCode);
        }

        var destinationStorage = new VaultStorageService(destinationDirectory);
        var destinationSecret = totpService.GenerateSecret();
        var destinationCode = ComputeTotp(destinationSecret);
        using var destination = new VaultService(destinationStorage, encryption, totpService);
        destination.InitializeNewVault("destination master password", destinationSecret, destinationCode, RecoveryKeyService.Generate(), true);
        var existing = destination.AddItem(new VaultItem { Title = "Existing", Password = "existing-password" });

        var preview = destination.PreviewBackupImport(backupJson, "correct backup passphrase", destinationCode);
        Assert.Equal(2, preview.NewItemCount);
        Assert.Equal(2, destination.ImportBackupJson(backupJson, "correct backup passphrase", destinationCode));
        Assert.Equal(0, destination.ImportBackupJson(backupJson, "correct backup passphrase", destinationCode));

        var items = destination.GetItems();
        Assert.Equal(3, items.Count);
        Assert.Equal("existing-password", destination.GetPassword(existing.Id, destinationCode));
        var recoveryItem = Assert.Single(items, item => item.Type == VaultItemType.RecoveryCodes);
        Assert.Equal(2, recoveryItem.RecoveryCodeCount);
        Assert.Equal(["abcd-1234", "efgh-5678"], destination.GetRecoveryCodes(recoveryItem.Id, destinationCode));

        var encryptedStorage = File.ReadAllText(destinationStorage.VaultPath);
        Assert.DoesNotContain("source-password", encryptedStorage);
        Assert.DoesNotContain("abcd-1234", encryptedStorage);
    }

    [Fact]
    public void Login_mode_defaults_to_hybrid_and_requires_master_password_to_change()
    {
        var storage = new VaultStorageService(tempDirectory);
        var encryption = new EncryptionService();
        var totpService = new TotpService();
        var secret = totpService.GenerateSecret();
        var code = ComputeTotp(secret);
        using var vaultService = new VaultService(storage, encryption, totpService);

        vaultService.InitializeNewVault("correct horse battery staple", secret, code, RecoveryKeyService.Generate(), true);

        Assert.Equal(VaultLoginMode.Hybrid, vaultService.LoginMode);
        Assert.False(vaultService.TrySetLoginMode("incorrect password", VaultLoginMode.GoogleAuthenticatorCode, out _));
        Assert.Equal(VaultLoginMode.Hybrid, vaultService.LoginMode);
        Assert.False(vaultService.TrySetLoginMode("correct horse battery staple", VaultLoginMode.GoogleAuthenticatorCode, out var modeError));
        Assert.Contains("both", modeError, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(VaultLoginMode.Hybrid, vaultService.LoginMode);
    }

    [Fact]
    public void Hidden_url_and_notes_are_preserved_when_vault_items_are_saved()
    {
        var storage = new VaultStorageService(tempDirectory);
        var encryption = new EncryptionService();
        var totpService = new TotpService();
        var secret = totpService.GenerateSecret();
        using var vaultService = new VaultService(storage, encryption, totpService);

        vaultService.InitializeNewVault("correct horse battery staple", secret, ComputeTotp(secret), RecoveryKeyService.Generate(), true);
        var item = vaultService.AddItem(new VaultItem
        {
            Title = "Private account",
            Password = "super-secret-value",
            Url = "https://example.com",
            HideUrl = true,
            Notes = "Private note",
            HideNotes = true,
            IsFavorite = true,
            LegacyFolder = "Personal",
            Tags = ["email", "important"],
            TotpSecretBase32 = "JBSWY3DPEHPK3PXP"
        });

        var savedItem = vaultService.GetItemForEditing(item.Id, ComputeTotp(secret));
        Assert.True(savedItem.HideUrl);
        Assert.True(savedItem.HideNotes);
        Assert.Equal("https://example.com", savedItem.Url);
        Assert.Equal("Private note", savedItem.Notes);
        Assert.True(savedItem.IsFavorite);
        Assert.Equal("Personal", savedItem.LegacyFolder);
        Assert.Equal(["email", "important"], savedItem.Tags);
        Assert.Equal("JBSWY3DPEHPK3PXP", savedItem.TotpSecretBase32);
        Assert.DoesNotContain("JBSWY3DPEHPK3PXP", File.ReadAllText(storage.VaultPath));

        var listItem = Assert.Single(vaultService.GetItems());
        Assert.True(listItem.HideUrl);
        Assert.True(listItem.HideNotes);
        Assert.Empty(listItem.Url);
        Assert.Empty(listItem.Notes);
        Assert.True(listItem.HasTotp);
        Assert.Empty(listItem.TotpSecretBase32);
    }

    [Fact]
    public void Successful_login_authorizes_sensitive_actions_for_five_hours()
    {
        var now = new DateTimeOffset(2026, 1, 1, 8, 0, 0, TimeSpan.Zero);
        var storage = new VaultStorageService(tempDirectory);
        var totpService = new TotpService();
        var secret = totpService.GenerateSecret();
        var code = ComputeTotp(secret);
        using var vault = new VaultService(storage, new EncryptionService(), totpService, utcNow: () => now);
        vault.InitializeNewVault("correct horse battery staple", secret, code, RecoveryKeyService.Generate(), true);
        var item = vault.AddItem(new VaultItem { Title = "Email", Password = "secret" });
        vault.ClearSession();
        Assert.True(vault.UnlockWithMasterPassword("correct horse battery staple").Success);
        Assert.False(vault.IsSignInSessionActive);
        Assert.True(vault.VerifyTotpForSession(code));

        Assert.True(vault.IsSignInSessionActive);
        Assert.True(vault.VerifyTotpForSensitiveAction(code));
        Assert.True(vault.IsSignInSessionActive);
        Assert.Equal("secret", vault.GetPassword(item.Id, string.Empty));

        now = now.AddHours(5).AddSeconds(1);
        Assert.False(vault.IsSignInSessionActive);
        Assert.Throws<InvalidOperationException>(() => vault.VerifyTotpForSensitiveAction(code));
        Assert.Throws<InvalidOperationException>(() => vault.GetPassword(item.Id, string.Empty));
    }

    [Fact]
    public void Security_timeouts_require_the_master_password_persist_and_control_sensitive_sessions()
    {
        var now = new DateTimeOffset(2026, 1, 1, 8, 0, 0, TimeSpan.Zero);
        var storage = new VaultStorageService(tempDirectory);
        var totpService = new TotpService();
        var secret = totpService.GenerateSecret();
        var code = ComputeTotp(secret);
        using var vault = new VaultService(storage, new EncryptionService(), totpService, utcNow: () => now);
        vault.InitializeNewVault("correct horse battery staple", secret, code, RecoveryKeyService.Generate(), true);

        Assert.Equal(
            new VaultSecuritySettings(1, 5, 1),
            vault.SecuritySettings);
        Assert.False(vault.TryUpdateSettings(
            "incorrect master password",
            VaultLoginMode.Hybrid,
            new VaultSecuritySettings(20, 2),
            out var wrongPasswordError));
        Assert.Contains("incorrect", wrongPasswordError, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(new VaultSecuritySettings(1, 5, 1), vault.SecuritySettings);

        Assert.True(vault.VerifyTotpForSensitiveAction(code));
        Assert.True(vault.IsSignInSessionActive);
        Assert.True(vault.TryUpdateSettings(
            "correct horse battery staple",
            VaultLoginMode.Hybrid,
            new VaultSecuritySettings(20, 2, 60),
            out var updateError), updateError);
        Assert.True(vault.IsSignInSessionActive);
        Assert.Equal(new VaultSecuritySettings(20, 2, 60), vault.SecuritySettings);

        Assert.True(vault.VerifyTotpForSensitiveAction(code));
        now = now.AddMinutes(2).AddSeconds(1);
        Assert.True(vault.IsSignInSessionActive);
        now = now.AddHours(5);
        Assert.False(vault.IsSignInSessionActive);
        Assert.Throws<InvalidOperationException>(() => vault.VerifyTotpForSensitiveAction(code));

        var persisted = storage.LoadConfig();
        Assert.Equal(20, persisted.InactivityLockTimeoutMinutes);
        Assert.Equal(2, persisted.SensitiveActionTimeoutMinutes);
        Assert.Equal(60, persisted.VaultOpenDurationMinutes);
        using (var reopened = new VaultService(storage, new EncryptionService(), totpService))
        {
            Assert.Equal(new VaultSecuritySettings(20, 2, 60), reopened.SecuritySettings);
        }

        Assert.Throws<InvalidOperationException>(() => vault.TryChangeMasterPassword(
            "correct horse battery staple",
            "a different secure master password",
            out _));
    }

    [Fact]
    public void Security_timeouts_reject_values_outside_supported_ranges()
    {
        var storage = new VaultStorageService(tempDirectory);
        var totpService = new TotpService();
        var secret = totpService.GenerateSecret();
        var code = ComputeTotp(secret);
        using var vault = new VaultService(storage, new EncryptionService(), totpService);
        vault.InitializeNewVault("correct horse battery staple", secret, code, RecoveryKeyService.Generate(), true);

        Assert.False(vault.TryUpdateSettings(
            "correct horse battery staple",
            VaultLoginMode.Hybrid,
            new VaultSecuritySettings(0, 5),
            out var inactivityError));
        Assert.Contains("inactivity", inactivityError, StringComparison.OrdinalIgnoreCase);
        Assert.False(vault.TryUpdateSettings(
            "correct horse battery staple",
            VaultLoginMode.Hybrid,
            new VaultSecuritySettings(10, 31),
            out var sensitiveError));
        Assert.Contains("sensitive-action", sensitiveError, StringComparison.OrdinalIgnoreCase);
        Assert.False(vault.TryUpdateSettings(
            "correct horse battery staple",
            VaultLoginMode.Hybrid,
            new VaultSecuritySettings(10, 5, 15),
            out var durationError));
        Assert.Contains("vault open duration", durationError, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(new VaultSecuritySettings(1, 5, 1), vault.SecuritySettings);
    }

    [Fact]
    public void Legacy_config_without_timeout_fields_uses_secure_defaults()
    {
        Directory.CreateDirectory(tempDirectory);
        File.WriteAllText(
            Path.Combine(tempDirectory, ".config"),
            "{\"Version\":1,\"LoginMode\":0}");
        var storage = new VaultStorageService(tempDirectory);
        using var vault = new VaultService(storage, new EncryptionService(), new TotpService());

        Assert.Equal(new VaultSecuritySettings(1, 5, 1), vault.SecuritySettings);
    }

    [Fact]
    public void Persisted_security_timeouts_are_validated_before_use()
    {
        Directory.CreateDirectory(tempDirectory);
        File.WriteAllText(
            Path.Combine(tempDirectory, ".config"),
            "{\"InactivityLockTimeoutMinutes\":0,\"SensitiveActionTimeoutMinutes\":5}");
        var storage = new VaultStorageService(tempDirectory);
        using var vault = new VaultService(storage, new EncryptionService(), new TotpService());

        var error = Assert.Throws<ArgumentOutOfRangeException>(() => vault.SecuritySettings);
        Assert.Contains("inactivity", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Csv_import_previews_adds_and_then_skips_matching_accounts()
    {
        var storage = new VaultStorageService(tempDirectory);
        var totpService = new TotpService();
        var secret = totpService.GenerateSecret();
        var code = ComputeTotp(secret);
        using var vault = new VaultService(storage, new EncryptionService(), totpService);
        vault.InitializeNewVault("correct horse battery staple", secret, code, RecoveryKeyService.Generate(), true);
        const string csv = "name,url,username,password,login_totp\nEmail,https://example.com,user@example.com,password,JBSWY3DPEHPK3PXP";

        var plan = vault.PreviewCsvImport(csv, code);
        Assert.Equal(1, plan.NewItemCount);
        Assert.Equal(1, vault.ImportCsv(csv, string.Empty));
        Assert.Equal(0, vault.ImportCsv(csv, string.Empty));

        var item = Assert.Single(vault.GetItems());
        Assert.True(item.HasTotp);
        Assert.Equal("password", vault.GetPassword(item.Id, string.Empty));
        Assert.DoesNotContain("password", File.ReadAllText(storage.VaultPath));
        Assert.DoesNotContain("JBSWY3DPEHPK3PXP", File.ReadAllText(storage.VaultPath));
    }

    public void Dispose()
    {
        if (!Directory.Exists(tempDirectory))
        {
            return;
        }

        foreach (var path in Directory.EnumerateFileSystemEntries(tempDirectory, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(path, FileAttributes.Normal);
        }

        Directory.Delete(tempDirectory, recursive: true);
    }

    private static string ComputeTotp(string secretBase32)
    {
        var secretBytes = Base32Encoding.ToBytes(secretBase32);
        return new Totp(secretBytes).ComputeTotp();
    }

    private static string GetInvalidTotpCode(string secretBase32, TotpService totpService)
    {
        for (var value = 0; value <= 999_999; value++)
        {
            var code = value.ToString("D6");
            if (!totpService.VerifyCode(secretBase32, code))
            {
                return code;
            }
        }

        throw new InvalidOperationException("Could not find an invalid TOTP code.");
    }
}
