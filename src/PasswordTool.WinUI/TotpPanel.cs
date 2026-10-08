using System.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using PasswordTool.Core.Models;
using PasswordTool.Presentation;
using Windows.Foundation;
using Path = Microsoft.UI.Xaml.Shapes.Path;

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
    private readonly Grid code = new() { ColumnSpacing = 4, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock firstHalf = new() { FontSize = 32, FontFamily = new FontFamily("Consolas") };
    private readonly TextBlock secondHalf = new() { FontSize = 32, FontFamily = new FontFamily("Consolas") };
    private readonly TextBlock identity = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
    private readonly Path progress = (Path)XamlReader.Load("""
        <Path xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
              Width="24" Height="24" Fill="{ThemeResource AccentFillColorDefaultBrush}" />
        """);
    private readonly Grid timeIndicator = new() { Width = 24, Height = 24, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button copy = new() { Content = "Copy", IsEnabled = false };
    private TotpCodeResult? current;
    private bool busy;
    private bool stopped;
    private int remainingSlices = -1;

    public TotpPanel(Guid credentialId, string title, string username, bool compact = false, HyperlinkButton? manage = null)
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
        code.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        code.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        code.Children.Add(firstHalf);
        Grid.SetColumn(secondHalf, 1);
        code.Children.Add(secondHalf);
        row.Children.Add(code);
        timeIndicator.Children.Add(progress);
        Grid.SetColumn(timeIndicator, 1);
        row.Children.Add(timeIndicator);
        body.Children.Add(row);
        var actions = new Grid { ColumnSpacing = 12 };
        actions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        actions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        if (manage is not null) actions.Children.Add(manage);
        Grid.SetColumn(copy, 1);
        actions.Children.Add(copy);
        body.Children.Add(actions);
        body.Children.Add(status);
        Content = body;
        AutomationProperties.SetHelpText(code, "Current verification code");
        AutomationProperties.SetAutomationId(code, "TxtTotpCode");
        AutomationProperties.SetAutomationId(firstHalf, "TxtTotpCodeFirstHalf");
        AutomationProperties.SetAutomationId(secondHalf, "TxtTotpCodeSecondHalf");
        AutomationProperties.SetName(copy, "Copy current verification code securely");
        AutomationProperties.SetAutomationId(copy, "BtnCopyTotp");
        AutomationProperties.SetName(timeIndicator, "Verification code time remaining");
        AutomationProperties.SetAutomationId(timeIndicator, "TotpTimeRemaining");
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
            firstHalf.Text = value.Code[..half];
            secondHalf.Text = value.Code[half..];
            AutomationProperties.SetName(code, firstHalf.Text + "\u2009" + secondHalf.Text);
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
        var slices = (int)Math.Ceiling(8 * seconds / current.PeriodSeconds);
        timer.Interval = TimeSpan.FromSeconds(slices == 0 ? 0.05 : Math.Max(0.05, seconds - (slices - 1) * current.PeriodSeconds / 8d));
        if (remainingSlices == slices) return;
        remainingSlices = slices;
        if (slices == 8)
            progress.Data = new EllipseGeometry { Center = new Point(12, 12), RadiusX = 12, RadiusY = 12 };
        else if (slices == 0) progress.Data = null;
        else
        {
            var angle = (8 - slices) * Math.PI / 4;
            var figure = new PathFigure { StartPoint = new Point(12, 12), IsClosed = true, IsFilled = true };
            figure.Segments.Add(new LineSegment { Point = new Point(12 + 12 * Math.Sin(angle), 12 - 12 * Math.Cos(angle)) });
            figure.Segments.Add(new ArcSegment { Point = new Point(12, 0), Size = new Size(12, 12), IsLargeArc = slices > 4, SweepDirection = SweepDirection.Clockwise });
            var geometry = new PathGeometry();
            geometry.Figures.Add(figure);
            progress.Data = geometry;
        }
        AutomationProperties.SetHelpText(timeIndicator, $"{slices}/8 remaining. Code expires in {Math.Ceiling(seconds):0} seconds.");
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
        firstHalf.Text = secondHalf.Text = string.Empty;
        AutomationProperties.SetName(code, string.Empty);
        progress.Data = null;
        remainingSlices = -1;
        AutomationProperties.SetHelpText(timeIndicator, string.Empty);
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
