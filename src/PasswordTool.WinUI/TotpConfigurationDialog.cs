using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using PasswordTool.Core.Models;
using PasswordTool.Core.Services;
using PasswordTool.Presentation;

namespace PasswordTool_WinUI;

internal static class TotpConfigurationDialog
{
    public static async Task<TotpConfiguration?> ShowAsync(TotpConfiguration? existing, string title, string username)
    {
        var shell = App.Services.GetRequiredService<ShellViewModel>();
        var version = shell.LifecycleVersion;
        if (!shell.IsCurrentNormalUnlock(version)) return null;
        var parser = App.Services.GetRequiredService<TotpService>();
        var method = new RadioButtons { Header = "Input method", Items = { "Secret key", "otpauth URI" }, SelectedIndex = 0 };
        var secret = new PasswordBox { Header = "Secret key", Password = existing?.Secret ?? string.Empty, PasswordRevealMode = PasswordRevealMode.Peek, MaxLength = 4096 };
        var uri = new PasswordBox { Header = "otpauth URI", Visibility = Visibility.Collapsed, PasswordRevealMode = PasswordRevealMode.Peek, MaxLength = 4096 };
        var issuer = new TextBox { Header = "Issuer", Text = existing?.Issuer ?? title, MaxLength = 4096 };
        var account = new TextBox { Header = "Account", Text = existing?.AccountName ?? username, MaxLength = 4096 };
        var algorithm = new AppComboBox { Header = "Algorithm", ItemsSource = Enum.GetValues<TotpAlgorithm>(), SelectedItem = existing?.Algorithm ?? TotpAlgorithm.Sha1 };
        var digits = new AppComboBox { Header = "Digits", ItemsSource = new[] { 6, 8 }, SelectedItem = existing?.Digits ?? 6 };
        var period = new TextBox { Header = "Period (seconds)", Text = (existing?.Period ?? 30).ToString(CultureInfo.InvariantCulture), MaxLength = 10 };
        var advancedBody = new StackPanel { Spacing = 12, Children = { algorithm, digits, period } };
        var advanced = new Expander { Header = "Advanced", Content = advancedBody, HorizontalAlignment = HorizontalAlignment.Stretch };
        var manual = new StackPanel { Spacing = 12, Children = { issuer, account, advanced } };
        var review = new TextBlock { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
        var error = new InfoBar { Severity = InfoBarSeverity.Error, IsClosable = false };
        var body = new StackPanel { Spacing = 12, MinWidth = 320, Children = { method, secret, uri, manual, review, error } };
        var dialog = new AppContentDialog
        {
            Title = existing is null ? "Add TOTP" : "Edit TOTP",
            Content = new ScrollViewer { Content = body, MaxHeight = 520, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled },
            PrimaryButtonText = existing is null ? "Add to draft" : "Update draft", CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary
        };
        AutomationProperties.SetAutomationId(secret, "TxtTotpSecret");
        AutomationProperties.SetAutomationId(uri, "TxtTotpUri");
        AutomationProperties.SetName(secret, "TOTP secret key");
        AutomationProperties.SetName(uri, "TOTP otpauth URI");
        AutomationProperties.SetAutomationId(method, "TotpInputMethod");
        TotpConfiguration? parsed = null;
        bool Parse()
        {
            parsed = null;
            error.IsOpen = false;
            if (method.SelectedIndex == 1 && !uri.Password.TrimStart().StartsWith("otpauth://", StringComparison.OrdinalIgnoreCase))
            {
                error.Message = "Enter an otpauth://totp URI, or choose Secret key.";
                error.IsOpen = true;
                return false;
            }
            if (!parser.TryParseWebsiteConfiguration(method.SelectedIndex == 1 ? uri.Password : secret.Password, out var configuration, out var message))
            {
                error.Message = message;
                error.IsOpen = true;
                return false;
            }
            if (method.SelectedIndex == 0)
            {
                if (secret.Password.TrimStart().StartsWith("otpauth:", StringComparison.OrdinalIgnoreCase))
                {
                    error.Message = "Choose otpauth URI to review imported parameters.";
                    error.IsOpen = true;
                    return false;
                }
                if (!int.TryParse(period.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) || seconds <= 0
                    || algorithm.SelectedItem is not TotpAlgorithm selectedAlgorithm || digits.SelectedItem is not int selectedDigits)
                {
                    error.Message = "Choose an algorithm, 6 or 8 digits, and a positive whole-number period.";
                    error.IsOpen = true;
                    return false;
                }
                configuration = configuration! with { Issuer = issuer.Text.Trim(), AccountName = account.Text.Trim(), Algorithm = selectedAlgorithm, Digits = selectedDigits, Period = seconds };
            }
            parsed = configuration;
            review.Text = $"Issuer: {configuration!.Issuer}\nAccount: {configuration.AccountName}\n{configuration.Algorithm} · {configuration.Digits} digits · {configuration.Period}s";
            return true;
        }
        method.SelectionChanged += (_, _) =>
        {
            var importing = method.SelectedIndex == 1;
            secret.Visibility = manual.Visibility = importing ? Visibility.Collapsed : Visibility.Visible;
            uri.Visibility = review.Visibility = importing ? Visibility.Visible : Visibility.Collapsed;
            parsed = null;
            error.IsOpen = false;
            review.Text = string.Empty;
        };
        uri.PasswordChanged += (_, _) => { review.Text = string.Empty; if (uri.Password.Length > 0) Parse(); };
        dialog.PrimaryButtonClick += (_, args) => args.Cancel = !Parse();
        dialog.Opened += (_, _) => secret.Focus(FocusState.Programmatic);
        try
        {
            var result = await App.Services.GetRequiredService<DialogLifetime>().ShowAsync(dialog, CancellationToken.None);
            return result == ContentDialogResult.Primary && shell.IsCurrentNormalUnlock(version) ? parsed : null;
        }
        finally
        {
            secret.Password = uri.Password = string.Empty;
            issuer.Text = account.Text = review.Text = string.Empty;
            parsed = null;
            existing = null;
            body.Children.Clear();
        }
    }
}
