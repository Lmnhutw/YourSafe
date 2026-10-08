using PasswordTool.Core.Models;
using PasswordTool.Core.Services;
using PasswordTool.Autofill;
using PasswordTool.Presentation.Autofill;

namespace PasswordTool.Presentation;

/// <summary>Owns app-flow state while keeping VaultService access serialized.</summary>
public sealed class AppFlowCoordinator
{
    private readonly VaultService vaultService;
    private readonly IVaultOperationRunner operations;
    private readonly TotpService totpService;
    private readonly ISensitiveClipboardService? clipboard;
    private readonly bool hasPartialStorage;
    private IReadOnlyList<VaultItemListItem> tableItems = [];
    private IReadOnlyList<VaultGroup> tableGroups = [];
    private IReadOnlyList<TrashItemListItem> tableTrash = [];
    private IReadOnlyList<VaultSnapshotInfo> tableSnapshots = [];
    private SettingsSnapshot tableSettings = new(VaultSecuritySettings.DefaultVaultOpenDurationMinutes, false, null, null);
    public Func<CancellationToken, Task<string?>>? RequestActionPasswordAsync { get; set; }

    public AppFlowCoordinator(VaultService vaultService, IVaultOperationRunner operations, TotpService totpService,
        ISensitiveClipboardService? clipboard = null)
    {
        this.vaultService = vaultService;
        this.operations = operations;
        this.totpService = totpService;
        this.clipboard = clipboard;
        hasPartialStorage = vaultService.HasPartialStorage;
        FlowState = vaultService.HasActiveVaultSession
            ? vaultService.NeedsRecoveryKey ? AppFlowState.SaveRecoveryKey : AppFlowState.Unlocked
            : ResolveInitialState(vaultService.IsInitialized, hasPartialStorage);
    }

    public AppFlowState FlowState { get; private set; }
    public bool HasPartialStorage => hasPartialStorage;
    public bool IsSignedIn => vaultService.IsSignInSessionActive;
    public bool NeedsRecoveryKey => vaultService.NeedsRecoveryKey;
    public bool IsVaultSessionActive => vaultService.HasActiveVaultSession;
    public async Task<bool> ValidateAuthenticatorSetupAsync(AuthenticatorSetup setup, string code)
    {
        var version = LifecycleVersion;
        var state = FlowState;
        var valid = await operations.RunAsync(() =>
        {
            EnsureCurrentFlow(version, state);
            return vaultService.ValidateAuthenticatorSetup(setup.SecretBase32, code);
        }).ConfigureAwait(false);
        EnsureCurrentFlow(version, state);
        return valid;
    }
    public async Task SaveRecoveryKeyAsync(string password, string key, bool saved)
    {
        var version = LifecycleVersion;
        var state = FlowState;
        if (state is not (AppFlowState.SaveRecoveryKey or AppFlowState.Unlocked))
            throw new InvalidOperationException("Recovery Key changes require a normal vault unlock.");
        await operations.RunAsync(() =>
        {
            EnsureCurrentFlow(version, state);
            if (!vaultService.HasActiveVaultSession) throw new OperationCanceledException();
            vaultService.SaveRecoveryKey(password, key, saved);
        }).ConfigureAwait(false);
        EnsureCurrentFlow(version, state);
        if (!vaultService.IsVaultUnlocked) throw new OperationCanceledException();
        FlowState = AppFlowState.Unlocked;
    }
    public void BeginRecoveryKeyReset()
    {
        if (FlowState != AppFlowState.Unlock) throw new InvalidOperationException("Lock the vault before starting recovery.");
        Interlocked.Increment(ref lifecycleVersion);
        FlowState = AppFlowState.RecoveryKeyValidation;
    }

    public async Task ValidateRecoveryKeyAsync(string key)
    {
        RequireFlowState(AppFlowState.RecoveryKeyValidation);
        var version = LifecycleVersion;
        await operations.RunAsync(() =>
        {
            EnsureCurrentFlow(version, AppFlowState.RecoveryKeyValidation);
            vaultService.ValidateRecoveryKey(key);
        }).ConfigureAwait(false);
        EnsureCurrentFlow(version, AppFlowState.RecoveryKeyValidation);
        FlowState = AppFlowState.RecoveryMasterPassword;
    }

    public void PrepareRecoveryKeyReset(string masterPassword, string confirmation)
    {
        RequireFlowState(AppFlowState.RecoveryMasterPassword);
        MasterPasswordService.ValidateNewMasterPassword(masterPassword);
        if (!string.Equals(masterPassword, confirmation, StringComparison.Ordinal))
            throw new ArgumentException("The Master Password values do not match.", nameof(confirmation));
        FlowState = AppFlowState.RecoveryKeySave;
    }

    public void ConfirmRecoveryKeySaved(bool saved)
    {
        RequireFlowState(AppFlowState.RecoveryKeySave);
        if (!saved) throw new InvalidOperationException("Save the replacement Recovery Key before continuing.");
        FlowState = AppFlowState.RecoveryAuthenticator;
    }

    public void CancelRecoveryKeyReset()
    {
        Interlocked.Increment(ref lifecycleVersion);
        FlowState = AppFlowState.Unlock;
    }

    public async Task ResetWithRecoveryKeyAsync(RecoveryKeyResetRequest request)
    {
        RequireFlowState(AppFlowState.RecoveryAuthenticator);
        var version = LifecycleVersion;
        await operations.RunAsync(() =>
        {
            EnsureCurrentFlow(version, AppFlowState.RecoveryAuthenticator);
            vaultService.ResetWithRecoveryKey(request);
        }).ConfigureAwait(false);
        EnsureCurrentFlow(version, AppFlowState.RecoveryAuthenticator);
        Interlocked.Increment(ref lifecycleVersion);
        FlowState = AppFlowState.Unlock;
    }
    private long lifecycleVersion;
    public long LifecycleVersion => Volatile.Read(ref lifecycleVersion);
    public bool IsCurrentUnlock(long version) => version == LifecycleVersion &&
        (FlowState == AppFlowState.Unlocked && vaultService.IsVaultUnlocked || FlowState == AppFlowState.TableLocked && IsSignedIn);
    public bool IsCurrentNormalUnlock(long version) => version == LifecycleVersion
        && FlowState == AppFlowState.Unlocked && vaultService.IsVaultUnlocked;

    public static AppFlowState ResolveInitialState(bool isInitialized, bool hasPartialStorage) =>
        hasPartialStorage ? AppFlowState.Recover : isInitialized ? AppFlowState.Unlock : AppFlowState.FirstLaunch;

    public async Task<bool> ValidateMasterPasswordAsync(string masterPassword, CancellationToken cancellationToken = default)
    {
        var version = LifecycleVersion;
        var state = FlowState;
        var valid = await operations.RunAsync(() =>
        {
            EnsureCurrentFlow(version, state);
            return vaultService.ValidateMasterPassword(masterPassword);
        }, cancellationToken).ConfigureAwait(false);
        EnsureCurrentFlow(version, state);
        return valid;
    }

    public async Task<VaultUnlockResult> UnlockAsync(string masterPassword, string totpCode, CancellationToken cancellationToken = default)
    {
        if (FlowState is not (AppFlowState.Unlock or AppFlowState.TableLocked))
            throw new InvalidOperationException("Unlock is unavailable in the current state.");
        var version = Interlocked.Increment(ref lifecycleVersion);
        var result = await operations.RunAsync(() =>
        {
            if (version != Volatile.Read(ref lifecycleVersion))
                return new VaultUnlockResult(VaultUnlockStatus.Failed, "Unlock cancelled.");
            var signedIn = vaultService.IsSignInSessionActive;
            var unlocked = vaultService.UnlockWithMasterPassword(masterPassword);
            if (!unlocked.Success) return unlocked;

            var verified = false;
            try
            {
                verified = signedIn ? vaultService.IsSignInSessionActive : vaultService.VerifyTotpForSession(totpCode);
                verified &= version == Volatile.Read(ref lifecycleVersion);
                return verified ? unlocked : new VaultUnlockResult(VaultUnlockStatus.Failed, "The Authenticator code is incorrect or has expired. Enter the current 6-digit code and try again.");
            }
            finally
            {
                if (!verified) vaultService.LockVault();
            }
        }, cancellationToken).ConfigureAwait(false);
        if (result.Success && version == Volatile.Read(ref lifecycleVersion) && vaultService.HasActiveVaultSession) FlowState = NeedsRecoveryKey ? AppFlowState.SaveRecoveryKey : AppFlowState.Unlocked;
        else if (result.Success) return new VaultUnlockResult(VaultUnlockStatus.Failed, "Unlock cancelled.");
        return result;
    }

    public void BeginNewVault()
    {
        Interlocked.Increment(ref lifecycleVersion);
        FlowState = AppFlowState.CreateMasterPassword;
    }

    public void BeginRecovery()
    {
        Interlocked.Increment(ref lifecycleVersion);
        FlowState = AppFlowState.Recover;
    }

    public void ReturnToFirstLaunch()
    {
        Interlocked.Increment(ref lifecycleVersion);
        FlowState = AppFlowState.FirstLaunch;
    }

    public AuthenticatorSetup PrepareAuthenticator(string masterPassword, string confirmation)
    {
        RequireFlowState(AppFlowState.CreateMasterPassword);
        MasterPasswordService.ValidateNewMasterPassword(masterPassword);
        if (!string.Equals(masterPassword, confirmation, StringComparison.Ordinal))
            throw new ArgumentException("The Master Password values do not match.", nameof(confirmation));

        var secret = totpService.GenerateSecret();
        FlowState = AppFlowState.SetupAuthenticator;
        return new AuthenticatorSetup(
            secret,
            totpService.CreateOtpAuthUri(secret, "YourSafe", Environment.UserName));
    }

    public AuthenticatorSetup CreateAuthenticatorSetup()
    {
        var secret = totpService.GenerateSecret();
        return new AuthenticatorSetup(
            secret,
            totpService.CreateOtpAuthUri(secret, "YourSafe", Environment.UserName));
    }

    public Task<VaultBackupInspection> InspectRecoveryBackupAsync(
        string backupPath,
        string passphrase,
        CancellationToken cancellationToken = default) =>
        operations.RunAsync(
            () => vaultService.InspectBackupJson(ReadBoundedBackupFile(backupPath), passphrase),
            cancellationToken);

    public async Task CompleteNewVaultAsync(
        string masterPassword,
        AuthenticatorSetup setup,
        string confirmationCode,
        string recoveryKey,
        bool recoveryKeySaved,
        CancellationToken cancellationToken = default)
    {
        RequireFlowState(AppFlowState.SetupAuthenticator);
        var version = LifecycleVersion;
        await operations.RunAsync(
            () =>
            {
                EnsureCurrentFlow(version, AppFlowState.SetupAuthenticator);
                vaultService.InitializeNewVault(masterPassword, setup.SecretBase32, confirmationCode, recoveryKey, recoveryKeySaved);
            },
            cancellationToken).ConfigureAwait(false);
        EnsureCurrentFlow(version, AppFlowState.SetupAuthenticator);
        if (!vaultService.IsVaultUnlocked) throw new OperationCanceledException();
        FlowState = AppFlowState.Unlocked;
    }

    public async Task CompleteRecoveryAsync(
        string backupPath,
        string backupPassphrase,
        string masterPassword,
        AuthenticatorSetup setup,
        string confirmationCode,
        string recoveryKey,
        bool recoveryKeySaved,
        CancellationToken cancellationToken = default)
    {
        RequireFlowState(AppFlowState.SetupAuthenticator);
        var version = LifecycleVersion;
        await operations.RunAsync(() =>
        {
            EnsureCurrentFlow(version, AppFlowState.SetupAuthenticator);
            vaultService.RecoverFromBackup(new VaultRecoveryRequest
            {
                BackupJson = ReadBoundedBackupFile(backupPath),
                BackupPassphrase = backupPassphrase,
                NewMasterPassword = masterPassword,
                NewTotpSecretBase32 = setup.SecretBase32,
                TotpConfirmationCode = confirmationCode,
                RecoveryKey = recoveryKey,
                RecoveryKeySaved = recoveryKeySaved
            });
        }, cancellationToken).ConfigureAwait(false);
        EnsureCurrentFlow(version, AppFlowState.SetupAuthenticator);
        if (!vaultService.IsVaultUnlocked) throw new OperationCanceledException();
        FlowState = AppFlowState.Unlocked;
    }

    public async Task LockAsync(CancellationToken cancellationToken = default)
    {
        var version = Interlocked.Increment(ref lifecycleVersion);
        FlowState = AppFlowState.Unlock;
        tableItems = [];
        tableGroups = [];
        tableTrash = [];
        tableSnapshots = [];
        tableSettings = new(VaultSecuritySettings.DefaultVaultOpenDurationMinutes, false, null, null);
        await operations.RunAsync(() =>
        {
            if (vaultService.IsSignInSessionActive) vaultService.LockVault();
            else vaultService.ClearSession();
        }, cancellationToken).ConfigureAwait(false);
        if (version == LifecycleVersion) FlowState = AppFlowState.Unlock;
    }

    public async Task LockTableAsync(CancellationToken cancellationToken = default)
    {
        var version = Interlocked.Increment(ref lifecycleVersion);
        FlowState = AppFlowState.TableLocked;
        await operations.RunAsync(vaultService.LockVault, cancellationToken);
        if (version == LifecycleVersion && !IsSignedIn) FlowState = AppFlowState.Unlock;
    }

    public async Task LogoutAsync(CancellationToken cancellationToken = default)
    {
        var version = Interlocked.Increment(ref lifecycleVersion);
        FlowState = AppFlowState.Unlock;
        tableItems = [];
        tableGroups = [];
        tableTrash = [];
        tableSnapshots = [];
        tableSettings = new(VaultSecuritySettings.DefaultVaultOpenDurationMinutes, false, null, null);
        await operations.RunAsync(vaultService.ClearSession, cancellationToken).ConfigureAwait(false);
        if (version == LifecycleVersion) FlowState = AppFlowState.Unlock;
    }

    public Task<IReadOnlyList<VaultItemListItem>> GetListItemsAsync(CancellationToken cancellationToken = default) =>
        FlowState == AppFlowState.TableLocked && IsSignedIn ? Task.FromResult(tableItems) :
        RunVaultAsync(() =>
        {
            CachePageMetadata();
            return tableItems = vaultService.GetItems().Select(VaultItemListItem.FromVaultItem).ToList();
        }, cancellationToken);

    public Task<IReadOnlyList<CredentialMetadata>> FindAutofillCredentialsAsync(string origin, CancellationToken cancellationToken = default)
    {
        AutofillPolicy.RequireOrigin(origin);
        return RunUnlockedVaultAsync(() => (IReadOnlyList<CredentialMetadata>)vaultService.GetAutofillCredentials()
            .Where(item => AutofillPolicy.Matches(item, origin))
            .Select(item => new CredentialMetadata(item.Id, item.Title, item.Username, item.HasPassword, item.HasTotp)).ToList(), cancellationToken);
    }

    public Task<CredentialSecret> GetAutofillCredentialSecretAsync(string origin, Guid id, CancellationToken cancellationToken = default)
    {
        AutofillPolicy.RequireOrigin(origin);
        return RunUnlockedVaultAsync(() =>
        {
            var candidate = vaultService.GetAutofillCredentials().FirstOrDefault(item => item.Id == id && item.HasPassword && AutofillPolicy.Matches(item, origin))
                ?? throw new UnauthorizedAccessException("The credential is unavailable for this origin.");
            var secret = vaultService.GetAutofillSecret(id, candidate.Url);
            return new CredentialSecret(secret.Username, secret.Password);
        }, cancellationToken);
    }

    public Task<TotpCodeResult> GetAutofillCredentialTotpAsync(string origin, Guid id, CancellationToken cancellationToken = default)
    {
        AutofillPolicy.RequireOrigin(origin);
        if (id == Guid.Empty) throw new ArgumentException("A credential is required.", nameof(id));
        return RunUnlockedVaultAsync(() =>
        {
            var candidate = vaultService.GetAutofillCredentials().FirstOrDefault(item => item.Id == id && item.HasTotp && AutofillPolicy.Matches(item, origin))
                ?? throw new UnauthorizedAccessException("The credential is unavailable for this origin.");
            return vaultService.GetAutofillTotp(id, candidate.Url);
        }, cancellationToken);
    }

    public Task CopyAutofillCredentialTotpAsync(string origin, Guid id, CancellationToken cancellationToken = default) =>
        CopyTotpAsync(() => GetAutofillCredentialTotpAsync(origin, id, cancellationToken), cancellationToken);

    public Task<IReadOnlyList<VaultGroup>> GetGroupsAsync(CancellationToken cancellationToken = default) =>
        FlowState == AppFlowState.TableLocked && IsSignedIn ? Task.FromResult(tableGroups) :
        RunVaultAsync(() => tableGroups = vaultService.GetGroups(), cancellationToken);

    public Task<VaultGroup> AddGroupAsync(string name, string? accentColor = null, CancellationToken cancellationToken = default) =>
        RunVaultAsync(() => vaultService.AddGroup(name, accentColor), cancellationToken);

    public Task UpdateGroupAsync(Guid id, string name, string? accentColor, CancellationToken cancellationToken = default) =>
        RunVaultAsync(() => vaultService.UpdateGroup(id, name, accentColor), cancellationToken);

    public Task DeleteGroupAsync(Guid id, string confirmation, string totpCode, CancellationToken cancellationToken = default) =>
        RunVaultAsync(() => vaultService.DeleteGroup(id, confirmation, totpCode), cancellationToken);

    public Task<VaultItem> GetItemForEditingAsync(
        Guid id,
        string totpCode,
        CancellationToken cancellationToken = default) =>
        RunVaultAsync(() => vaultService.GetItemForEditing(id, totpCode), cancellationToken);

    public Task AddItemAsync(VaultItem item, CancellationToken cancellationToken = default) =>
        RunVaultAsync(() => vaultService.AddItem(item), cancellationToken);

    public Task UpdateItemAsync(VaultItem item, CancellationToken cancellationToken = default) =>
        RunVaultAsync(() => vaultService.UpdateItem(item), cancellationToken);

    public Task MoveItemToGroupAsync(Guid itemId, Guid? groupId, CancellationToken cancellationToken = default) =>
        RunVaultAsync(() =>
        {
            var item = vaultService.GetItemForEditing(itemId, string.Empty);
            item.GroupId = groupId;
            vaultService.UpdateItem(item);
        }, cancellationToken);

    public Task DeleteItemAsync(Guid id, CancellationToken cancellationToken = default) =>
        RunVaultAsync(() => vaultService.DeleteItem(id), cancellationToken);

    public Task<string> GetUsernameAsync(Guid id, CancellationToken cancellationToken = default) =>
        RunVaultAsync(() => vaultService.GetUsername(id), cancellationToken);

    public Task<string> GetPasswordAsync(
        Guid id,
        string totpCode,
        CancellationToken cancellationToken = default) =>
        RunVaultAsync(() => vaultService.GetPassword(id, totpCode), cancellationToken);

    public Task<IReadOnlyList<string>> GetRecoveryCodesAsync(
        Guid id,
        string totpCode,
        CancellationToken cancellationToken = default) =>
        RunVaultAsync(() => vaultService.GetRecoveryCodes(id, totpCode), cancellationToken);

    public Task<TotpCodeResult> GetWebsiteTotpCodeAsync(
        Guid id,
        CancellationToken cancellationToken = default) =>
        RunUnlockedVaultAsync(() => vaultService.GetWebsiteTotpCode(id), cancellationToken);

    public Task CopyWebsiteTotpAsync(Guid id, CancellationToken cancellationToken = default) =>
        CopyTotpAsync(() => GetWebsiteTotpCodeAsync(id, cancellationToken), cancellationToken);

    public Task<TotpCodeResult> GetWebsiteTotpCodeAsync(
        Guid id,
        string totpCode,
        CancellationToken cancellationToken = default) =>
        GetWebsiteTotpCodeAsync(id, cancellationToken);

    private async Task CopyTotpAsync(Func<Task<TotpCodeResult>> getCode, CancellationToken cancellationToken)
    {
        var version = LifecycleVersion;
        var target = clipboard ?? throw new InvalidOperationException("The clipboard is unavailable.");
        var result = await getCode().ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsCurrentNormalUnlock(version)) throw new OperationCanceledException();
        await target.CopyAsync(result.Code, () =>
        {
            var now = DateTimeOffset.UtcNow;
            return IsCurrentNormalUnlock(version) && now < result.ExpiresAtUtc
                && now >= result.ExpiresAtUtc.AddSeconds(-result.PeriodSeconds);
        }, cancellationToken, clearAutomatically: false).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsCurrentNormalUnlock(version)) throw new OperationCanceledException();
    }

    public Task<IReadOnlyList<PasswordHistoryEntry>> GetPasswordHistoryAsync(
        Guid id,
        string totpCode,
        CancellationToken cancellationToken = default) =>
        RunVaultAsync(() => vaultService.GetPasswordHistory(id, totpCode), cancellationToken);

    public Task<IReadOnlyList<TrashItemListItem>> GetDeletedItemsAsync(CancellationToken cancellationToken = default) =>
        FlowState == AppFlowState.TableLocked && IsSignedIn ? Task.FromResult(tableTrash) :
        RunVaultAsync(
            () => tableTrash = vaultService.GetDeletedItems().Select(TrashItemListItem.FromVaultItem).ToList(),
            cancellationToken);

    public Task RestoreDeletedItemAsync(Guid id, CancellationToken cancellationToken = default) =>
        RunVaultAsync(() => vaultService.RestoreDeletedItem(id), cancellationToken);

    public Task PermanentlyDeleteItemAsync(
        Guid id,
        string totpCode,
        CancellationToken cancellationToken = default) =>
        RunVaultAsync(() => vaultService.PermanentlyDeleteItem(id, totpCode), cancellationToken);

    public Task<SettingsSnapshot> GetSettingsAsync(CancellationToken cancellationToken = default) =>
        FlowState == AppFlowState.TableLocked && IsSignedIn ? Task.FromResult(tableSettings) :
        RunVaultAsync(() => tableSettings = ReadSettings(), cancellationToken);

    private SettingsSnapshot ReadSettings()
    {
        var security = vaultService.SecuritySettings;
        return new SettingsSnapshot(security.VaultOpenDurationMinutes, vaultService.NeedsKdfUpgrade,
            vaultService.LastExternalBackupAt, vaultService.LastVerifiedBackupAt);
    }

    private void CachePageMetadata()
    {
        tableTrash = vaultService.GetDeletedItems().Select(TrashItemListItem.FromVaultItem).ToList();
        tableSnapshots = vaultService.GetSnapshots();
        tableSettings = ReadSettings();
    }

    public Task<OperationResult> UpdateSettingsAsync(
        string masterPassword,
        int vaultDurationMinutes,
        CancellationToken cancellationToken = default) =>
        RunVaultAsync(() =>
        {
            var success = vaultService.TryUpdateSettings(
                masterPassword,
                VaultLoginMode.Hybrid,
                new VaultSecuritySettings(
                    VaultSecuritySettings.DefaultInactivityLockTimeoutMinutes,
                    VaultSecuritySettings.DefaultSensitiveActionTimeoutMinutes,
                    vaultDurationMinutes),
                out var message);
            return new OperationResult(success, message);
        }, cancellationToken);

    public Task<OperationResult> ChangeMasterPasswordAsync(
        string currentMasterPassword,
        string newMasterPassword,
        CancellationToken cancellationToken = default) =>
        RunVaultAsync(() =>
        {
            var success = vaultService.TryChangeMasterPassword(currentMasterPassword, newMasterPassword, out var message);
            return new OperationResult(success, message);
        }, cancellationToken);

    public Task<OperationResult> UpgradeKdfAsync(
        string masterPassword,
        CancellationToken cancellationToken = default) =>
        RunVaultAsync(() =>
        {
            var success = vaultService.TryUpgradeKdf(masterPassword, out var message);
            return new OperationResult(success, message);
        }, cancellationToken);

    public Task<OperationResult> ResetAuthenticatorAsync(
        string masterPassword,
        AuthenticatorSetup setup,
        string confirmationCode,
        CancellationToken cancellationToken = default) =>
        RunVaultAsync(() =>
        {
            var success = vaultService.TryResetAuthenticator(
                masterPassword,
                setup.SecretBase32,
                confirmationCode,
                out var message);
            return new OperationResult(success, message);
        }, cancellationToken);

    public Task CreateExternalBackupAsync(
        string destinationPath,
        string passphrase,
        string totpCode,
        CancellationToken cancellationToken = default) =>
        RunVaultAsync(() => vaultService.CreateExternalBackupFile(destinationPath, passphrase, totpCode), cancellationToken);

    public Task<VaultBackupInspection> VerifyExternalBackupAsync(
        string path,
        string passphrase,
        CancellationToken cancellationToken = default) =>
        RunVaultAsync(() => vaultService.VerifyExternalBackupFile(path, passphrase), cancellationToken);

    public Task<VaultBackupImportPlan> PreviewBackupImportAsync(
        string path,
        string passphrase,
        string totpCode,
        CancellationToken cancellationToken = default) =>
        RunVaultAsync(
            () => vaultService.PreviewBackupImport(ReadBoundedFile(path, VaultBackupService.MaxBackupJsonCharacters), passphrase, totpCode),
            cancellationToken);

    public Task<int> ImportBackupAsync(
        string path,
        string passphrase,
        string totpCode,
        CancellationToken cancellationToken = default) =>
        RunVaultAsync(
            () => vaultService.ImportBackupJson(ReadBoundedFile(path, VaultBackupService.MaxBackupJsonCharacters), passphrase, totpCode),
            cancellationToken);

    public Task<VaultCsvImportPlan> PreviewCsvImportAsync(
        string path,
        string totpCode,
        CancellationToken cancellationToken = default) =>
        RunVaultAsync(
            () => vaultService.PreviewCsvImport(ReadBoundedFile(path, VaultCsvImportService.MaxCsvCharacters), totpCode),
            cancellationToken);

    public Task<int> ImportCsvAsync(
        string path,
        string totpCode,
        CancellationToken cancellationToken = default) =>
        RunVaultAsync(
            () => vaultService.ImportCsv(ReadBoundedFile(path, VaultCsvImportService.MaxCsvCharacters), totpCode),
            cancellationToken);

    public Task<IReadOnlyList<VaultSnapshotInfo>> GetSnapshotsAsync(CancellationToken cancellationToken = default) =>
        FlowState == AppFlowState.TableLocked && IsSignedIn ? Task.FromResult(tableSnapshots) :
        RunVaultAsync(() => tableSnapshots = vaultService.GetSnapshots(), cancellationToken);

    public Task<IReadOnlyList<VaultSecurityFinding>> GetSecurityFindingsAsync(
        string totpCode,
        CancellationToken cancellationToken = default) =>
        RunVaultAsync(() => vaultService.GetSecurityFindings(totpCode), cancellationToken);

    public async Task<OperationResult> RestoreSnapshotAsync(
        string snapshotId,
        string masterPassword,
        CancellationToken cancellationToken = default)
    {
        var version = LifecycleVersion;
        var result = await operations.RunAsync(() =>
        {
            if (!IsCurrentUnlock(version)) throw new OperationCanceledException();
            var success = vaultService.TryRestoreSnapshot(snapshotId, masterPassword, out var message);
            return new OperationResult(success, message);
        }, cancellationToken).ConfigureAwait(false);
        if (version != LifecycleVersion) throw new OperationCanceledException();
        if (result.Success)
        {
            Interlocked.Increment(ref lifecycleVersion);
            FlowState = AppFlowState.Unlock;
        }
        else if (!IsCurrentUnlock(version)) throw new OperationCanceledException();
        return result;
    }

    private void RequireFlowState(AppFlowState state)
    {
        if (FlowState != state) throw new InvalidOperationException("The wizard step is not available in the current state.");
    }

    private void EnsureCurrentFlow(long version, AppFlowState state)
    {
        if (version != LifecycleVersion || FlowState != state
            || state is (AppFlowState.Unlocked or AppFlowState.SaveRecoveryKey) && !vaultService.HasActiveVaultSession)
            throw new OperationCanceledException();
    }

    private static string ReadBoundedBackupFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("A backup file is required.", nameof(path));
        var info = new FileInfo(path);
        if (info.Length > VaultBackupService.MaxBackupJsonCharacters)
            throw new InvalidDataException("The selected backup exceeds the 10 MB limit.");
        return File.ReadAllText(path);
    }

    private async Task<T> RunVaultAsync<T>(Func<T> operation, CancellationToken cancellationToken, bool allowTableLocked = true)
    {
        var version = LifecycleVersion;
        var locked = FlowState == AppFlowState.TableLocked;
        string? actionPassword = null;
        if (locked)
        {
            if (!allowTableLocked || !IsSignedIn || RequestActionPasswordAsync is null) throw new OperationCanceledException("Unlock the vault on the desktop first.");
            actionPassword = await RequestActionPasswordAsync(cancellationToken);
            if (actionPassword is null || !IsCurrentUnlock(version)) throw new OperationCanceledException();
        }
        T result;
        try
        {
            result = await operations.RunAsync(() =>
            {
                if (!IsCurrentUnlock(version)) throw new OperationCanceledException("The vault was locked or the sign-in session expired.");
                if (!locked) return operation();
                try
                {
                    if (!vaultService.IsSignInSessionActive) throw new OperationCanceledException();
                    var unlock = vaultService.UnlockWithMasterPassword(actionPassword!);
                    if (!unlock.Success) throw new UnauthorizedAccessException("The Master Password is incorrect.");
                    if (!IsCurrentUnlock(version) || !vaultService.HasActiveVaultSession) throw new OperationCanceledException();
                    var value = operation();
                    tableItems = vaultService.GetItems().Select(VaultItemListItem.FromVaultItem).ToList();
                    CachePageMetadata();
                    tableGroups = vaultService.GetGroups();
                    return value;
                }
                finally { vaultService.LockVault(); }
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception) when (!IsCurrentUnlock(version))
        {
            throw new OperationCanceledException("The vault was locked or the sign-in session expired.");
        }
        if (!IsCurrentUnlock(version)) throw new OperationCanceledException("The vault was locked or the sign-in session expired.");
        return result;
    }

    private async Task<T> RunUnlockedVaultAsync<T>(Func<T> operation, CancellationToken cancellationToken)
    {
        var version = LifecycleVersion;
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsCurrentNormalUnlock(version)) throw new OperationCanceledException("Unlock the vault on the desktop first.");
        T result;
        try
        {
            result = await operations.RunAsync(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!IsCurrentNormalUnlock(version)) throw new OperationCanceledException();
                return operation();
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception) when (!IsCurrentNormalUnlock(version))
        {
            throw new OperationCanceledException("The vault was locked or the sign-in session expired.");
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsCurrentNormalUnlock(version)) throw new OperationCanceledException();
        return result;
    }

    private Task RunVaultAsync(Action operation, CancellationToken cancellationToken) =>
        RunVaultAsync(() => { operation(); return true; }, cancellationToken);

    private static string ReadBoundedFile(string path, int maximumCharacters)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("A file is required.", nameof(path));
        var info = new FileInfo(path);
        if (info.Length > maximumCharacters) throw new InvalidDataException("The selected file exceeds the 10 MB limit.");
        var content = File.ReadAllText(path);
        if (content.Length > maximumCharacters) throw new InvalidDataException("The selected file exceeds the 10 MB limit.");
        return content;
    }
}
