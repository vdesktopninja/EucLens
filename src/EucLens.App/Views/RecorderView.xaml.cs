using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace EucLens.App.Views;

public partial class RecorderView : UserControl
{
    public RecorderView()
    {
        InitializeComponent();
        // Choosing a recording only selects it. The list scrolls itself to reveal a row; that request must never go on to the page, so the page stays where the user left it.
        RecordingsList.RequestBringIntoView += (_, e) => e.Handled = true;
        // The wheel scrolls the list while it can, and the page when the list is at its end (or too short to scroll), so the page never feels stuck under the pointer.
        RecordingsList.PreviewMouseWheel += OnListWheel;
        RecordingsList.Loaded += (_, _) => { if (ListScroll is { } scroll) { scroll.ScrollChanged -= OnListScrollChanged; scroll.ScrollChanged += OnListScrollChanged; AlignHeader(scroll); } };
        RecordingsList.Unloaded += (_, _) => { if (ListScroll is { } scroll) scroll.ScrollChanged -= OnListScrollChanged; };
    }

    private ScrollViewer? ListScroll => RecordingsList.Template?.FindName("RecordingsScroll", RecordingsList) as ScrollViewer;

    /// <summary>The list reserves room for its scroll bar only while it has one; the header keeps the same right edge so the columns stay aligned.</summary>
    private void OnListScrollChanged(object sender, ScrollChangedEventArgs e) { if (e.ViewportHeightChange != 0 || e.ExtentHeightChange != 0) AlignHeader((ScrollViewer)sender); }

    private void AlignHeader(ScrollViewer scroll)
    {
        var bar = scroll.ComputedVerticalScrollBarVisibility == Visibility.Visible ? SystemParameters.VerticalScrollBarWidth : 0;
        RecordingsHeader.Margin = new Thickness(8, 0, 8 + bar, 2);
    }

    private void OnListWheel(object sender, MouseWheelEventArgs e)
    {
        if (ListScroll is not { } scroll) return;
        var atTop = e.Delta > 0 && scroll.VerticalOffset <= 0;
        var atBottom = e.Delta < 0 && scroll.VerticalOffset >= scroll.ScrollableHeight;
        if (scroll.ScrollableHeight > 0 && !atTop && !atBottom) return;
        e.Handled = true;
        var forwarded = new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta) { RoutedEvent = MouseWheelEvent, Source = sender };
        (RecordingsList.Parent as UIElement)?.RaiseEvent(forwarded);
    }
}
