using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PasswordTool.Presentation;

namespace PasswordTool_WinUI;

internal sealed class NavigationDialogService(ISensitiveClipboardService clipboard, DialogLifetime lifetime) : IUserDialogService
{
    public async Task<string?> PromptMasterPasswordAsync(CancellationToken cancellationToken = default)
    {
        var password = new PasswordBox { Header = "Master Password" };
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(new TextBlock { Text = "Your Login is still active. Confirm your Master Password for this action, or use Unlock Vault to stop repeated prompts.", TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(password);
        var dialog = new ContentDialog { Title = "Vault table is locked", Content = panel, PrimaryButtonText = "Confirm", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary };
        try { return await lifetime.ShowAsync(dialog, cancellationToken) == ContentDialogResult.Primary ? password.Password : null; }
        finally { password.Password = string.Empty; }
    }
    public async Task<(string Confirmation, string TotpCode)?> ConfirmGroupDeletionAsync(
        string groupName, CancellationToken cancellationToken = default)
    {
        var expected = $"Confirm delete all data in \"{groupName}\"";
        var confirmation = new TextBox { Header = "Type the exact confirmation below" };
        var dialog = new ContentDialog
        {
            Title = "Delete group and all its data",
            Content = new StackPanel
            {
                Spacing = 12,
                Children =
                {
                    new TextBlock
                    {
                        Text = $"Permanently delete \"{groupName}\" and ALL its passwords, recovery codes, notes, and other saved data, including items in Trash? Review this group carefully. This cannot be undone in the vault.",
                        TextWrapping = TextWrapping.Wrap
                    },
                    new TextBlock { Text = expected, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true },
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
        await ShowDialogAsync(new ContentDialog
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
        var text = new TextBlock
        {
            Text = value,
            TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap,
            IsTextSelectionEnabled = false
        };
        var dialog = new ContentDialog
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
            await clipboard.CopyAsync(value, cancellationToken);
        };
        try { await ShowDialogAsync(dialog, cancellationToken); }
        finally { text.Text = string.Empty; value = string.Empty; }
    }

    private async Task<bool> ShowConfirmAsync(
        string title,
        string message,
        string confirmText,
        CancellationToken cancellationToken)
    {
        var result = await ShowDialogAsync(new ContentDialog
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
        var result = await ShowDialogAsync(new ContentDialog
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

    private async Task<string?> PromptTotpDialogAsync(string title, string message, CancellationToken cancellationToken)
    {
        var input = new SixDigitCodeInput();
        var dialog = new ContentDialog
        {
            Title = title,
            Content = new StackPanel
            {
                Spacing = 12,
                Children =
                {
                    new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                    input
                }
            },
            PrimaryButtonText = "Continue",
            IsPrimaryButtonEnabled = false,
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary
        };
        input.CodeChanged += (_, _) => dialog.IsPrimaryButtonEnabled = input.Code.Length == 6;
        dialog.Loaded += (_, _) => input.FocusFirst();
        var result = await ShowDialogAsync(dialog, cancellationToken);
        var code = result == ContentDialogResult.Primary ? input.Code : null;
        input.Clear();
        return code;
    }

    private Task<ContentDialogResult> ShowDialogAsync(ContentDialog dialog, CancellationToken cancellationToken) =>
        lifetime.ShowAsync(dialog, cancellationToken);
}
