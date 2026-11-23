using Microsoft.Win32;
using PasswordTool_WinUI;

namespace PasswordTool.Presentation.Tests;

public sealed class WindowsStartupRegistrationTests
{
    [Theory]
    [InlineData(null, WindowsStartupState.NotInstalled)]
    [InlineData(@"C:\Portable\YourSafe.exe", WindowsStartupState.NotInstalled)]
    [InlineData(@"C:\Program Files\YourSafe\YourSafe.exe", WindowsStartupState.Off)]
    public void Startup_registration_is_opt_in_and_scoped_to_the_installed_executable(string? installedPath, object expectedState)
    {
        if (!OperatingSystem.IsWindows()) return;
        const string executable = @"C:\Program Files\YourSafe\YourSafe.exe";
        const string foreignCommand = "\"D:\\Other\\YourSafe.exe\" --windows-startup";
        var key = $@"Software\YourSafeStartupTest-{Guid.NewGuid():N}";
        using var user = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
        try
        {
            using var install = user.CreateSubKey(key);
            if (installedPath is not null) install.SetValue("InstalledExecutable", installedPath);
            using var run = install.CreateSubKey("Run");
            var registration = new WindowsStartupRegistration(executable, key, $@"{key}\Run");

            Assert.Equal(expectedState, registration.Read());
            if (Equals(expectedState, WindowsStartupState.Off))
            {
                registration.SetEnabled(true);
                Assert.Equal($"\"{executable}\" --windows-startup", run.GetValue("YourSafe"));
                Assert.Equal(RegistryValueKind.String, run.GetValueKind("YourSafe"));
                Assert.Equal(WindowsStartupState.On, registration.Read());
                run.SetValue("OtherApp", "keep");
                registration.SetEnabled(false);
                Assert.Equal(WindowsStartupState.Off, registration.Read());
                Assert.Null(run.GetValue("YourSafe"));
                Assert.Equal("keep", run.GetValue("OtherApp"));
            }

            run.SetValue("YourSafe", foreignCommand);
            Assert.Throws<InvalidOperationException>(() => { if (OperatingSystem.IsWindows()) registration.SetEnabled(true); });
            Assert.Throws<InvalidOperationException>(() => { if (OperatingSystem.IsWindows()) registration.SetEnabled(false); });
            Assert.Equal(foreignCommand, run.GetValue("YourSafe"));
        }
        finally { user.DeleteSubKeyTree(key, throwOnMissingSubKey: false); }
    }
}
