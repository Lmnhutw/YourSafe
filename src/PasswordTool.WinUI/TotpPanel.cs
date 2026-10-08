using System.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using PasswordTool.Core.Models;
using PasswordTool.Presentation;

namespace PasswordTool_WinUI;

/// <summary>A live, secret-free view shared by the vault flyout and Details.</summary>
internal sealed class TotpPanel : UserControl, IDisposable
{
    private readonly AppFlowCoordinator flow = App.Services.GetRequiredService<AppFlowCoordinator>();
    private readonly ShellViewModel shell = App.Services.GetRequiredService<ShellViewModel>();
    private readonly Guid credentialId;
    private readonly long version;
    private readonly bool compact;
    private readonly CancellationTokenSource lifetime = new();
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly TextBlock code = new() { FontSize = 32, FontFamily = new FontFamily("Consolas") };
    private readonly TextBlock identity = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock countdown = new();
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
    private readonly ProgressBar progress = new() { Minimum = 0, Maximum = 1 };
    private readonly Button copy = new() { Content = "Copy", IsEnabled = false };
    private TotpCodeResult? current;
    private bool busy;
    private bool stopped;

    public TotpPanel(Guid credentialId, string title, string username, bool compact = false)
    {
        this.credentialId = credentialId;
        this.compact = compact;
        version = shell.LifecycleVersion;
        var body = new StackPanel { Spacing = 8, MinWidth = compact ? 256 : 280, MaxWidth = compact ? 256 : 380 };
        if (!compact)
        {
            body.Children.Add(new TextBlock { Text = title, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
            body.Children.Add(new TextBlock { Text = username, TextWrapping = TextWrapping.Wrap });
            body.Children.Add(identity);
            body.Children.Add(new TextBlock { Text = "Verification code" });
        }
        var row = new Grid { ColumnSpacing = 12 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(code);
        Grid.SetColumn(copy, 1);
        row.Children.Add(copy);
        body.Children.Add(row);
        var remaining = new Grid { ColumnSpacing = 12 };
        remaining.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        remaining.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        progress.VerticalAlignment = countdown.VerticalAlignment = VerticalAlignment.Center;
        remaining.Children.Add(progress);
        Grid.SetColumn(countdown, 1);
        remaining.Children.Add(countdown);
        body.Children.Add(remaining);
        body.Children.Add(status);
        Content = body;
        AutomationProperties.SetHelpText(code, "Current verification code");
        AutomationProperties.SetAutomationId(code, "TxtTotpCode");
        AutomationProperties.SetName(copy, "Copy current verification code securely");
        AutomationProperties.SetAutomationId(copy, "BtnCopyTotp");
        AutomationProperties.SetName(progress, "Verification code time remaining");
        copy.Click += Copy_Click;
        timer.Tick += Tick;
        Loaded += async (_, _) =>
        {
            if (stopped) return;
            shell.PropertyChanged += Shell_PropertyChanged;
            await RefreshAsync();
            if (!stopped) timer.Start();
        };
        Unloaded += (_, _) => Dispose();
    }

    private bool IsCurrent => !stopped && shell.IsCurrentNormalUnlock(version);

    private void Shell_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!IsCurrent) Dispose();
    }

    private async void Tick(object? sender, object e)
    {
        if (!IsCurrent) { Dispose(); return; }
        if (current is null) return;
        var now = DateTimeOffset.UtcNow;
        if (now >= current.ExpiresAtUtc || now < current.ExpiresAtUtc.AddSeconds(-current.PeriodSeconds))
        {
            ClearCode();
            await RefreshAsync();
        }
        else UpdateCountdown(now);
    }

    private async Task RefreshAsync()
    {
        if (busy || !IsCurrent) return;
        busy = true;
        try
        {
            var value = await flow.GetWebsiteTotpCodeAsync(credentialId, lifetime.Token);
            if (!IsCurrent) return;
            var now = DateTimeOffset.UtcNow;
            if (now >= value.ExpiresAtUtc || now < value.ExpiresAtUtc.AddSeconds(-value.PeriodSeconds))
                throw new OperationCanceledException();
            current = value;
            copy.Content = "Copy";
            var half = value.Code.Length / 2;
            code.Text = value.Code[..half] + " " + value.Code[half..];
            identity.Text = string.Join(" · ", new[] { value.Issuer, value.AccountName }.Where(text => !string.IsNullOrWhiteSpace(text)));
            copy.IsEnabled = true;
            UpdateCountdown(now);
        }
        catch (Exception)
        {
            if (!stopped) Stop("Verification code unavailable. Unlock the vault and reopen this panel.");
        }
        finally { busy = false; }
    }

    private void UpdateCountdown(DateTimeOffset now)
    {
        if (current is null) return;
        var seconds = Math.Clamp((current.ExpiresAtUtc - now).TotalSeconds, 0, current.PeriodSeconds);
        countdown.Text = $"{Math.Ceiling(seconds):0}s";
        progress.Value = seconds / current.PeriodSeconds;
    }

    private async void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (!IsCurrent || !copy.IsEnabled) return;
        copy.IsEnabled = false;
        try
        {
            await flow.CopyWebsiteTotpAsync(credentialId, lifetime.Token);
            if (IsCurrent)
            {
                AutomationProperties.SetHelpText(copy, "Copied.");
                if (compact) copy.Content = "Copied";
                else
                {
                    status.Text = "Copied.";
                    status.Visibility = Visibility.Visible;
                }
            }
        }
        catch (Exception) { if (!stopped) Stop("Copy failed. Unlock the vault and reopen this panel."); }
        finally { if (IsCurrent && current is not null) copy.IsEnabled = true; }
    }

    private void ClearCode()
    {
        current = null;
        code.Text = countdown.Text = string.Empty;
        progress.Value = 0;
        copy.IsEnabled = false;
    }

    private void Stop(string message)
    {
        Dispose();
        status.Text = message;
        status.Visibility = Visibility.Visible;
    }

    public void Dispose()
    {
        if (stopped) return;
        stopped = true;
        timer.Stop();
        timer.Tick -= Tick;
        shell.PropertyChanged -= Shell_PropertyChanged;
        lifetime.Cancel();
        lifetime.Dispose();
        ClearCode();
        identity.Text = status.Text = string.Empty;
    }
}
