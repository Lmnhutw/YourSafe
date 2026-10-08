using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace PasswordTool_WinUI;

internal sealed class AppContentDialog : ContentDialog
{
    public bool CompactFooter { get; init; }

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        if (CompactFooter && GetTemplateChild("CommandSpace") is Grid footer)
        {
            footer.Padding = new Thickness(20, 10, 20, 10);
            if (GetTemplateChild("CloseButton") is Button close)
            {
                close.HorizontalAlignment = HorizontalAlignment.Right;
                close.MinWidth = 96;
            }
        }
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
