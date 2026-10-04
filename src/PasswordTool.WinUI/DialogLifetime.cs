using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Extensions.DependencyInjection;

namespace PasswordTool_WinUI;

internal sealed class DialogLifetime
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private ContentDialog? active;
    private int generation;

    public void DismissAll()
    {
        generation++;
        active?.Hide();
    }

    public async Task<ContentDialogResult> ShowAsync(ContentDialog dialog, CancellationToken cancellationToken)
    {
        var requestedGeneration = generation;
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (requestedGeneration != generation) return ContentDialogResult.None;
            active = dialog;
            dialog.XamlRoot = ((FrameworkElement)App.Window.Content).XamlRoot;
            App.Services.GetRequiredService<AppearanceService>().ApplyTo(dialog);
            using var registration = cancellationToken.Register(() =>
                App.DispatcherQueue.TryEnqueue(() => { if (active == dialog) dialog.Hide(); }));
            var result = await dialog.ShowAsync();
            return cancellationToken.IsCancellationRequested || requestedGeneration != generation
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
