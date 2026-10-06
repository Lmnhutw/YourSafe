using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PasswordTool.Core.Models;
using PasswordTool.Core.Services;

namespace PasswordTool_WinUI;

internal sealed class PasswordGeneratorDialogService(PasswordGeneratorService generator, DialogLifetime lifetime)
{
    private readonly SemaphoreSlim dialogGate = new(1, 1);

    public async Task<string?> ShowAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await dialogGate.WaitAsync(cancellationToken);
        string generated = string.Empty;
        try
        {
            var mode = new AppComboBox { Header = "Mode", SelectedIndex = 0 };
            mode.Items.Add("Password");
            mode.Items.Add("Passphrase");

            var length = new NumberBox { Header = "Length", Minimum = 8, Maximum = 128, Value = 24, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline };
            var chunks = new NumberBox { Header = "Word-pair chunks", Minimum = 4, Maximum = 12, Value = 6, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline, Visibility = Visibility.Collapsed };
            var uppercase = new CheckBox { Content = "Uppercase", IsChecked = true };
            var lowercase = new CheckBox { Content = "Lowercase", IsChecked = true };
            var digits = new CheckBox { Content = "Digits", IsChecked = true };
            var symbols = new CheckBox { Content = "Symbols", IsChecked = true };
            var characterOptions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
            characterOptions.Children.Add(uppercase);
            characterOptions.Children.Add(lowercase);
            characterOptions.Children.Add(digits);
            characterOptions.Children.Add(symbols);

            var generatedText = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = false,
                FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas")
            };
            var strengthText = new TextBlock();
            var errorText = new TextBlock
            {
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemFillColorCriticalBrush"],
                TextWrapping = TextWrapping.Wrap
            };
            var generateButton = new Button { Content = "Generate another" };

            void Generate()
            {
                try
                {
                    var passphrase = mode.SelectedIndex == 1;
                    generated = passphrase
                        ? generator.GeneratePassphrase((int)chunks.Value)
                        : generator.GeneratePassword(new PasswordGenerationOptions
                        {
                            Length = (int)length.Value,
                            IncludeUppercase = uppercase.IsChecked == true,
                            IncludeLowercase = lowercase.IsChecked == true,
                            IncludeDigits = digits.IsChecked == true,
                            IncludeSymbols = symbols.IsChecked == true
                        });
                    var strength = passphrase
                        ? generator.EstimatePassphraseStrength((int)chunks.Value)
                        : generator.EstimatePasswordStrength(generated);
                    generatedText.Text = generated;
                    strengthText.Text = $"{strength.Rating} · approximately {strength.EstimatedEntropyBits:0} bits";
                    errorText.Text = string.Empty;
                }
                catch (ArgumentException exception)
                {
                    generated = string.Empty;
                    generatedText.Text = string.Empty;
                    strengthText.Text = string.Empty;
                    errorText.Text = exception.Message;
                }
            }

            mode.SelectionChanged += (_, _) =>
            {
                var passphrase = mode.SelectedIndex == 1;
                length.Visibility = passphrase ? Visibility.Collapsed : Visibility.Visible;
                characterOptions.Visibility = passphrase ? Visibility.Collapsed : Visibility.Visible;
                chunks.Visibility = passphrase ? Visibility.Visible : Visibility.Collapsed;
                Generate();
            };
            generateButton.Click += (_, _) => Generate();

            var content = new StackPanel { Spacing = 12, MinWidth = 440 };
            content.Children.Add(mode);
            content.Children.Add(length);
            content.Children.Add(chunks);
            content.Children.Add(characterOptions);
            content.Children.Add(generateButton);
            content.Children.Add(new TextBlock { Text = "Generated value", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            content.Children.Add(generatedText);
            content.Children.Add(strengthText);
            content.Children.Add(errorText);

            var dialog = new AppContentDialog
            {
                XamlRoot = ((FrameworkElement)App.Window.Content).XamlRoot,
                Title = "Password generator",
                Content = content,
                PrimaryButtonText = "Use this value",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary
            };
            dialog.PrimaryButtonClick += (_, args) => args.Cancel = string.IsNullOrEmpty(generated);
            Generate();
            var result = await lifetime.ShowAsync(dialog, cancellationToken);
            generatedText.Text = string.Empty;
            cancellationToken.ThrowIfCancellationRequested();
            return result == ContentDialogResult.Primary ? generated : null;
        }
        finally
        {
            generated = string.Empty;
            dialogGate.Release();
        }
    }
}
