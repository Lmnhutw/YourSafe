using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Extensions.DependencyInjection;

namespace PasswordTool_WinUI;

internal sealed class DialogLifetime
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private ContentDialog? active;
    private int generation;

    public int Generation => Volatile.Read(ref generation);
    public bool IsCurrent(int expectedGeneration) => expectedGeneration == Generation;

    public void DismissAll()
    {
        Interlocked.Increment(ref generation);
        active?.Hide();
    }

    public async Task<ContentDialogResult> ShowAsync(ContentDialog dialog, CancellationToken cancellationToken, Func<bool>? canShow = null)
    {
        var requestedGeneration = Generation;
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (cancellationToken.IsCancellationRequested || !IsCurrent(requestedGeneration)
                || canShow is not null && !canShow()) return ContentDialogResult.None;
            active = dialog;
            dialog.XamlRoot = ((FrameworkElement)App.Window.Content).XamlRoot;
            App.Services.GetRequiredService<AppearanceService>().ApplyTo(dialog);
            using var registration = cancellationToken.Register(() =>
                App.DispatcherQueue.TryEnqueue(() => { if (active == dialog) dialog.Hide(); }));
            var result = await dialog.ShowAsync();
            return cancellationToken.IsCancellationRequested || !IsCurrent(requestedGeneration)
                || canShow is not null && !canShow()
                ? ContentDialogResult.None : result;
        }
        finally
        {
            active = null;
            App.Services.GetRequiredService<AppearanceService>().Release(dialog);
            dialog.Content = null;
            gate.Release();
        }
    }
}
