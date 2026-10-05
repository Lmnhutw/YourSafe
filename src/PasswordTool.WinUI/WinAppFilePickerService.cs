using Microsoft.Windows.Storage.Pickers;
using PasswordTool.Presentation;

namespace PasswordTool_WinUI;

internal sealed class WinAppFilePickerService : IFilePickerService
{
    public async Task<string?> PickOpenPathAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var picker = new FileOpenPicker(App.Window.AppWindow.Id);
        picker.FileTypeFilter.Add("*");
        var result = await picker.PickSingleFileAsync();
        cancellationToken.ThrowIfCancellationRequested();
        return result?.Path;
    }

    public async Task<string?> PickSavePathAsync(
        string suggestedFileName,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var picker = new FileSavePicker(App.Window.AppWindow.Id)
        {
            SuggestedFileName = suggestedFileName
        };
        var extension = Path.GetExtension(suggestedFileName);
        picker.FileTypeChoices.Add("YourSafe data", [string.Equals(extension, ".csv", StringComparison.OrdinalIgnoreCase) ? ".csv" : ".json"]);
        var result = await picker.PickSaveFileAsync();
        cancellationToken.ThrowIfCancellationRequested();
        return result?.Path;
    }
}
