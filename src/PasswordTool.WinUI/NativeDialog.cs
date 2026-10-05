using System.Runtime.InteropServices;

namespace PasswordTool_WinUI;

internal static class NativeDialog
{
    private const uint MbOk = 0x00000000;
    private const uint MbIconInformation = 0x00000040;
    private const uint MbIconError = 0x00000010;

    public static void ShowInformation(string message) =>
        MessageBox(0, message, "YourSafe", MbOk | MbIconInformation);

    public static void ShowFatalError() =>
        MessageBox(0, "YourSafe encountered an unexpected error and locked the vault.", "YourSafe", MbOk | MbIconError);

    [DllImport("user32.dll", EntryPoint = "MessageBoxW", CharSet = CharSet.Unicode)]
    private static extern int MessageBox(nint windowHandle, string text, string caption, uint type);
}
