using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Markup;
using PasswordTool.Presentation.Autofill;

namespace PasswordTool_WinUI;

internal sealed class AutofillApprovalService(DialogLifetime dialogs)
{
    public Task<bool> ApproveAsync(AutofillApprovalRequest request, CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!App.DispatcherQueue.TryEnqueue(async () =>
        {
            if (cancellationToken.IsCancellationRequested) { completion.TrySetResult(false); return; }
            try
            {
                if (App.Window.AppWindow.Presenter is OverlappedPresenter presenter && presenter.State != OverlappedPresenterState.Restored)
                    presenter.Restore();
                App.Window.AppWindow.Show();
                App.Window.Activate();
                var action = request.Action switch
                {
                    AutofillAction.Password => "Approve to fill password?",
                    AutofillAction.ViewTotp => "Approve to view verification code?",
                    AutofillAction.CopyTotp => "Approve to copy verification code?",
                    _ => throw new InvalidOperationException()
                };
                var disclosure = request.Action switch
                {
                    AutofillAction.Password => "Your saved password will be shared with the extension for one autofill. The destination website can read the filled password.",
                    AutofillAction.ViewTotp => "Your current verification code will be shared with the extension once.",
                    AutofillAction.CopyTotp => "Your current verification code will be copied once. Other apps may read the clipboard. YourSafe tries to clear it after 30 seconds unless another app has replaced it.",
                    _ => throw new InvalidOperationException()
                };
                var content = (StackPanel)XamlReader.Load("""
                    <StackPanel xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                                xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" Spacing="20">
                        <Border Height="1" Background="{ThemeResource CardStrokeColorDefaultBrush}" />
                        <StackPanel Spacing="8">
                            <TextBlock Text="Requested website" FontSize="12" Foreground="{ThemeResource TextFillColorSecondaryBrush}" />
                            <Grid ColumnSpacing="12">
                                <Grid.ColumnDefinitions><ColumnDefinition Width="20" /><ColumnDefinition Width="*" /></Grid.ColumnDefinitions>
                                <FontIcon Glyph="&#xE774;" FontSize="16" VerticalAlignment="Top" Margin="0,4,0,0" />
                                <TextBlock x:Name="RequestedWebsite" Grid.Column="1" FontSize="16" FontWeight="SemiBold"
                                           TextWrapping="Wrap" AutomationProperties.AutomationId="TxtAutofillApprovalDetails" />
                            </Grid>
                        </StackPanel>
                        <StackPanel Spacing="8">
                            <TextBlock Text="Account" FontSize="12" Foreground="{ThemeResource TextFillColorSecondaryBrush}" />
                            <Grid ColumnSpacing="12">
                                <Grid.ColumnDefinitions><ColumnDefinition Width="20" /><ColumnDefinition Width="*" /></Grid.ColumnDefinitions>
                                <FontIcon Glyph="&#xE77B;" FontSize="16" VerticalAlignment="Top" Margin="0,4,0,0" />
                                <StackPanel Grid.Column="1" Spacing="2">
                                    <TextBlock x:Name="AccountTitle" FontWeight="SemiBold" TextWrapping="Wrap"
                                               AutomationProperties.AutomationId="TxtAutofillApprovalAccount" />
                                    <TextBlock x:Name="AccountUsername" Foreground="{ThemeResource TextFillColorSecondaryBrush}" TextWrapping="Wrap"
                                               AutomationProperties.AutomationId="TxtAutofillApprovalUsername" />
                                </StackPanel>
                            </Grid>
                        </StackPanel>
                        <Border Padding="12" CornerRadius="4" Background="{ThemeResource AutofillWarningBackgroundBrush}"
                                BorderBrush="{ThemeResource AutofillWarningBorderBrush}" BorderThickness="1"
                                AutomationProperties.AutomationId="AutofillApprovalWarning">
                            <Grid ColumnSpacing="8">
                                <Grid.ColumnDefinitions><ColumnDefinition Width="16" /><ColumnDefinition Width="*" /></Grid.ColumnDefinitions>
                                <FontIcon Glyph="&#xE83D;" FontSize="14" VerticalAlignment="Top" Margin="0,3,0,0" />
                                <StackPanel Grid.Column="1" Spacing="4">
                                    <TextBlock Text="Only approve if this matches your browser tab." FontSize="13" TextWrapping="Wrap" />
                                    <TextBlock Text="YourSafe cannot independently verify the tab address." FontSize="12"
                                               Foreground="{ThemeResource TextFillColorSecondaryBrush}" TextWrapping="Wrap" />
                                </StackPanel>
                            </Grid>
                        </Border>
                        <Expander Header="Saved URL and permissions" HorizontalAlignment="Stretch"
                                  AutomationProperties.AutomationId="ExpAutofillApprovalDetails">
                            <StackPanel Spacing="12">
                                <TextBlock x:Name="SavedWebsite" TextWrapping="Wrap" AutomationProperties.AutomationId="TxtAutofillApprovalSavedUrl" />
                                <TextBlock x:Name="Permissions" TextWrapping="Wrap" AutomationProperties.AutomationId="TxtAutofillApprovalPermissions" />
                            </StackPanel>
                        </Expander>
                    </StackPanel>
                    """);
                ((TextBlock)content.FindName("RequestedWebsite")).Text = request.Origin;
                ((TextBlock)content.FindName("AccountTitle")).Text = request.Credential.Title;
                ((TextBlock)content.FindName("AccountUsername")).Text = request.Credential.Username;
                ((TextBlock)content.FindName("SavedWebsite")).Text = request.Credential.Url;
                ((TextBlock)content.FindName("Permissions")).Text = disclosure;
                var title = (StackPanel)XamlReader.Load("""
                    <StackPanel xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                                xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" Spacing="4">
                        <TextBlock x:Name="ActionTitle" FontSize="20" FontWeight="SemiBold" TextAlignment="Center"
                                   TextWrapping="Wrap" AutomationProperties.AutomationId="TxtAutofillApprovalTitle" />
                        <TextBlock Text="Review this request before approving." FontSize="12" TextAlignment="Center"
                                   Foreground="{ThemeResource TextFillColorSecondaryBrush}" TextWrapping="Wrap" />
                    </StackPanel>
                    """);
                ((TextBlock)title.FindName("ActionTitle")).Text = action;
                var dialog = new AppContentDialog
                {
                    Title = title,
                    CenterTitle = true,
                    Content = new ScrollViewer { Content = content, MaxHeight = 460, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled },
                    PrimaryButtonText = "Approve",
                    CloseButtonText = "Cancel",
                    DefaultButton = ContentDialogButton.Close
                };
                dialog.Resources["ContentDialogMinWidth"] = 440d;
                dialog.Resources["ContentDialogMaxWidth"] = 440d;
                AutomationProperties.SetAutomationId(dialog, "DlgAutofillApproval");
                AutomationProperties.SetName(dialog, action);
                completion.TrySetResult(await dialogs.ShowAsync(dialog, cancellationToken,
                    () => !cancellationToken.IsCancellationRequested) == ContentDialogResult.Primary
                    && !cancellationToken.IsCancellationRequested);
            }
            catch { completion.TrySetResult(false); }
        })) completion.TrySetResult(false);
        return completion.Task;
    }
}
