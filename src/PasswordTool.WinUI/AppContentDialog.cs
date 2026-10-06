using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace PasswordTool_WinUI;

internal sealed class AppContentDialog : ContentDialog
{
    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        if (string.IsNullOrEmpty(PrimaryButtonText) || string.IsNullOrEmpty(CloseButtonText)
            || GetTemplateChild("CommandSpace") is not Grid commands) return;

        // Reverse the native footer without mirroring button text or changing dialog results.
        commands.FlowDirection = FlowDirection.RightToLeft;
        foreach (var button in commands.Children.OfType<Button>())
        {
            button.FlowDirection = FlowDirection.LeftToRight;
            button.TabIndex = button.Name switch
            {
                "CloseButton" => 0,
                "SecondaryButton" => 1,
                _ => 2
            };
        }
    }
}
