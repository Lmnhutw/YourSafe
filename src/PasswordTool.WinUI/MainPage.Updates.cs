using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using PasswordTool.Core.Services;

namespace PasswordTool_WinUI;

public sealed partial class MainPage
{
    private readonly DispatcherTimer updateCheckTimer = new() { Interval = TimeSpan.FromMinutes(15) };
    private string updatePreferencesPath = null!;
    private UpdateCheckPreferences updatePreferences = new();
    private CancellationTokenSource? updateCheckLifetime;
    private bool syncingDailyUpdateToggle;
    private bool updateCheckInProgress;
    private Version? offeredUpdate;

    private void InitializeUpdateChecks()
    {
        var installed = ReleaseUpdateCheck.InstalledVersion;
        if (installed == new Version(0, 0, 0))
        {
            InstalledVersionText.Text = "Development build";
            CheckUpdatesButton.IsEnabled = DailyUpdateToggle.IsEnabled = false;
            ShowUpdateCheckStatus("Update checks are available in versioned builds.");
            return;
        }

        InstalledVersionText.Text = $"Installed version: {installed}";
        updatePreferencesPath = Path.Combine(App.Services.GetRequiredService<VaultService>().StorageDirectory, "update-preferences.json");
        updateCheckTimer.Tick += async (_, _) => await CheckForUpdatesAsync(manual: false);
        Loaded += async (_, _) =>
        {
            updateCheckLifetime = new();
            updatePreferences = UpdateCheckPreferences.Load(updatePreferencesPath);
            SyncDailyUpdateToggle();
            await CheckForUpdatesAsync(manual: false);
        };
        Unloaded += (_, _) =>
        {
            updateCheckTimer.Stop();
            updateCheckLifetime?.Cancel();
            updateCheckLifetime?.Dispose();
            updateCheckLifetime = null;
        };
    }

    private void SyncDailyUpdateToggle()
    {
        syncingDailyUpdateToggle = true;
        DailyUpdateToggle.IsOn = updatePreferences.DailyCheckEnabled;
        syncingDailyUpdateToggle = false;
        if (updatePreferences.DailyCheckEnabled) updateCheckTimer.Start();
        else updateCheckTimer.Stop();
    }

    private async void DailyUpdateCheck_Toggled(object sender, RoutedEventArgs e)
    {
        if (syncingDailyUpdateToggle || updateCheckLifetime is null) return;
        var preferences = updatePreferences with { DailyCheckEnabled = DailyUpdateToggle.IsOn };
        try
        {
            preferences.Save(updatePreferencesPath);
            updatePreferences = preferences;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            ShowUpdateCheckStatus("Couldn’t save your update preference. Try again.");
            SyncDailyUpdateToggle();
            return;
        }

        SyncDailyUpdateToggle();
        await CheckForUpdatesAsync(manual: false);
    }

    private async void CheckForUpdates_Click(object sender, RoutedEventArgs e) => await CheckForUpdatesAsync(manual: true);

    private async Task CheckForUpdatesAsync(bool manual)
    {
        if (updateCheckLifetime is null || updateCheckInProgress) return;
        var now = DateTimeOffset.UtcNow;
        if (!manual && !updatePreferences.IsDailyCheckDue(now)) return;

        var cancellationToken = updateCheckLifetime.Token;
        updateCheckInProgress = true;
        CheckUpdatesButton.IsEnabled = false;
        if (manual) ShowUpdateCheckStatus("Checking for updates…");
        try
        {
            if (updatePreferences.DailyCheckEnabled)
            {
                updatePreferences = updatePreferences with { LastAttemptUtc = now };
                try { updatePreferences.Save(updatePreferencesPath); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    if (!manual) return; // Keep daily checks throttled across restarts.
                }
            }

            var (succeeded, newer) = await ReleaseUpdateCheck.CheckAsync(cancellationToken);
            if (cancellationToken.IsCancellationRequested || (!manual && !updatePreferences.DailyCheckEnabled)) return;
            if (!succeeded)
            {
                if (manual) ShowUpdateCheckStatus("Couldn’t check for updates. Check your connection and try again.");
                return;
            }
            if (newer is null)
            {
                offeredUpdate = null;
                UpdateAvailableInfoBar.IsOpen = false;
                SettingsUpdateLink.Visibility = Visibility.Collapsed;
                if (manual) ShowUpdateCheckStatus("You’re using the latest available version.");
                else UpdateCheckStatus.Visibility = Visibility.Collapsed;
                return;
            }

            SettingsUpdateLink.NavigateUri = UpdateBannerLink.NavigateUri = ReleaseUpdateCheck.ReleasePage(newer);
            SettingsUpdateLink.Visibility = Visibility.Visible;
            var message = $"YourSafe {newer} is available.";
            ShowUpdateCheckStatus(message);
            UpdateAvailableInfoBar.Title = message;
            if (manual || offeredUpdate != newer) UpdateAvailableInfoBar.IsOpen = true;
            offeredUpdate = newer;
        }
        finally
        {
            updateCheckInProgress = false;
            CheckUpdatesButton.IsEnabled = true;
        }
    }

    private void ShowUpdateCheckStatus(string message)
    {
        UpdateCheckStatus.Text = message;
        UpdateCheckStatus.Visibility = Visibility.Visible;
    }
}
