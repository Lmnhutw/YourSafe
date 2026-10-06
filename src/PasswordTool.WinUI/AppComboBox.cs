using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media.Animation;

namespace PasswordTool_WinUI;

public sealed class AppComboBox : ComboBox
{
    private ContentPresenter? selectionPresenter;

    public AppComboBox()
    {
        DropDownOpened += (_, _) => KeepSelectionVisible();
        DropDownClosed += (_, _) => KeepSelectionVisible();
    }

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        selectionPresenter = GetTemplateChild("ContentPresenter") as ContentPresenter;
        if (GetTemplateChild("Popup") is not Popup popup) return;
        popup.PlacementTarget = GetTemplateChild("Background") as FrameworkElement ?? this;
        popup.DesiredPlacement = PopupPlacementMode.BottomEdgeAlignedLeft;
        popup.RegisterPropertyChangedCallback(Popup.HorizontalOffsetProperty, ResetNativeOffset);
        popup.RegisterPropertyChangedCallback(Popup.VerticalOffsetProperty, ResetNativeOffset);
        popup.ChildTransitions = new TransitionCollection
        {
            new PopupThemeTransition { FromHorizontalOffset = 0, FromVerticalOffset = -8 }
        };
        if (GetTemplateChild("LayoutRoot") is FrameworkElement root)
        {
            // The native split animation follows the selected row and hides the faceplate.
            foreach (var group in VisualStateManager.GetVisualStateGroups(root).Where(group => group.Name == "DropDownStates"))
                foreach (var state in group.States.Where(state => state.Name == "Opened")) state.Storyboard = new Storyboard();
        }
    }

    private void KeepSelectionVisible()
    {
        if (selectionPresenter is null || SelectedItem is null) return;
        selectionPresenter.ContentTemplate = string.IsNullOrEmpty(DisplayMemberPath) ? ItemTemplate : null;
        // Keep the binding on a separate text element: binding the presenter's Content
        // prevents ComboBox from replacing it when an option is selected.
        var text = new TextBlock();
        text.SetBinding(TextBlock.TextProperty, new Binding
        {
            Source = SelectedItem is ComboBoxItem item ? item.Content : SelectedItem,
            Path = string.IsNullOrEmpty(DisplayMemberPath) ? null : new PropertyPath(DisplayMemberPath)
        });
        selectionPresenter.Content = text;
    }

    private static void ResetNativeOffset(DependencyObject sender, DependencyProperty property)
    {
        // ComboBox normally offsets the popup to align the selected row with the field.
        if (sender.GetValue(property) is double offset && offset != 0) sender.SetValue(property, 0d);
    }
}
