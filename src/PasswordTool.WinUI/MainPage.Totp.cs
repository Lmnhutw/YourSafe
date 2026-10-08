using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Automation;
using PasswordTool.Presentation;

namespace PasswordTool_WinUI;

public sealed partial class MainPage
{
    private TotpPanel? activeTotpPanel;
    private Flyout? activeTotpFlyout;

    private void CloseTotpPanel()
    {
        activeTotpPanel?.Dispose();
        activeTotpPanel = null;
        activeTotpFlyout?.Hide();
        activeTotpFlyout = null;
    }

    private async Task<bool> EnsureTotpUnlockedAsync()
    {
        if (ViewModel.IsTableLocked) await ViewModel.UnlockTableAsync();
        return ViewModel.IsCurrentNormalUnlock(ViewModel.LifecycleVersion);
    }

    private async void ViewTotpRowButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: Guid id } button) return;
        CloseTotpPanel();
        if (!await EnsureTotpUnlockedAsync()) return;
        var item = ViewModel.Vault.Items.FirstOrDefault(item => item.Id == id && item.HasTotp);
        if (item is null) return;
        var manage = new HyperlinkButton { Content = "Manage verification code →", Padding = new Thickness(0, 4, 0, 4), HorizontalAlignment = HorizontalAlignment.Left };
        AutomationProperties.SetName(manage, "Manage verification code");
        var livePanel = activeTotpPanel = new TotpPanel(id, item.Title, item.Username, compact: true, manage: manage);
        var presenterStyle = new Style(typeof(FlyoutPresenter));
        presenterStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(12)));
        var flyout = activeTotpFlyout = new Flyout { Content = livePanel, Placement = FlyoutPlacementMode.Bottom, FlyoutPresenterStyle = presenterStyle };
        var version = ViewModel.LifecycleVersion;
        var manageRequested = false;
        manage.Click += (_, _) => { manageRequested = true; CloseTotpPanel(); };
        flyout.Closed += async (_, _) =>
        {
            livePanel.Dispose();
            flyout.Content = null;
            if (activeTotpFlyout == flyout) { activeTotpFlyout = null; activeTotpPanel = null; }
            if (manageRequested && ViewModel.IsCurrentNormalUnlock(version)) await ManageTotpAsync(id);
        };
        flyout.ShowAt(button);
    }

    private async Task ManageTotpAsync(Guid id)
    {
        CloseTotpPanel();
        if (!await EnsureTotpUnlockedAsync()) return;
        await OpenEditorAsync(id);
        if (ViewModel.CurrentRoute == AppRoute.ItemEditor && editingItemId == id)
        {
            EditorPage.UpdateLayout();
            EditorTotpButton.Focus(FocusState.Programmatic);
        }
    }

    private void UpdateTotpEditorStatus()
    {
        EditorTotpStatus.Text = draftTotp is null ? "No verification code added" : "Verification code added";
        EditorTotpButton.Content = draftTotp is null ? "Add verification code" : "Edit verification code";
        EditorRemoveTotpButton.Visibility = draftTotp is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void EditTotpButton_Click(object sender, RoutedEventArgs e)
    {
        var version = ViewModel.LifecycleVersion;
        if (!ViewModel.IsCurrentNormalUnlock(version)) return;
        var configuration = await TotpConfigurationDialog.ShowAsync(draftTotp, EditorItemTitle.Text, EditorUsername.Text);
        if (configuration is null || !ViewModel.IsCurrentNormalUnlock(version) || ViewModel.CurrentRoute != AppRoute.ItemEditor) return;
        draftTotp = configuration;
        UpdateTotpEditorStatus();
    }

    private async void RemoveTotpButton_Click(object sender, RoutedEventArgs e)
    {
        var version = ViewModel.LifecycleVersion;
        if (draftTotp is null || !ViewModel.IsCurrentNormalUnlock(version)) return;
        if (!await dialogs.ConfirmAsync("Remove verification code?", $"Remove the verification code from '{EditorItemTitle.Text}'? Select Save item to keep this change.", "Remove")
            || !ViewModel.IsCurrentNormalUnlock(version) || ViewModel.CurrentRoute != AppRoute.ItemEditor) return;
        draftTotp = null;
        UpdateTotpEditorStatus();
    }

    private async Task CopyRecoveryCodesAsync(Guid id)
    {
        var flow = App.Services.GetRequiredService<AppFlowCoordinator>();
        var version = ViewModel.LifecycleVersion;
        try
        {
            var codes = await flow.GetRecoveryCodesAsync(id, string.Empty);
            if (ViewModel.IsCurrentUnlock(version))
                await sensitiveClipboard.CopyAsync(string.Join(Environment.NewLine, codes), () => ViewModel.IsCurrentUnlock(version));
        }
        catch (OperationCanceledException) { }
        catch (Exception) { await dialogs.ShowErrorAsync("Copy failed", "Unlock the vault and try again."); }
    }
}
