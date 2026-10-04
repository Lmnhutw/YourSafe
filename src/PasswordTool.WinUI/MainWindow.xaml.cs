using Microsoft.UI.Xaml;
using Microsoft.UI.Windowing;
using Microsoft.Extensions.DependencyInjection;
using System.Runtime.InteropServices;
using Windows.Graphics;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace PasswordTool_WinUI;

/// <summary>
/// The application window. This hosts a Frame that displays pages. Add your
/// UI and logic to MainPage.xaml / MainPage.xaml.cs instead of here so you
/// can use Page features such as navigation events and the Loaded lifecycle.
/// </summary>
public sealed partial class MainWindow : Window
{
    private const int MinimumLogicalWidth = 480;
    private const int MinimumLogicalHeight = 400;
    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hwnd);

    public MainWindow()
    {
        InitializeComponent();
        App.Services.GetRequiredService<AppearanceService>().Attach((FrameworkElement)Content);

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        AppWindow.SetIcon(System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));

        ResizeToWorkArea(1280, 720);
        AppWindow.Changed += AppWindow_Changed;

        // Navigate the root frame to the main page on startup.
        RootFrame.Navigate(typeof(MainPage));
#if DEBUG
        if (Environment.GetEnvironmentVariable("PASSWORDTOOL_UI_TEST_DIRECTORY") is { Length: > 0 } testDirectory)
        {
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(AppTitleBar, "YourSafeDisposableUiTest");
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetHelpText(AppTitleBar,
                System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(testDirectory)));
            if (int.TryParse(Environment.GetEnvironmentVariable("PASSWORDTOOL_UI_TEST_WIDTH"), out var testWidth))
                ResizeToWorkArea(Math.Max(MinimumLogicalWidth, testWidth), 720);
            if (Enum.TryParse<ElementTheme>(Environment.GetEnvironmentVariable("PASSWORDTOOL_UI_TEST_THEME"), out var testTheme))
                ((FrameworkElement)Content).RequestedTheme = testTheme;
        }
#endif
    }

    private void ResizeToWorkArea(int logicalWidth, int logicalHeight)
    {
        var display = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest);
        var workArea = display.WorkArea;
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var scale = GetDpiForWindow(hwnd) / 96d;
        var width = Math.Min((int)(logicalWidth * scale), workArea.Width);
        var height = Math.Min((int)(logicalHeight * scale), workArea.Height);
        UpdateMinimumSize();
        AppWindow.MoveAndResize(new RectInt32(workArea.X + (workArea.Width - width) / 2,
            workArea.Y + (workArea.Height - height) / 2, width, height), display);
    }

    private void UpdateMinimumSize()
    {
        if (AppWindow.Presenter is not OverlappedPresenter presenter) return;
        var workArea = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
        var scale = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96d;
        // Leave half-screen Snap available even on small displays at high scaling.
        var width = Math.Min((int)(MinimumLogicalWidth * scale), Math.Max(1, workArea.Width / 2));
        var height = Math.Min((int)(MinimumLogicalHeight * scale), Math.Max(1, workArea.Height / 2));
        if (presenter.PreferredMinimumWidth != width) presenter.PreferredMinimumWidth = width;
        if (presenter.PreferredMinimumHeight != height) presenter.PreferredMinimumHeight = height;
    }

    private void AppWindow_Changed(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (args.DidPositionChange || args.DidSizeChange) UpdateMinimumSize();
    }
}
