using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation;
using PasswordTool.Presentation.Autofill;

namespace PasswordTool_WinUI;

internal sealed class AutofillApprovalService(DialogLifetime dialogs)
{
    public Task<bool> ApproveAsync(AutofillApprovalRequest request, CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!App.DispatcherQueue.TryEnqueue(async () =>
        {
            if (cancellationToken.IsCancellationRequested) { completion.TrySetResult(false); return; }
            try
            {
                if (App.Window.AppWindow.Presenter is OverlappedPresenter presenter && presenter.State != OverlappedPresenterState.Restored)
                    presenter.Restore();
                App.Window.AppWindow.Show();
                App.Window.Activate();
                var action = request.Action switch
                {
                    AutofillAction.Password => "Share the saved password with the extension for one autofill.",
                    AutofillAction.ViewTotp => "Share the current verification code with the extension once.",
                    AutofillAction.CopyTotp => "Copy the current verification code to the clipboard once.",
                    _ => throw new InvalidOperationException()
                };
                var disclosure = request.Action == AutofillAction.CopyTotp
                    ? "Other applications may read the clipboard. YourSafe attempts to clear a still-owned copied value after 30 seconds."
                    : "The extension can read the password or code shared with it. A destination website can read a filled password.";
                var text = new TextBlock
                {
                    Text = $"Account: {request.Credential.Title}\nUsername: {request.Credential.Username}\nWebsite saved in vault: {request.Credential.Url}\n\n{action}\n\nWebsite reported by extension (not independently verified): {request.Origin}\n\n{disclosure}",
                    TextWrapping = TextWrapping.Wrap
                };
                AutomationProperties.SetAutomationId(text, "TxtAutofillApprovalDetails");
                var dialog = new AppContentDialog
                {
                    Title = "Approve browser request",
                    Content = text,
                    PrimaryButtonText = "Allow once",
                    CloseButtonText = "Cancel",
                    DefaultButton = ContentDialogButton.Close
                };
                completion.TrySetResult(await dialogs.ShowAsync(dialog, cancellationToken,
                    () => !cancellationToken.IsCancellationRequested) == ContentDialogResult.Primary
                    && !cancellationToken.IsCancellationRequested);
            }
            catch { completion.TrySetResult(false); }
        })) completion.TrySetResult(false);
        return completion.Task;
    }
}
