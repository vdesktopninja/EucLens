using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using EucLens.App.Presentation;
namespace EucLens.App.Views;
public partial class DiagnosticsView : UserControl
{
    private ScrollViewer? page;

    public DiagnosticsView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    /// <summary>
    /// The findings region fills the visible page height. The page's scroll viewer outlives this view, so the subscription is
    /// explicit and removed on unload: a closed Diagnostics page is never kept alive by the page host.
    /// </summary>
    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        for (DependencyObject? parent = VisualTreeHelper.GetParent(this); parent is not null && page is null; parent = VisualTreeHelper.GetParent(parent))
            page = parent as ScrollViewer;
        if (page is null) return;
        page.ScrollChanged += OnPageScrollChanged;
        UpdateRegionHeight();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (page is null) return;
        page.ScrollChanged -= OnPageScrollChanged;
        page = null;
    }

    private void OnPageScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.ViewportHeightChange != 0) UpdateRegionHeight();
    }

    private void UpdateRegionHeight()
    {
        if (page is null) return;
        var height = SplitLayout.RegionHeight(page.ViewportHeight, (double)FindResource("FindingsRegionReserve"), (double)FindResource("FindingsRegionMinHeight"));
        if (double.IsNaN(FindingsRegion.Height) || Math.Abs(FindingsRegion.Height - height) > 0.5) FindingsRegion.Height = height;
    }

    /// <summary>
    /// When the administrator picks a finding (keyboard focus is in the list), the findings region is brought into view so the
    /// evidence is on screen. Automatic selection changes, such as a new top finding, never move the page.
    /// </summary>
    private void OnFindingSelected(object sender, SelectionChangedEventArgs e)
    {
        if (FindingsList.IsKeyboardFocusWithin) FindingsRegion.BringIntoView();
    }
}
