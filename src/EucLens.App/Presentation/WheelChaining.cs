using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace EucLens.App.Presentation;

/// <summary>
/// A scroll viewer nested in a scrolling page captures the mouse wheel even at its end, so the page seems stuck.
/// With chaining enabled the wheel scrolls the inner pane first and the page once the pane has reached its end.
/// </summary>
public static class WheelChaining
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached("Enabled", typeof(bool), typeof(WheelChaining),
        new PropertyMetadata(false, Changed));
    public static bool GetEnabled(DependencyObject target) => (bool)target.GetValue(EnabledProperty);
    public static void SetEnabled(DependencyObject target, bool value) => target.SetValue(EnabledProperty, value);

    private static void Changed(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        if (target is not ScrollViewer viewer) return;
        viewer.PreviewMouseWheel -= Wheel;
        if ((bool)args.NewValue) viewer.PreviewMouseWheel += Wheel;
    }

    /// <summary>True when the viewer cannot scroll further in the direction of <paramref name="delta"/>.</summary>
    public static bool IsAtEdge(ScrollViewer viewer, int delta) =>
        viewer.ScrollableHeight <= 0 || (delta > 0 ? viewer.VerticalOffset <= 0 : viewer.VerticalOffset >= viewer.ScrollableHeight - 0.5);

    private static void Wheel(object sender, MouseWheelEventArgs e)
    {
        var viewer = (ScrollViewer)sender;
        if (e.Handled || !IsAtEdge(viewer, e.Delta) || VisualTreeHelper.GetParent(viewer) is not UIElement parent) return;
        e.Handled = true;
        parent.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta) { RoutedEvent = UIElement.MouseWheelEvent, Source = viewer });
    }
}
