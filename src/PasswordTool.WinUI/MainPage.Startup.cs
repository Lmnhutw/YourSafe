using System.Security;
using Microsoft.UI.Xaml;

namespace PasswordTool_WinUI;

public sealed partial class MainPage
{
    private readonly WindowsStartupRegistration startupRegistration = WindowsStartupRegistration.ForCurrentProcess();
    private bool syncingStartupToggle;

    private void RefreshStartupSetting()
    {
        StartupError.Visibility = Visibility.Collapsed;
        syncingStartupToggle = true;
        try
        {
            var state = startupRegistration.Read();
            StartupToggle.IsOn = state == WindowsStartupState.On;
            StartupToggle.IsEnabled = state is WindowsStartupState.On or WindowsStartupState.Off;
            StartupDescription.Text = state switch
            {
                WindowsStartupState.NotInstalled => "Install YourSafe using the Windows installer to use this option.",
                WindowsStartupState.OtherEntry => "A different YourSafe startup entry already exists. Manage it in Windows Startup Apps.",
                _ => "Automatically opens YourSafe when you sign in to Windows."
            };
        }
        catch (Exception error) when (error is IOException or SecurityException or UnauthorizedAccessException)
        {
            StartupToggle.IsEnabled = false;
            StartupDescription.Text = "Couldn’t read the Windows startup setting.";
        }
        finally { syncingStartupToggle = false; }
    }

    private void StartupToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (syncingStartupToggle || !StartupToggle.IsEnabled) return;
        string? message = null;
        try { startupRegistration.SetEnabled(StartupToggle.IsOn); }
        catch (InvalidOperationException error) { message = error.Message; }
        catch (Exception error) when (error is IOException or SecurityException or UnauthorizedAccessException)
        {
            message = "Couldn’t change the Windows startup setting.";
        }

        RefreshStartupSetting();
        if (message is null) return;
        StartupError.Text = message;
        StartupError.Visibility = Visibility.Visible;
    }
}
