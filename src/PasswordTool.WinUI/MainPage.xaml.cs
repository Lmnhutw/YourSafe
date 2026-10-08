using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using PasswordTool.Core.Models;
using PasswordTool.Presentation;
using QRCoder;
using Windows.Storage.Streams;

namespace PasswordTool_WinUI;

public sealed partial class MainPage : Page
{
    public ShellViewModel ViewModel { get; } = App.Services.GetRequiredService<ShellViewModel>();
    private readonly ISystemLockMonitor systemLockMonitor = App.Services.GetRequiredService<ISystemLockMonitor>();
    private readonly ISensitiveClipboardService sensitiveClipboard = App.Services.GetRequiredService<ISensitiveClipboardService>();
    private readonly IUserDialogService dialogs = App.Services.GetRequiredService<IUserDialogService>();
    private readonly AppearanceService appearance = App.Services.GetRequiredService<AppearanceService>();
    private readonly PasswordGeneratorDialogService passwordGeneratorDialog = App.Services.GetRequiredService<PasswordGeneratorDialogService>();
    private int lifecycleLockInProgress;
    private Guid? editingItemId;
    private TotpConfiguration? draftTotp;
    private AuthenticatorSetup? settingsAuthenticatorSetup;
    private bool recoveryWizardInProgress;
    private bool setupCompletionInProgress;
    private bool rotatingRecoveryKey;
    private bool updatingAppearanceInputs = true;
    private bool updatingNavigation;
    private bool confirmingEditorDiscard;
    private bool preservingEditorDuringTimeoutUnlock;
    private VaultItemEditorInput? editorBaseline;
    private readonly DispatcherTimer signInTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool hadSignIn;
    private readonly Windows.UI.ViewManagement.AccessibilitySettings accessibility = new();
    private readonly Windows.UI.ViewManagement.UISettings uiSettings = new();

    public MainPage()
    {
        InitializeComponent();
        signInTimer.Tick += async (_, _) =>
        {
            if (ViewModel.FlowState == AppFlowState.Unlocked && ViewModel.IsSignedIn && !ViewModel.IsVaultSessionActive)
            {
                if (Interlocked.Exchange(ref lifecycleLockInProgress, 1) != 0) return;
                try
                {
                    preservingEditorDuringTimeoutUnlock = ViewModel.CurrentRoute == AppRoute.ItemEditor
                        && draftTotp is null && editorBaseline?.TotpConfiguration is null;
                    await ViewModel.LockTableAsync(preserveCurrentRoute: preservingEditorDuringTimeoutUnlock);
                    if (preservingEditorDuringTimeoutUnlock && ViewModel.IsTableLocked)
                        await ViewModel.UnlockTableAsync();
                    if (preservingEditorDuringTimeoutUnlock && ViewModel.IsTableLocked)
                    {
                        ViewModel.Navigate(AppRoute.Vault);
                        ApplyRoute(AppRoute.Vault);
                    }
                    else if (preservingEditorDuringTimeoutUnlock) preservingEditorDuringTimeoutUnlock = false;
                }
                finally { Interlocked.Exchange(ref lifecycleLockInProgress, 0); }
            }
            else if (ViewModel.FlowState == AppFlowState.SaveRecoveryKey && !ViewModel.IsVaultSessionActive)
                SystemLockMonitor_LockRequired(this, EventArgs.Empty);
            if (ViewModel.IsSignedIn) hadSignIn = true;
            else if (hadSignIn)
            {
                hadSignIn = false;
                SystemLockMonitor_LockRequired(this, EventArgs.Empty);
            }
        };
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        ViewModel.Vault.PropertyChanged += Vault_PropertyChanged;
        Loaded += (_, _) =>
        {
            systemLockMonitor.LockRequired += SystemLockMonitor_LockRequired;
            // SystemEvents initialization must run after XAML finishes the Loaded callback.
            DispatcherQueue.TryEnqueue(systemLockMonitor.Start);
            signInTimer.Start();
            uiSettings.ColorValuesChanged += UiSettings_ColorValuesChanged;
            appearance.Changed += Appearance_Changed;
            SyncAppearanceInputs();
            ApplyGroupTabPlacement();
            ApplyShellState();
            FocusCurrentAuthenticationStep();
        };
        Unloaded += (_, _) =>
        {
            systemLockMonitor.LockRequired -= SystemLockMonitor_LockRequired;
            uiSettings.ColorValuesChanged -= UiSettings_ColorValuesChanged;
            appearance.Changed -= Appearance_Changed;
            systemLockMonitor.Stop();
            signInTimer.Stop();
            CloseTotpPanel();
            ClearEditor();
        };
    }

    public static Visibility BoolToVisibility(bool value) => value ? Visibility.Visible : Visibility.Collapsed;
    public static bool Not(bool value) => !value;
    public static bool CanEditItem(bool isSaving, bool isTableLocked) => !isSaving && !isTableLocked;
    public static bool HasSelection(object? value) => value is not null;
    public static Visibility EmptyVisibility(int count) => count == 0 ? Visibility.Visible : Visibility.Collapsed;
    public static Visibility InvertBoolToVisibility(bool value) => value ? Visibility.Collapsed : Visibility.Visible;
    public static string ItemAutomationId(string action, Guid id) => $"{action}_{id:N}";
    public static string GroupAutomationId(Guid? id) => id is null ? "Group_All" : $"Group_{id:N}";
    private static Brush GroupBrush(string value) => new SolidColorBrush(Windows.UI.Color.FromArgb(255,
            Convert.ToByte(value.Substring(1, 2), 16),
            Convert.ToByte(value.Substring(3, 2), 16),
            Convert.ToByte(value.Substring(5, 2), 16)));
    private static Brush GroupTextBrush(string value) => GroupBrush(AppearanceSettings.TextColorFor(value));

    private void VaultRow_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not DependencyObject row) return;
        ApplyVaultIconHover(row);
        if (row is Grid grid) ApplyCredentialColumns(grid);
    }

    private void CredentialGrid_Loaded(object sender, RoutedEventArgs e) => ApplyCredentialColumns((Grid)sender);

    private void CredentialTable_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        ApplyCredentialColumns(CredentialHeader);
        for (var index = 0; index < CredentialList.Items.Count; index++)
            if (CredentialList.ContainerFromIndex(index) is ListViewItem { ContentTemplateRoot: Grid row }) ApplyCredentialColumns(row);
    }

    private void ApplyCredentialColumns(Grid grid)
    {
        if (grid is null || CredentialTable is null) return;
        var widths = CredentialColumns.Calculate(CredentialTable.ActualWidth - 16);
        for (var index = 0; index < widths.Length; index++)
            grid.ColumnDefinitions[index].Width = new GridLength(widths[index]);
    }

    private void ApplyVaultIconHover(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is Button button)
            {
                if (button.Content is IconElement) button.Style = (Style)Resources["VaultRowIconButtonStyle"];
                continue;
            }

            ApplyVaultIconHover(child);
        }
    }

    private void Vault_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(VaultWorkspaceViewModel.IsVerticalTabs)) ApplyGroupTabPlacement();
        if (e.PropertyName == nameof(VaultWorkspaceViewModel.SelectedGroup))
        {
            UpdateGroupTabs();
            DispatcherQueue.TryEnqueue(BringActiveGroupIntoView);
        }
    }

    private void UiSettings_ColorValuesChanged(Windows.UI.ViewManagement.UISettings sender, object args) =>
        DispatcherQueue.TryEnqueue(UpdateGroupTabs);

    private static string DisplayGroupName(VaultItemGroup group) => group.Name;
    private static string? GroupColor(VaultItemGroup group) => group.IsAll ? null : group.AccentColor;
    private string? DisplayGroupColor(VaultItemGroup group) => accessibility.HighContrast ? null : GroupColor(group);

    private void UpdateGroupTab(Button button)
    {
        if (button.Tag is not VaultItemGroup group) return;
        var selected = group == ViewModel.Vault.SelectedGroup;
        button.ClearValue(Control.BackgroundProperty);
        button.ClearValue(Control.ForegroundProperty);
        button.ClearValue(Control.BorderBrushProperty);
        button.Style = (Style)Resources[selected ? "VaultSelectedTabButtonStyle" : "VaultTabButtonStyle"];
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, DisplayGroupName(group));
        ToolTipService.SetToolTip(button, DisplayGroupName(group));
        if (button.Content is Grid label && label.Children.OfType<TextBlock>().FirstOrDefault() is { } title)
            title.Text = DisplayGroupName(group);
        if (button.Content is Grid content && content.Children.OfType<Border>().FirstOrDefault() is { } marker)
        {
            marker.Visibility = DisplayGroupColor(group) is null ? Visibility.Collapsed : Visibility.Visible;
            if (DisplayGroupColor(group) is { } markerColor) marker.Background = GroupBrush(markerColor);
        }
        if (selected && DisplayGroupColor(group) is { Length: > 0 } color)
        {
            button.Background = GroupBrush(color);
            button.Foreground = GroupTextBrush(color);
        }
        button.MinHeight = ViewModel.Vault.IsVerticalTabs ? 40 : 48;
        button.Padding = ViewModel.Vault.IsVerticalTabs ? new Thickness(12, 8, 12, 8) : new Thickness(12, 8, 12, 16);
        button.BorderThickness = ViewModel.Vault.IsVerticalTabs ? new Thickness(1, 1, selected ? 0 : 1, 1) : new Thickness(1, 1, 1, selected ? 0 : 1);
        button.CornerRadius = ViewModel.Vault.IsVerticalTabs ? new CornerRadius(6, 0, 0, 6) : new CornerRadius(6, 6, 0, 0);
        button.FontWeight = selected ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetItemStatus(button, selected ? "Selected" : "Not selected");
    }

    private void UpdateGroupTabs()
    {
        if (GroupTabsRepeater is null) return;
        UpdateGroupTab(AllGroupTab);
        for (var index = 0; index < ViewModel.Vault.GroupTabs.Count; index++)
            if (GroupTabsRepeater.TryGetElement(index) is Button button) UpdateGroupTab(button);
        GroupTableFrame.ClearValue(Border.BackgroundProperty);
    }

    private void GroupTab_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is Button button) UpdateGroupTab(button);
    }

    private void GroupTab_ActualThemeChanged(FrameworkElement sender, object args) => UpdateGroupTabs();

    private void GroupTabsRepeater_ElementPrepared(ItemsRepeater sender, ItemsRepeaterElementPreparedEventArgs args)
    {
        if (args.Element is Button button) UpdateGroupTab(button);
    }

    private void GroupTabPlacement_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!updatingAppearanceInputs && GroupTabPlacement.SelectedIndex >= 0)
            SaveAppearance(appearance.Settings with { IsVerticalTabs = GroupTabPlacement.SelectedIndex == 1 });
    }

    private void Appearance_Changed(object? sender, EventArgs e)
    {
        SyncAppearanceInputs();
        UpdateGroupTabs();
    }

    private void SyncAppearanceInputs()
    {
        updatingAppearanceInputs = true;
        try
        {
            AppThemeInput.SelectedIndex = (int)appearance.Settings.Theme;
            GroupTabPlacement.SelectedIndex = appearance.Settings.IsVerticalTabs ? 1 : 0;
            ViewModel.Vault.IsVerticalTabs = appearance.Settings.IsVerticalTabs;
        }
        finally { updatingAppearanceInputs = false; }
    }

    private void AppearanceSelection_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (updatingAppearanceInputs || AppThemeInput.SelectedIndex < 0) return;
        SaveAppearance(appearance.Settings with { Theme = (AppearanceTheme)AppThemeInput.SelectedIndex });
    }

    private void SaveAppearance(AppearanceSettings settings)
    {
        try { appearance.Save(settings); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            SyncAppearanceInputs();
            ViewModel.StatusMessage = "Appearance could not be saved. Check access to the app's local storage and try again.";
            ViewModel.IsStatusOpen = true;
        }
    }

    private void ApplyGroupTabPlacement()
    {
        if (GroupTabStrip is null) return;
        var vertical = ViewModel.Vault.IsVerticalTabs;
        GroupContainer.RowSpacing = vertical ? 8 : 0;
        GroupTabStrip.VerticalAlignment = vertical ? VerticalAlignment.Stretch : VerticalAlignment.Bottom;
        Grid.SetRow(GroupTableFrame, 1);
        Grid.SetColumn(GroupTableFrame, vertical ? 1 : 0);
        Grid.SetRowSpan(GroupTableFrame, 1);
        Grid.SetColumnSpan(GroupTableFrame, vertical ? 1 : 2);
        Grid.SetRow(GroupTabStrip, vertical ? 1 : 0);
        Grid.SetRowSpan(GroupTabStrip, 1);
        Grid.SetColumnSpan(GroupTabStrip, 1);
        GroupContainer.ColumnDefinitions[0].Width = new GridLength(1, vertical ? GridUnitType.Auto : GridUnitType.Star);
        GroupContainer.ColumnDefinitions[1].Width = new GridLength(1, vertical ? GridUnitType.Star : GridUnitType.Auto);
        GroupTabStrip.Width = vertical ? 180 : double.NaN;
        GroupTabStrip.Margin = vertical ? new Thickness(0, 12, -1, 12) : new Thickness(12, 0, 12, -1);
        AllGroupTab.HorizontalAlignment = vertical ? HorizontalAlignment.Stretch : HorizontalAlignment.Left;
        ScrollableGroupTabs.Orientation = vertical ? Orientation.Vertical : Orientation.Horizontal;
        Grid.SetRow(GroupTabsScroller, vertical ? 1 : 0);
        Grid.SetColumn(GroupTabsScroller, vertical ? 0 : 2);
        Grid.SetColumnSpan(GroupTabsScroller, vertical ? 4 : 1);
        GroupTabsScroller.Margin = vertical ? new Thickness(0, 4, 0, 0) : new Thickness(4, 0, 0, 0);
        GroupTabsScroller.HorizontalScrollMode = vertical ? ScrollMode.Disabled : ScrollMode.Enabled;
        GroupTabsScroller.HorizontalScrollBarVisibility = vertical ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Hidden;
        GroupTabsScroller.VerticalScrollMode = vertical ? ScrollMode.Enabled : ScrollMode.Disabled;
        GroupTabsScroller.VerticalScrollBarVisibility = vertical ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;
        ((StackLayout)GroupTabsRepeater.Layout).Orientation = vertical ? Orientation.Vertical : Orientation.Horizontal;
        GroupTabsScroller.ChangeView(0, 0, null, disableAnimation: true);
        UpdateGroupTabs();
        DispatcherQueue.TryEnqueue(BringActiveGroupIntoView);
    }

    private void BringActiveGroupIntoView()
    {
        var index = ViewModel.Vault.GroupTabs.IndexOf(ViewModel.Vault.SelectedGroup);
        if (index >= 0)
        {
            var element = GroupTabsRepeater.GetOrCreateElement(index);
            GroupTabsRepeater.UpdateLayout();
            element.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = true });
        }
        else AllGroupTab.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = true });
        UpdateTabOverflow();
    }

    private void UpdateTabOverflow()
    {
        if (GroupTabsScroller is null) return;
        var show = !ViewModel.Vault.IsVerticalTabs && GroupTabsScroller.ScrollableWidth > 1;
        PreviousGroupTab.Visibility = NextGroupTab.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        PreviousGroupTab.IsEnabled = GroupTabsScroller.HorizontalOffset > 1;
        NextGroupTab.IsEnabled = GroupTabsScroller.HorizontalOffset < GroupTabsScroller.ScrollableWidth - 1;
    }

    private void GroupTabsScroller_ViewChanged(object sender, ScrollViewerViewChangedEventArgs e) => UpdateTabOverflow();
    private void GroupTabsScroller_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateTabOverflow();
    private void GroupTabStrip_SizeChanged(object sender, SizeChangedEventArgs e) => DispatcherQueue.TryEnqueue(BringActiveGroupIntoView);
    private void PreviousGroupTab_Click(object sender, RoutedEventArgs e) => ScrollGroupTabs(-1);
    private void NextGroupTab_Click(object sender, RoutedEventArgs e) => ScrollGroupTabs(1);
    private void ScrollGroupTabs(int direction) => GroupTabsScroller.ChangeView(
        Math.Clamp(GroupTabsScroller.HorizontalOffset + direction * Math.Max(120, GroupTabsScroller.ViewportWidth * 0.75), 0, GroupTabsScroller.ScrollableWidth), null, null);
    private void ClearVaultFilters_Click(object sender, RoutedEventArgs e) => ViewModel.Vault.ResetToDefaultView();

    private void GroupTab_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (sender is not Button { Tag: VaultItemGroup group }) return;
        var vertical = ViewModel.Vault.IsVerticalTabs;
        var index = group.IsAll ? 0 : ViewModel.Vault.GroupTabs.IndexOf(group) + 1;
        var next = e.Key switch
        {
            Windows.System.VirtualKey.Home => 0,
            Windows.System.VirtualKey.End => ViewModel.Vault.GroupTabs.Count,
            Windows.System.VirtualKey.Left when !vertical => index - 1,
            Windows.System.VirtualKey.Right when !vertical => index + 1,
            Windows.System.VirtualKey.Up when vertical => index - 1,
            Windows.System.VirtualKey.Down when vertical => index + 1,
            _ => -1
        };
        if (next < 0 || next > ViewModel.Vault.GroupTabs.Count) return;
        e.Handled = true;
        ViewModel.Vault.SelectGroup(next == 0 ? ViewModel.Vault.AllGroup : ViewModel.Vault.GroupTabs[next - 1]);
        var target = next == 0 ? AllGroupTab : (Button)GroupTabsRepeater.GetOrCreateElement(next - 1);
        target.Focus(FocusState.Keyboard);
        BringActiveGroupIntoView();
    }

    private async void UnlockButton_Click(object sender, RoutedEventArgs e)
    {
        if (!UnlockButton.IsEnabled || recoveryWizardInProgress) return;
        UnlockButton.IsEnabled = false;
        try
        {
            var password = MasterPasswordInput.Password;
            if (!await ViewModel.ValidateMasterPasswordAsync(password)) return;
            var code = ViewModel.IsSignedIn ? string.Empty : await dialogs.PromptTotpAsync(
                "Sign in", "Enter the current 6-digit code from Google Authenticator.");
            if (code is null) return;

            await ViewModel.UnlockAsync(password, code);
            if (ViewModel.FlowState == AppFlowState.SaveRecoveryKey)
            {
                var key = await ConfirmRecoveryKeyAsync();
                try
                {
                    if (key is null) await ViewModel.LockCommand.ExecuteAsync(null);
                    else await ViewModel.SaveRecoveryKeyAsync(password, key, true);
                }
                catch (OperationCanceledException) { await ViewModel.LockCommand.ExecuteAsync(null); }
                catch (Exception)
                {
                    await ViewModel.LockCommand.ExecuteAsync(null);
                    await dialogs.ShowErrorAsync("Recovery Key was not saved", "The vault remains locked. Sign in again to finish saving the Recovery Key.");
                }
                finally { key = null; }
            }
            hadSignIn = ViewModel.IsSignedIn;

            if (ViewModel.IsUnlocked)
            {
                MasterPasswordInput.Password = string.Empty;
            }
            ApplyShellState();
        }
        catch (OperationCanceledException) { }
        finally
        {
            MasterPasswordInput.Password = string.Empty;
            UnlockButton.IsEnabled = true;
        }
    }

    private void MasterPasswordInput_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            UnlockButton_Click(UnlockButton, new RoutedEventArgs());
            e.Handled = true;
        }
    }

    private async void ShellNavigation_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (updatingNavigation) return;
        var selectedItem = args.SelectedItemContainer ?? args.SelectedItem as NavigationViewItem;
        if (selectedItem?.Tag is not string tag) return;
        if (Enum.TryParse<AppRoute>(tag, out var route))
        {
            var version = ViewModel.LifecycleVersion;
            if (route != ViewModel.CurrentRoute && !await CanLeaveEditorAsync())
            {
                ApplyRoute(ViewModel.CurrentRoute);
                return;
            }
            if (!ViewModel.IsCurrentUnlock(version)) return;
            if (route != AppRoute.ItemEditor) ClearEditor();
            ViewModel.Navigate(route);
            if (route == AppRoute.Settings) await ViewModel.Settings.LoadAsync();
            if (route == AppRoute.Backup) await ViewModel.Backup.LoadAsync();
            if (route == AppRoute.Trash) await ViewModel.Trash.LoadAsync();
            if (ViewModel.IsCurrentUnlock(version) && ViewModel.CurrentRoute == route) ApplyRoute(route);
        }
    }

    private async void LockVaultButton_Click(object sender, RoutedEventArgs e)
    {
        if (!VaultLockButton.IsEnabled) return;
        VaultLockButton.IsEnabled = false;
        try
        {
            if (ViewModel.IsTableLocked)
            {
                await ViewModel.UnlockTableAsync();
                if (!ViewModel.IsTableLocked && preservingEditorDuringTimeoutUnlock)
                {
                    preservingEditorDuringTimeoutUnlock = false;
                    ViewModel.Navigate(AppRoute.ItemEditor);
                    ApplyRoute(AppRoute.ItemEditor);
                    EditorItemTitle.Focus(FocusState.Programmatic);
                }
                return;
            }
            App.Services.GetRequiredService<DialogLifetime>().DismissAll();
            var locking = ViewModel.LockTableAsync();
            var version = ViewModel.LifecycleVersion;
            await locking;
            if (version != ViewModel.LifecycleVersion) return;
            ClearEditor();
            ClearSettingsInputs();
            ClearBackupInputs();
            ApplyShellState();
            if (!ViewModel.IsTableLocked) MasterPasswordInput.Focus(FocusState.Programmatic);
        }
        finally { VaultLockButton.IsEnabled = true; UpdateVaultLockButton(); }
    }

    private void UpdateVaultLockButton(bool hover = false)
    {
        var locked = ViewModel.IsTableLocked;
        VaultLockLabel.Text = locked ? "Unlock vault" : "Lock vault";
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(VaultLockButton, VaultLockLabel.Text);
        VaultLockIcon.Glyph = locked != hover ? "\uE72E" : "\uE785";
    }

    private void VaultLockButton_PointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e) => UpdateVaultLockButton(true);
    private void VaultLockButton_PointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e) => UpdateVaultLockButton();

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ShellViewModel.IsUnlocked) or nameof(ShellViewModel.FlowState)) ApplyShellState();
    }

    private void CreateVaultButton_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.BeginNewVault();
        ApplyShellState();
        FocusCurrentAuthenticationStep();
    }

    private void RecoverVaultButton_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.BeginRecovery();
        ApplyShellState();
    }

    private async void ChooseRecoveryButton_Click(object sender, RoutedEventArgs e) =>
        await ViewModel.SelectRecoveryFileAsync();

    private async void InspectRecoveryButton_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.InspectRecoveryAsync(RecoveryPassphraseInput.Password);
        RecoverySummaryInfoBar.IsOpen = !string.IsNullOrWhiteSpace(ViewModel.RecoverySummary);
    }

    private void ContinueRecoveryButton_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.ContinueRecovery();
        ApplyShellState();
        FocusCurrentAuthenticationStep();
    }

    private async void ContinueMasterPasswordButton_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.PrepareAuthenticator(NewMasterPasswordInput.Password, ConfirmMasterPasswordInput.Password);
        ApplyShellState();
        if (ViewModel.FlowState == AppFlowState.SetupAuthenticator && ViewModel.PendingAuthenticatorSetup is { } setup)
        {
            var version = ViewModel.LifecycleVersion;
            var qr = await CreateQrBitmapAsync(setup.OtpAuthUri);
            if (version != ViewModel.LifecycleVersion || ViewModel.FlowState != AppFlowState.SetupAuthenticator
                || ViewModel.PendingAuthenticatorSetup != setup) return;
            AuthenticatorSecretText.Text = setup.SecretBase32;
            AuthenticatorQrImage.Source = qr;
            AuthenticatorConfirmationInput.FocusFirst();
        }
    }

    private async void CompleteAuthenticatorButton_Click(object sender, RoutedEventArgs e)
    {
        if (setupCompletionInProgress) return;
        setupCompletionInProgress = true;
        var version = ViewModel.LifecycleVersion;
        string? recoveryKey = null;
        try
        {
            if (ViewModel.PendingAuthenticatorSetup is not { } setup
                || !await ViewModel.ValidateAuthenticatorSetupAsync(setup, AuthenticatorConfirmationInput.Code))
            {
                await dialogs.ShowErrorAsync("Setup was not completed", "Authenticator setup could not be verified.");
                return;
            }
            if (version != ViewModel.LifecycleVersion || ViewModel.FlowState != AppFlowState.SetupAuthenticator) return;
            recoveryKey = await ConfirmRecoveryKeyAsync();
            if (recoveryKey is null || version != ViewModel.LifecycleVersion || ViewModel.FlowState != AppFlowState.SetupAuthenticator) return;
            await ViewModel.CompleteAuthenticatorSetupAsync(
                NewMasterPasswordInput.Password,
                RecoveryPassphraseInput.Password,
                AuthenticatorConfirmationInput.Code,
                recoveryKey,
                true);
        }
        catch (OperationCanceledException) { }
        finally
        {
            recoveryKey = null;
            if (version == ViewModel.LifecycleVersion)
            {
                ClearFirstLaunchInputs();
                if (ViewModel.FlowState == AppFlowState.SetupAuthenticator) ViewModel.CancelFirstLaunchStep();
                ApplyShellState();
            }
            setupCompletionInProgress = false;
        }
    }

    private async void CopyAuthenticatorSecretButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.PendingAuthenticatorSetup is { } setup) await sensitiveClipboard.CopyAsync(setup.SecretBase32);
    }

    private async Task<string?> ConfirmRecoveryKeyAsync()
    {
        var key = PasswordTool.Core.Services.RecoveryKeyService.Generate();
        var text = new TextBlock { Text = key, TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("Consolas"), IsTextSelectionEnabled = false };
        var copy = new Button { Content = "Copy key securely" };
        var countdown = new TextBlock { Text = "0", FontSize = 18, MinWidth = 28, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(countdown, "Seconds until clipboard clears");
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        long copiedAt = 0;
        var closed = false;
        void UpdateCountdown(object? sender, object args)
        {
            var remaining = Math.Max(0, (int)Math.Ceiling(30 - Stopwatch.GetElapsedTime(copiedAt).TotalSeconds));
            countdown.Text = remaining.ToString();
            if (remaining == 0)
            {
                timer.Stop();
                copy.Content = "Copy key securely";
            }
        }
        timer.Tick += UpdateCountdown;
        copy.Click += async (_, _) =>
        {
            if (closed || !copy.IsEnabled) return;
            copy.IsEnabled = false;
            try
            {
                var startedAt = Stopwatch.GetTimestamp();
                await sensitiveClipboard.CopyAsync(text.Text);
                if (closed) return;
                copiedAt = startedAt;
                copy.Content = "Copied";
                timer.Stop();
                UpdateCountdown(null, EventArgs.Empty);
                timer.Start();
            }
            catch (Exception) { if (!closed) copy.Content = "Copy failed — click to retry"; }
            finally { if (!closed) copy.IsEnabled = true; }
        };
        var saved = new CheckBox { Content = "I saved this key in a safe place outside this device." };
        var panel = new StackPanel { Spacing = 16 };
        panel.Children.Add(new TextBlock { Text = "This recovery code lets you create a new Master Password if you forget it. Save the new code somewhere safe outside this device. After you confirm it is saved, your previous recovery code will stop working. Exported backups keep the credentials they had when they were created.", TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(text);
        var copyRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        copyRow.Children.Add(copy);
        copyRow.Children.Add(countdown);
        panel.Children.Add(copyRow);
        panel.Children.Add(new TextBlock
        {
            Text = "The copied key is automatically cleared from the clipboard after 30 seconds. Copy again to restart the countdown. You can copy the key while this dialog is open; closing it clears the copied key.\n\nPaste with Ctrl+V. This key is excluded from Windows clipboard history.",
            TextWrapping = TextWrapping.Wrap
        });
        panel.Children.Add(saved);
        var dialog = new AppContentDialog { Title = "Save new recovery code", Content = panel, PrimaryButtonText = "I've saved it", CloseButtonText = "Cancel", IsPrimaryButtonEnabled = false };
        saved.Checked += (_, _) => dialog.IsPrimaryButtonEnabled = true;
        saved.Unchecked += (_, _) => dialog.IsPrimaryButtonEnabled = false;
        var confirmed = false;
        try
        {
            confirmed = await App.Services.GetRequiredService<DialogLifetime>().ShowAsync(dialog, CancellationToken.None) == ContentDialogResult.Primary;
            return confirmed ? key : null;
        }
        finally
        {
            closed = true;
            copy.IsEnabled = false;
            timer.Stop();
            timer.Tick -= UpdateCountdown;
            text.Text = string.Empty;
            key = string.Empty;
            await sensitiveClipboard.ClearOwnedValueAsync();
        }
    }

    private async void RotateRecoveryKeyButton_Click(object sender, RoutedEventArgs e)
    {
        if (rotatingRecoveryKey) return;
        rotatingRecoveryKey = true;
        var version = ViewModel.LifecycleVersion;
        string? key = null;
        try
        {
            if (!await ViewModel.ValidateMasterPasswordAsync(SettingsMasterPassword.Password) || !ViewModel.IsCurrentUnlock(version)
                || ViewModel.CurrentRoute != AppRoute.Settings) return;
            key = await ConfirmRecoveryKeyAsync();
            if (key is null || !ViewModel.IsCurrentUnlock(version)) return;
            await ViewModel.SaveRecoveryKeyAsync(SettingsMasterPassword.Password, key, true);
        }
        catch (OperationCanceledException) { }
        catch (Exception) { await dialogs.ShowErrorAsync("Recovery Key was not changed", "Verify your Master Password and try again."); }
        finally
        {
            key = null;
            if (version == ViewModel.LifecycleVersion) SettingsMasterPassword.Password = string.Empty;
            rotatingRecoveryKey = false;
        }
    }

    private async void ForgotMasterPasswordButton_Click(object sender, RoutedEventArgs e)
    {
        if (recoveryWizardInProgress) return;
        recoveryWizardInProgress = true;
        var recovery = new PasswordBox { Header = "Recovery Key" };
        var password = new PasswordBox { Header = "New Master Password" };
        var confirm = new PasswordBox { Header = "Confirm Master Password" };
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(recovery);
        var dialog = new AppContentDialog { Title = "Validate Recovery Key", Content = panel, PrimaryButtonText = "Continue", CloseButtonText = "Cancel" };
        string? newKey = null;
        long? wizardVersion = null;
        try
        {
            var entryVersion = ViewModel.LifecycleVersion;
            var explanation = new AppContentDialog
            {
                Title = "Forgot Master Password?",
                Content = new TextBlock
                {
                    FontSize = 16,
                    TextWrapping = TextWrapping.Wrap,
                    Text = "YourSafe cannot show or retrieve your Master Password.\n\n"
                        + "If you saved your Recovery Key, you can use it to create a new Master Password and set up a new Authenticator. Your saved vault items are kept.\n\n"
                        + "You will also receive a replacement Recovery Key. Save it before finishing; you will then return to Login. Without your current Recovery Key, these credentials cannot be reset."
                },
                PrimaryButtonText = "Enter Recovery Key",
                CloseButtonText = "Back to Login",
                DefaultButton = ContentDialogButton.Close
            };
            if (await App.Services.GetRequiredService<DialogLifetime>().ShowAsync(explanation, CancellationToken.None) != ContentDialogResult.Primary
                || entryVersion != ViewModel.LifecycleVersion || ViewModel.FlowState != AppFlowState.Unlock) return;
            ViewModel.BeginRecoveryKeyReset();
            var version = ViewModel.LifecycleVersion;
            wizardVersion = version;
            if (await App.Services.GetRequiredService<DialogLifetime>().ShowAsync(dialog, CancellationToken.None) != ContentDialogResult.Primary) return;
            await ViewModel.ValidateRecoveryKeyAsync(recovery.Password);
            if (version != ViewModel.LifecycleVersion) return;
            var passwordPanel = new StackPanel { Spacing = 12 };
            passwordPanel.Children.Add(password);
            passwordPanel.Children.Add(confirm);
            var passwordDialog = new AppContentDialog { Title = "Choose new Master Password", Content = passwordPanel, PrimaryButtonText = "Continue", CloseButtonText = "Cancel" };
            if (await App.Services.GetRequiredService<DialogLifetime>().ShowAsync(passwordDialog, CancellationToken.None) != ContentDialogResult.Primary
                || version != ViewModel.LifecycleVersion) return;
            ViewModel.PrepareRecoveryKeyReset(password.Password, confirm.Password);
            newKey = await ConfirmRecoveryKeyAsync();
            if (newKey is null || version != ViewModel.LifecycleVersion) return;
            ViewModel.ConfirmRecoveryKeySaved(true);
            var setup = ViewModel.Settings.PrepareAuthenticator();
            var secret = setup.SecretBase32;
            var qr = new Image { Width = 220, Height = 220, Source = await CreateQrBitmapAsync(setup.OtpAuthUri) };
            var code = new SixDigitCodeInput();
            var authPanel = new StackPanel { Spacing = 12 };
            authPanel.Children.Add(qr);
            authPanel.Children.Add(code);
            var authDialog = new AppContentDialog { Title = "Set up new Authenticator", Content = authPanel, PrimaryButtonText = "Reset credentials", CloseButtonText = "Cancel" };
            try
            {
                if (version != ViewModel.LifecycleVersion) return;
                if (await App.Services.GetRequiredService<DialogLifetime>().ShowAsync(authDialog, CancellationToken.None) != ContentDialogResult.Primary) return;
                await ViewModel.ResetWithRecoveryKeyAsync(new RecoveryKeyResetRequest(recovery.Password, password.Password, newKey, true, secret, code.Code));
                ApplyShellState();
            }
            finally { secret = string.Empty; code.Clear(); qr.Source = null; }
        }
        catch (OperationCanceledException) { }
        catch (Exception) { await dialogs.ShowErrorAsync("Reset was not completed", "Check the Recovery Key, matching passwords, and current Authenticator code. No incomplete credential changes were saved."); }
        finally
        {
            recovery.Password = password.Password = confirm.Password = string.Empty;
            newKey = null;
            if (wizardVersion == ViewModel.LifecycleVersion)
            {
                ViewModel.CancelRecoveryKeyReset();
                ApplyShellState();
            }
            recoveryWizardInProgress = false;
        }
    }

    private void BackToWelcomeButton_Click(object sender, RoutedEventArgs e)
    {
        ClearFirstLaunchInputs();
        ViewModel.CancelFirstLaunchStep();
        ApplyShellState();
    }

    private void ExitButton_Click(object sender, RoutedEventArgs e) => Application.Current.Exit();

    private void SensitiveClipboardKeyboardAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (FocusManager.GetFocusedElement(XamlRoot) is FrameworkElement { Tag: "SensitiveInput" })
            args.Handled = true;
    }

    private void SensitiveTextBox_ContextMenuOpening(object sender, ContextMenuEventArgs e) => e.Handled = true;

    private void SystemLockMonitor_LockRequired(object? sender, EventArgs e)
    {
        DispatcherQueue.TryEnqueue(async () =>
        {
            if (Interlocked.Exchange(ref lifecycleLockInProgress, 1) != 0) return;
            try
            {
                var wasFirstLaunch = ViewModel.FlowState is AppFlowState.FirstLaunch or AppFlowState.CreateMasterPassword or AppFlowState.SetupAuthenticator
                    || ViewModel.FlowState == AppFlowState.Recover && !ViewModel.HasPartialStorage;
                var wasPartialStorage = ViewModel.FlowState == AppFlowState.Recover && ViewModel.HasPartialStorage;
                App.Services.GetRequiredService<DialogLifetime>().DismissAll();
                var locking = ViewModel.LockCommand.ExecuteAsync(null);
                var version = ViewModel.LifecycleVersion;
                await locking;
                if (version != ViewModel.LifecycleVersion) return;
                if (wasFirstLaunch) ViewModel.CancelFirstLaunchStep();
                else if (wasPartialStorage) ViewModel.BeginRecovery();
                ClearEditor();
                ClearSettingsInputs();
                ClearBackupInputs();
                ClearFirstLaunchInputs();
                ApplyShellState();
                MasterPasswordInput.Focus(FocusState.Programmatic);
            }
            finally
            {
                systemLockMonitor.Start();
                Interlocked.Exchange(ref lifecycleLockInProgress, 0);
            }
        });
    }

    private void ApplyShellState()
    {
        UpdateVaultLockButton();
        if (ViewModel.FlowState != AppFlowState.Unlocked) CloseTotpPanel();
        if (ViewModel.IsTableLocked)
        {
            if (!preservingEditorDuringTimeoutUnlock) ClearEditor();
            ClearSettingsInputs(); ClearBackupInputs();
            App.Services.GetRequiredService<DialogLifetime>().DismissAll();
        }
        AuthenticationPanel.Visibility = ViewModel.IsUnlocked ? Visibility.Collapsed : Visibility.Visible;
        ShellNavigation.Visibility = ViewModel.IsUnlocked ? Visibility.Visible : Visibility.Collapsed;
        LoadingPanel.Visibility = Visibility.Collapsed;
        PartialStoragePanel.Visibility = Visibility.Collapsed;
        FirstLaunchPanel.Visibility = Visibility.Collapsed;
        RecoveryPanel.Visibility = Visibility.Collapsed;
        CreateMasterPasswordPanel.Visibility = Visibility.Collapsed;
        SetupAuthenticatorPanel.Visibility = Visibility.Collapsed;
        UnlockPanel.Visibility = Visibility.Collapsed;
        UnlockHeading.Text = ViewModel.IsSignedIn ? "Unlock Vault" : "Login";
        UnlockDescription.Text = ViewModel.IsSignedIn
            ? "Enter your Master Password to unlock the vault."
            : "Enter your Master Password and Google Authenticator code to sign in.";
        UnlockButton.Content = ViewModel.IsSignedIn ? "Unlock" : "Sign in";
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(UnlockButton, ViewModel.IsSignedIn ? "Unlock vault" : "Sign in");

        if (!ViewModel.IsUnlocked)
        {
            ClearEditor();
            ClearSettingsInputs();
            ClearBackupInputs();
            MasterPasswordInput.Password = string.Empty;
            App.Services.GetRequiredService<DialogLifetime>().DismissAll();
            if (ViewModel.FlowState is AppFlowState.Unlock or AppFlowState.FirstLaunch)
                ClearFirstLaunchInputs();
            switch (ViewModel.FlowState)
            {
                case AppFlowState.Loading:
                    LoadingPanel.Visibility = Visibility.Visible;
                    break;
                case AppFlowState.FirstLaunch:
                    FirstLaunchPanel.Visibility = Visibility.Visible;
                    break;
                case AppFlowState.Recover when ViewModel.HasPartialStorage:
                    PartialStoragePanel.Visibility = Visibility.Visible;
                    break;
                case AppFlowState.Recover:
                    RecoveryPanel.Visibility = Visibility.Visible;
                    break;
                case AppFlowState.CreateMasterPassword:
                    CreateMasterPasswordPanel.Visibility = Visibility.Visible;
                    break;
                case AppFlowState.SetupAuthenticator:
                    SetupAuthenticatorPanel.Visibility = Visibility.Visible;
                    break;
                default:
                    UnlockPanel.Visibility = Visibility.Visible;
                    break;
            }
        }

        AuthenticationCard.MaxWidth = UnlockPanel.Visibility == Visibility.Visible ? 460 : 600;
        ApplyRoute(ViewModel.CurrentRoute);
    }

    private void FocusCurrentAuthenticationStep()
    {
        if (ViewModel.IsUnlocked) return;
        switch (ViewModel.FlowState)
        {
            case AppFlowState.Unlock:
                MasterPasswordInput.Focus(FocusState.Programmatic);
                break;
            case AppFlowState.CreateMasterPassword:
                NewMasterPasswordInput.Focus(FocusState.Programmatic);
                break;
        }
    }

    private void ClearFirstLaunchInputs()
    {
        NewMasterPasswordInput.Password = string.Empty;
        ConfirmMasterPasswordInput.Password = string.Empty;
        RecoveryPassphraseInput.Password = string.Empty;
        AuthenticatorConfirmationInput.Clear();
        AuthenticatorSecretText.Text = string.Empty;
        AuthenticatorQrImage.Source = null;
        RecoverySummaryInfoBar.IsOpen = false;
    }

    private static async Task<BitmapImage> CreateQrBitmapAsync(string value)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(value, QRCodeGenerator.ECCLevel.Q);
        using var qrCode = new PngByteQRCode(data);
        var png = qrCode.GetGraphic(12);
        try
        {
            using var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream))
            {
                writer.WriteBytes(png);
                await writer.StoreAsync();
                writer.DetachStream();
            }
            stream.Seek(0);
            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(stream);
            return bitmap;
        }
        finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(png); }
    }

    private void ApplyRoute(AppRoute route)
    {
        if (route != AppRoute.Vault) CloseTotpPanel();
        WorkspaceContent.MaxWidth = double.PositiveInfinity;
        updatingNavigation = true;
        try
        {
            ShellNavigation.SelectedItem = ShellNavigation.MenuItems.OfType<NavigationViewItem>()
                .FirstOrDefault(item => item.Tag as string == route.ToString());
        }
        finally { updatingNavigation = false; }
        VaultPage.Visibility = route == AppRoute.Vault ? Visibility.Visible : Visibility.Collapsed;
        TrashPage.Visibility = route == AppRoute.Trash ? Visibility.Visible : Visibility.Collapsed;
        EditorPage.Visibility = route == AppRoute.ItemEditor ? Visibility.Visible : Visibility.Collapsed;
        HashToolPage.Visibility = route == AppRoute.HashTool ? Visibility.Visible : Visibility.Collapsed;
        BackupPage.Visibility = route == AppRoute.Backup ? Visibility.Visible : Visibility.Collapsed;
        SecurityCheckPage.Visibility = route == AppRoute.SecurityCheck ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = route == AppRoute.Settings ? Visibility.Visible : Visibility.Collapsed;
        if (route == AppRoute.Settings)
            VaultDurationInput.SelectedIndex = Array.IndexOf(new[] { 1, 2, 5, 10, 30, 60, 120, 300 }, (int)ViewModel.Settings.VaultDurationMinutes);
    }

    private void AddItemButton_Click(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.IsUnlocked || ViewModel.CurrentRoute == AppRoute.ItemEditor) return;
        ClearEditor();
        ViewModel.BeginAddItem();
        editorBaseline = ReadEditorInput();
        ApplyRoute(AppRoute.ItemEditor);
        EditorItemTitle.Focus(FocusState.Programmatic);
    }

    private async void AddGroupButton_Click(object sender, RoutedEventArgs e)
    {
        var name = await PromptAsync("Create group", "Group name", string.Empty);
        if (name is not null) await ViewModel.CreateGroupAsync(name);
    }

    private async void VaultItems_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (sender is ListView { SelectedItem: VaultItemListItem item }) await OpenEditorAsync(item.Id);
    }

    private async void VaultItems_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Enter) return;
        e.Handled = true;
        if (sender is ListView { SelectedItem: VaultItemListItem item }) await OpenEditorAsync(item.Id);
    }

    private async Task OpenSelectedEditorAsync()
    {
        if (ViewModel.Vault.SelectedItem is not { } selected) return;
        await OpenEditorAsync(selected.Id);
    }

    private void GroupHeaderButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is VaultItemGroup group) ViewModel.Vault.SelectGroup(group);
    }

    private async void RenameGroupMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not VaultItemGroup { Id: { } id, IsAll: false } group) return;
        var version = ViewModel.LifecycleVersion;
        var name = await PromptAsync("Rename tab", "Tab name", DisplayGroupName(group), "Confirm", "TxtTabName");
        if (name is null || !ViewModel.IsCurrentUnlock(version)) return;
        await ViewModel.UpdateGroupAsync(id, name, group.AccentColor);
    }

    private async void ChangeGroupColorMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not VaultItemGroup { Id: { } id, IsAll: false } group) return;
        var version = ViewModel.LifecycleVersion;
        var chosen = await PickColorAsync(DisplayGroupName(group), GroupColor(group));
        if (!chosen.Confirmed || !ViewModel.IsCurrentUnlock(version)) return;
        await ViewModel.UpdateGroupAsync(id, group.Name, chosen.Color);
    }

    private async Task<(bool Confirmed, string? Color)> PickColorAsync(string tabName, string? current)
    {
        var picker = new ColorPicker
        {
            Color = ((SolidColorBrush)GroupBrush(current ?? AppearanceSettings.DefaultAccent)).Color,
            IsAlphaEnabled = false, IsHexInputVisible = false,
            IsColorChannelTextInputVisible = false, IsMoreButtonVisible = false,
            IsAlphaSliderVisible = false, IsAlphaTextInputVisible = false
        };
        var input = new TextBox { Header = "RGB color (#RRGGBB)", Text = current ?? AppearanceSettings.DefaultAccent, MaxLength = 7 };
        var error = new InfoBar { IsClosable = false, Severity = InfoBarSeverity.Error, Title = "Color is not valid" };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(picker, "GroupColorPicker");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(picker, "Choose a color");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(input, "TxtGroupColorHex");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(error, "GroupColorError");
        var previewText = new TextBlock { Text = tabName, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
        var preview = new Border { Child = previewText, Padding = new Thickness(16, 8, 16, 8), CornerRadius = new CornerRadius(6), HorizontalAlignment = HorizontalAlignment.Left };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(preview, "TabColorPreview");
        void UpdatePreview(string color)
        {
            preview.Background = GroupBrush(color);
            previewText.Foreground = GroupTextBrush(color);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(preview, $"Preview: {tabName}, {color}");
        }
        UpdatePreview(current ?? AppearanceSettings.DefaultAccent);
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(preview);
        panel.Children.Add(input);
        panel.Children.Add(error);
        panel.Children.Add(picker);
        var dialog = new AppContentDialog
        {
            Title = $"Color for {tabName}", Content = panel, PrimaryButtonText = "Save",
            SecondaryButtonText = "Reset color", CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary
        };
        var syncing = false;
        picker.ColorChanged += (_, args) =>
        {
            if (syncing) return;
            input.Text = $"#{args.NewColor.R:X2}{args.NewColor.G:X2}{args.NewColor.B:X2}";
        };
        input.TextChanged += (_, _) =>
        {
            try
            {
                var normalized = AppearanceSettings.NormalizeRgb(input.Text);
                syncing = true;
                try { picker.Color = ((SolidColorBrush)GroupBrush(normalized)).Color; }
                finally { syncing = false; }
                UpdatePreview(normalized);
                error.IsOpen = false;
            }
            catch (ArgumentException) { error.Message = "Enter # followed by six hexadecimal digits, such as #204BDB."; error.IsOpen = true; }
        };
        dialog.PrimaryButtonClick += (_, args) =>
        {
            try { AppearanceSettings.NormalizeRgb(input.Text); }
            catch (ArgumentException) { args.Cancel = true; error.IsOpen = true; }
        };
        var result = await App.Services.GetRequiredService<DialogLifetime>().ShowAsync(dialog, CancellationToken.None);
        return result switch
        {
            ContentDialogResult.Primary => (true, AppearanceSettings.NormalizeRgb(input.Text)),
            ContentDialogResult.Secondary => (true, null),
            _ => (false, null)
        };
    }

    private async void DeleteGroupMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is VaultItemGroup { Id: { } id } group) await ViewModel.DeleteGroupAsync(id, group.Name);
    }

    private async Task<string?> PromptAsync(string title, string header, string value, string primaryText = "Save", string? automationId = null)
    {
        var input = new TextBox { Header = header, Text = value, MinWidth = 320 };
        if (automationId is not null) Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(input, automationId);
        var dialog = new AppContentDialog { XamlRoot = XamlRoot, Title = title, Content = input, PrimaryButtonText = primaryText, CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary };
        if (primaryText == "Confirm")
        {
            input.MaxLength = 100;
            dialog.IsPrimaryButtonEnabled = !string.IsNullOrWhiteSpace(input.Text);
            input.TextChanged += (_, _) => dialog.IsPrimaryButtonEnabled = !string.IsNullOrWhiteSpace(input.Text);
        }
        dialog.Opened += (_, _) => { input.Focus(FocusState.Programmatic); input.SelectAll(); };
        return await App.Services.GetRequiredService<DialogLifetime>().ShowAsync(dialog, CancellationToken.None) == ContentDialogResult.Primary ? input.Text : null;
    }

    private async Task OpenEditorAsync(Guid itemId, bool duplicate = false)
    {
        if (ViewModel.CurrentRoute == AppRoute.ItemEditor) return;
        var version = ViewModel.LifecycleVersion;
        var route = ViewModel.CurrentRoute;
        var item = await ViewModel.GetItemForEditingAsync(itemId);
        if (item is null || !ViewModel.IsCurrentUnlock(version) || ViewModel.CurrentRoute != route) return;

        editingItemId = duplicate ? null : item.Id;
        EditorTitle.Text = duplicate ? "Duplicate item" : "Edit item";
        EditorItemTitle.Text = item.Title + (duplicate ? " (copy)" : string.Empty);
        EditorUsername.Text = item.Username;
        EditorPassword.Password = item.Password;
        EditorRecoveryCodes.Text = string.Join(Environment.NewLine, item.RecoveryCodes);
        draftTotp = item.GetTotpConfiguration();
        UpdateTotpEditorStatus();
        EditorUrl.Text = item.Url;
        EditorNoUrl.IsChecked = string.IsNullOrWhiteSpace(item.Url);
        EditorUrl.IsEnabled = EditorNoUrl.IsChecked != true;
        EditorGroup.SelectedItem = ViewModel.Vault.GroupOptions.FirstOrDefault(option => option.GroupId == item.GroupId && !option.CreatesNew) ?? ViewModel.Vault.GroupOptions[0];
        EditorTags.Text = string.Join(", ", item.Tags);
        EditorNotes.Text = item.Notes;
        EditorFavorite.IsChecked = item.IsFavorite;
        EditorHideUrl.IsChecked = item.HideUrl;
        EditorHideNotes.IsChecked = item.HideNotes;
        editorBaseline = ReadEditorInput();
        ViewModel.Navigate(AppRoute.ItemEditor);
        ApplyRoute(AppRoute.ItemEditor);
        EditorItemTitle.Focus(FocusState.Programmatic);
    }

    private void MoreItemActions_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: Guid id } button) return;
        var flyout = new MenuFlyout();
        void Add(string title, RoutedEventHandler handler, Symbol? icon = null)
        {
            var menu = new MenuFlyoutItem { Text = title, Tag = id };
            if (icon is { } symbol) menu.Icon = new SymbolIcon(symbol);
            menu.Click += handler;
            flyout.Items.Add(menu);
        }
        Add("View details", async (_, _) => await ShowItemDetailsAsync(id), Symbol.View);
        Add("Duplicate", async (_, _) => await OpenEditorAsync(id, duplicate: true), Symbol.Copy);
        Add("Move to group", async (_, _) => await MoveItemToGroupAsync(id), Symbol.Folder);
        if (ViewModel.Vault.Items.Any(item => item.Id == id && item.HasPassword))
            Add("History", PasswordHistoryRowMenuItem_Click, Symbol.Clock);
        flyout.Items.Add(new MenuFlyoutSeparator());
        Add("Delete", TrashRowMenuItem_Click, Symbol.Delete);
        ((MenuFlyoutItem)flyout.Items[^1]).Foreground = (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"];
        flyout.ShowAt(button);
    }

    private async Task MoveItemToGroupAsync(Guid id)
    {
        var version = ViewModel.LifecycleVersion;
        var item = await ViewModel.GetItemForEditingAsync(id);
        if (item is null || !ViewModel.IsCurrentUnlock(version)) return;
        var picker = new AppComboBox { Header = "Group", ItemsSource = ViewModel.Vault.GroupOptions.Where(group => !group.CreatesNew).ToList(), DisplayMemberPath = "Label" };
        picker.SelectedItem = ((IEnumerable<VaultGroupOption>)picker.ItemsSource).FirstOrDefault(group => group.GroupId == item.GroupId);
        var dialog = new AppContentDialog { Title = "Move to group", Content = picker, PrimaryButtonText = "Move", CloseButtonText = "Cancel" };
        if (await App.Services.GetRequiredService<DialogLifetime>().ShowAsync(dialog, CancellationToken.None) != ContentDialogResult.Primary
            || !ViewModel.IsCurrentUnlock(version) || picker.SelectedItem is not VaultGroupOption selected) return;
        await ViewModel.MoveItemToGroupAsync(id, selected.GroupId);
    }

    private async Task ShowItemDetailsAsync(Guid id)
    {
        var version = ViewModel.LifecycleVersion;
        var item = ViewModel.Vault.Items.FirstOrDefault(item => item.Id == id);
        if (item is null || !ViewModel.IsCurrentUnlock(version)) return;
        var panel = new StackPanel { Spacing = 12 };
        var details = new Grid { ColumnSpacing = 16, RowSpacing = 8 };
        details.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        details.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        foreach (var (label, value) in new[]
        {
            ("Title", item.Title), ("Username", item.Username),
            ("URL", item.Url), ("Updated", item.UpdatedDisplay)
        })
        {
            var row = details.RowDefinitions.Count;
            details.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var heading = new TextBlock { Text = label, Opacity = 0.7 };
            var text = new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(value) ? "—" : value,
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true
            };
            Grid.SetRow(heading, row);
            Grid.SetRow(text, row);
            Grid.SetColumn(text, 1);
            details.Children.Add(heading);
            details.Children.Add(text);
        }
        panel.Children.Add(details);
        if (item.HasRecoveryCodes)
        {
            panel.Children.Add(new TextBlock { Text = $"{item.RecoveryCodeCount} recovery codes saved" });
            panel.Children.Add(new TextBlock
            {
                Text = "Use these recovery codes to regain access to this website or app account. To recover your YourSafe vault, use your YourSafe recovery code.",
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.7
            });
        }
        var dialog = new AppContentDialog
        {
            Title = "View details", Content = panel, CloseButtonText = "Close", CompactFooter = true,
            DefaultButton = ContentDialogButton.Close
        };
        dialog.Resources["ContentDialogPadding"] = new Thickness(20, 16, 20, 16);
        void View(string label, Func<Task> action)
        {
            var button = new Button { Content = label };
            button.Click += async (_, _) => { dialog.Hide(); await action(); };
            panel.Children.Add(button);
        }
        TotpPanel? totpPanel = null;
        if (item.HasTotp)
        {
            if (!await EnsureTotpUnlockedAsync()) return;
            CloseTotpPanel();
            totpPanel = activeTotpPanel = new TotpPanel(id, item.Title, item.Username);
            panel.Children.Add(totpPanel);
            View("Manage TOTP", () => ManageTotpAsync(id));
        }
        if (item.HasPassword) View("View password", () => ViewModel.RevealPasswordAsync(id));
        if (item.HasRecoveryCodes) View("View recovery codes", () => ViewModel.RevealRecoveryCodesAsync(id));
        if (item.HasRecoveryCodes) View("Copy recovery codes", () => CopyRecoveryCodesAsync(id));
        if (item.HasNotes) View("View notes", () => ViewModel.RevealNotesAsync(id));
        try { await App.Services.GetRequiredService<DialogLifetime>().ShowAsync(dialog, CancellationToken.None); }
        finally
        {
            totpPanel?.Dispose();
            if (activeTotpPanel == totpPanel) activeTotpPanel = null;
            panel.Children.Clear();
        }
    }

    private async void SaveEditorButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button saveButton || !saveButton.IsEnabled || ViewModel.IsSavingItem) return;
        saveButton.IsEnabled = false;
        try
        {
            var version = ViewModel.LifecycleVersion;
            if (!ViewModel.IsCurrentUnlock(version) || ViewModel.CurrentRoute != AppRoute.ItemEditor) return;
            if (string.IsNullOrWhiteSpace(EditorItemTitle.Text) || string.IsNullOrWhiteSpace(EditorUsername.Text)
                || string.IsNullOrWhiteSpace(EditorPassword.Password) && draftTotp is null && string.IsNullOrWhiteSpace(EditorRecoveryCodes.Text)
                || EditorNoUrl.IsChecked != true && string.IsNullOrWhiteSpace(EditorUrl.Text))
            {
                var dialog = new AppContentDialog
                {
                    Title = "Required fields",
                    Content = "Enter a title, username, and a password, TOTP, or recovery codes. Enter a URL or check No URL.",
                    CloseButtonText = "OK"
                };
                await App.Services.GetRequiredService<DialogLifetime>().ShowAsync(dialog, CancellationToken.None);
                return;
            }
            var groupOption = EditorGroup.SelectedItem as VaultGroupOption ?? ViewModel.Vault.GroupOptions[0];
            if (groupOption.CreatesNew)
            {
                var name = await PromptAsync("Create group", "Group name", string.Empty);
                if (name is null || !ViewModel.IsCurrentUnlock(version) || ViewModel.CurrentRoute != AppRoute.ItemEditor) return;
                var group = await ViewModel.CreateGroupAsync(name);
                if (group is null || !ViewModel.IsCurrentUnlock(version) || ViewModel.CurrentRoute != AppRoute.ItemEditor) return;
                EditorGroup.SelectedItem = ViewModel.Vault.GroupOptions.First(option => option.GroupId == group.Id);
            }
            var saved = await ViewModel.SaveItemAsync(ReadEditorInput());
            if (!saved || !ViewModel.IsCurrentUnlock(version)) return;
            ClearEditor();
            ApplyRoute(AppRoute.Vault);
        }
        finally { saveButton.IsEnabled = true; }
    }

    private async void CancelEditorButton_Click(object sender, RoutedEventArgs e)
    {
        if (!await CanLeaveEditorAsync()) return;
        ClearEditor();
        ViewModel.Navigate(AppRoute.Vault);
        ApplyRoute(AppRoute.Vault);
    }

    private VaultItemEditorInput ReadEditorInput() => new(
        editingItemId, EditorItemTitle.Text, EditorUsername.Text, EditorPassword.Password,
        EditorRecoveryCodes.Text, string.Empty, EditorUrl.Text, EditorNotes.Text,
        (EditorGroup.SelectedItem as VaultGroupOption)?.GroupId, EditorTags.Text,
        EditorFavorite.IsChecked == true, EditorHideUrl.IsChecked == true, EditorHideNotes.IsChecked == true, draftTotp);

    private async Task<bool> CanLeaveEditorAsync()
    {
        if (ViewModel.CurrentRoute != AppRoute.ItemEditor) return true;
        if (ViewModel.IsSavingItem || confirmingEditorDiscard) return false;
        if (editorBaseline == ReadEditorInput() && EditorGroup.SelectedItem is not VaultGroupOption { CreatesNew: true }) return true;
        var version = ViewModel.LifecycleVersion;
        confirmingEditorDiscard = true;
        try
        {
            return await dialogs.ConfirmAsync("Discard changes?", "Your changes have not been saved. Discard them and leave this item?", "Discard")
                && ViewModel.IsCurrentUnlock(version);
        }
        finally { confirmingEditorDiscard = false; }
    }

    private async void GeneratePasswordButton_Click(object sender, RoutedEventArgs e)
    {
        var version = ViewModel.LifecycleVersion;
        var generated = await passwordGeneratorDialog.ShowAsync();
        if (generated is not null && ViewModel.IsCurrentUnlock(version)) EditorPassword.Password = generated;
    }

    private void ClearEditor()
    {
        editorBaseline = null;
        editingItemId = null;
        draftTotp = null;
        UpdateTotpEditorStatus();
        EditorTitle.Text = "Add item";
        EditorItemTitle.Text = string.Empty;
        EditorUsername.Text = string.Empty;
        EditorPassword.Password = string.Empty;
        EditorRecoveryCodes.Text = string.Empty;
        EditorUrl.Text = string.Empty;
        EditorNoUrl.IsChecked = false;
        EditorUrl.IsEnabled = true;
        EditorGroup.SelectedIndex = 0;
        EditorTags.Text = string.Empty;
        EditorNotes.Text = string.Empty;
        EditorFavorite.IsChecked = false;
        EditorHideUrl.IsChecked = false;
        EditorHideNotes.IsChecked = false;
    }

    private void EditorNoUrl_Changed(object sender, RoutedEventArgs e)
    {
        var noUrl = EditorNoUrl.IsChecked == true;
        EditorUrl.IsEnabled = !noUrl;
        if (noUrl) EditorUrl.Text = string.Empty;
    }

    private void ClearSettingsInputs()
    {
        SettingsMasterPassword.Password = string.Empty;
        CurrentMasterPassword.Password = string.Empty;
        ReplacementMasterPassword.Password = string.Empty;
        ConfirmReplacementMasterPassword.Password = string.Empty;
        AuthenticatorResetMasterPassword.Password = string.Empty;
        AuthenticatorResetCode.Clear();
        SettingsAuthenticatorSecret.Text = string.Empty;
        SettingsAuthenticatorQr.Source = null;
        SettingsAuthenticatorQr.Visibility = Visibility.Collapsed;
        settingsAuthenticatorSetup = null;
    }

    private void ClearBackupInputs()
    {
        BackupPassphrase.Password = string.Empty;
        BackupPassphraseConfirmation.Password = string.Empty;
        SnapshotMasterPassword.Password = string.Empty;
    }

    private async void CopyUsernameRowButton_Click(object sender, RoutedEventArgs e)
    {
        if (GetItemId(sender) is { } id) await ViewModel.CopyUsernameAsync(id);
    }

    private async void RevealPasswordRowButton_Click(object sender, RoutedEventArgs e)
    {
        if (GetItemId(sender) is { } id) await ViewModel.RevealPasswordAsync(id);
    }

    private async void CopyPasswordRowButton_Click(object sender, RoutedEventArgs e)
    {
        if (GetItemId(sender) is { } id) await ViewModel.CopyPasswordAsync(id);
    }

    private async void RevealRecoveryCodesRowButton_Click(object sender, RoutedEventArgs e)
    {
        if (GetItemId(sender) is { } id) await ViewModel.RevealRecoveryCodesAsync(id);
    }

    private async void RevealNotesRowButton_Click(object sender, RoutedEventArgs e)
    {
        if (GetItemId(sender) is { } id) await ViewModel.RevealNotesAsync(id);
    }

    private async void EditRowMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (GetItemId(sender) is { } id) await OpenEditorAsync(id);
    }

    private async void PasswordHistoryRowMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (GetItemId(sender) is { } id) await ViewModel.ViewPasswordHistoryAsync(id);
    }

    private async void TrashRowMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (GetItemId(sender) is { } id) await ViewModel.DeleteItemAsync(id);
    }

    private static Guid? GetItemId(object sender) => (sender as FrameworkElement)?.Tag is Guid id ? id : null;

    private void CloseTrashButton_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.Navigate(AppRoute.Vault);
        ApplyRoute(AppRoute.Vault);
    }

    private async void RestoreTrashButton_Click(object sender, RoutedEventArgs e) => await ViewModel.Trash.RestoreSelectedAsync();
    private async void DeleteTrashPermanentlyButton_Click(object sender, RoutedEventArgs e) => await ViewModel.Trash.PermanentlyDeleteSelectedAsync();

    private void SearchKeyboardAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (!ViewModel.IsUnlocked || ViewModel.CurrentRoute != AppRoute.Vault) return;
        VaultSearchInput.Focus(FocusState.Programmatic);
        VaultSearchInput.SelectAll();
        args.Handled = true;
    }

    private void NewItemKeyboardAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (!ViewModel.IsUnlocked) return;
        if (ViewModel.CurrentRoute == AppRoute.ItemEditor) { args.Handled = true; return; }
        AddItemButton_Click(sender, new RoutedEventArgs());
        args.Handled = true;
    }

    private void HashModeSelector_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (HashGeneratePanel is null) return;
        var mode = sender.Items.IndexOf(sender.SelectedItem);
        HashGeneratePanel.Visibility = mode == 0 ? Visibility.Visible : Visibility.Collapsed;
        HashVerifyPanel.Visibility = mode == 1 ? Visibility.Visible : Visibility.Collapsed;
        HashInspectPanel.Visibility = mode == 2 ? Visibility.Visible : Visibility.Collapsed;
        HashErrorInfoBar.IsOpen = false;
    }

    private void GenerateHashButton_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.HashTool.GenerateCommand.Execute(HashGeneratePassword.Password);
        HashGeneratePassword.Password = string.Empty;
        HashErrorInfoBar.IsOpen = !string.IsNullOrWhiteSpace(ViewModel.HashTool.ErrorMessage);
    }

    private void VerifyHashButton_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.HashTool.VerifyCommand.Execute(new HashVerificationRequest(HashVerifyPassword.Password, HashVerifyStored.Text));
        HashVerifyPassword.Password = string.Empty;
        HashErrorInfoBar.IsOpen = !string.IsNullOrWhiteSpace(ViewModel.HashTool.ErrorMessage);
    }

    private void InspectHashButton_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.HashTool.InspectCommand.Execute(HashInspectStored.Text);
        HashErrorInfoBar.IsOpen = !string.IsNullOrWhiteSpace(ViewModel.HashTool.ErrorMessage);
        var info = ViewModel.HashTool.Inspection;
        HashInspectionText.Text = info is null
            ? string.Empty
            : $"Algorithm: {info.AlgorithmName}\nSecure for password storage: {(info.IsSecureForPasswordStorage ? "Yes" : "No")}\n" +
              $"Version: {info.Version?.ToString() ?? "n/a"}\nIterations: {info.Iterations?.ToString("N0") ?? "n/a"}\n" +
              $"Work factor: {info.WorkFactor?.ToString() ?? "n/a"}\nMemory cost: {info.MemoryCost?.ToString("N0") ?? "n/a"}\n{info.Notes}";
    }

    private async void SaveSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        if (VaultDurationInput.SelectedIndex < 0) return;
        ViewModel.Settings.VaultDurationMinutes = new[] { 1, 2, 5, 10, 30, 60, 120, 300 }[VaultDurationInput.SelectedIndex];
        try
        {
            var version = ViewModel.LifecycleVersion;
            if (await ViewModel.Settings.SaveAsync(SettingsMasterPassword.Password) && ViewModel.IsCurrentUnlock(version))
            {
                var dialog = new AppContentDialog
                {
                    Title = "Settings saved",
                    Content = "Vault timeout saved. Restart YourSafe to apply it.",
                    CloseButtonText = "Got it"
                };
                await App.Services.GetRequiredService<DialogLifetime>().ShowAsync(dialog, CancellationToken.None);
            }
        }
        finally { SettingsMasterPassword.Password = string.Empty; }
    }


    private async void ChangeMasterPasswordButton_Click(object sender, RoutedEventArgs e)
    {
        var changed = await ViewModel.Settings.ChangeMasterPasswordAsync(
            CurrentMasterPassword.Password,
            ReplacementMasterPassword.Password,
            ConfirmReplacementMasterPassword.Password);
        if (!changed) return;
        CurrentMasterPassword.Password = string.Empty;
        ReplacementMasterPassword.Password = string.Empty;
        ConfirmReplacementMasterPassword.Password = string.Empty;
    }

    private async void UpgradeKdfButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || !button.IsEnabled) return;
        button.IsEnabled = false;
        var version = ViewModel.LifecycleVersion;
        try
        {
            if (!await dialogs.ConfirmAsync("Upgrade password protection?",
                "This makes guessing your password harder. Your Master Password and saved items stay the same. Upgrade now?", "Upgrade")
                || !ViewModel.IsCurrentUnlock(version)) return;
            if (await ViewModel.Settings.UpgradeKdfAsync(CurrentMasterPassword.Password))
                CurrentMasterPassword.Password = string.Empty;
        }
        finally { button.IsEnabled = ViewModel.Settings.NeedsKdfUpgrade; }
    }

    private async void PrepareAuthenticatorResetButton_Click(object sender, RoutedEventArgs e)
    {
        var version = ViewModel.LifecycleVersion;
        var setup = settingsAuthenticatorSetup = ViewModel.Settings.PrepareAuthenticator();
        var bitmap = await CreateQrBitmapAsync(setup.OtpAuthUri);
        if (!ViewModel.IsCurrentUnlock(version) || settingsAuthenticatorSetup != setup) return;
        SettingsAuthenticatorSecret.Text = setup.SecretBase32;
        SettingsAuthenticatorQr.Source = bitmap;
        SettingsAuthenticatorQr.Visibility = Visibility.Visible;
        AuthenticatorResetCode.FocusFirst();
    }

    private async void ResetAuthenticatorButton_Click(object sender, RoutedEventArgs e)
    {
        if (settingsAuthenticatorSetup is null) return;
        var reset = await ViewModel.Settings.ResetAuthenticatorAsync(
            AuthenticatorResetMasterPassword.Password,
            settingsAuthenticatorSetup,
            AuthenticatorResetCode.Code);
        if (!reset) return;

        AuthenticatorResetMasterPassword.Password = string.Empty;
        AuthenticatorResetCode.Clear();
        SettingsAuthenticatorSecret.Text = string.Empty;
        SettingsAuthenticatorQr.Source = null;
        SettingsAuthenticatorQr.Visibility = Visibility.Collapsed;
        settingsAuthenticatorSetup = null;
    }

    private async void BackupReminderCreate_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.IsBackupReminderOpen = false;
        ViewModel.Navigate(AppRoute.Backup);
        ApplyRoute(AppRoute.Backup);
        await ViewModel.Backup.LoadAsync();
    }

    private void BackupReminderLater_Click(object sender, RoutedEventArgs e) => ViewModel.IsBackupReminderOpen = false;

    private async void ExportBackupButton_Click(object sender, RoutedEventArgs e) =>
        await ViewModel.Backup.ExportAsync();

    private async void VerifyBackupButton_Click(object sender, RoutedEventArgs e) =>
        await ViewModel.Backup.VerifyAsync();

    private async void PreviewBackupImportButton_Click(object sender, RoutedEventArgs e) =>
        await ViewModel.Backup.PreviewImportAsync();

    private async void ImportBackupButton_Click(object sender, RoutedEventArgs e) =>
        await ViewModel.Backup.ImportBackupAsync();

    private async void PreviewCsvButton_Click(object sender, RoutedEventArgs e) => await ViewModel.Backup.PreviewCsvAsync();
    private async void ImportCsvButton_Click(object sender, RoutedEventArgs e) => await ViewModel.Backup.ImportCsvAsync();

    private async void RestoreSnapshotButton_Click(object sender, RoutedEventArgs e)
    {
        if (!await ViewModel.Backup.RestoreSnapshotAsync(SnapshotMasterPassword.Password)) return;
        SnapshotMasterPassword.Password = string.Empty;
        await ViewModel.LockCommand.ExecuteAsync(null);
        ClearEditor();
        ClearSettingsInputs();
        ClearBackupInputs();
        ApplyShellState();
        FocusCurrentAuthenticationStep();
    }

    private async void RunSecurityCheckButton_Click(object sender, RoutedEventArgs e) => await ViewModel.SecurityCheck.RunAsync();

    private async void EditSecurityFindingButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SecurityCheck.SelectedFinding is not { } finding) return;
        ViewModel.Vault.SelectItem(finding.ItemId);
        await OpenSelectedEditorAsync();
    }
}
