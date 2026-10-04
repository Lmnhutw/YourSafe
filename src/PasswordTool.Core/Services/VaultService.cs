using System.Security.Cryptography;
using System.Text;
using System.ComponentModel;
using OtpNet;
using PasswordTool.Core.Models;

namespace PasswordTool.Core.Services;

public sealed class VaultService : IDisposable
{
    private readonly EncryptionService encryptionService;
    private readonly MasterPasswordService masterPasswordService;
    private readonly TotpService totpService;
    private readonly TrustedUnlockTokenService trustedUnlockTokenService;
    private readonly VaultBackupService backupService;
    private readonly VaultCsvImportService csvImportService;
    private readonly PasswordGeneratorService passwordGeneratorService;
    private readonly VaultStorageService storageService;
    private readonly Func<DateTimeOffset> utcNow;

    private byte[]? encryptionKey;
    private string? totpSecretBase32;
    private VaultData? vaultData;
    private bool isVaultOpen;
    private bool encryptionUsesEnvelope;
    private bool recoveryKeyRequired;
    private bool masterPasswordAuthenticated;
    private DateTimeOffset? signInSessionExpiresAt;
    private DateTimeOffset? vaultExpiresAt;
    private readonly RecoveryKeyService recoveryKeys = new();
    private bool disposed;

    public VaultService()
        : this(new VaultStorageService(), new EncryptionService(), new TotpService())
    {
    }

    public VaultService(
        VaultStorageService storageService,
        EncryptionService encryptionService,
        TotpService totpService,
        TrustedUnlockTokenService? trustedUnlockTokenService = null,
        Func<DateTimeOffset>? utcNow = null,
        VaultCsvImportService? csvImportService = null)
    {
        this.storageService = storageService;
        this.encryptionService = encryptionService;
        this.totpService = totpService;
        this.trustedUnlockTokenService = trustedUnlockTokenService ?? new TrustedUnlockTokenService();
        backupService = new VaultBackupService(encryptionService);
        this.csvImportService = csvImportService ?? new VaultCsvImportService(totpService);
        passwordGeneratorService = new PasswordGeneratorService();
        this.utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        masterPasswordService = new MasterPasswordService(encryptionService);
    }

    public bool IsInitialized => storageService.IsInitialized;

    public bool HasPartialStorage => storageService.HasPartialStorage;

    public string StorageDirectory => storageService.AppDirectory;

    public string ConfigPath => storageService.ConfigPath;

    public string VaultPath => storageService.VaultPath;

    public string TrustedUnlockTokenPath => storageService.TrustedUnlockTokenPath;

    public bool IsSignInSessionActive => signInSessionExpiresAt is { } expiresAt && expiresAt > utcNow();
    public DateTimeOffset? LoginExpiresAt => signInSessionExpiresAt;
    public DateTimeOffset? VaultExpiresAt => vaultExpiresAt;
    public bool HasActiveVaultSession => IsSignInSessionActive && isVaultOpen && encryptionKey is not null
        && vaultExpiresAt > utcNow();
    public bool IsVaultUnlocked => HasActiveVaultSession && !recoveryKeyRequired;
    public bool NeedsRecoveryKey => storageService.LoadConfig().RecoveryKeySlot is null;

    public void CheckExpiration()
    {
        var now = utcNow();
        if (signInSessionExpiresAt is { } login && now >= login) ClearSession();
        else if (vaultExpiresAt is { } vault && now >= vault) LockVault();
    }

    private void BeginVaultDeadline()
    {
        var duration = SecuritySettings.VaultOpenDurationMinutes;
        var deadline = utcNow().AddMinutes(duration);
        vaultExpiresAt = signInSessionExpiresAt is { } login && login < deadline ? login : deadline;
    }

    public void SaveRecoveryKey(string masterPassword, string recoveryKey, bool saved)
    {
        EnsureOpen(allowRecoverySetup: true);
        if (recoveryKeyRequired && !masterPasswordAuthenticated)
            throw new UnauthorizedAccessException("Sign in with the Master Password before creating a Recovery Key.");
        if (!saved) throw new InvalidOperationException("Confirm that the Recovery Key has been saved.");
        var config = storageService.LoadConfig();
        byte[] verification = [];
        byte[] migratedKey = [];
        try
        {
            if (!TryVerifyCurrentMasterPassword(masterPassword, config, out verification))
                throw new UnauthorizedAccessException("The Master Password is incorrect.");
            var payload = storageService.LoadVaultPayload();
            var key = encryptionKey!;
            RejectReusedRecoveryKey(recoveryKey, config.RecoveryKeySlot);
            if (config.Version < 3)
            {
                migratedKey = RandomNumberGenerator.GetBytes(32);
                var migrated = masterPasswordService.CreateEnvelopeConfig(masterPassword, totpSecretBase32!, migratedKey);
                migrated.CreatedAt = config.CreatedAt;
                migrated.LastExternalBackupAt = config.LastExternalBackupAt;
                migrated.LastVerifiedBackupAt = config.LastVerifiedBackupAt;
                migrated.CredentialRevision = config.CredentialRevision;
                config = migrated;
                key = migratedKey;
                payload = encryptionService.EncryptObject(vaultData, key, MasterPasswordService.VaultContext);
            }
            config.RecoveryKeySlot = recoveryKeys.Wrap(recoveryKey, key);
            if (config.Version < 4) config.VaultOpenDurationMinutes = 1;
            config.Version = 4;
            config.CredentialRevision++;
            config.UpdatedAt = utcNow();
            EnsureOpen(allowRecoverySetup: true);
            SaveRecoveryState(config, payload, masterPassword, recoveryKey, key, totpSecretBase32!, requireOpen: true);
            if (migratedKey.Length > 0)
            {
                CryptographicOperations.ZeroMemory(encryptionKey!);
                encryptionKey = migratedKey;
                migratedKey = [];
                encryptionUsesEnvelope = true;
            }
            TryDeleteTrustedUnlockToken();
            recoveryKeyRequired = false;
        }
        finally { CryptographicOperations.ZeroMemory(verification); CryptographicOperations.ZeroMemory(migratedKey); }
    }

    public void ValidateRecoveryKey(string recoveryKey)
    {
        ThrowIfDisposed();
        var config = storageService.LoadConfig();
        if (config.Version != 4) throw new NotSupportedException("Unsupported Recovery Key configuration.");
        var key = recoveryKeys.Unwrap(recoveryKey, config.RecoveryKeySlot
            ?? throw new InvalidOperationException("This vault has no Recovery Key."));
        try { _ = encryptionService.DecryptObject<VaultData>(storageService.LoadVaultPayload(), key, MasterPasswordService.VaultContext); }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    public void ResetWithRecoveryKey(RecoveryKeyResetRequest request)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(request);
        if (!request.RecoveryKeySaved) throw new InvalidOperationException("Confirm that the new Recovery Key has been saved.");
        MasterPasswordService.ValidateNewMasterPassword(request.NewMasterPassword);
        if (!totpService.IsSecretValid(request.NewTotpSecretBase32)
            || !totpService.VerifyCode(request.NewTotpSecretBase32, request.TotpConfirmationCode))
            throw new UnauthorizedAccessException("Authenticator setup could not be verified.");
        var config = storageService.LoadConfig();
        if (config.Version != 4) throw new NotSupportedException("Unsupported Recovery Key configuration.");
        var key = recoveryKeys.Unwrap(request.RecoveryKey, config.RecoveryKeySlot
            ?? throw new InvalidOperationException("This vault has no Recovery Key."));
        try
        {
            RejectReusedRecoveryKey(request.NewRecoveryKey, config.RecoveryKeySlot);
            byte[] previousMasterKey = [], previousAuthenticator = [], replacementAuthenticator = [];
            try
            {
                if (masterPasswordService.TryUnlockConfig(request.NewMasterPassword, config, out previousMasterKey, out _))
                    throw new ArgumentException("Choose a different Master Password.", nameof(request));
                var previousSecret = encryptionService.DecryptString(config.EncryptedTotpSecret, key, MasterPasswordService.TotpSecretContext);
                if (!totpService.TryNormalizeWebsiteSecret(previousSecret, out var normalizedPrevious)
                    || !totpService.TryNormalizeWebsiteSecret(request.NewTotpSecretBase32, out var normalizedReplacement))
                    throw new CryptographicException("The Authenticator secret is invalid.");
                previousAuthenticator = Base32Encoding.ToBytes(normalizedPrevious);
                replacementAuthenticator = Base32Encoding.ToBytes(normalizedReplacement);
                if (CryptographicOperations.FixedTimeEquals(previousAuthenticator, replacementAuthenticator))
                    throw new ArgumentException("Set up a new Authenticator.", nameof(request));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(previousMasterKey);
                CryptographicOperations.ZeroMemory(previousAuthenticator);
                CryptographicOperations.ZeroMemory(replacementAuthenticator);
            }
            var payload = storageService.LoadVaultPayload();
            _ = encryptionService.DecryptObject<VaultData>(payload, key, MasterPasswordService.VaultContext);
            masterPasswordService.RewrapVaultKey(request.NewMasterPassword, config, key);
            config.RecoveryKeySlot = recoveryKeys.Wrap(request.NewRecoveryKey, key);
            config.EncryptedTotpSecret = encryptionService.EncryptString(request.NewTotpSecretBase32, key, MasterPasswordService.TotpSecretContext);
            config.Version = 4;
            config.CredentialRevision++;
            config.UpdatedAt = utcNow();
            SaveRecoveryState(config, payload, request.NewMasterPassword, request.NewRecoveryKey, key, request.NewTotpSecretBase32);
            TryDeleteTrustedUnlockToken();
            ClearSession();
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    private void SaveRecoveryState(AppConfig config, string payload, string password, string recoveryKey, byte[] key,
        string expectedTotpSecret, bool requireOpen = false)
    {
        storageService.SaveStateVerified(config, payload, (staged, ciphertext) =>
        {
            byte[] master = [], recovery = [];
            try
            {
                if (!masterPasswordService.TryUnlockConfig(password, staged, out master, out var stagedTotpSecret))
                    throw new CryptographicException("The staged Master wrapper is invalid.");
                recovery = recoveryKeys.Unwrap(recoveryKey, staged.RecoveryKeySlot!);
                if (!CryptographicOperations.FixedTimeEquals(key, master) || !CryptographicOperations.FixedTimeEquals(key, recovery))
                    throw new CryptographicException("The staged key wrappers do not match.");
                if (!string.Equals(expectedTotpSecret, stagedTotpSecret, StringComparison.Ordinal))
                    throw new CryptographicException("The staged Authenticator does not match.");
                _ = encryptionService.DecryptObject<VaultData>(ciphertext, master, MasterPasswordService.VaultContext);
            }
            finally { CryptographicOperations.ZeroMemory(master); CryptographicOperations.ZeroMemory(recovery); }
        }, requireOpen ? () => EnsureOpen(allowRecoverySetup: true) : null);
    }

    private void RejectReusedRecoveryKey(string candidate, RecoveryKeySlot? previous)
    {
        if (previous is null) return;
        byte[] key;
        try { key = recoveryKeys.Unwrap(candidate, previous); }
        catch (CryptographicException) { return; }
        CryptographicOperations.ZeroMemory(key);
        throw new ArgumentException("Generate a new Recovery Key.");
    }

    public bool IsGoogleAuthenticatorConfigured
    {
        get
        {
            ThrowIfDisposed();
            return !string.IsNullOrWhiteSpace(totpSecretBase32);
        }
    }

    public bool CanUnlockWithGoogleAuthenticatorToken
    {
        get
        {
            ThrowIfDisposed();

            try
            {
                var config = storageService.LoadConfig();
                if (config.Version is < 3 or > 4 || config.MasterKeySlot is null || string.IsNullOrWhiteSpace(config.EncryptedTotpSecret) || !storageService.HasTrustedUnlockToken)
                {
                    return false;
                }

                var token = storageService.LoadTrustedUnlockToken();
                return trustedUnlockTokenService.IsTokenUsable(token, ComputeConfigFingerprint(config), utcNow());
            }
            catch (Exception ex) when (ex is IOException
                or UnauthorizedAccessException
                or InvalidOperationException
                or CryptographicException
                or FormatException
                or System.Text.Json.JsonException
                or PlatformNotSupportedException)
            {
                return false;
            }
        }
    }

    public VaultLoginMode LoginMode
    {
        get
        {
            ThrowIfDisposed();
            return VaultLoginMode.Hybrid;
        }
    }

    public VaultSecuritySettings SecuritySettings
    {
        get
        {
            ThrowIfDisposed();
            var config = storageService.LoadConfig();
            if (config.Version < 4) config.VaultOpenDurationMinutes = 1;
            VaultSecuritySettings.Validate(
                config.InactivityLockTimeoutMinutes,
                config.SensitiveActionTimeoutMinutes,
                config.VaultOpenDurationMinutes);
            return new VaultSecuritySettings(
                config.InactivityLockTimeoutMinutes,
                config.SensitiveActionTimeoutMinutes,
                config.VaultOpenDurationMinutes);
        }
    }

    public bool NeedsKdfUpgrade
    {
        get
        {
            ThrowIfDisposed();
            return MasterPasswordService.NeedsKdfUpgrade(storageService.LoadConfig());
        }
    }

    public DateTimeOffset? LastExternalBackupAt
    {
        get
        {
            ThrowIfDisposed();
            return storageService.LoadConfig().LastExternalBackupAt;
        }
    }

    public DateTimeOffset? LastVerifiedBackupAt
    {
        get
        {
            ThrowIfDisposed();
            return storageService.LoadConfig().LastVerifiedBackupAt;
        }
    }

    public void InitializeNewVault(string masterPassword, string totpSecretBase32, string confirmationTotpCode,
        string recoveryKey, bool recoveryKeySaved)
    {
        ThrowIfDisposed();
        if (!recoveryKeySaved) throw new InvalidOperationException("Confirm that the Recovery Key has been saved.");
        if (storageService.HasConfig || storageService.HasVault)
            throw new InvalidOperationException("PasswordTool storage already exists.");
        if (!totpService.IsSecretValid(totpSecretBase32))
            throw new ArgumentException("The generated TOTP secret is invalid.", nameof(totpSecretBase32));
        if (!totpService.VerifyCode(totpSecretBase32, confirmationTotpCode))
            throw new UnauthorizedAccessException("Authenticator setup could not be verified.");

        var vaultKey = RandomNumberGenerator.GetBytes(MasterPasswordService.DefaultKeySizeBytes);
        try
        {
            var config = masterPasswordService.CreateEnvelopeConfig(masterPassword, totpSecretBase32, vaultKey);
            config.Version = 4;
            config.CredentialRevision = 1;
            config.RecoveryKeySlot = recoveryKeys.Wrap(recoveryKey, vaultKey);
            var data = new VaultData();
            var payload = encryptionService.EncryptObject(data, vaultKey, MasterPasswordService.VaultContext);
            SaveRecoveryState(config, payload, masterPassword, recoveryKey, vaultKey, totpSecretBase32);

            ClearSession();
            encryptionKey = vaultKey;
            vaultKey = [];
            encryptionUsesEnvelope = true;
            this.totpSecretBase32 = totpSecretBase32;
            vaultData = data;
            isVaultOpen = true;
            signInSessionExpiresAt = utcNow().AddMinutes(VaultSecuritySettings.MaximumSessionDurationMinutes);
            BeginVaultDeadline();
            SaveTrustedUnlockToken(config);
        }
        finally { if (vaultKey.Length > 0) CryptographicOperations.ZeroMemory(vaultKey); }
    }
    public VaultBackupInspection InspectBackupJson(string backupJson, string passphrase)
    {
        ThrowIfDisposed();
        return backupService.InspectBackup(backupJson, passphrase);
    }

    public VaultBackupInspection VerifyBackupJson(string backupJson, string passphrase)
    {
        ThrowIfDisposed();
        EnsureOpen();
        var inspection = backupService.InspectBackup(backupJson, passphrase);
        UpdateBackupHealth(lastVerifiedBackupAt: utcNow());
        return inspection;
    }

    public VaultBackupInspection VerifyExternalBackupFile(string backupPath, string passphrase)
    {
        return VerifyBackupJson(ReadBoundedBackupFile(backupPath), passphrase);
    }

    public void CreateExternalBackupFile(string destinationPath, string passphrase, string totpCode)
    {
        ThrowIfDisposed();
        EnsureOpen();
        RequireSensitiveTotp(totpCode);

        var backupJson = backupService.CreateBackup(vaultData!.Items, passphrase, utcNow());
        EnsureOpen();
        WriteExternalBackupFile(destinationPath, backupJson,
            path => backupService.InspectBackup(ReadBoundedBackupFile(path), passphrase));
        try
        {
            var now = utcNow();
            UpdateBackupHealth(lastExternalBackupAt: now, lastVerifiedBackupAt: now);
        }
        catch (Exception ex)
        {
            throw new BackupOperationException(
                "Backup created and verified, but the app's backup status could not be updated.", ex);
        }
    }

    public void RecoverFromBackup(VaultRecoveryRequest request)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(request);
        if (storageService.HasConfig || storageService.HasVault)
            throw new InvalidOperationException("Recovery is available only when no PasswordTool storage exists.");

        if (!request.RecoveryKeySaved) throw new InvalidOperationException("Confirm that the Recovery Key has been saved.");

        MasterPasswordService.ValidateNewMasterPassword(request.NewMasterPassword);
        if (!totpService.IsSecretValid(request.NewTotpSecretBase32))
            throw new ArgumentException("The generated TOTP secret is invalid.", nameof(request));
        if (!totpService.VerifyCode(request.NewTotpSecretBase32, request.TotpConfirmationCode))
            throw new UnauthorizedAccessException("Authenticator setup could not be verified.");

        var recoveredItems = backupService.ReadBackup(request.BackupJson, request.BackupPassphrase);
        var now = utcNow();
        NormalizeItems(recoveredItems, now);
        var vaultKey = RandomNumberGenerator.GetBytes(MasterPasswordService.DefaultKeySizeBytes);
        try
        {
            var config = masterPasswordService.CreateEnvelopeConfig(request.NewMasterPassword, request.NewTotpSecretBase32, vaultKey);
            config.Version = 4;
            config.CredentialRevision = 1;
            config.RecoveryKeySlot = recoveryKeys.Wrap(request.RecoveryKey, vaultKey);
            config.CreatedAt = now;
            config.UpdatedAt = now;
            config.LastVerifiedBackupAt = now;
            var recoveredVault = new VaultData { Items = recoveredItems.Select(item => Clone(item, includePassword: true)).ToList() };
            NormalizeGroups(recoveredVault, now);
            var payload = encryptionService.EncryptObject(recoveredVault, vaultKey, MasterPasswordService.VaultContext);
            SaveRecoveryState(config, payload, request.NewMasterPassword, request.RecoveryKey, vaultKey, request.NewTotpSecretBase32);

            ClearSession();
            encryptionKey = vaultKey;
            vaultKey = [];
            encryptionUsesEnvelope = true;
            totpSecretBase32 = request.NewTotpSecretBase32;
            vaultData = recoveredVault;
            isVaultOpen = true;
            signInSessionExpiresAt = utcNow().AddMinutes(VaultSecuritySettings.MaximumSessionDurationMinutes);
            BeginVaultDeadline();
            SaveTrustedUnlockToken(config);
        }
        finally { if (vaultKey.Length > 0) CryptographicOperations.ZeroMemory(vaultKey); }
    }
    public bool TryUnlockMasterPassword(string masterPassword, out string errorMessage)
    {
        var result = UnlockWithMasterPassword(masterPassword);
        errorMessage = result.Success ? string.Empty : result.Message;
        return result.Success;
    }

    public bool ValidateMasterPassword(string masterPassword)
    {
        ThrowIfDisposed();
        byte[] key = [];
        try
        {
            var config = storageService.LoadConfig();
            if (!masterPasswordService.TryUnlockConfig(masterPassword, config, out key, out _)) return false;
            var payload = storageService.LoadVaultPayload();
            _ = config.Version >= 3
                ? encryptionService.DecryptObject<VaultData>(payload, key, MasterPasswordService.VaultContext)
                : encryptionService.DecryptObject<VaultData>(payload, key);
            return true;
        }
        finally { if (key.Length > 0) CryptographicOperations.ZeroMemory(key); }
    }

    public VaultUnlockResult UnlockWithMasterPassword(string masterPassword)
    {
        ThrowIfDisposed();
        CheckExpiration();
        var signedIn = signInSessionExpiresAt is not null;
        LockVault();
        byte[]? unlockedKey = null;
        try
        {
            var config = storageService.LoadConfig();
            if (!masterPasswordService.TryUnlockConfig(masterPassword, config, out unlockedKey, out var decryptedTotpSecret))
                return new VaultUnlockResult(VaultUnlockStatus.Failed, "The Master Password is incorrect. Please try again.");

            var encryptedVaultJson = storageService.LoadVaultPayload();
            var decryptedVault = config.Version >= 3
                ? encryptionService.DecryptObject<VaultData>(encryptedVaultJson, unlockedKey, MasterPasswordService.VaultContext)
                : encryptionService.DecryptObject<VaultData>(encryptedVaultJson, unlockedKey);
            decryptedVault.Items ??= [];
            NormalizeItems(decryptedVault.Items, utcNow());
            NormalizeGroups(decryptedVault, utcNow());

            var status = VaultUnlockStatus.Unlocked;
            var message = string.Empty;
            if (config.Version < 3)
            {
                status = VaultUnlockStatus.UnlockedMigrationDeferred;
                message = "Save a Recovery Key after sign-in to finish the security upgrade.";
            }

            encryptionKey = unlockedKey;
            unlockedKey = null;
            encryptionUsesEnvelope = config.Version >= 3;
            masterPasswordAuthenticated = true;
            recoveryKeyRequired = config.RecoveryKeySlot is null;
            totpSecretBase32 = string.IsNullOrWhiteSpace(decryptedTotpSecret) ? null : decryptedTotpSecret;
            vaultData = decryptedVault;
            isVaultOpen = true;
            if (signedIn) BeginVaultDeadline();
            if (signedIn && !IsSignInSessionActive)
            {
                ClearSession();
                return new VaultUnlockResult(VaultUnlockStatus.Failed, "The sign-in session expired. Sign in again.");
            }
            if (signedIn && !recoveryKeyRequired)
            {
                EnsureOpen();
                PurgeExpiredTrash();
            }
            return new VaultUnlockResult(status, message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
            or CryptographicException or FormatException or PlatformNotSupportedException or System.Text.Json.JsonException)
        {
            LockVault();
            return new VaultUnlockResult(VaultUnlockStatus.Failed,
                "The Master Password is incorrect, or PasswordTool storage could not be opened.");
        }
        finally { if (unlockedKey is { Length: > 0 }) CryptographicOperations.ZeroMemory(unlockedKey); }
    }
    public bool TryUnlockWithGoogleAuthenticator(string code, out string errorMessage)
    {
        ThrowIfDisposed();
        ClearSession();
        errorMessage = string.Empty;
        byte[]? trustedKey = null;
        byte[]? trustedAuthenticatorSecret = null;
        if (string.IsNullOrWhiteSpace(code))
        {
            errorMessage = "Google Authenticator code is required.";
            return false;
        }

        try
        {
            var config = storageService.LoadConfig();
            if (config.Version is < 3 or > 4 || config.MasterKeySlot is null)
            {
                errorMessage = "Enter the Master Password once to upgrade this vault before using trusted unlock.";
                return false;
            }
            if (string.IsNullOrWhiteSpace(config.EncryptedTotpSecret) || !storageService.HasTrustedUnlockToken)
            {
                errorMessage = "Enter the Master Password to create a 1-day Google Authenticator login token.";
                return false;
            }

            var fingerprint = ComputeConfigFingerprint(config);
            var token = storageService.LoadTrustedUnlockToken();
            if (!trustedUnlockTokenService.TryUnprotectAuthenticatorSecret(token, fingerprint, utcNow(),
                out trustedAuthenticatorSecret, out errorMessage))
            {
                TryDeleteTrustedUnlockToken();
                return false;
            }
            if (!totpService.VerifyCode(trustedAuthenticatorSecret, code))
            {
                errorMessage = "Invalid Google Authenticator code.";
                return false;
            }
            if (!trustedUnlockTokenService.TryUnprotectVaultKey(token, fingerprint, utcNow(), out trustedKey, out errorMessage))
            {
                TryDeleteTrustedUnlockToken();
                return false;
            }

            var decryptedTotpSecret = encryptionService.DecryptString(config.EncryptedTotpSecret, trustedKey,
                MasterPasswordService.TotpSecretContext);
            var decryptedVault = encryptionService.DecryptObject<VaultData>(storageService.LoadVaultPayload(), trustedKey,
                MasterPasswordService.VaultContext);
            decryptedVault.Items ??= [];
            NormalizeItems(decryptedVault.Items, utcNow());
            NormalizeGroups(decryptedVault, utcNow());

            encryptionKey = trustedKey;
            trustedKey = null;
            encryptionUsesEnvelope = true;
            totpSecretBase32 = decryptedTotpSecret;
            recoveryKeyRequired = config.RecoveryKeySlot is null;
            vaultData = decryptedVault;
            isVaultOpen = true;
            signInSessionExpiresAt = utcNow().AddMinutes(VaultSecuritySettings.MaximumSessionDurationMinutes);
            BeginVaultDeadline();
            if (!recoveryKeyRequired) PurgeExpiredTrash();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
            or CryptographicException or FormatException or System.Text.Json.JsonException)
        {
            ClearSession();
            errorMessage = "Google Authenticator login could not open PasswordTool storage. Enter the Master Password to create a new 1-day token.";
            return false;
        }
        finally
        {
            if (trustedKey is { Length: > 0 }) CryptographicOperations.ZeroMemory(trustedKey);
            if (trustedAuthenticatorSecret is { Length: > 0 }) CryptographicOperations.ZeroMemory(trustedAuthenticatorSecret);
        }
    }
    public bool VerifyTotpForSession(string code)
    {
        ThrowIfDisposed();
        CheckExpiration();
        EnsureMasterPasswordUnlocked();

        if (totpSecretBase32 is null)
        {
            return false;
        }

        if (!totpService.VerifyCode(totpSecretBase32!, code))
        {
            return false;
        }

        CheckExpiration();
        EnsureMasterPasswordUnlocked();
        var signedIn = signInSessionExpiresAt is not null;
        isVaultOpen = true;
        if (!signedIn)
            signInSessionExpiresAt = utcNow().AddMinutes(VaultSecuritySettings.MaximumSessionDurationMinutes);
        if (vaultExpiresAt is null) BeginVaultDeadline();
        if (!signedIn && masterPasswordAuthenticated && !recoveryKeyRequired)
            SaveTrustedUnlockToken(storageService.LoadConfig());
        CheckExpiration();
        EnsureMasterPasswordUnlocked();
        if (!recoveryKeyRequired)
        {
            EnsureOpen();
            PurgeExpiredTrash();
        }
        return true;
    }

    public bool ValidateAuthenticatorSetup(string secret, string code) =>
        totpService.IsSecretValid(secret) && totpService.VerifyCode(secret, code);

    public bool VerifyTotpForSensitiveAction(string code)
    {
        ThrowIfDisposed();
        EnsureOpen();
        if (totpSecretBase32 is null)
        {
            return true;
        }

        return IsSignInSessionActive;
    }

    public bool TrySetLoginMode(string masterPassword, VaultLoginMode loginMode, out string errorMessage)
    {
        return TryUpdateSettings(masterPassword, loginMode, SecuritySettings, out errorMessage);
    }

    public bool TryUpdateSettings(
        string masterPassword,
        VaultLoginMode loginMode,
        VaultSecuritySettings securitySettings,
        out string errorMessage)
    {
        ThrowIfDisposed();
        EnsureOpen();
        errorMessage = string.Empty;
        ArgumentNullException.ThrowIfNull(securitySettings);

        try
        {
            VaultSecuritySettings.Validate(
                securitySettings.InactivityLockTimeoutMinutes,
                securitySettings.SensitiveActionTimeoutMinutes,
                securitySettings.VaultOpenDurationMinutes);
        }
        catch (ArgumentOutOfRangeException ex)
        {
            errorMessage = ex.Message;
            return false;
        }

        byte[]? verificationKey = null;
        try
        {
            var config = storageService.LoadConfig();
            if (!TryVerifyCurrentMasterPassword(masterPassword, config, out verificationKey))
            {
                errorMessage = "The Master Password is incorrect.";
                return false;
            }

            if (loginMode != VaultLoginMode.Hybrid)
            {
                errorMessage = "PasswordTool requires both the Master Password and Google Authenticator.";
                return false;
            }

            config.LoginMode = VaultLoginMode.Hybrid;
            config.InactivityLockTimeoutMinutes = securitySettings.InactivityLockTimeoutMinutes;
            config.SensitiveActionTimeoutMinutes = securitySettings.SensitiveActionTimeoutMinutes;
            config.VaultOpenDurationMinutes = securitySettings.VaultOpenDurationMinutes;
            config.UpdatedAt = utcNow();
            SaveSessionConfig(config);
            return true;
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or InvalidOperationException
            or CryptographicException
            or FormatException
            or System.Text.Json.JsonException)
        {
            errorMessage = "The security settings could not be saved.";
            return false;
        }
        finally
        {
            if (verificationKey is { Length: > 0 })
            {
                CryptographicOperations.ZeroMemory(verificationKey);
            }
        }
    }

    public bool TryChangeMasterPassword(string currentMasterPassword, string newMasterPassword, out string errorMessage)
    {
        ThrowIfDisposed();
        EnsureOpen();
        errorMessage = string.Empty;
        byte[]? verificationKey = null;
        try
        {
            var config = storageService.LoadConfig();
            if (config.Version < 3 || !TryVerifyCurrentMasterPassword(currentMasterPassword, config, out verificationKey))
            {
                errorMessage = "The current Master Password is incorrect.";
                return false;
            }

            masterPasswordService.RewrapVaultKey(newMasterPassword, config, encryptionKey!);
            config.CredentialRevision++;
            config.UpdatedAt = utcNow();
            SaveSessionConfig(config);
            TryDeleteTrustedUnlockToken();
            SaveTrustedUnlockToken(config);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException
            or InvalidOperationException or CryptographicException or FormatException or NotSupportedException)
        {
            errorMessage = ex is ArgumentException ? ex.Message : "The Master Password could not be changed.";
            return false;
        }
        finally { if (verificationKey is { Length: > 0 }) CryptographicOperations.ZeroMemory(verificationKey); }
    }
    public bool TryUpgradeKdf(string masterPassword, out string errorMessage)
    {
        ThrowIfDisposed();
        EnsureOpen();
        if (!NeedsKdfUpgrade)
        {
            errorMessage = string.Empty;
            return true;
        }

        return TryChangeMasterPassword(masterPassword, masterPassword, out errorMessage);
    }

    public bool TryResetAuthenticator(
        string masterPassword,
        string newTotpSecretBase32,
        string confirmationCode,
        out string errorMessage)
    {
        ThrowIfDisposed();
        EnsureOpen();
        errorMessage = string.Empty;
        byte[]? verificationKey = null;

        try
        {
            var config = storageService.LoadConfig();
            if (!TryVerifyCurrentMasterPassword(masterPassword, config, out verificationKey))
            {
                errorMessage = "The Master Password is incorrect.";
                return false;
            }

            if (!totpService.IsSecretValid(newTotpSecretBase32)
                || !totpService.VerifyCode(newTotpSecretBase32, confirmationCode))
            {
                errorMessage = "The new Authenticator secret or confirmation code is invalid.";
                return false;
            }

            config.EncryptedTotpSecret = encryptionService.EncryptString(newTotpSecretBase32, encryptionKey!, MasterPasswordService.TotpSecretContext);
            config.CredentialRevision++;
            config.UpdatedAt = utcNow();
            SaveSessionConfig(config);
            totpSecretBase32 = newTotpSecretBase32;
            TryDeleteTrustedUnlockToken();
            SaveTrustedUnlockToken(config);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException
            or InvalidOperationException or CryptographicException or FormatException or NotSupportedException)
        {
            errorMessage = "The PasswordTool Authenticator could not be reset.";
            return false;
        }
        finally
        {
            if (verificationKey is { Length: > 0 }) CryptographicOperations.ZeroMemory(verificationKey);
        }
    }

    public IReadOnlyList<VaultSnapshotInfo> GetSnapshots()
    {
        ThrowIfDisposed();
        EnsureOpen();
        return storageService.GetSnapshots();
    }

    public bool TryRestoreSnapshot(string snapshotId, string currentMasterPassword, out string errorMessage)
    {
        ThrowIfDisposed();
        EnsureOpen();
        errorMessage = string.Empty;
        byte[]? verificationKey = null;
        try
        {
            var config = storageService.LoadConfig();
            if (!TryVerifyCurrentMasterPassword(currentMasterPassword, config, out verificationKey))
            {
                errorMessage = "The Master Password is incorrect.";
                return false;
            }

            storageService.RestoreSnapshot(snapshotId, () => EnsureOpen());
            ClearSession();
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException
            or InvalidOperationException or CryptographicException or FormatException or System.Text.Json.JsonException)
        {
            errorMessage = "The selected snapshot could not be restored.";
            return false;
        }
        finally
        {
            if (verificationKey is { Length: > 0 }) CryptographicOperations.ZeroMemory(verificationKey);
        }
    }

    public IReadOnlyList<VaultItem> GetItems()
    {
        ThrowIfDisposed();
        EnsureOpen();

        var items = vaultData!.Items
            .Where(item => !item.IsDeleted)
            .OrderBy(item => item.Title, StringComparer.CurrentCultureIgnoreCase)
            .Select(CloneForList)
            .ToList();
        EnsureOpen();
        return items;
    }

    public IReadOnlyList<VaultGroup> GetGroups()
    {
        ThrowIfDisposed();
        EnsureOpen();
        var groups = vaultData!.Groups.OrderBy(group => group.SortOrder).ThenBy(group => group.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(CloneGroup).ToList();
        EnsureOpen();
        return groups;
    }

    public VaultGroup AddGroup(string name, string? accentColor = null)
    {
        ThrowIfDisposed();
        EnsureOpen();
        name = ValidateGroupName(name);
        if (vaultData!.Groups.Any(group => string.Equals(group.Name, name, StringComparison.CurrentCultureIgnoreCase)))
            throw new ArgumentException("A group with this name already exists.", nameof(name));
        var now = utcNow();
        var group = new VaultGroup { Name = name, AccentColor = NormalizeAccentColor(accentColor), SortOrder = vaultData.Groups.Count, CreatedAt = now, UpdatedAt = now };
        vaultData.Groups.Add(group);
        SaveVault();
        return CloneGroup(group);
    }

    public void UpdateGroup(Guid id, string name, string? accentColor)
    {
        ThrowIfDisposed();
        EnsureOpen();
        name = ValidateGroupName(name);
        if (vaultData!.Groups.Any(group => group.Id != id && string.Equals(group.Name, name, StringComparison.CurrentCultureIgnoreCase)))
            throw new ArgumentException("A group with this name already exists.", nameof(name));
        var group = vaultData.Groups.FirstOrDefault(group => group.Id == id) ?? throw new KeyNotFoundException("Group not found.");
        accentColor = NormalizeAccentColor(accentColor);
        group.Name = name;
        group.AccentColor = accentColor;
        group.UpdatedAt = utcNow();
        SaveVault();
    }

    public void DeleteGroup(Guid id, string confirmation, string totpCode)
    {
        ThrowIfDisposed();
        EnsureOpen();
        var group = vaultData!.Groups.FirstOrDefault(group => group.Id == id) ?? throw new KeyNotFoundException("Group not found.");
        if (!string.Equals(confirmation, $"Confirm delete all data in \"{group.Name}\"", StringComparison.Ordinal))
            throw new ArgumentException("The confirmation text does not match the group name.", nameof(confirmation));
        if (totpSecretBase32 is null || !totpService.VerifyCode(totpSecretBase32, totpCode))
            throw new UnauthorizedAccessException("Incorrect authenticator code. Please try again.");

        var data = vaultData;
        var previousItems = data.Items;
        var previousGroups = data.Groups;
        data.Items = previousItems.Where(item => item.GroupId != id).ToList();
        data.Groups = previousGroups.Where(candidate => candidate.Id != id).ToList();
        try { SaveVault(); }
        catch
        {
            data.Items = previousItems;
            data.Groups = previousGroups;
            throw;
        }
    }

    public VaultItem GetItemForEditing(Guid id, string totpCode)
    {
        ThrowIfDisposed();
        EnsureOpen();
        RequireSensitiveTotp(totpCode);
        var item = Clone(FindItem(id), includePassword: true);
        EnsureOpen();
        return item;
    }

    public IReadOnlyList<PasswordHistoryEntry> GetPasswordHistory(Guid id, string totpCode)
    {
        ThrowIfDisposed();
        EnsureOpen();
        RequireSensitiveTotp(totpCode);
        var item = FindItem(id);
        var history = item.PasswordHistory
            .OrderByDescending(entry => entry.ChangedAt)
            .Select(entry => new PasswordHistoryEntry { Password = entry.Password, ChangedAt = entry.ChangedAt })
            .ToList();
        EnsureOpen();
        return history;
    }

    public IReadOnlyList<VaultItem> GetDeletedItems()
    {
        ThrowIfDisposed();
        EnsureOpen();
        var items = vaultData!.Items.Where(item => item.IsDeleted)
            .OrderByDescending(item => item.DeletedAt)
            .Select(CloneForList)
            .ToList();
        EnsureOpen();
        return items;
    }

    public void RestoreDeletedItem(Guid id)
    {
        ThrowIfDisposed();
        EnsureOpen();
        var item = FindAnyItem(id);
        if (!item.IsDeleted) throw new InvalidOperationException("The selected item is not in Trash.");
        item.IsDeleted = false;
        item.DeletedAt = null;
        item.UpdatedAt = utcNow();
        SaveVault();
    }

    public void PermanentlyDeleteItem(Guid id, string totpCode)
    {
        ThrowIfDisposed();
        EnsureOpen();
        RequireSensitiveTotp(totpCode);
        var item = FindAnyItem(id);
        if (!item.IsDeleted) throw new InvalidOperationException("Move the item to Trash before permanently deleting it.");
        vaultData!.Items.Remove(item);
        SaveVault();
    }

    public IReadOnlyList<VaultSecurityFinding> GetSecurityFindings(string totpCode)
    {
        ThrowIfDisposed();
        EnsureOpen();
        RequireSensitiveTotp(totpCode);
        var activePasswordItems = vaultData!.Items
            .Where(item => !item.IsDeleted && item.Type == VaultItemType.Password && !string.IsNullOrWhiteSpace(item.Password))
            .ToList();
        var reusedIds = activePasswordItems
            .Where(item => !string.IsNullOrEmpty(item.Password))
            .GroupBy(item => item.Password, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .SelectMany(group => group.Select(item => item.Id))
            .ToHashSet();
        var now = utcNow().ToUniversalTime();
        var oldBefore = now.AddDays(-365);
        var findings = new List<VaultSecurityFinding>();

        foreach (var item in activePasswordItems)
        {
            var strength = passwordGeneratorService.EstimatePasswordStrength(item.Password);
            if (item.Password.Length < 12 || strength.EstimatedEntropyBits < 60)
            {
                findings.Add(new(item.Id, item.Title, VaultSecurityFindingType.WeakPassword,
                    "Use a longer, randomly generated password."));
            }
            if (reusedIds.Contains(item.Id))
            {
                findings.Add(new(item.Id, item.Title, VaultSecurityFindingType.ReusedPassword,
                    "This password is also used by another active item."));
            }
            var passwordChangedAt = PasswordLifecycle.GetEffectivePasswordChangedAt(item, now);
            if (passwordChangedAt <= oldBefore)
            {
                findings.Add(new(item.Id, item.Title, VaultSecurityFindingType.OldPassword,
                    "Change this password because it is at least one year old.")
                {
                    PasswordChangedAt = passwordChangedAt
                });
            }
        }

        var result = findings
            .OrderBy(finding => finding.Type)
            .ThenBy(finding => finding.Title, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(finding => finding.ItemId)
            .ToList();
        EnsureOpen();
        return result;
    }

    public string GetUsername(Guid id)
    {
        ThrowIfDisposed();
        EnsureOpen();
        var username = FindItem(id).Username;
        EnsureOpen();
        return username;
    }

    public string GetPassword(Guid id, string totpCode)
    {
        ThrowIfDisposed();
        EnsureOpen();
        RequireSensitiveTotp(totpCode);
        var item = FindItem(id);
        if (item.Type != VaultItemType.Password)
        {
            throw new InvalidOperationException("This vault item stores recovery codes, not a password.");
        }

        EnsureOpen();
        return item.Password;
    }

    public IReadOnlyList<string> GetRecoveryCodes(Guid id, string totpCode)
    {
        ThrowIfDisposed();
        EnsureOpen();
        RequireSensitiveTotp(totpCode);

        var item = FindItem(id);
        var codes = item.RecoveryCodes.ToList();
        EnsureOpen();
        return codes;
    }

    public TotpCodeResult GetWebsiteTotpCode(Guid id, string totpCode)
    {
        ThrowIfDisposed();
        EnsureOpen();
        RequireSensitiveTotp(totpCode);
        var item = FindItem(id);
        if (item.Type != VaultItemType.Password || string.IsNullOrWhiteSpace(item.TotpSecretBase32))
        {
            throw new InvalidOperationException("This vault item does not contain a website TOTP secret.");
        }

        var code = totpService.GetCurrentCode(item.TotpSecretBase32, utcNow());
        EnsureOpen();
        return code;
    }

    public string ExportBackupJson(string passphrase, string totpCode)
    {
        ThrowIfDisposed();
        EnsureOpen();
        RequireSensitiveTotp(totpCode);
        var backup = backupService.CreateBackup(vaultData!.Items, passphrase, utcNow());
        EnsureOpen();
        return backup;
    }

    public VaultBackupImportPlan PreviewBackupImport(string backupJson, string passphrase, string totpCode)
    {
        ThrowIfDisposed();
        EnsureOpen();
        RequireSensitiveTotp(totpCode);

        var importedItems = backupService.ReadBackup(backupJson, passphrase);
        var plan = backupService.CreateImportPlan(importedItems, vaultData!.Items);
        EnsureOpen();
        return plan;
    }

    public int ImportBackupJson(string backupJson, string passphrase, string totpCode)
    {
        ThrowIfDisposed();
        EnsureOpen();
        RequireSensitiveTotp(totpCode);

        var importedItems = backupService.ReadBackup(backupJson, passphrase);
        NormalizeItems(importedItems, utcNow());
        var plan = backupService.CreateImportPlan(importedItems, vaultData!.Items);
        var newIds = plan.Items
            .Where(item => item.Status == VaultBackupImportStatus.New)
            .Select(item => item.Id)
            .ToHashSet();

        if (newIds.Count == 0)
        {
            return 0;
        }

        var newItems = importedItems
            .Where(item => newIds.Contains(item.Id))
            .Select(item => Clone(item, includePassword: true))
            .ToList();

        EnsureOpen();
        var data = vaultData!;
        foreach (var item in newItems.Where(item => item.GroupId is not null && !data.Groups.Any(group => group.Id == item.GroupId))) item.GroupId = null;

        data.Items.AddRange(newItems);
        try
        {
            SaveVault();
            return newItems.Count;
        }
        catch
        {
            foreach (var newItem in newItems)
            {
                data.Items.Remove(newItem);
            }

            throw;
        }
    }

    public VaultCsvImportPlan PreviewCsvImport(string csv, string totpCode)
    {
        ThrowIfDisposed();
        EnsureOpen();
        RequireSensitiveTotp(totpCode);
        var importedItems = csvImportService.Parse(csv);
        var plan = csvImportService.CreateImportPlan(importedItems, vaultData!.Items);
        EnsureOpen();
        return plan;
    }

    public int ImportCsv(string csv, string totpCode)
    {
        ThrowIfDisposed();
        EnsureOpen();
        RequireSensitiveTotp(totpCode);
        var importedItems = csvImportService.Parse(csv);
        var newItems = csvImportService.SelectNewItems(importedItems, vaultData!.Items)
            .Select(item => Clone(item, includePassword: true))
            .ToList();
        if (newItems.Count == 0)
        {
            return 0;
        }

        EnsureOpen();
        var data = vaultData!;
        var now = utcNow();
        MigrateImportedFolders(newItems, now);
        foreach (var item in newItems)
        {
            item.Id = Guid.NewGuid();
            item.CreatedAt = now;
            item.UpdatedAt = now;
            item.PasswordChangedAt = now;
            ValidateVaultItem(item);
        }

        data.Items.AddRange(newItems);
        try
        {
            SaveVault();
            return newItems.Count;
        }
        catch
        {
            foreach (var item in newItems) data.Items.Remove(item);
            throw;
        }
    }

    public VaultItem AddItem(VaultItem item)
    {
        ThrowIfDisposed();
        EnsureOpen();
        ValidateVaultItem(item);

        var now = utcNow();
        var newItem = Clone(item, includePassword: true);
        newItem.Id = Guid.NewGuid();
        newItem.CreatedAt = now;
        newItem.UpdatedAt = now;
        newItem.PasswordChangedAt = string.IsNullOrWhiteSpace(newItem.Password) ? null : now;

        vaultData!.Items.Add(newItem);
        SaveVault();
        return CloneForList(newItem);
    }

    public void UpdateItem(VaultItem item)
    {
        ThrowIfDisposed();
        EnsureOpen();
        if (item.Type == VaultItemType.RecoveryCodes)
        {
            item.Password = string.Empty;
            item.TotpSecretBase32 = string.Empty;
            item.PasswordHistory = [];
            item.PasswordChangedAt = null;
        }
        ValidateVaultItem(item);

        var existing = FindItem(item.Id);
        var now = utcNow();
        var hadPassword = !string.IsNullOrWhiteSpace(existing.Password);
        var hasPassword = !string.IsNullOrWhiteSpace(item.Password);
        var changesPassword = hadPassword && hasPassword
            && !string.Equals(existing.Password, item.Password, StringComparison.Ordinal);
        if (changesPassword)
        {
            existing.PasswordHistory.Insert(0, new PasswordHistoryEntry
            {
                Password = existing.Password,
                ChangedAt = now
            });
            existing.PasswordHistory = existing.PasswordHistory.Take(10).ToList();
        }
        existing.Title = item.Title.Trim();
        existing.Type = item.Type;
        existing.Username = item.Username.Trim();
        existing.Password = item.Password;
        existing.TotpSecretBase32 = item.TotpSecretBase32;
        existing.RecoveryCodes = [.. item.RecoveryCodes];
        existing.Url = item.Url.Trim();
        existing.HideUrl = item.HideUrl;
        existing.Notes = item.Notes;
        existing.HideNotes = item.HideNotes;
        existing.IsFavorite = item.IsFavorite;
        existing.GroupId = item.GroupId;
        existing.Tags = [.. item.Tags];
        existing.UpdatedAt = now;
        if (!hasPassword)
        {
            existing.PasswordHistory = [];
            existing.PasswordChangedAt = null;
        }
        else if (!hadPassword || changesPassword)
        {
            existing.PasswordChangedAt = now;
        }

        SaveVault();
    }

    public void DeleteItem(Guid id)
    {
        ThrowIfDisposed();
        EnsureOpen();

        var item = FindItem(id);
        item.IsDeleted = true;
        item.DeletedAt = utcNow();
        item.UpdatedAt = item.DeletedAt.Value;
        SaveVault();
    }

    public void ClearSession()
    {
        LockVault();
        signInSessionExpiresAt = null;
    }

    public void LockVault()
    {
        if (encryptionKey is { Length: > 0 })
        {
            CryptographicOperations.ZeroMemory(encryptionKey);
        }

        encryptionKey = null;
        totpSecretBase32 = null;
        vaultData = null;
        isVaultOpen = false;
        encryptionUsesEnvelope = false;
        masterPasswordAuthenticated = false;
        vaultExpiresAt = null;
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        ClearSession();
        disposed = true;
    }

    private void SaveVault()
    {
        EnsureOpen();
        var payload = encryptionUsesEnvelope
            ? encryptionService.EncryptObject(vaultData, encryptionKey!, MasterPasswordService.VaultContext)
            : encryptionService.EncryptObject(vaultData, encryptionKey!);
        EnsureOpen();
        storageService.SaveStateVerified(storageService.LoadConfig(), payload, (_, _) => EnsureOpen(), () => EnsureOpen());
    }

    private void UpdateBackupHealth(
        DateTimeOffset? lastExternalBackupAt = null,
        DateTimeOffset? lastVerifiedBackupAt = null)
    {
        var config = storageService.LoadConfig();
        if (lastExternalBackupAt.HasValue) config.LastExternalBackupAt = lastExternalBackupAt;
        if (lastVerifiedBackupAt.HasValue) config.LastVerifiedBackupAt = lastVerifiedBackupAt;
        config.UpdatedAt = utcNow();
        SaveSessionConfig(config);
    }

    private void SaveSessionConfig(AppConfig config)
    {
        EnsureOpen();
        storageService.SaveStateVerified(config, storageService.LoadVaultPayload(), (_, _) => EnsureOpen(), () => EnsureOpen());
    }

    private static string ReadBoundedBackupFile(string backupPath)
    {
        if (string.IsNullOrWhiteSpace(backupPath))
        {
            throw new ArgumentException("A backup file is required.", nameof(backupPath));
        }

        var info = new FileInfo(backupPath);
        if (!info.Exists)
        {
            throw new FileNotFoundException("The selected backup file was not found.", backupPath);
        }
        if (info.Length > VaultBackupService.MaxBackupJsonCharacters)
        {
            throw new InvalidDataException("The selected backup exceeds the 10 MB limit.");
        }

        return File.ReadAllText(backupPath);
    }

    internal static void WriteExternalBackupFile(string destinationPath, string backupJson, Action<string> verify)
    {
        if (string.IsNullOrWhiteSpace(destinationPath))
        {
            throw new ArgumentException("A backup destination is required.", nameof(destinationPath));
        }

        var fullPath = Path.GetFullPath(destinationPath);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new ArgumentException("The backup destination is invalid.", nameof(destinationPath));
        if (!Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException("The backup destination folder does not exist.");
        }

        var tempPath = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(tempPath, backupJson);
            try { verify(tempPath); }
            catch (Exception ex)
            {
                throw new BackupOperationException(
                    "Backup verification failed before saving. Any previous backup was preserved.", ex);
            }
            File.Move(tempPath, fullPath, overwrite: true);
            try { verify(fullPath); }
            catch (Exception ex)
            {
                throw new BackupOperationException(
                    "The backup file was written, but verification failed. Do not rely on it for recovery; create and verify another backup.", ex);
            }
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
    }

    private void SaveTrustedUnlockToken(AppConfig config)
    {
        if (!encryptionUsesEnvelope || encryptionKey is null || string.IsNullOrWhiteSpace(totpSecretBase32)
            || config.Version < 3 || config.MasterKeySlot is null)
            return;

        try
        {
            var now = utcNow();
            var token = trustedUnlockTokenService.CreateToken(encryptionKey, totpSecretBase32,
                ComputeConfigFingerprint(config), now, now.AddDays(1));
            storageService.SaveTrustedUnlockToken(token);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
            or CryptographicException or InvalidOperationException or NotSupportedException or Win32Exception)
        {
            TryDeleteTrustedUnlockToken();
        }
    }
    private void TryDeleteTrustedUnlockToken()
    {
        try
        {
            storageService.DeleteTrustedUnlockToken();
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or NotSupportedException)
        {
        }
    }

    internal static byte[] ComputeConfigFingerprint(AppConfig config)
    {
        var slot = config.MasterKeySlot ?? throw new InvalidOperationException("The Master Password key slot is missing.");
        var fingerprintInput = string.Join('\n',
            config.Version.ToString(System.Globalization.CultureInfo.InvariantCulture),
            slot.Version.ToString(System.Globalization.CultureInfo.InvariantCulture), slot.WrapAlgorithm,
            slot.KdfAlgorithm, slot.KdfIterations.ToString(System.Globalization.CultureInfo.InvariantCulture),
            slot.KdfMemorySizeKb.ToString(System.Globalization.CultureInfo.InvariantCulture),
            slot.KdfParallelism.ToString(System.Globalization.CultureInfo.InvariantCulture),
            slot.KeySizeBytes.ToString(System.Globalization.CultureInfo.InvariantCulture), slot.SaltBase64,
            slot.WrappedVaultKey, config.EncryptedTotpSecret, config.CredentialRevision.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return SHA256.HashData(Encoding.UTF8.GetBytes(fingerprintInput));
    }
    private VaultItem FindItem(Guid id)
    {
        return vaultData!.Items.FirstOrDefault(item => item.Id == id && !item.IsDeleted)
            ?? throw new InvalidOperationException("The selected vault item no longer exists.");
    }

    private VaultItem FindAnyItem(Guid id) => vaultData!.Items.FirstOrDefault(item => item.Id == id)
        ?? throw new InvalidOperationException("The selected vault item no longer exists.");

    private bool TryVerifyCurrentMasterPassword(string masterPassword, AppConfig config, out byte[] verificationKey)
    {
        verificationKey = [];
        if (!masterPasswordService.TryUnlockConfig(masterPassword, config, out verificationKey, out _)) return false;
        if (encryptionKey is not { Length: > 0 }
            || verificationKey.Length != encryptionKey.Length
            || !CryptographicOperations.FixedTimeEquals(verificationKey, encryptionKey))
        {
            CryptographicOperations.ZeroMemory(verificationKey);
            verificationKey = [];
            return false;
        }
        return true;
    }

    private void PurgeExpiredTrash()
    {
        var cutoff = utcNow().AddDays(-30);
        var removed = vaultData!.Items.RemoveAll(item => item.IsDeleted && item.DeletedAt is { } deletedAt && deletedAt <= cutoff);
        if (removed > 0) SaveVault();
    }

    private void RequireSensitiveTotp(string code)
    {
        if (!VerifyTotpForSensitiveAction(code))
        {
            throw new UnauthorizedAccessException("Authenticator verification failed.");
        }
    }

    private void EnsureMasterPasswordUnlocked()
    {
        if (encryptionKey is null || vaultData is null)
        {
            throw new InvalidOperationException("The vault is not unlocked with the Master Password.");
        }
    }

    private void EnsureOpen(bool allowRecoverySetup = false)
    {
        CheckExpiration();
        EnsureMasterPasswordUnlocked();

        if (!isVaultOpen)
        {
            throw new InvalidOperationException("The vault requires Authenticator verification before it can be opened.");
        }

        if (totpSecretBase32 is null)
            throw new UnauthorizedAccessException("A Google Authenticator secret is required to open the vault.");

        if (recoveryKeyRequired && !allowRecoverySetup)
            throw new InvalidOperationException("Save a Recovery Key before accessing the vault.");

        if (!IsSignInSessionActive)
        {
            if (signInSessionExpiresAt is not null) ClearSession();
            throw new InvalidOperationException("The sign-in session expired. Unlock the vault again to continue.");
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
    }

    private void ValidateVaultItem(VaultItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        if (string.IsNullOrWhiteSpace(item.Title))
        {
            throw new ArgumentException("Title is required.", nameof(item));
        }

        if (!Enum.IsDefined(item.Type))
        {
            throw new ArgumentException("The selected vault item type is not supported.", nameof(item));
        }

        item.RecoveryCodes ??= [];
        item.Tags ??= [];
        item.PasswordHistory ??= [];
        item.Tags = item.Tags
            .Select(tag => tag.Trim())
            .Where(tag => tag.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (item.GroupId is { } groupId && !vaultData!.Groups.Any(group => group.Id == groupId))
            throw new ArgumentException("The selected group does not exist.", nameof(item));
        if (string.IsNullOrWhiteSpace(item.Password) && item.RecoveryCodes.Count == 0)
        {
            throw new ArgumentException("A password or at least two recovery codes are required.", nameof(item));
        }

        if (string.IsNullOrWhiteSpace(item.Password) && item.PasswordHistory.Count != 0)
        {
            throw new ArgumentException("Password history requires a password.", nameof(item));
        }

        if (item.Type == VaultItemType.Password && !string.IsNullOrWhiteSpace(item.TotpSecretBase32))
        {
            if (!totpService.TryNormalizeWebsiteSecret(item.TotpSecretBase32, out var normalizedSecret))
            {
                throw new ArgumentException("The website TOTP secret is invalid.", nameof(item));
            }
            item.TotpSecretBase32 = normalizedSecret;
        }

        if (item.Type == VaultItemType.RecoveryCodes
            && (!string.IsNullOrEmpty(item.Password)
                || !string.IsNullOrEmpty(item.TotpSecretBase32)
                || item.RecoveryCodes.Count < 2))
        {
            throw new ArgumentException("A recovery-code item requires at least two codes and cannot contain a password.", nameof(item));
        }

        VaultBackupService.ValidateItems([item]);
    }

    private static VaultItem Clone(VaultItem item, bool includePassword)
    {
        return new VaultItem
        {
            Id = item.Id,
            Title = item.Title,
            Type = item.Type,
            Username = item.Username,
            Password = includePassword ? item.Password : string.Empty,
            TotpSecretBase32 = includePassword ? item.TotpSecretBase32 : string.Empty,
            RecoveryCodes = includePassword ? [.. item.RecoveryCodes] : [],
            Url = item.Url,
            HideUrl = item.HideUrl,
            Notes = item.Notes,
            HideNotes = item.HideNotes,
            IsFavorite = item.IsFavorite,
            GroupId = item.GroupId,
            LegacyFolder = item.LegacyFolder,
            Tags = [.. item.Tags],
            PasswordHistory = includePassword
                ? item.PasswordHistory.Select(entry => new PasswordHistoryEntry
                {
                    Password = entry.Password,
                    ChangedAt = entry.ChangedAt
                }).ToList()
                : [],
            IsDeleted = item.IsDeleted,
            DeletedAt = item.DeletedAt,
            CreatedAt = item.CreatedAt,
            UpdatedAt = item.UpdatedAt,
            PasswordChangedAt = item.PasswordChangedAt
        };
    }

    private static VaultItem CloneForList(VaultItem item)
    {
        var clone = Clone(item, includePassword: false);
        clone.RecoveryCodeCount = item.RecoveryCodes.Count;
        clone.HasPassword = !string.IsNullOrWhiteSpace(item.Password);
        clone.HasTotp = !string.IsNullOrWhiteSpace(item.TotpSecretBase32);
        if (clone.HideUrl)
        {
            clone.Url = string.Empty;
        }

        if (clone.HideNotes)
        {
            clone.Notes = string.Empty;
        }

        return clone;
    }

    private static void NormalizeItems(IEnumerable<VaultItem> items, DateTimeOffset utcNow)
    {
        foreach (var item in items)
        {
            item.RecoveryCodes ??= [];
            item.Tags ??= [];
            item.PasswordHistory ??= [];
            if (item.Type == VaultItemType.RecoveryCodes || string.IsNullOrWhiteSpace(item.Password))
            {
                item.PasswordChangedAt = null;
            }
            else if (item.PasswordChangedAt is not { } changedAt || changedAt.ToUniversalTime() > utcNow.ToUniversalTime())
            {
                item.PasswordChangedAt = PasswordLifecycle.GetEffectivePasswordChangedAt(item, utcNow);
            }
        }
    }

    private static void NormalizeGroups(VaultData data, DateTimeOffset now)
    {
        data.Groups ??= [];
        foreach (var group in data.Groups)
        {
            group.Name = ValidateGroupName(group.Name);
            // Invalid cosmetic metadata from older builds must not prevent opening the vault.
            try { group.AccentColor = NormalizeAccentColor(group.AccentColor); }
            catch (ArgumentException) { group.AccentColor = null; }
        }
        foreach (var item in data.Items)
        {
            if (string.IsNullOrWhiteSpace(item.LegacyFolder)) continue;
            var name = item.LegacyFolder.Trim();
            var group = data.Groups.FirstOrDefault(group => string.Equals(group.Name, name, StringComparison.CurrentCultureIgnoreCase));
            if (group is null)
            {
                group = new VaultGroup { Name = name, SortOrder = data.Groups.Count, CreatedAt = now, UpdatedAt = now };
                data.Groups.Add(group);
            }
            item.GroupId = group.Id;
            item.LegacyFolder = null;
        }
        var validIds = data.Groups.Select(group => group.Id).ToHashSet();
        foreach (var item in data.Items.Where(item => item.GroupId is not null && !validIds.Contains(item.GroupId.Value))) item.GroupId = null;
    }

    private void MigrateImportedFolders(IEnumerable<VaultItem> items, DateTimeOffset now)
    {
        foreach (var item in items)
        {
            if (string.IsNullOrWhiteSpace(item.LegacyFolder)) continue;
            var name = item.LegacyFolder.Trim();
            var group = vaultData!.Groups.FirstOrDefault(group => string.Equals(group.Name, name, StringComparison.CurrentCultureIgnoreCase));
            if (group is null)
            {
                group = new VaultGroup { Name = name, SortOrder = vaultData.Groups.Count, CreatedAt = now, UpdatedAt = now };
                vaultData.Groups.Add(group);
            }
            item.GroupId = group.Id;
            item.LegacyFolder = null;
        }
    }

    private static VaultGroup CloneGroup(VaultGroup group) => new()
    {
        Id = group.Id,
        Name = group.Name,
        AccentColor = group.AccentColor,
        SortOrder = group.SortOrder,
        CreatedAt = group.CreatedAt,
        UpdatedAt = group.UpdatedAt
    };

    private static string ValidateGroupName(string name)
    {
        name = name?.Trim() ?? string.Empty;
        if (name.Length == 0) throw new ArgumentException("Group name is required.", nameof(name));
        if (name.Length > 100) throw new ArgumentException("Group name cannot exceed 100 characters.", nameof(name));
        return name;
    }

    private static string? NormalizeAccentColor(string? value)
    {
        value = value?.Trim();
        if (string.IsNullOrEmpty(value)) return null;
        if (value.Length != 7 || value[0] != '#' || !int.TryParse(value.AsSpan(1), System.Globalization.NumberStyles.AllowHexSpecifier, null, out _))
            throw new ArgumentException("Accent color must use #RRGGBB format.", nameof(value));
        return value.ToUpperInvariant();
    }
}
