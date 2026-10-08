using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PasswordTool.Core.Models;

namespace PasswordTool.Presentation;

public sealed partial class ShellViewModel : ObservableObject
{
    private readonly AppFlowCoordinator flow;
    private readonly INavigationService navigation;
    private readonly ISensitiveClipboardService clipboard;
    private readonly IFilePickerService filePicker;
    private readonly IUserErrorMapper errorMapper;
    private readonly IUserDialogService dialogs;
    private bool backupReminderOffered;

    public ShellViewModel(
        AppFlowCoordinator flow,
        VaultWorkspaceViewModel vault,
        HashToolViewModel hashTool,
        SettingsViewModel settings,
        BackupViewModel backup,
        SecurityCheckViewModel securityCheck,
        TrashViewModel trash,
        INavigationService navigation,
        ISensitiveClipboardService clipboard,
        IFilePickerService filePicker,
        IUserErrorMapper errorMapper,
        IUserDialogService dialogs)
    {
        this.flow = flow;
        this.navigation = navigation;
        this.clipboard = clipboard;
        this.filePicker = filePicker;
        this.errorMapper = errorMapper;
        this.dialogs = dialogs;
        if (dialogs is not null) flow.RequestActionPasswordAsync = dialogs.PromptMasterPasswordAsync;
        Vault = vault;
        HashTool = hashTool;
        Settings = settings;
        Backup = backup;
        SecurityCheck = securityCheck;
        Trash = trash;
        FlowState = flow.FlowState;
        CurrentRoute = navigation.CurrentRoute;
    }

    public VaultWorkspaceViewModel Vault { get; }
    public HashToolViewModel HashTool { get; }
    public SettingsViewModel Settings { get; }
    public BackupViewModel Backup { get; }
    public SecurityCheckViewModel SecurityCheck { get; }
    public TrashViewModel Trash { get; }

    [ObservableProperty] public partial AppFlowState FlowState { get; set; }
    [ObservableProperty] public partial AppRoute CurrentRoute { get; set; } = AppRoute.Vault;
    [ObservableProperty] public partial string StatusMessage { get; set; } = string.Empty;
    [ObservableProperty] public partial bool IsStatusOpen { get; set; }
    [ObservableProperty] public partial bool IsSavingItem { get; set; }
    [ObservableProperty] public partial bool IsRecovering { get; set; }
    [ObservableProperty] public partial bool IsBackupReminderOpen { get; set; }
    [ObservableProperty] public partial string RecoveryPath { get; set; } = string.Empty;
    [ObservableProperty] public partial string RecoverySummary { get; set; } = string.Empty;
    [ObservableProperty] public partial AuthenticatorSetup? PendingAuthenticatorSetup { get; set; }

    public bool IsUnlocked => FlowState is AppFlowState.Unlocked or AppFlowState.TableLocked;
    public bool IsTableLocked => FlowState == AppFlowState.TableLocked;
    public bool IsSignedIn => flow.IsSignedIn;
    public bool IsVaultSessionActive => flow.IsVaultSessionActive;
    public bool NeedsRecoveryKey => flow.NeedsRecoveryKey;
    public Task<bool> ValidateAuthenticatorSetupAsync(AuthenticatorSetup setup, string code) => flow.ValidateAuthenticatorSetupAsync(setup, code);
    public async Task SaveRecoveryKeyAsync(string password, string key, bool saved)
    {
        var version = LifecycleVersion;
        await flow.SaveRecoveryKeyAsync(password, key, saved);
        if (version != LifecycleVersion || !flow.IsVaultSessionActive) return;
        FlowState = flow.FlowState;
        await CompleteUnlockAsync(new VaultUnlockResult(VaultUnlockStatus.Unlocked));
    }
    public void BeginRecoveryKeyReset()
    {
        flow.BeginRecoveryKeyReset();
        FlowState = flow.FlowState;
        ClearAuthenticationStatus();
    }
    public async Task ValidateRecoveryKeyAsync(string key)
    {
        await flow.ValidateRecoveryKeyAsync(key);
        FlowState = flow.FlowState;
    }
    public void PrepareRecoveryKeyReset(string masterPassword, string confirmation)
    {
        flow.PrepareRecoveryKeyReset(masterPassword, confirmation);
        FlowState = flow.FlowState;
    }
    public void ConfirmRecoveryKeySaved(bool saved)
    {
        flow.ConfirmRecoveryKeySaved(saved);
        FlowState = flow.FlowState;
    }
    public void CancelRecoveryKeyReset()
    {
        flow.CancelRecoveryKeyReset();
        PendingAuthenticatorSetup = null;
        FlowState = flow.FlowState;
        ClearAuthenticationStatus();
    }
    public async Task ResetWithRecoveryKeyAsync(RecoveryKeyResetRequest request)
    {
        await flow.ResetWithRecoveryKeyAsync(request);
        await LockAsync();
    }
    public long LifecycleVersion => flow.LifecycleVersion;
    public bool IsCurrentUnlock(long version) => flow.IsCurrentUnlock(version) && IsUnlocked;
    public bool IsCurrentNormalUnlock(long version) => flow.IsCurrentNormalUnlock(version);
    public bool HasPartialStorage => flow.HasPartialStorage;
    public bool IsVaultRoute => CurrentRoute == AppRoute.Vault;
    public bool IsHashToolRoute => CurrentRoute == AppRoute.HashTool;

    public async Task<bool> ValidateMasterPasswordAsync(string masterPassword)
    {
        var version = LifecycleVersion;
        IsStatusOpen = false;
        try
        {
            var valid = await flow.ValidateMasterPasswordAsync(masterPassword);
            if (version != LifecycleVersion) return false;
            if (valid) return true;
            StatusMessage = "The Master Password is incorrect. Please try again.";
            IsStatusOpen = true;
        }
        catch (Exception exception) { if (version == LifecycleVersion) ShowMappedError(exception); }
        return false;
    }

    public async Task UnlockAsync(string masterPassword, string totpCode)
    {
        IsStatusOpen = false;
        var operation = flow.UnlockAsync(masterPassword, totpCode);
        var version = LifecycleVersion;
        var result = await operation;
        if (version != LifecycleVersion) return;
        FlowState = flow.FlowState;
        if (!result.Success)
        {
            StatusMessage = result.Message;
            IsStatusOpen = true;
            return;
        }

        try { if (!NeedsRecoveryKey) await CompleteUnlockAsync(result); }
        catch (OperationCanceledException) { }
    }

    public void BeginNewVault()
    {
        IsRecovering = false;
        flow.BeginNewVault();
        FlowState = flow.FlowState;
        ClearAuthenticationStatus();
    }

    public void BeginRecovery()
    {
        IsRecovering = true;
        flow.BeginRecovery();
        FlowState = flow.FlowState;
        ClearAuthenticationStatus();
    }

    public async Task SelectRecoveryFileAsync()
    {
        var version = LifecycleVersion;
        var path = await filePicker.PickOpenPathAsync();
        if (version == LifecycleVersion && FlowState == AppFlowState.Recover && !string.IsNullOrWhiteSpace(path)) RecoveryPath = path;
    }

    public async Task InspectRecoveryAsync(string passphrase)
    {
        var version = LifecycleVersion;
        try
        {
            var inspection = await flow.InspectRecoveryBackupAsync(RecoveryPath, passphrase);
            if (version != LifecycleVersion || FlowState != AppFlowState.Recover) return;
            RecoverySummary = FormatInspection(inspection);
            ClearAuthenticationStatus();
        }
        catch (Exception exception)
        {
            if (version == LifecycleVersion) ShowMappedError(exception);
        }
    }

    public void ContinueRecovery()
    {
        if (string.IsNullOrWhiteSpace(RecoverySummary)) return;
        flow.BeginNewVault();
        FlowState = flow.FlowState;
    }

    public void PrepareAuthenticator(string masterPassword, string confirmation)
    {
        try
        {
            PendingAuthenticatorSetup = flow.PrepareAuthenticator(masterPassword, confirmation);
            FlowState = flow.FlowState;
            ClearAuthenticationStatus();
        }
        catch (Exception exception)
        {
            ShowMappedError(exception);
        }
    }

    public async Task CompleteAuthenticatorSetupAsync(
        string masterPassword,
        string backupPassphrase,
        string confirmationCode,
        string recoveryKey,
        bool recoveryKeySaved)
    {
        if (PendingAuthenticatorSetup is null) return;
        try
        {
            if (IsRecovering)
            {
                await flow.CompleteRecoveryAsync(
                    RecoveryPath,
                    backupPassphrase,
                    masterPassword,
                    PendingAuthenticatorSetup,
                    confirmationCode,
                    recoveryKey,
                    recoveryKeySaved);
            }
            else
            {
                await flow.CompleteNewVaultAsync(masterPassword, PendingAuthenticatorSetup, confirmationCode, recoveryKey, recoveryKeySaved);
            }

            FlowState = flow.FlowState;
            PendingAuthenticatorSetup = null;
            RecoveryPath = string.Empty;
            RecoverySummary = string.Empty;
            await CompleteUnlockAsync(new VaultUnlockResult(VaultUnlockStatus.Unlocked));
        }
        catch (Exception exception)
        {
            ShowMappedError(exception);
        }
    }

    public void CancelFirstLaunchStep()
    {
        PendingAuthenticatorSetup = null;
        RecoveryPath = string.Empty;
        RecoverySummary = string.Empty;
        IsRecovering = false;
        flow.ReturnToFirstLaunch();
        FlowState = flow.FlowState;
        ClearAuthenticationStatus();
    }

    [RelayCommand]
    private async Task LockAsync()
    {
        IsBackupReminderOpen = false;
        FlowState = AppFlowState.Unlock;
        Vault.Clear();
        Trash.Clear();
        SecurityCheck.Clear();
        Backup.Clear();
        Settings.Clear();
        PendingAuthenticatorSetup = null;
        RecoveryPath = RecoverySummary = string.Empty;
        IsRecovering = false;
        var operation = flow.LockAsync();
        var version = LifecycleVersion;
        await operation;
        if (version != LifecycleVersion) return;
        Vault.Clear();
        await clipboard.ClearOwnedValueAsync();
        if (version != LifecycleVersion) return;
        navigation.ResetForLock();
        FlowState = flow.FlowState;
        CurrentRoute = navigation.CurrentRoute;
        StatusMessage = string.Empty;
        IsStatusOpen = false;
        OnPropertyChanged(nameof(IsSignedIn));
    }

    public async Task LockTableAsync(bool preserveCurrentRoute = false)
    {
        if (!IsSignedIn) { await LogoutAsync(); return; }
        var locking = flow.LockTableAsync();
        var version = LifecycleVersion;
        await locking;
        if (version != LifecycleVersion) return;
        Trash.Clear(); SecurityCheck.Clear(); Backup.Clear(); Settings.Clear();
        await clipboard.ClearOwnedValueAsync();
        if (version != LifecycleVersion) return;
        if (!preserveCurrentRoute)
        {
            navigation.Navigate(AppRoute.Vault);
            CurrentRoute = AppRoute.Vault;
        }
        FlowState = flow.FlowState;
        OnPropertyChanged(nameof(IsTableLocked));
    }

    public async Task UnlockTableAsync()
    {
        var version = LifecycleVersion;
        var password = await dialogs.PromptMasterPasswordAsync();
        if (password is null || !IsCurrentUnlock(version)) return;
        await UnlockAsync(password, string.Empty);
        OnPropertyChanged(nameof(IsTableLocked));
    }

    [RelayCommand]
    private async Task LogoutAsync()
    {
        FlowState = AppFlowState.Unlock;
        Vault.Clear();
        Trash.Clear();
        SecurityCheck.Clear();
        Backup.Clear();
        Settings.Clear();
        PendingAuthenticatorSetup = null;
        RecoveryPath = RecoverySummary = string.Empty;
        IsRecovering = false;
        var operation = flow.LogoutAsync();
        var version = LifecycleVersion;
        await operation;
        if (version != LifecycleVersion) return;
        await LockAsync();
    }

    public void Navigate(AppRoute route)
    {
        if (!IsUnlocked || !IsSignedIn) return;
        navigation.Navigate(route);
        CurrentRoute = navigation.CurrentRoute;
        OnPropertyChanged(nameof(IsVaultRoute));
        OnPropertyChanged(nameof(IsHashToolRoute));
    }

    public void BeginAddItem() => Navigate(AppRoute.ItemEditor);

    public async Task<VaultItem?> GetSelectedItemForEditingAsync()
    {
        if (Vault.SelectedItem is not { } selected) return null;
        return await GetItemForEditingAsync(selected.Id);
    }

    public async Task<VaultItem?> GetItemForEditingAsync(Guid itemId)
    {
        var version = LifecycleVersion;
        try
        {
            var item = await flow.GetItemForEditingAsync(itemId, string.Empty);
            return IsCurrentUnlock(version) ? item : null;
        }
        catch (Exception exception)
        {
            ShowMappedError(exception);
            return null;
        }
    }

    public async Task<bool> SaveItemAsync(VaultItemEditorInput input)
    {
        if (IsSavingItem) return false;
        IsSavingItem = true;
        var version = LifecycleVersion;
        try
        {
            var item = input.ToVaultItem();
            if (input.Id is null) await flow.AddItemAsync(item);
            else await flow.UpdateItemAsync(item);
            await Vault.RefreshAsync();
            if (!IsCurrentUnlock(version)) return false;
            Navigate(AppRoute.Vault);
            ClearAuthenticationStatus();
            if (input.Id is null && !backupReminderOffered)
            {
                try
                {
                    if ((await flow.GetListItemsAsync()).Count == 1
                        && (await flow.GetDeletedItemsAsync()).Count == 0)
                    {
                        var settings = await flow.GetSettingsAsync();
                        if (!IsCurrentUnlock(version)) return false;
                        backupReminderOffered = true;
                        IsBackupReminderOpen = settings.LastExternalBackupAt is null;
                    }
                }
                catch { /* Saving the item succeeded; a reminder must not turn it into a failure. */ }
            }
            return true;
        }
        catch (Exception exception)
        {
            ShowMappedError(exception);
            return false;
        }
        finally { IsSavingItem = false; }
    }

    public async Task<VaultGroup?> CreateGroupAsync(string name, string? accentColor = null)
    {
        var version = LifecycleVersion;
        try
        {
            var group = await flow.AddGroupAsync(name, accentColor);
            await Vault.RefreshAsync();
            return IsCurrentUnlock(version) ? group : null;
        }
        catch (Exception exception) { ShowMappedError(exception); return null; }
    }

    public async Task<bool> UpdateGroupAsync(Guid id, string name, string? accentColor)
    {
        var version = LifecycleVersion;
        try { await flow.UpdateGroupAsync(id, name, accentColor); await Vault.RefreshAsync(); return IsCurrentUnlock(version); }
        catch (Exception exception) { ShowMappedError(exception); return false; }
    }

    public async Task DeleteGroupAsync(Guid id, string name)
    {
        var version = LifecycleVersion;
        var confirmation = await dialogs.ConfirmGroupDeletionAsync(name);
        if (confirmation is null || !IsCurrentUnlock(version)) return;
        try { await flow.DeleteGroupAsync(id, confirmation.Value.Confirmation, confirmation.Value.TotpCode); await Vault.RefreshAsync(); }
        catch (UnauthorizedAccessException exception) { ShowMappedError(exception); }
        catch (Exception exception) { ShowMappedError(exception); }
    }

    public async Task DeleteItemAsync(Guid itemId)
    {
        var version = LifecycleVersion;
        var title = Vault.Items.FirstOrDefault(item => item.Id == itemId)?.Title;
        if (title is null || !await dialogs.ConfirmAsync("Move to Trash", $"Move '{title}' to Trash?", "Move to Trash") || !IsCurrentUnlock(version)) return;
        try
        {
            await flow.DeleteItemAsync(itemId);
            await Vault.RefreshAsync();
            if (!IsCurrentUnlock(version)) return;
            ClearAuthenticationStatus();
        }
        catch (Exception exception)
        {
            ShowMappedError(exception);
        }
    }

    public async Task MoveItemToGroupAsync(Guid itemId, Guid? groupId)
    {
        var version = LifecycleVersion;
        try
        {
            await flow.MoveItemToGroupAsync(itemId, groupId);
            if (!IsCurrentUnlock(version)) return;
            await Vault.RefreshAsync();
        }
        catch (Exception exception) { ShowMappedError(exception); }
    }

    public async Task CopyUsernameAsync(Guid itemId)
    {
        var version = LifecycleVersion;
        try
        {
            var value = await flow.GetUsernameAsync(itemId);
            if (IsCurrentUnlock(version)) await clipboard.CopyAsync(value);
        }
        catch (Exception exception)
        {
            ShowMappedError(exception);
        }
    }

    public async Task RevealPasswordAsync(Guid itemId)
    {
        var version = LifecycleVersion;
        try
        {
            var value = await flow.GetPasswordAsync(itemId, string.Empty);
            if (IsCurrentUnlock(version)) await dialogs.ShowSecretAsync("Password", value, multiline: false);
        }
        catch (Exception exception)
        {
            ShowMappedError(exception);
        }
    }

    public async Task RevealRecoveryCodesAsync(Guid itemId)
    {
        var version = LifecycleVersion;
        try
        {
            var codes = await flow.GetRecoveryCodesAsync(itemId, string.Empty);
            if (!IsCurrentUnlock(version)) return;
            await dialogs.ShowSecretAsync("Recovery codes", string.Join(Environment.NewLine, codes), multiline: true);
        }
        catch (Exception exception)
        {
            ShowMappedError(exception);
        }
    }

    public async Task RevealNotesAsync(Guid itemId)
    {
        var version = LifecycleVersion;
        var item = Vault.Items.FirstOrDefault(candidate => candidate.Id == itemId);
        if (item is null || !item.HasNotes) return;

        var notes = item.Notes;
        if (item.HideNotes || IsTableLocked)
        {
            var fullItem = await GetItemForEditingAsync(itemId);
            if (fullItem is null) return;
            notes = fullItem.Notes;
        }

        try
        {
            if (!IsCurrentUnlock(version)) return;
            await dialogs.ShowSecretAsync("Notes", notes, multiline: true);
        }
        catch (Exception exception)
        {
            ShowMappedError(exception);
        }
    }

    public async Task CopyPasswordAsync(Guid itemId)
    {
        var version = LifecycleVersion;
        try
        {
            var value = await flow.GetPasswordAsync(itemId, string.Empty);
            if (IsCurrentUnlock(version)) await clipboard.CopyAsync(value);
        }
        catch (Exception exception)
        {
            ShowMappedError(exception);
        }
    }

    public async Task ViewPasswordHistoryAsync(Guid itemId)
    {
        var version = LifecycleVersion;
        if (Vault.Items.All(item => item.Id != itemId || !item.HasPassword)) return;
        try
        {
            var history = await flow.GetPasswordHistoryAsync(itemId, string.Empty);
            if (!IsCurrentUnlock(version)) return;
            var text = history.Count == 0
                ? "No previous passwords are stored for this item."
                : string.Join(Environment.NewLine + Environment.NewLine, history.Select(entry =>
                    $"Changed {entry.ChangedAt.ToLocalTime():g}{Environment.NewLine}{entry.Password}"));
            await dialogs.ShowSecretAsync("Password history", text, multiline: true);
        }
        catch (Exception exception)
        {
            ShowMappedError(exception);
        }
    }

    partial void OnFlowStateChanged(AppFlowState value)
    {
        OnPropertyChanged(nameof(IsUnlocked));
        OnPropertyChanged(nameof(IsTableLocked));
    }

    private async Task CompleteUnlockAsync(VaultUnlockResult result)
    {
        var version = LifecycleVersion;
        if (!IsCurrentUnlock(version)) return;
        StatusMessage = result.Message;
        IsStatusOpen = !string.IsNullOrWhiteSpace(result.Message);
        await Vault.RefreshAsync();
    }

    private void ShowMappedError(Exception exception)
    {
        if (exception is OperationCanceledException) return;
        StatusMessage = errorMapper.Map(exception);
        IsStatusOpen = true;
    }

    private void ClearAuthenticationStatus()
    {
        StatusMessage = string.Empty;
        IsStatusOpen = false;
    }

    private static string FormatInspection(VaultBackupInspection inspection) =>
        $"{inspection.Format} v{inspection.Version} · {inspection.TotalItemCount:N0} items · " +
        $"{inspection.ActiveItemCount:N0} active · {inspection.TrashItemCount:N0} in Trash";

}
