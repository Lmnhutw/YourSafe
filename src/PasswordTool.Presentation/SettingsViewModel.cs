using CommunityToolkit.Mvvm.ComponentModel;
using PasswordTool.Core.Models;

namespace PasswordTool.Presentation;

public sealed partial class SettingsViewModel(AppFlowCoordinator flow, IUserErrorMapper errorMapper) : ObservableObject
{
    [ObservableProperty] public partial double VaultDurationMinutes { get; set; } = VaultSecuritySettings.DefaultVaultOpenDurationMinutes;
    [ObservableProperty] public partial bool NeedsKdfUpgrade { get; set; }
    [ObservableProperty] public partial string BackupHealthText { get; set; } = string.Empty;
    [ObservableProperty] public partial string StatusMessage { get; set; } = string.Empty;
    [ObservableProperty] public partial bool IsStatusOpen { get; set; }
    [ObservableProperty] public partial bool IsBusy { get; set; }
    [ObservableProperty] public partial bool BrowserIntegrationEnabled { get; set; }

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
        return await CompleteAsync(flow.UpdateSettingsAsync(
            masterPassword,
            (int)VaultDurationMinutes), "Security settings saved.");
    }

    public async Task<bool> ChangeMasterPasswordAsync(string currentPassword, string newPassword, string confirmation)
    {
        if (!string.Equals(newPassword, confirmation, StringComparison.Ordinal))
        {
            ShowError("The new Master Passwords do not match.");
            return false;
        }

        return await CompleteAsync(flow.ChangeMasterPasswordAsync(currentPassword, newPassword), "Master Password changed.");
    }

    public async Task<bool> UpgradeKdfAsync(string masterPassword)
    {
        var success = await CompleteAsync(flow.UpgradeKdfAsync(masterPassword), "Your password protection is up to date.");
        if (success) NeedsKdfUpgrade = false;
        return success;
    }

    public AuthenticatorSetup PrepareAuthenticator() => flow.CreateAuthenticatorSetup();

    public async Task<bool> ResetAuthenticatorAsync(string masterPassword, AuthenticatorSetup setup, string code)
    {
        return await CompleteAsync(flow.ResetAuthenticatorAsync(masterPassword, setup, code), "New Authenticator verified and saved.");
    }

    private async Task<bool> CompleteAsync(Task<OperationResult> operation, string successMessage)
    {
        var version = flow.LifecycleVersion;
        OperationResult result;
        try { result = await operation; }
        catch (OperationCanceledException) { return false; }
        catch (Exception exception) { if (flow.IsCurrentUnlock(version)) ShowError(errorMapper.Map(exception)); return false; }
        if (!flow.IsCurrentUnlock(version)) return false;
        StatusMessage = result.Success ? successMessage : result.Message;
        IsStatusOpen = true;
        return result.Success;
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
    }
}
