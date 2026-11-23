using System.Runtime.Versioning;
using Microsoft.Win32;

namespace PasswordTool_WinUI;

internal enum WindowsStartupState { NotInstalled, Off, On, OtherEntry }

[SupportedOSPlatform("windows")]
internal sealed class WindowsStartupRegistration(string? executablePath, string installKeyPath, string runKeyPath)
{
    internal const string LaunchArgument = "--windows-startup";
    private const string InstallKey = @"Software\Lmnhutw\YourSafe";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "YourSafe";
    private readonly string command = $"\"{executablePath}\" {LaunchArgument}";

    internal static WindowsStartupRegistration ForCurrentProcess()
    {
#if DEBUG
        if (Environment.GetEnvironmentVariable("PASSWORDTOOL_UI_TEST_DIRECTORY") is { Length: > 0 } directory)
        {
            // Disposable UI fixtures must never write to Windows Startup Apps.
            var marker = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(directory))));
            var fixtureKey = $@"{InstallKey}\UiTests\{marker}";
            return new(Environment.ProcessPath, fixtureKey, $@"{fixtureKey}\Run");
        }
#endif
        return new(Environment.ProcessPath, InstallKey, RunKey);
    }

    internal WindowsStartupState Read()
    {
        using var user = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
        return Read(user);
    }

    internal void SetEnabled(bool enabled)
    {
        using var user = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
        var state = Read(user);
        if (state is not (WindowsStartupState.Off or WindowsStartupState.On))
            throw new InvalidOperationException("The current installation cannot change the startup entry.");
        if (enabled == (state == WindowsStartupState.On)) return;

        if (enabled)
        {
            // Windows limits Run value command lines to 260 characters.
            if (command.Length > 260) throw new InvalidOperationException("The installation path is too long for Windows startup.");
            using var run = user.CreateSubKey(runKeyPath);
            run.SetValue(ValueName, command, RegistryValueKind.String);
        }
        else
        {
            using var run = user.OpenSubKey(runKeyPath, writable: true);
            run?.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }

    private WindowsStartupState Read(RegistryKey user)
    {
        using var install = user.OpenSubKey(installKeyPath);
        if (executablePath is null
            || install?.GetValue("InstalledExecutable", null, RegistryValueOptions.DoNotExpandEnvironmentNames) is not string installed
            || !string.Equals(installed, executablePath, StringComparison.OrdinalIgnoreCase))
            return WindowsStartupState.NotInstalled;

        using var run = user.OpenSubKey(runKeyPath);
        var entry = run?.GetValue(ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        if (entry is null) return WindowsStartupState.Off;
        return entry is string registered && string.Equals(registered, command, StringComparison.OrdinalIgnoreCase)
            ? WindowsStartupState.On : WindowsStartupState.OtherEntry;
    }
}
