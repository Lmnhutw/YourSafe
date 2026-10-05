using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using PasswordTool.Core.Models;
using PasswordTool.Core.Services;

namespace PasswordTool.Core.Tests;

public sealed class RecoveryKeyAndDeadlineTests : IDisposable
{
    private const string Password = "correct horse battery staple";
    private const string NewPassword = "a different strong master password";
    private readonly string directory = Path.Combine(Path.GetTempPath(), "PasswordTool.RecoveryKey.Tests", Guid.NewGuid().ToString("N"));
    private readonly TotpService totp = new();

    [Fact]
    public void Wrappers_unwrap_the_same_dek_and_reject_wrong_keys_formats_and_tampering()
    {
        var recovery = new RecoveryKeyService();
        var key = RecoveryKeyService.Generate();
        var dek = RandomNumberGenerator.GetBytes(32);
        var slot = recovery.Wrap(key, dek);
        var masters = new MasterPasswordService();
        var config = masters.CreateEnvelopeConfig(Password, totp.GenerateSecret(), dek);
        Assert.False(masters.TryUnlockConfig("wrong password", config, out _, out _));
        Assert.True(masters.TryUnlockConfig(Password, config, out var masterDek, out _));
        var unwrapped = recovery.Unwrap(key, slot);
        try { Assert.Equal(dek, unwrapped); Assert.Equal(masterDek, unwrapped); }
        finally { CryptographicOperations.ZeroMemory(unwrapped); CryptographicOperations.ZeroMemory(masterDek); CryptographicOperations.ZeroMemory(dek); }
        Assert.ThrowsAny<CryptographicException>(() => recovery.Unwrap(RecoveryKeyService.Generate(), slot));
        var originalWrapper = slot.WrappedVaultKey;
        var envelope = JsonNode.Parse(originalWrapper)!;
        var ciphertext = Convert.FromBase64String(envelope["CipherTextBase64"]!.GetValue<string>());
        ciphertext[0] ^= 1;
        envelope["CipherTextBase64"] = Convert.ToBase64String(ciphertext);
        slot.WrappedVaultKey = envelope.ToJsonString();
        Assert.ThrowsAny<CryptographicException>(() => recovery.Unwrap(key, slot));
        var masterEnvelope = JsonNode.Parse(config.MasterKeySlot!.WrappedVaultKey)!;
        var masterTag = Convert.FromBase64String(masterEnvelope["TagBase64"]!.GetValue<string>());
        masterTag[0] ^= 1;
        masterEnvelope["TagBase64"] = Convert.ToBase64String(masterTag);
        config.MasterKeySlot.WrappedVaultKey = masterEnvelope.ToJsonString();
        Assert.False(masters.TryUnlockConfig(Password, config, out _, out _));
        slot.WrappedVaultKey = originalWrapper;
        Assert.Throws<FormatException>(() => recovery.Unwrap(key.Replace("RK1", "RK2"), slot));
        slot.Version = 2;
        Assert.Throws<NotSupportedException>(() => recovery.Unwrap(key, slot));
        slot.Version = 1;
        slot.WrappedVaultKey = slot.WrappedVaultKey.Replace("AES-256-GCM", "AES-128-GCM");
        Assert.Throws<CryptographicException>(() => recovery.Unwrap(key, slot));
    }

    [Fact]
    public void Reset_requires_confirmation_and_otp_preserves_ciphertext_and_revokes_old_credentials()
    {
        var storage = new VaultStorageService(directory);
        var oldKey = RecoveryKeyService.Generate();
        var secret = totp.GenerateSecret();
        using var vault = new VaultService(storage, new EncryptionService(), totp);
        vault.InitializeNewVault(Password, secret, totp.GetCurrentCode(secret).Code, oldKey, true);
        vault.AddItem(new VaultItem { Title = "Account", Password = "account-secret" });
        var originalConfig = File.ReadAllText(storage.ConfigPath);
        var originalVault = File.ReadAllText(storage.VaultPath);
        var oldToken = storage.LoadTrustedUnlockToken();
        vault.ClearSession();
        vault.ValidateRecoveryKey(oldKey);
        Assert.False(vault.IsSignInSessionActive);
        Assert.Throws<InvalidOperationException>(() => vault.GetItems());
        var newSecret = totp.GenerateSecret();
        var oldCode = totp.GetCurrentCode(secret).Code;
        while (totp.VerifyCode(newSecret, oldCode) || totp.VerifyCode(secret, totp.GetCurrentCode(newSecret).Code))
            newSecret = totp.GenerateSecret();
        var newKey = RecoveryKeyService.Generate();
        var request = new RecoveryKeyResetRequest(oldKey, NewPassword, newKey, true, newSecret, totp.GetCurrentCode(newSecret).Code);
        Assert.DoesNotContain(oldKey, request.ToString());
        Assert.Throws<InvalidOperationException>(() => vault.ResetWithRecoveryKey(request with { RecoveryKeySaved = false }));
        Assert.Throws<UnauthorizedAccessException>(() => vault.ResetWithRecoveryKey(request with { TotpConfirmationCode = "invalid" }));
        Assert.Equal(originalConfig, File.ReadAllText(storage.ConfigPath));
        Assert.Equal(originalVault, File.ReadAllText(storage.VaultPath));
        vault.ResetWithRecoveryKey(request);
        Assert.Equal(originalVault, File.ReadAllText(storage.VaultPath));
        Assert.False(vault.IsVaultUnlocked);
        Assert.False(vault.IsSignInSessionActive);
        Assert.False(vault.UnlockWithMasterPassword(Password).Success);
        Assert.ThrowsAny<CryptographicException>(() => vault.ValidateRecoveryKey(oldKey));
        vault.ValidateRecoveryKey(newKey);
        storage.SaveTrustedUnlockToken(oldToken);
        Assert.False(vault.TryUnlockWithGoogleAuthenticator(totp.GetCurrentCode(secret).Code, out _));
        Assert.True(vault.UnlockWithMasterPassword(request.NewMasterPassword).Success);
        Assert.False(vault.VerifyTotpForSession(oldCode));
        Assert.False(vault.VerifyTotpForSession("invalid"));
        Assert.True(vault.VerifyTotpForSession(totp.GetCurrentCode(newSecret).Code));
        Assert.Single(vault.GetItems());
        Assert.Contains(storage.GetSnapshots(), snapshot => snapshot.HasOldSecurityState);
    }

    [Theory]
    [InlineData("staged")]
    [InlineData("verified")]
    [InlineData("config-replaced")]
    [InlineData("vault-replaced")]
    public void Recovery_write_failures_preserve_old_pair_and_restart_can_retry(string checkpoint)
    {
        var storage = new VaultStorageService(directory);
        var oldKey = RecoveryKeyService.Generate();
        var secret = totp.GenerateSecret();
        using (var initial = new VaultService(storage, new EncryptionService(), totp))
            initial.InitializeNewVault(Password, secret, totp.GetCurrentCode(secret).Code, oldKey, true);
        var config = File.ReadAllText(storage.ConfigPath);
        var ciphertext = File.ReadAllText(storage.VaultPath);
        var faulting = new VaultStorageService(directory, stage => { if (stage == checkpoint) throw new IOException("Injected failure"); });
        var nextSecret = totp.GenerateSecret();
        var request = new RecoveryKeyResetRequest(oldKey, NewPassword, RecoveryKeyService.Generate(), true, nextSecret, totp.GetCurrentCode(nextSecret).Code);
        using (var vault = new VaultService(faulting, new EncryptionService(), totp))
            Assert.Throws<IOException>(() => vault.ResetWithRecoveryKey(request));
        var reopened = new VaultStorageService(directory);
        Assert.Equal(config, File.ReadAllText(reopened.ConfigPath));
        Assert.Equal(ciphertext, File.ReadAllText(reopened.VaultPath));
        using var retry = new VaultService(reopened, new EncryptionService(), totp);
        retry.ValidateRecoveryKey(oldKey);
        retry.ResetWithRecoveryKey(request);
    }

    [Fact]
    public void Recovery_staging_rejects_an_authenticator_different_from_the_verified_setup()
    {
        var storage = new VaultStorageService(directory);
        var key = RecoveryKeyService.Generate();
        var oldSecret = totp.GenerateSecret();
        var encryption = new EncryptionService();
        using (var initial = new VaultService(storage, encryption, totp))
            initial.InitializeNewVault(Password, oldSecret, totp.GetCurrentCode(oldSecret).Code, key, true);
        var originalConfig = File.ReadAllText(storage.ConfigPath);
        var originalVault = File.ReadAllText(storage.VaultPath);
        var faulting = new VaultStorageService(directory, stage =>
        {
            if (stage != "staged") return;
            var stagedPath = Directory.EnumerateFiles(directory, ".config.*.tmp").Single();
            var stagedConfig = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(stagedPath))!;
            Assert.True(new MasterPasswordService(encryption).TryUnlockConfig(NewPassword, stagedConfig, out var dek, out _));
            try { stagedConfig.EncryptedTotpSecret = encryption.EncryptString(oldSecret, dek, MasterPasswordService.TotpSecretContext); }
            finally { CryptographicOperations.ZeroMemory(dek); }
            File.WriteAllText(stagedPath, JsonSerializer.Serialize(stagedConfig));
        });
        var newSecret = totp.GenerateSecret();
        var request = new RecoveryKeyResetRequest(key, NewPassword, RecoveryKeyService.Generate(), true, newSecret, totp.GetCurrentCode(newSecret).Code);
        using var vault = new VaultService(faulting, encryption, totp);
        Assert.Throws<CryptographicException>(() => vault.ResetWithRecoveryKey(request));
        Assert.Equal(originalConfig, File.ReadAllText(storage.ConfigPath));
        Assert.Equal(originalVault, File.ReadAllText(storage.VaultPath));
        vault.ValidateRecoveryKey(key);
    }

    [Theory]
    [InlineData("password")]
    [InlineData("authenticator")]
    [InlineData("recovery-key")]
    public void Reset_rejects_reuse_of_any_previous_credential(string reusedCredential)
    {
        var storage = new VaultStorageService(directory);
        var recoveryKey = RecoveryKeyService.Generate();
        var secret = totp.GenerateSecret();
        using var vault = new VaultService(storage, new EncryptionService(), totp);
        vault.InitializeNewVault(Password, secret, totp.GetCurrentCode(secret).Code, recoveryKey, true);
        vault.ClearSession();
        var config = File.ReadAllText(storage.ConfigPath);
        var payload = File.ReadAllText(storage.VaultPath);
        var replacementSecret = reusedCredential == "authenticator" ? secret.ToLowerInvariant() : totp.GenerateSecret();
        var request = new RecoveryKeyResetRequest(recoveryKey,
            reusedCredential == "password" ? Password : NewPassword,
            reusedCredential == "recovery-key" ? recoveryKey : RecoveryKeyService.Generate(),
            true, replacementSecret, totp.GetCurrentCode(replacementSecret).Code);
        Assert.Throws<ArgumentException>(() => vault.ResetWithRecoveryKey(request));
        Assert.False(vault.IsSignInSessionActive);
        Assert.Equal(config, File.ReadAllText(storage.ConfigPath));
        Assert.Equal(payload, File.ReadAllText(storage.VaultPath));
        vault.ValidateRecoveryKey(recoveryKey);
    }

    [Fact]
    public void V3_enrollment_requires_password_login_and_rotation_preserves_ciphertext()
    {
        var storage = new VaultStorageService(directory);
        var secret = totp.GenerateSecret();
        using var vault = new VaultService(storage, new EncryptionService(), totp);
        vault.InitializeNewVault(Password, secret, totp.GetCurrentCode(secret).Code, RecoveryKeyService.Generate(), true);
        var config = storage.LoadConfig();
        config.Version = 3;
        config.RecoveryKeySlot = null;
        config.VaultOpenDurationMinutes = 120;
        storage.SaveConfig(config);
        vault.ClearSession();
        var original = File.ReadAllText(storage.ConfigPath);
        var ciphertext = File.ReadAllText(storage.VaultPath);
        Assert.True(vault.UnlockWithMasterPassword(Password).Success);
        Assert.Throws<InvalidOperationException>(() => vault.SaveRecoveryKey(Password, RecoveryKeyService.Generate(), true));
        Assert.Equal(original, File.ReadAllText(storage.ConfigPath));
        Assert.True(vault.VerifyTotpForSession(totp.GetCurrentCode(secret).Code));
        Assert.False(vault.IsVaultUnlocked);
        Assert.True(vault.HasActiveVaultSession);
        Assert.Throws<InvalidOperationException>(() => vault.GetItems());
        var key = RecoveryKeyService.Generate();
        Assert.Throws<InvalidOperationException>(() => vault.SaveRecoveryKey(Password, key, false));
        Assert.Equal(original, File.ReadAllText(storage.ConfigPath));
        vault.SaveRecoveryKey(Password, key, true);
        Assert.Equal(ciphertext, File.ReadAllText(storage.VaultPath));
        Assert.Equal(1, storage.LoadConfig().VaultOpenDurationMinutes);
        Assert.Throws<UnauthorizedAccessException>(() => vault.SaveRecoveryKey("wrong password", RecoveryKeyService.Generate(), true));
        var nextKey = RecoveryKeyService.Generate();
        vault.SaveRecoveryKey(Password, nextKey, true);
        Assert.Equal(ciphertext, File.ReadAllText(storage.VaultPath));
        Assert.ThrowsAny<CryptographicException>(() => vault.ValidateRecoveryKey(key));
        vault.ValidateRecoveryKey(nextKey);
    }

    [Fact]
    public void Trusted_v3_unlock_cannot_create_a_recovery_wrapper_or_access_the_workspace()
    {
        var now = DateTimeOffset.UtcNow;
        var storage = new VaultStorageService(directory);
        var encryption = new EncryptionService();
        var secret = totp.GenerateSecret();
        var dek = RandomNumberGenerator.GetBytes(32);
        try
        {
            var config = new MasterPasswordService(encryption).CreateEnvelopeConfig(Password, secret, dek);
            storage.SaveState(config, encryption.EncryptObject(new VaultData(), dek, MasterPasswordService.VaultContext));
            storage.SaveTrustedUnlockToken(new TrustedUnlockTokenService().CreateToken(dek, secret,
                VaultService.ComputeConfigFingerprint(config), now, now.AddDays(1)));
        }
        finally { CryptographicOperations.ZeroMemory(dek); }
        var originalConfig = File.ReadAllText(storage.ConfigPath);
        var originalVault = File.ReadAllText(storage.VaultPath);
        using var vault = new VaultService(storage, encryption, totp, utcNow: () => now);
        Assert.True(vault.TryUnlockWithGoogleAuthenticator(totp.GetCurrentCode(secret).Code, out _));
        Assert.True(vault.IsSignInSessionActive);
        Assert.True(vault.HasActiveVaultSession);
        Assert.False(vault.IsVaultUnlocked);
        Assert.Throws<InvalidOperationException>(() => vault.GetItems());
        Assert.Throws<UnauthorizedAccessException>(() => vault.SaveRecoveryKey(Password, RecoveryKeyService.Generate(), true));
        Assert.Equal(originalConfig, File.ReadAllText(storage.ConfigPath));
        Assert.Equal(originalVault, File.ReadAllText(storage.VaultPath));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(300)]
    public void Otp_cannot_renew_an_expired_vault_or_login_session(int elapsedMinutes)
    {
        var now = DateTimeOffset.UtcNow;
        var secret = totp.GenerateSecret();
        using var vault = new VaultService(new VaultStorageService(directory), new EncryptionService(), totp, utcNow: () => now);
        vault.InitializeNewVault(Password, secret, totp.GetCurrentCode(secret).Code, RecoveryKeyService.Generate(), true);
        var loginDeadline = vault.LoginExpiresAt;
        now = now.AddMinutes(elapsedMinutes);
        Assert.Throws<InvalidOperationException>(() => vault.VerifyTotpForSession(totp.GetCurrentCode(secret).Code));
        Assert.False(vault.HasActiveVaultSession);
        Assert.Equal(elapsedMinutes < 300 ? loginDeadline : null, vault.LoginExpiresAt);
        Assert.Null(vault.VaultExpiresAt);
        Assert.Throws<InvalidOperationException>(() => vault.GetItems());
    }

    [Fact]
    public void Login_after_expiration_requires_master_and_otp_and_refreshes_the_trusted_token()
    {
        var now = DateTimeOffset.UtcNow;
        var storage = new VaultStorageService(directory);
        var secret = totp.GenerateSecret();
        using var vault = new VaultService(storage, new EncryptionService(), totp, utcNow: () => now);
        vault.InitializeNewVault(Password, secret, totp.GetCurrentCode(secret).Code, RecoveryKeyService.Generate(), true);
        storage.DeleteTrustedUnlockToken();
        now = now.AddHours(5);
        Assert.True(vault.UnlockWithMasterPassword(Password).Success);
        Assert.False(vault.IsSignInSessionActive);
        Assert.False(vault.HasActiveVaultSession);
        Assert.Throws<InvalidOperationException>(() => vault.GetItems());
        Assert.False(vault.VerifyTotpForSession("invalid"));
        Assert.False(storage.HasTrustedUnlockToken);
        Assert.True(vault.VerifyTotpForSession(totp.GetCurrentCode(secret).Code));
        Assert.Equal(now.AddHours(5), vault.LoginExpiresAt);
        Assert.True(storage.HasTrustedUnlockToken);
        vault.ClearSession();
        Assert.True(vault.TryUnlockWithGoogleAuthenticator(totp.GetCurrentCode(secret).Code, out _));
    }

    [Theory]
    [InlineData("staged")]
    [InlineData("verified")]
    [InlineData("config-replaced")]
    [InlineData("vault-replaced")]
    public void Rotation_failure_preserves_existing_key_and_config(string checkpoint)
    {
        var storage = new VaultStorageService(directory);
        var key = RecoveryKeyService.Generate();
        var secret = totp.GenerateSecret();
        using (var initial = new VaultService(storage, new EncryptionService(), totp))
            initial.InitializeNewVault(Password, secret, totp.GetCurrentCode(secret).Code, key, true);
        var config = File.ReadAllText(storage.ConfigPath);
        var payload = File.ReadAllText(storage.VaultPath);
        var faulting = new VaultStorageService(directory, stage => { if (stage == checkpoint) throw new IOException("Injected rotation failure"); });
        using var vault = new VaultService(faulting, new EncryptionService(), totp);
        Assert.True(vault.UnlockWithMasterPassword(Password).Success);
        Assert.True(vault.VerifyTotpForSession(totp.GetCurrentCode(secret).Code));
        Assert.Throws<IOException>(() => vault.SaveRecoveryKey(Password, RecoveryKeyService.Generate(), true));
        Assert.Equal(config, File.ReadAllText(storage.ConfigPath));
        Assert.Equal(payload, File.ReadAllText(storage.VaultPath));
        vault.ValidateRecoveryKey(key);
    }

    [Theory]
    [InlineData("rotation")]
    [InlineData("settings")]
    [InlineData("item")]
    public void Deadline_crossing_at_staged_commit_does_not_write_state(string operation)
    {
        var now = DateTimeOffset.UtcNow;
        var storage = new VaultStorageService(directory);
        var secret = totp.GenerateSecret();
        using (var initial = new VaultService(storage, new EncryptionService(), totp, utcNow: () => now))
            initial.InitializeNewVault(Password, secret, totp.GetCurrentCode(secret).Code, RecoveryKeyService.Generate(), true);
        var config = File.ReadAllText(storage.ConfigPath);
        var payload = File.ReadAllText(storage.VaultPath);
        var guarded = new VaultStorageService(directory, stage => { if (stage == "verified") now = now.AddMinutes(1); });
        using var vault = new VaultService(guarded, new EncryptionService(), totp, utcNow: () => now);
        Assert.True(vault.UnlockWithMasterPassword(Password).Success);
        Assert.True(vault.VerifyTotpForSession(totp.GetCurrentCode(secret).Code));
        if (operation == "rotation")
            Assert.Throws<InvalidOperationException>(() => vault.SaveRecoveryKey(Password, RecoveryKeyService.Generate(), true));
        else if (operation == "settings")
            Assert.False(vault.TryUpdateSettings(Password, VaultLoginMode.Hybrid, new VaultSecuritySettings(1, 5, 30), out _));
        else
            Assert.Throws<InvalidOperationException>(() => vault.AddItem(new VaultItem { Title = "Expired", Password = "must not persist" }));
        Assert.Equal(config, File.ReadAllText(storage.ConfigPath));
        Assert.Equal(payload, File.ReadAllText(storage.VaultPath));
        Assert.False(vault.HasActiveVaultSession);
    }

    [Fact]
    public void Recovery_rejects_unsupported_config_versions_without_writing()
    {
        var storage = new VaultStorageService(directory);
        var key = RecoveryKeyService.Generate();
        var secret = totp.GenerateSecret();
        using var vault = new VaultService(storage, new EncryptionService(), totp);
        vault.InitializeNewVault(Password, secret, totp.GetCurrentCode(secret).Code, key, true);
        vault.ClearSession();
        var config = storage.LoadConfig();
        config.Version = 5;
        storage.SaveConfig(config);
        var original = File.ReadAllText(storage.ConfigPath);
        Assert.Throws<NotSupportedException>(() => vault.ValidateRecoveryKey(key));
        var request = new RecoveryKeyResetRequest(key, Password, RecoveryKeyService.Generate(), true, secret, totp.GetCurrentCode(secret).Code);
        Assert.Throws<NotSupportedException>(() => vault.ResetWithRecoveryKey(request));
        Assert.False(vault.CanUnlockWithGoogleAuthenticatorToken);
        Assert.False(vault.TryUnlockWithGoogleAuthenticator(totp.GetCurrentCode(secret).Code, out _));
        Assert.Equal(original, File.ReadAllText(storage.ConfigPath));
    }

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(5)] [InlineData(10)]
    [InlineData(30)] [InlineData(60)] [InlineData(120)] [InlineData(300)]
    public void Fixed_deadlines_ignore_activity_and_reunlock_never_extends_login(int duration)
    {
        var now = DateTimeOffset.UtcNow;
        var storage = new VaultStorageService(directory);
        var secret = totp.GenerateSecret();
        using var original = new VaultService(storage, new EncryptionService(), totp, utcNow: () => now);
        original.InitializeNewVault(Password, secret, totp.GetCurrentCode(secret).Code, RecoveryKeyService.Generate(), true);
        Assert.True(original.TryUpdateSettings(Password, VaultLoginMode.Hybrid, new VaultSecuritySettings(1, 5, duration), out _));
        original.LockVault();
        Assert.True(original.UnlockWithMasterPassword(Password).Success);
        Assert.Equal(now.AddMinutes(1), original.VaultExpiresAt); // Saved settings require an app restart.
        original.Dispose();
        using var vault = new VaultService(storage, new EncryptionService(), totp, utcNow: () => now);
        Assert.True(vault.UnlockWithMasterPassword(Password).Success);
        Assert.True(vault.VerifyTotpForSession(totp.GetCurrentCode(secret).Code));
        var loginDeadline = vault.LoginExpiresAt;
        vault.LockVault();
        Assert.True(vault.UnlockWithMasterPassword(Password).Success);
        var deadline = vault.VaultExpiresAt;
        Assert.Equal(now.AddMinutes(duration), deadline);
        now = now.AddMinutes(duration).AddTicks(-1);
        Assert.Empty(vault.GetItems());
        Assert.True(vault.VerifyTotpForSession(totp.GetCurrentCode(secret).Code));
        Assert.Equal(deadline, vault.VaultExpiresAt);
        now = now.AddTicks(1);
        Assert.Throws<InvalidOperationException>(() => vault.GetItems());
        Assert.False(vault.IsVaultUnlocked);
        Assert.Equal(duration < 300, vault.IsSignInSessionActive);
        if (duration == 300) { Assert.Null(vault.LoginExpiresAt); return; }
        Assert.True(vault.UnlockWithMasterPassword(Password).Success);
        Assert.Equal(loginDeadline, vault.LoginExpiresAt);
        Assert.True(vault.VaultExpiresAt <= loginDeadline);
        now = loginDeadline!.Value;
        Assert.Throws<InvalidOperationException>(() => vault.GetItems());
        Assert.Null(vault.LoginExpiresAt);
        Assert.Null(vault.VaultExpiresAt);
    }

    [Fact]
    public void Setup_does_not_commit_without_recovery_confirmation()
    {
        var storage = new VaultStorageService(directory);
        var secret = totp.GenerateSecret();
        using var vault = new VaultService(storage, new EncryptionService(), totp);
        Assert.Throws<InvalidOperationException>(() => vault.InitializeNewVault(Password, secret, totp.GetCurrentCode(secret).Code, RecoveryKeyService.Generate(), false));
        Assert.Throws<UnauthorizedAccessException>(() => vault.InitializeNewVault(Password, secret, "invalid", RecoveryKeyService.Generate(), true));
        Assert.False(storage.HasConfig);
        Assert.False(storage.HasVault);
    }

    [Fact]
    public void Expired_trash_is_purged_only_after_a_successful_normal_password_and_otp_login()
    {
        var now = DateTimeOffset.UtcNow;
        var storage = new VaultStorageService(directory);
        var secret = totp.GenerateSecret();
        using var vault = new VaultService(storage, new EncryptionService(), totp, utcNow: () => now);
        vault.InitializeNewVault(Password, secret, totp.GetCurrentCode(secret).Code, RecoveryKeyService.Generate(), true);
        var expired = vault.AddItem(new VaultItem { Title = "Expired", Password = "old secret" });
        vault.DeleteItem(expired.Id);
        var active = vault.AddItem(new VaultItem { Title = "Active", Password = "kept secret" });
        now = now.AddDays(1);
        Assert.True(vault.UnlockWithMasterPassword(Password).Success);
        Assert.True(vault.VerifyTotpForSession(totp.GetCurrentCode(secret).Code));
        var recent = vault.AddItem(new VaultItem { Title = "Recent", Password = "recent secret" });
        vault.DeleteItem(recent.Id);
        now = now.AddDays(29);
        vault.ClearSession();
        var config = File.ReadAllText(storage.ConfigPath);
        var payload = File.ReadAllText(storage.VaultPath);

        Assert.False(vault.UnlockWithMasterPassword("wrong password").Success);
        Assert.Equal(config, File.ReadAllText(storage.ConfigPath));
        Assert.Equal(payload, File.ReadAllText(storage.VaultPath));
        Assert.True(vault.UnlockWithMasterPassword(Password).Success);
        Assert.Equal(payload, File.ReadAllText(storage.VaultPath));
        Assert.False(vault.VerifyTotpForSession("invalid"));
        Assert.Equal(config, File.ReadAllText(storage.ConfigPath));
        Assert.Equal(payload, File.ReadAllText(storage.VaultPath));
        Assert.Throws<InvalidOperationException>(() => vault.GetDeletedItems());

        Assert.True(vault.VerifyTotpForSession(totp.GetCurrentCode(secret).Code));
        Assert.Equal(recent.Id, Assert.Single(vault.GetDeletedItems()).Id);
        Assert.Equal(active.Id, Assert.Single(vault.GetItems()).Id);
        Assert.NotEqual(payload, File.ReadAllText(storage.VaultPath));
        vault.ClearSession();
        Assert.True(vault.UnlockWithMasterPassword(Password).Success);
        Assert.True(vault.VerifyTotpForSession(totp.GetCurrentCode(secret).Code));
        Assert.Equal(recent.Id, Assert.Single(vault.GetDeletedItems()).Id);
    }

    [Fact]
    public void Password_reunlock_purges_newly_expired_trash_without_extending_the_active_login()
    {
        var now = DateTimeOffset.UtcNow;
        var storage = new VaultStorageService(directory);
        var secret = totp.GenerateSecret();
        using var vault = new VaultService(storage, new EncryptionService(), totp, utcNow: () => now);
        vault.InitializeNewVault(Password, secret, totp.GetCurrentCode(secret).Code, RecoveryKeyService.Generate(), true);
        var expired = vault.AddItem(new VaultItem { Title = "Expired", Password = "old secret" });
        vault.DeleteItem(expired.Id);
        now = now.AddDays(30).AddHours(-2);
        Assert.True(vault.UnlockWithMasterPassword(Password).Success);
        Assert.True(vault.VerifyTotpForSession(totp.GetCurrentCode(secret).Code));
        Assert.Single(vault.GetDeletedItems());
        var loginDeadline = vault.LoginExpiresAt;
        var payload = File.ReadAllText(storage.VaultPath);
        vault.LockVault();
        now = now.AddHours(2);
        Assert.True(vault.IsSignInSessionActive);
        Assert.False(vault.UnlockWithMasterPassword("wrong password").Success);
        Assert.Equal(payload, File.ReadAllText(storage.VaultPath));
        Assert.True(vault.UnlockWithMasterPassword(Password).Success);
        Assert.Empty(vault.GetDeletedItems());
        Assert.Equal(loginDeadline, vault.LoginExpiresAt);
        Assert.NotEqual(payload, File.ReadAllText(storage.VaultPath));
    }

    [Fact]
    public void V3_enrollment_preserves_expired_trash_ciphertext_until_the_next_normal_unlock()
    {
        var now = DateTimeOffset.UtcNow;
        var storage = new VaultStorageService(directory);
        var secret = totp.GenerateSecret();
        using var vault = new VaultService(storage, new EncryptionService(), totp, utcNow: () => now);
        vault.InitializeNewVault(Password, secret, totp.GetCurrentCode(secret).Code, RecoveryKeyService.Generate(), true);
        var expired = vault.AddItem(new VaultItem { Title = "Expired", Password = "old secret" });
        vault.DeleteItem(expired.Id);
        var config = storage.LoadConfig();
        config.Version = 3;
        config.RecoveryKeySlot = null;
        storage.SaveConfig(config);
        vault.ClearSession();
        now = now.AddDays(31);
        var payload = File.ReadAllText(storage.VaultPath);
        Assert.True(vault.UnlockWithMasterPassword(Password).Success);
        Assert.True(vault.VerifyTotpForSession(totp.GetCurrentCode(secret).Code));
        Assert.Equal(payload, File.ReadAllText(storage.VaultPath));
        vault.SaveRecoveryKey(Password, RecoveryKeyService.Generate(), true);
        Assert.Equal(payload, File.ReadAllText(storage.VaultPath));
        Assert.Equal(expired.Id, Assert.Single(vault.GetDeletedItems()).Id);
        vault.LockVault();
        Assert.True(vault.UnlockWithMasterPassword(Password).Success);
        Assert.Empty(vault.GetDeletedItems());
    }

    [Theory]
    [InlineData("staged")]
    [InlineData("verified")]
    [InlineData("config-replaced")]
    [InlineData("vault-replaced")]
    public void Setup_write_failures_leave_no_partial_storage_and_restart_can_retry(string checkpoint)
    {
        var secret = totp.GenerateSecret();
        var faulting = new VaultStorageService(directory, stage => { if (stage == checkpoint) throw new IOException("Injected setup failure"); });
        using (var vault = new VaultService(faulting, new EncryptionService(), totp))
            Assert.Throws<IOException>(() => vault.InitializeNewVault(Password, secret, totp.GetCurrentCode(secret).Code, RecoveryKeyService.Generate(), true));
        var reopened = new VaultStorageService(directory);
        Assert.False(reopened.HasConfig);
        Assert.False(reopened.HasVault);
        using var retry = new VaultService(reopened, new EncryptionService(), totp);
        retry.InitializeNewVault(Password, secret, totp.GetCurrentCode(secret).Code, RecoveryKeyService.Generate(), true);
        Assert.True(retry.IsVaultUnlocked);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Restart_completes_a_partial_legacy_migration_to_one_verified_pair(bool configWasReplaced)
    {
        var encryption = new EncryptionService();
        var masters = new MasterPasswordService(encryption);
        var secret = totp.GenerateSecret();
        var storage = new VaultStorageService(directory);
        var legacyConfig = masters.CreateConfig(Password, secret);
        var legacyKey = masters.DeriveKey(Password, legacyConfig);
        try { storage.SaveState(legacyConfig, encryption.EncryptObject(new VaultData { Items = [new VaultItem { Title = "Preserved", Password = "account secret" }] }, legacyKey)); }
        finally { CryptographicOperations.ZeroMemory(legacyKey); }
        var originalConfig = File.ReadAllText(storage.ConfigPath);
        var originalVault = File.ReadAllText(storage.VaultPath);
        var recoveryKey = RecoveryKeyService.Generate();
        using (var migration = new VaultService(storage, encryption, totp))
        {
            Assert.True(migration.UnlockWithMasterPassword(Password).Success);
            Assert.True(migration.VerifyTotpForSession(totp.GetCurrentCode(secret).Code));
            migration.SaveRecoveryKey(Password, recoveryKey, true);
        }
        var intendedConfig = File.ReadAllText(storage.ConfigPath);
        var intendedVault = File.ReadAllText(storage.VaultPath);
        Assert.NotEqual(originalVault, intendedVault);
        File.SetAttributes(storage.ConfigPath, FileAttributes.Normal);
        File.SetAttributes(storage.VaultPath, FileAttributes.Normal);
        File.WriteAllText(storage.ConfigPath, configWasReplaced ? intendedConfig : originalConfig);
        File.WriteAllText(storage.VaultPath, configWasReplaced ? originalVault : intendedVault);
        File.WriteAllText(storage.StateTransactionPath, JsonSerializer.Serialize(new { ConfigJson = intendedConfig, VaultJson = intendedVault }));
        var recovered = new VaultStorageService(directory);
        Assert.Equal(intendedConfig, File.ReadAllText(recovered.ConfigPath));
        Assert.Equal(intendedVault, File.ReadAllText(recovered.VaultPath));
        Assert.False(File.Exists(recovered.StateTransactionPath));
        using var vault = new VaultService(recovered, encryption, totp);
        vault.ValidateRecoveryKey(recoveryKey);
        Assert.True(vault.UnlockWithMasterPassword(Password).Success);
        Assert.True(vault.VerifyTotpForSession(totp.GetCurrentCode(secret).Code));
        Assert.Equal("account secret", vault.GetPassword(Assert.Single(vault.GetItems()).Id, string.Empty));
    }

    [Fact]
    public void Create_opt_in_disposable_ui_vault()
    {
        var target = Environment.GetEnvironmentVariable("PASSWORDTOOL_UI_TEST_DIRECTORY");
        if (string.IsNullOrEmpty(target)) return;
        var storage = new VaultStorageService(target);
        Assert.False(storage.HasConfig || storage.HasVault);
        using var vault = new VaultService(storage, new EncryptionService(), totp);
        const string secret = "JBSWY3DPEHPK3PXP"; // Synthetic UI-test credentials only.
        vault.InitializeNewVault(Password, secret, totp.GetCurrentCode(secret).Code, RecoveryKeyService.Generate(), true);
        var group = vault.AddGroup("Work", "#336699");
        for (var i = 0; i < 20; i++)
            vault.AddItem(new VaultItem { Title = $"Test account {i:D2} with a long title", Username = "user@example.test", Password = "synthetic-test-password", Notes = "A long note for checking ellipsis and icon alignment", GroupId = group.Id });
        Assert.True(vault.TryUpdateSettings(Password, VaultLoginMode.Hybrid, new VaultSecuritySettings(1, 5, 30), out _));
    }

    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
}
