using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Automation;
using PasswordTool.Presentation;

namespace PasswordTool_WinUI;

internal sealed class NavigationDialogService(ISensitiveClipboardService clipboard, DialogLifetime lifetime, AppFlowCoordinator flow) : IUserDialogService
{
    public Task<string?> PromptMasterPasswordAsync(CancellationToken cancellationToken = default) =>
        PromptMasterPasswordAsync("Unlock vault", "Unlock vault", cancellationToken);

    public async Task<string?> PromptMasterPasswordAsync(string title, string confirmText, CancellationToken cancellationToken = default)
    {
        var version = flow.LifecycleVersion;
        var password = new PasswordBox { Header = "Master Password" };
        var error = InlineValidation.CreateError("TxtUnlockMasterPasswordError");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(password, "TxtUnlockMasterPassword");
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(new TextBlock
        {
            Text = "Enter your Master Password to continue. Unsaved changes to an item are cleared when the vault locks.",
            TextWrapping = TextWrapping.Wrap
        });
        panel.Children.Add(password);
        panel.Children.Add(error);
        var dialog = new AppContentDialog { Title = title, Content = panel, PrimaryButtonText = confirmText, CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary };
        password.PasswordChanged += (_, _) => InlineValidation.ClearError(password, error);
        InlineValidation.AttachAsyncSubmit(dialog, password, error, async token =>
            new OperationResult(await flow.ValidateMasterPasswordAsync(password.Password, token),
                "The Master Password is incorrect. Please try again."), cancellationToken);
        dialog.Opened += (_, _) => password.Focus(FocusState.Programmatic);
        try { return await lifetime.ShowAsync(dialog, cancellationToken, () => flow.IsCurrentUnlock(version)) == ContentDialogResult.Primary ? password.Password : null; }
        finally { password.Password = string.Empty; }
    }
    public async Task<(string Confirmation, string TotpCode)?> ConfirmGroupDeletionAsync(
        string groupName, CancellationToken cancellationToken = default)
    {
        var expected = $"delete {groupName}";
        var instruction = new TextBlock { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
        instruction.Inlines.Add(new Run { Text = "Type " });
        var phrase = new Bold();
        phrase.Inlines.Add(new Run { Text = expected });
        instruction.Inlines.Add(phrase);
        instruction.Inlines.Add(new Run { Text = " to delete the group" });
        AutomationProperties.SetName(instruction, $"Type {expected} to delete the group");
        var confirmation = new TextBox();
        AutomationProperties.SetAutomationId(confirmation, "TxtGroupDeletionConfirmation");
        AutomationProperties.SetName(confirmation, "Group deletion confirmation");
        AutomationProperties.SetHelpText(confirmation, $"Type {expected} to delete the group");
        var dialog = new AppContentDialog
        {
            Title = "Delete group and all its data",
            Content = new StackPanel
            {
                Spacing = 12,
                Children =
                {
                    new TextBlock
                    {
                        Text = $"Permanently delete \"{groupName}\" and all its saved items, including passwords, verification codes, recovery codes, notes, and items in Trash? This cannot be undone.",
                        TextWrapping = TextWrapping.Wrap
                    },
                    instruction,
                    confirmation
                }
            },
            PrimaryButtonText = "Delete all data",
            IsPrimaryButtonEnabled = false,
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close
        };
        void UpdateConfirmation() => dialog.IsPrimaryButtonEnabled =
            string.Equals(confirmation.Text, expected, StringComparison.Ordinal);
        confirmation.TextChanged += (_, _) => UpdateConfirmation();
        try
        {
            return await ShowDialogAsync(dialog, cancellationToken) == ContentDialogResult.Primary
                ? (confirmation.Text, string.Empty) : null;
        }
        finally { confirmation.Text = string.Empty; }
    }

    public Task<bool> ConfirmAsync(
        string title,
        string message,
        string confirmText,
        CancellationToken cancellationToken = default) =>
        ShowConfirmAsync(title, message, confirmText, cancellationToken);

    public async Task ShowErrorAsync(string title, string message, CancellationToken cancellationToken = default)
    {
        await ShowDialogAsync(new AppContentDialog
        {
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            CloseButtonText = "Close",
            DefaultButton = ContentDialogButton.Close
        }, cancellationToken);
    }

    public Task<string?> PromptTotpAsync(
        string title,
        string message,
        CancellationToken cancellationToken = default) =>
        PromptTotpDialogAsync(title, message, cancellationToken);

    public Task<string?> PromptTotpAsync(string title, string message,
        Func<string, CancellationToken, Task<OperationResult>> submit, CancellationToken cancellationToken = default) =>
        PromptTotpDialogAsync(title, message, cancellationToken, submit);

    public Task<string?> PromptBackupPassphraseAsync(
        string title,
        string message,
        CancellationToken cancellationToken = default) =>
        PromptSecretAsync(title, message, "Backup password", cancellationToken);

    public async Task ShowSecretAsync(
        string title,
        string value,
        bool multiline,
        CancellationToken cancellationToken = default)
    {
        var version = flow.LifecycleVersion;
        if (!flow.IsCurrentUnlock(version)) return;
        var generation = lifetime.Generation;
        using var copyLifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var text = new TextBlock
        {
            Text = value,
            TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap,
            IsTextSelectionEnabled = false
        };
        var dialog = new AppContentDialog
        {
            Title = title,
            Content = new ScrollViewer { Content = text, MaxHeight = 360 },
            PrimaryButtonText = "Copy securely",
            CloseButtonText = "Close",
            DefaultButton = ContentDialogButton.Close
        };
        dialog.PrimaryButtonClick += async (_, args) =>
        {
            args.Cancel = true;
            dialog.PrimaryButtonText = "Copy securely";
            try
            {
                await clipboard.CopyAsync(value,
                    () => flow.IsCurrentUnlock(version) && lifetime.IsCurrent(generation) && !copyLifetime.IsCancellationRequested,
                    copyLifetime.Token);
            }
            catch (OperationCanceledException) { }
            catch (InvalidOperationException)
            {
                if (!copyLifetime.IsCancellationRequested && lifetime.IsCurrent(generation))
                    dialog.PrimaryButtonText = "Copy failed — try again";
            }
        };
        dialog.Closed += (_, _) => copyLifetime.Cancel();
        try { await lifetime.ShowAsync(dialog, cancellationToken, () => flow.IsCurrentUnlock(version)); }
        finally { copyLifetime.Cancel(); text.Text = string.Empty; value = string.Empty; }
    }

    private async Task<bool> ShowConfirmAsync(
        string title,
        string message,
        string confirmText,
        CancellationToken cancellationToken)
    {
        var result = await ShowDialogAsync(new AppContentDialog
        {
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = confirmText,
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close
        }, cancellationToken);
        return result == ContentDialogResult.Primary;
    }

    private async Task<string?> PromptSecretAsync(
        string title,
        string message,
        string header,
        CancellationToken cancellationToken)
    {
        var input = new PasswordBox { Header = header };
        var content = new StackPanel { Spacing = 12 };
        content.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap });
        content.Children.Add(input);
        var result = await ShowDialogAsync(new AppContentDialog
        {
            Title = title,
            Content = content,
            PrimaryButtonText = "Continue",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary
        }, cancellationToken);
        var value = result == ContentDialogResult.Primary ? input.Password : null;
        input.Password = string.Empty;
        return value;
    }

    private async Task<string?> PromptTotpDialogAsync(string title, string message, CancellationToken cancellationToken,
        Func<string, CancellationToken, Task<OperationResult>>? submit = null)
    {
        var input = new SixDigitCodeInput();
        var error = InlineValidation.CreateError("TxtAuthenticatorCodeError");
        var dialog = new AppContentDialog
        {
            Title = title,
            Content = new StackPanel
            {
                Spacing = 12,
                Children =
                {
                    new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                    input,
                    error
                }
            },
            PrimaryButtonText = "Continue",
            IsPrimaryButtonEnabled = false,
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary
        };
        input.CodeChanged += (_, _) =>
        {
            dialog.IsPrimaryButtonEnabled = input.Code.Length == 6;
            InlineValidation.ClearError(input, error);
        };
        if (submit is not null)
            InlineValidation.AttachAsyncSubmit(dialog, input, error, token => submit(input.Code, token), cancellationToken);
        dialog.Loaded += (_, _) => input.FocusFirst();
        try
        {
            var result = await ShowDialogAsync(dialog, cancellationToken);
            return result == ContentDialogResult.Primary ? input.Code : null;
        }
        finally { input.Clear(); }
    }

    private Task<ContentDialogResult> ShowDialogAsync(ContentDialog dialog, CancellationToken cancellationToken) =>
        lifetime.ShowAsync(dialog, cancellationToken);
}
