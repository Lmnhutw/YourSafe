using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.Extensions.DependencyInjection;
using PasswordTool.Presentation;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace PasswordTool_WinUI;

/// <summary>
/// Provides application-specific behavior to supplement the default Application class.
/// </summary>
public partial class App : Application
{
    private SingleInstanceGuard? singleInstance;
    private int fatalErrorHandling;
    private AutofillPipeServer? autofillServer;
    /// <summary>
    /// The main application window. Use <c>App.Window</c> from any class that needs
    /// the window reference (for dialogs, pickers, interop, etc.).
    /// </summary>
    public static Window Window { get; private set; } = null!;

    /// <summary>
    /// The UI thread dispatcher. Use <c>App.DispatcherQueue</c> to marshal calls
    /// to the UI thread. Fully qualified to avoid CS0104 ambiguity with
    /// <see cref="Windows.System.DispatcherQueue"/>.
    /// </summary>
    public static Microsoft.UI.Dispatching.DispatcherQueue DispatcherQueue { get; private set; } = null!;
    public static ServiceProvider Services { get; private set; } = null!;


    /// <summary>
    /// The native window handle (HWND). Use for file pickers,
    /// <c>DataTransferManager</c>, and any WinRT interop that requires
    /// <c>InitializeWithWindow</c>.
    /// </summary>
    public static nint WindowHandle =>
        WinRT.Interop.WindowNative.GetWindowHandle(Window);

    /// <summary>
    /// Initializes the singleton application object.
    /// </summary>
    public App()
    {
        InitializeComponent();
        UnhandledException += App_UnhandledException;
        TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;
    }

    /// <summary>
    /// Invoked when the application is launched.
    /// </summary>
    /// <param name="args">Details about the launch request and process.</param>
    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        singleInstance = SingleInstanceGuard.TryAcquire(out var acquired);
        if (!acquired)
        {
            NativeDialog.ShowInformation("YourSafe is already running.");
            singleInstance.Dispose();
            singleInstance = null;
            Exit();
            return;
        }

        Services = ServiceRegistration.Build();
        DispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        Window = new MainWindow();
        Window.Closed += Window_Closed;
        Window.Activate();
        var flow = Services.GetRequiredService<AppFlowCoordinator>();
        flow.RequestAutofillApprovalAsync = Services.GetRequiredService<AutofillApprovalService>().ApproveAsync;
        autofillServer = new AutofillPipeServer(flow);
    }

    private async void App_UnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        await HandleFatalErrorAsync();
    }

    private void TaskScheduler_UnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        e.SetObserved();
        if (DispatcherQueue is not null) DispatcherQueue.TryEnqueue(async () => await HandleFatalErrorAsync());
    }

    private async Task HandleFatalErrorAsync()
    {
        if (Interlocked.Exchange(ref fatalErrorHandling, 1) != 0) return;
        try
        {
            if (Services is not null)
            {
                var logout = Services.GetRequiredService<AppFlowCoordinator>().LogoutAsync();
                try
                {
                    Services.GetRequiredService<DialogLifetime>().DismissAll();
                    await Services.GetRequiredService<ISensitiveClipboardService>().ClearOwnedValueAsync();
                }
                finally { await logout; }
            }
        }
        catch
        {
            // Fatal cleanup must not disclose or log the original exception or vault data.
        }

        NativeDialog.ShowFatalError();
        Exit();
    }

    private async void Window_Closed(object sender, WindowEventArgs args)
    {
        try
        {
            // Invalidate queued secret work before any asynchronous close/clipboard cleanup.
            var logout = Services.GetRequiredService<AppFlowCoordinator>().LogoutAsync();
            try
            {
                Services.GetRequiredService<DialogLifetime>().DismissAll();
                await Services.GetRequiredService<ISensitiveClipboardService>().ClearOwnedValueAsync();
            }
            finally
            {
                try { if (autofillServer is not null) await autofillServer.StopAsync(); }
                finally { autofillServer = null; await logout; }
            }
        }
        catch
        {
            // The window is closing; cleanup remains best-effort and secret-free.
        }
        finally
        {
            Services.Dispose();
            singleInstance?.Dispose();
            singleInstance = null;
        }
    }
}
