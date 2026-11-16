using CommunityToolkit.Mvvm.ComponentModel;
using PasswordTool.Core.Models;
using PasswordTool.Core.Services;

namespace PasswordTool.Presentation;

public sealed partial class SettingsViewModel(AppFlowCoordinator flow, IUserErrorMapper errorMapper) : ObservableObject
{
    [ObservableProperty] public partial double VaultDurationMinutes { get; set; } = VaultSecuritySettings.DefaultVaultOpenDurationMinutes;
    [ObservableProperty] public partial bool NeedsKdfUpgrade { get; set; }
    [ObservableProperty] public partial string BackupHealthText { get; set; } = string.Empty;
    [ObservableProperty] public partial string StatusMessage { get; set; } = string.Empty;
    [ObservableProperty] public partial bool IsStatusOpen { get; set; }
    [ObservableProperty] public partial bool IsBusy { get; set; }
    [ObservableProperty] public partial string SecuritySettingsPasswordError { get; set; } = string.Empty;
    [ObservableProperty] public partial string CurrentMasterPasswordError { get; set; } = string.Empty;
    [ObservableProperty] public partial string NewMasterPasswordError { get; set; } = string.Empty;
    [ObservableProperty] public partial string ConfirmMasterPasswordError { get; set; } = string.Empty;
    [ObservableProperty] public partial string AuthenticatorMasterPasswordError { get; set; } = string.Empty;
    [ObservableProperty] public partial string AuthenticatorCodeError { get; set; } = string.Empty;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BrowserIntegrationDescription))]
    public partial bool BrowserIntegrationEnabled { get; set; }

    public string BrowserIntegrationDescription => BrowserIntegrationEnabled
        ? "Browser integration is on. Matching account names and usernames are shared with your extension. Passwords and verification codes require your approval each time."
        : "Browser integration is off. Your browser cannot access saved accounts. Turn it on to enable autofill and verification codes.";

    partial void OnBrowserIntegrationEnabledChanged(bool value)
    {
        try { flow.SetBrowserIntegrationEnabled(value); }
        catch (OperationCanceledException)
        {
            BrowserIntegrationEnabled = false;
            ShowError("Unlock the vault before enabling browser integration.");
        }
    }

    public async Task LoadAsync()
    {
        IsBusy = true;
        var version = flow.LifecycleVersion;
        try
        {
            var snapshot = await flow.GetSettingsAsync();
            if (!flow.IsCurrentUnlock(version)) return;
            VaultDurationMinutes = snapshot.VaultDurationMinutes;
            BrowserIntegrationEnabled = flow.BrowserIntegrationEnabled;
            NeedsKdfUpgrade = snapshot.NeedsKdfUpgrade;
            BackupHealthText = $"Last external backup: {FormatDate(snapshot.LastExternalBackupAt)} · " +
                $"Last verified: {FormatDate(snapshot.LastVerifiedBackupAt)}";
        }
        catch (Exception exception)
        {
            if (flow.IsCurrentUnlock(version) && exception is not OperationCanceledException) ShowError(errorMapper.Map(exception));
        }
        finally
        {
            if (flow.IsCurrentUnlock(version)) IsBusy = false;
        }
    }

    public async Task<bool> SaveAsync(string masterPassword)
    {
        if (IsBusy) return false;
        SecuritySettingsPasswordError = string.Empty;
        var durationMinutes = (int)VaultDurationMinutes;
        return await CompleteAsync(async () =>
        {
            if (!await ValidatePasswordAsync(masterPassword, message => SecuritySettingsPasswordError = message)) return null;
            return await flow.UpdateSettingsAsync(
                masterPassword,
                durationMinutes);
        }, "Security settings saved.");
    }

    public async Task<bool> ChangeMasterPasswordAsync(string currentPassword, string newPassword, string confirmation)
    {
        if (IsBusy) return false;
        IsStatusOpen = false;
        CurrentMasterPasswordError = NewMasterPasswordError = ConfirmMasterPasswordError = string.Empty;
        try { MasterPasswordService.ValidateNewMasterPassword(newPassword); }
        catch (ArgumentException)
        {
            NewMasterPasswordError = "Use a Master Password between 12 and 4096 characters.";
            return false;
        }
        if (!string.Equals(newPassword, confirmation, StringComparison.Ordinal))
        {
            ConfirmMasterPasswordError = "The new Master Passwords do not match.";
            return false;
        }

        return await CompleteAsync(async () =>
        {
            if (!await ValidatePasswordAsync(currentPassword, message => CurrentMasterPasswordError = message)) return null;
            return await flow.ChangeMasterPasswordAsync(currentPassword, newPassword);
        }, "Master Password changed.");
    }

    public async Task<bool> UpgradeKdfAsync(string masterPassword)
    {
        if (IsBusy) return false;
        CurrentMasterPasswordError = string.Empty;
        var success = await CompleteAsync(async () =>
        {
            if (!await ValidatePasswordAsync(masterPassword, message => CurrentMasterPasswordError = message)) return null;
            return await flow.UpgradeKdfAsync(masterPassword);
        }, "Your password protection is up to date.");
        if (success) NeedsKdfUpgrade = false;
        return success;
    }

    public AuthenticatorSetup PrepareAuthenticator() => flow.CreateAuthenticatorSetup();

    public async Task<bool> ResetAuthenticatorAsync(string masterPassword, AuthenticatorSetup setup, string code)
    {
        if (IsBusy) return false;
        AuthenticatorMasterPasswordError = AuthenticatorCodeError = string.Empty;
        return await CompleteAsync(async () =>
        {
            if (!await ValidatePasswordAsync(masterPassword, message => AuthenticatorMasterPasswordError = message)) return null;
            if (!await flow.ValidateAuthenticatorSetupAsync(setup, code))
            {
                AuthenticatorCodeError = "The Authenticator code is incorrect or has expired. Enter the current 6-digit code.";
                return null;
            }
            return await flow.ResetAuthenticatorAsync(masterPassword, setup, code);
        }, "New Authenticator verified and saved.");
    }

    private async Task<bool> ValidatePasswordAsync(string password, Action<string> setError)
    {
        var version = flow.LifecycleVersion;
        if (await flow.ValidateMasterPasswordAsync(password)) return true;
        if (flow.IsCurrentUnlock(version)) setError("The Master Password is incorrect.");
        return false;
    }

    private async Task<bool> CompleteAsync(Func<Task<OperationResult?>> operation, string successMessage)
    {
        if (IsBusy) return false;
        IsBusy = true;
        var version = flow.LifecycleVersion;
        IsStatusOpen = false;
        try
        {
            var result = await operation();
            if (result is null || !flow.IsCurrentUnlock(version)) return false;
            StatusMessage = result.Success ? successMessage : result.Message;
            IsStatusOpen = true;
            return result.Success;
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception exception)
        {
            if (flow.IsCurrentUnlock(version)) ShowError(errorMapper.Map(exception));
            return false;
        }
        finally { if (version == flow.LifecycleVersion) IsBusy = false; }
    }

    private void ShowError(string message)
    {
        StatusMessage = message;
        IsStatusOpen = true;
    }

    private static string FormatDate(DateTimeOffset? value) => value?.ToLocalTime().ToString("g") ?? "not available";

    public void Clear()
    {
        BrowserIntegrationEnabled = flow.BrowserIntegrationEnabled;
        BackupHealthText = StatusMessage = string.Empty;
        IsStatusOpen = false;
        IsBusy = false;
        SecuritySettingsPasswordError = CurrentMasterPasswordError = NewMasterPasswordError = ConfirmMasterPasswordError = string.Empty;
        AuthenticatorMasterPasswordError = AuthenticatorCodeError = string.Empty;
    }
}
