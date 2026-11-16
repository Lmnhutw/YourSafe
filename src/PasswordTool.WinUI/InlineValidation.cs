using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using PasswordTool.Presentation;
using System.Runtime.CompilerServices;

namespace PasswordTool_WinUI;

internal static class InlineValidation
{
    private static readonly ConditionalWeakTable<Control, string> originalHelpText = new();
    public static TextBlock CreateError(string automationId)
    {
        var error = (TextBlock)XamlReader.Load("""
            <TextBlock xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                       FontSize="12" TextWrapping="Wrap" Visibility="Collapsed"
                       Foreground="{ThemeResource SystemFillColorCriticalBrush}" />
            """);
        AutomationProperties.SetAutomationId(error, automationId);
        AutomationProperties.SetLiveSetting(error, AutomationLiveSetting.Polite);
        return error;
    }

    public static void SetError(Control input, TextBlock error, string message, bool selectInput = true)
    {
        error.Text = message;
        error.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
        var hint = originalHelpText.GetValue(input, AutomationProperties.GetHelpText);
        AutomationProperties.SetHelpText(input, string.IsNullOrEmpty(message) ? hint : message);
        if (selectInput && message.Length > 0)
        {
            if (input.IsEnabled) SelectForRetry(input);
            else input.DispatcherQueue.TryEnqueue(() =>
            {
                if (input.IsLoaded && input.IsEnabled && error.Text == message && error.Visibility == Visibility.Visible)
                    SelectForRetry(input);
            });
        }
    }

    public static void ClearError(Control input, TextBlock error) => SetError(input, error, string.Empty, false);

    public static void SelectForRetry(Control input)
    {
        input.Focus(FocusState.Programmatic);
        switch (input)
        {
            case PasswordBox password: password.SelectAll(); break;
            case TextBox text: text.SelectAll(); break;
            case SixDigitCodeInput code: code.SelectForRetry(); break;
        }
    }

    public static void AttachAsyncSubmit(ContentDialog dialog, Control input, TextBlock error,
        Func<CancellationToken, Task<OperationResult>> submit, CancellationToken cancellationToken = default)
    {
        var lifetime = App.Services.GetRequiredService<DialogLifetime>();
        var generation = lifetime.Generation;
        var pending = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var submitting = false;
        var closed = false;
        dialog.Closed += (_, _) =>
        {
            closed = true;
            pending.Cancel();
            if (!submitting) pending.Dispose();
        };
        dialog.PrimaryButtonClick += async (_, args) =>
        {
            args.Cancel = true;
            if (submitting || closed || pending.IsCancellationRequested || !lifetime.IsCurrent(generation)) return;
            var deferral = args.GetDeferral();
            submitting = true;
            dialog.IsPrimaryButtonEnabled = false;
            input.IsEnabled = false;
            string? failure = null;
            try
            {
                var result = await submit(pending.Token);
                if (closed || pending.IsCancellationRequested || !lifetime.IsCurrent(generation)) return;
                args.Cancel = !result.Success;
                if (!result.Success && result.Message.Length > 0) failure = result.Message;
            }
            catch (OperationCanceledException) { }
            catch (Exception exception)
            {
                if (!closed && !pending.IsCancellationRequested && lifetime.IsCurrent(generation))
                    failure = App.Services.GetRequiredService<IUserErrorMapper>().Map(exception);
            }
            finally
            {
                submitting = false;
                if (!closed && !pending.IsCancellationRequested && lifetime.IsCurrent(generation))
                {
                    input.IsEnabled = true;
                    dialog.IsPrimaryButtonEnabled = true;
                    if (failure is not null) SetError(input, error, failure);
                }
                deferral.Complete();
                if (closed) pending.Dispose();
            }
        };
    }
}
