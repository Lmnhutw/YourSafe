using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using PasswordTool.Core.Models;
using PasswordTool.Core.Services;
using PasswordTool.Presentation;

namespace PasswordTool_WinUI;

internal static class TotpConfigurationDialog
{
    private static StackPanel HeaderWithHelp(string label, string explanation, string helpId)
    {
        var help = (Button)XamlReader.Load("""
            <Button xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    Style="{StaticResource SubtleButtonStyle}" Width="24" Height="24"
                    MinWidth="0" MinHeight="0" Padding="2" VerticalAlignment="Center"
                    Foreground="{ThemeResource TextFillColorSecondaryBrush}">
                <FontIcon Glyph="&#xE946;" FontSize="14" />
            </Button>
            """);
        var tooltip = new ToolTip { Content = new TextBlock { Text = explanation, TextWrapping = TextWrapping.Wrap, MaxWidth = 300 } };
        ToolTipService.SetToolTip(help, tooltip);
        AutomationProperties.SetName(help, $"Help: {label}");
        AutomationProperties.SetHelpText(help, explanation);
        AutomationProperties.SetAutomationId(help, helpId);
        help.GotFocus += (_, _) => { if (help.FocusState == FocusState.Keyboard) tooltip.IsOpen = true; };
        help.LostFocus += (_, _) => tooltip.IsOpen = false;
        help.Click += (_, _) => tooltip.IsOpen = true;
        return new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 4,
            Children = { new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center }, help }
        };
    }

    private static StackPanel FieldWithHelp(Control input, string label, string explanation, string helpId)
    {
        AutomationProperties.SetName(input, label);
        return new StackPanel { Spacing = 4, Children = { HeaderWithHelp(label, explanation, helpId), input } };
    }

    public static async Task<TotpConfiguration?> ShowAsync(TotpConfiguration? existing, string title, string username)
    {
        var shell = App.Services.GetRequiredService<ShellViewModel>();
        var version = shell.LifecycleVersion;
        if (!shell.IsCurrentNormalUnlock(version)) return null;
        var parser = App.Services.GetRequiredService<TotpService>();
        var method = new RadioButtons { Header = HeaderWithHelp("Choose a method", "Get a setup key or link from the website's two-step verification settings.", "HelpTotpInputMethod"), Items = { "Enter a setup key", "Paste a setup link" }, SelectedIndex = 0 };
        var secret = new PasswordBox { Password = existing?.Secret ?? string.Empty, PasswordRevealMode = PasswordRevealMode.Peek, MaxLength = 4096 };
        var secretField = FieldWithHelp(secret, "Secret key", "Can't scan the QR code? Choose manual setup on the website and copy its key here. This private string of letters and numbers is the secret key, not the changing verification code.", "HelpTotpSecret");
        var secretError = InlineValidation.CreateError("TxtTotpSecretError");
        secretField.Children.Add(secretError);
        var uri = new PasswordBox { PasswordRevealMode = PasswordRevealMode.Peek, MaxLength = 4096 };
        var uriField = FieldWithHelp(uri, "Setup link", "Paste a link starting with otpauth://totp/. It contains the secret and settings from the QR code. An ordinary website URL won't work. If no link is available, choose Enter a setup key.", "HelpTotpUri");
        var uriError = InlineValidation.CreateError("TxtTotpUriError");
        uriField.Children.Add(uriError);
        uriField.Visibility = Visibility.Collapsed;
        var issuer = new TextBox { Text = existing?.Issuer ?? title, MaxLength = 4096 };
        var issuerField = FieldWithHelp(issuer, "Website or service (optional)", "Issuer means the service that gave you the key, such as Google or GitHub. This label helps you recognize the account; it doesn't change the code.", "HelpTotpIssuer");
        var account = new TextBox { Header = "Account name", Text = existing?.AccountName ?? username, MaxLength = 4096 };
        var algorithm = new AppComboBox { Header = "Algorithm", ItemsSource = Enum.GetValues<TotpAlgorithm>(), SelectedItem = existing?.Algorithm ?? TotpAlgorithm.Sha1 };
        var digits = new AppComboBox { Header = "Digits", ItemsSource = new[] { 6, 8 }, SelectedItem = existing?.Digits ?? 6 };
        var algorithmError = InlineValidation.CreateError("TxtTotpAlgorithmError");
        var digitsError = InlineValidation.CreateError("TxtTotpDigitsError");
        var period = new TextBox { Text = (existing?.Period ?? 30).ToString(CultureInfo.InvariantCulture), MaxLength = 10 };
        var periodField = FieldWithHelp(period, "Code refresh interval (seconds)", "Match the website's interval, usually 30 seconds.", "HelpTotpPeriod");
        var periodError = InlineValidation.CreateError("TxtTotpPeriodError");
        periodField.Children.Add(periodError);
        var advancedBody = new StackPanel { Spacing = 12, Children = { algorithm, algorithmError, digits, digitsError, periodField } };
        var advanced = new Expander { Header = "Advanced settings", Content = advancedBody, HorizontalAlignment = HorizontalAlignment.Stretch };
        var manual = new StackPanel { Spacing = 12, Children = { issuerField, account, advanced } };
        var review = new TextBlock { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
        var body = new StackPanel { Spacing = 12, MinWidth = 320, MaxWidth = 420, Children = { method, secretField, uriField, manual, review } };
        var dialog = new AppContentDialog
        {
            Title = existing is null ? "Add verification code" : "Edit verification code",
            Content = new ScrollViewer { Content = body, MaxHeight = 520, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled },
            PrimaryButtonText = "Apply", CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary
        };
        AutomationProperties.SetAutomationId(secret, "TxtTotpSecret");
        AutomationProperties.SetAutomationId(uri, "TxtTotpUri");
        AutomationProperties.SetName(secret, "Verification code secret key");
        AutomationProperties.SetName(uri, "Verification code setup link");
        AutomationProperties.SetAutomationId(method, "TotpInputMethod");
        AutomationProperties.SetAutomationId(period, "TxtTotpPeriod");
        AutomationProperties.SetAutomationId(issuer, "TxtTotpIssuer");
        AutomationProperties.SetAutomationId(advanced, "TotpWebsiteSettings");
        TotpConfiguration? parsed = null;
        void ClearErrors()
        {
            InlineValidation.ClearError(secret, secretError);
            InlineValidation.ClearError(uri, uriError);
            InlineValidation.ClearError(algorithm, algorithmError);
            InlineValidation.ClearError(digits, digitsError);
            InlineValidation.ClearError(period, periodError);
        }
        bool Parse(bool selectInput)
        {
            parsed = null;
            ClearErrors();
            bool Fail(Control input, TextBlock error, string message)
            {
                InlineValidation.SetError(input, error, message, selectInput);
                return false;
            }
            if (method.SelectedIndex == 1 && !uri.Password.TrimStart().StartsWith("otpauth://", StringComparison.OrdinalIgnoreCase))
            {
                return Fail(uri, uriError, "Paste the full setup link starting with otpauth://totp, or choose Enter a setup key.");
            }
            if (!parser.TryParseWebsiteConfiguration(method.SelectedIndex == 1 ? uri.Password : secret.Password, out var configuration, out var message))
            {
                return method.SelectedIndex == 1 ? Fail(uri, uriError, message) : Fail(secret, secretError, message);
            }
            if (method.SelectedIndex == 0)
            {
                if (secret.Password.TrimStart().StartsWith("otpauth:", StringComparison.OrdinalIgnoreCase))
                {
                    return Fail(secret, secretError, "This is a setup link. Choose Paste a setup link to import its settings.");
                }
                if (algorithm.SelectedItem is not TotpAlgorithm selectedAlgorithm)
                {
                    advanced.IsExpanded = true;
                    return Fail(algorithm, algorithmError, "Choose the algorithm used by the website.");
                }
                if (digits.SelectedItem is not int selectedDigits || selectedDigits is not (6 or 8))
                {
                    advanced.IsExpanded = true;
                    return Fail(digits, digitsError, "Choose 6 or 8 digits to match the website.");
                }
                if (!int.TryParse(period.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) || seconds <= 0)
                {
                    advanced.IsExpanded = true;
                    return Fail(period, periodError, "Enter a refresh interval greater than zero.");
                }
                configuration = configuration! with { Issuer = issuer.Text.Trim(), AccountName = account.Text.Trim(), Algorithm = selectedAlgorithm, Digits = selectedDigits, Period = seconds };
            }
            parsed = configuration;
            review.Text = $"Website or service: {configuration!.Issuer}\nAccount: {configuration.AccountName}\nCode settings will be copied from this link.";
            return true;
        }
        method.SelectionChanged += (_, _) =>
        {
            var importing = method.SelectedIndex == 1;
            secretField.Visibility = manual.Visibility = importing ? Visibility.Collapsed : Visibility.Visible;
            uriField.Visibility = review.Visibility = importing ? Visibility.Visible : Visibility.Collapsed;
            parsed = null;
            ClearErrors();
            review.Text = string.Empty;
        };
        secret.PasswordChanged += (_, _) => { parsed = null; InlineValidation.ClearError(secret, secretError); };
        period.TextChanged += (_, _) => { parsed = null; InlineValidation.ClearError(period, periodError); };
        algorithm.SelectionChanged += (_, _) => { parsed = null; InlineValidation.ClearError(algorithm, algorithmError); };
        digits.SelectionChanged += (_, _) => { parsed = null; InlineValidation.ClearError(digits, digitsError); };
        uri.PasswordChanged += (_, _) => { review.Text = string.Empty; InlineValidation.ClearError(uri, uriError); if (uri.Password.Length > 0) Parse(false); };
        dialog.PrimaryButtonClick += (_, args) => args.Cancel = !Parse(true);
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
