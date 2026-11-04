using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using EucLens.App.Presentation;
using EucLens.App.ViewModels;

namespace EucLens.App.Views;

public partial class SessionHistoryView : UserControl
{
    public SessionHistoryView()
    {
        InitializeComponent();
        Strip.RangeRequested += OnRange; Overlay.RangeRequested += OnRange;
        Strip.NavigationRequested += OnNavigate; Overlay.NavigationRequested += OnNavigate;
        Strip.MarkerActivated += (_, e) => History?.ActivateMarker(e.At);
        Overlay.HoverChanged += OnHover;
        // The performance timeline shares the range gestures of the strip; a click on a finding selects it, Ctrl+Left and Ctrl+Right step through them.
        PerformanceTimeline.RangeRequested += OnRange; PerformanceTimeline.NavigationRequested += OnNavigate;
        PerformanceTimeline.FindingActivated += (_, e) => { if (History?.Findings.SelectById(e.Id) == true) BringChartIntoView(); };
        PerformanceTimeline.FindingStepRequested += (_, e) => { if (History?.Findings.SelectAdjacent(e.Direction) == true) BringChartIntoView(); };
        foreach (var list in new[] { FindingsList, CorrelationList, ContextList })
        {
            // A click on a finding brings its chart into view; moving with the arrow keys never moves the page. Escape returns to the whole recording.
            list.PreviewMouseLeftButtonUp += OnFindingClicked;
            list.KeyDown += OnFindingKey;
            if (ReferenceEquals(list, FindingsList)) list.PreviewKeyDown += OnGroupKey;
        }
        Loaded += (_, _) => Watch();
        Unloaded += (_, _) => Unwatch();
        DataContextChanged += (_, _) => { if (IsLoaded) { Unwatch(); Watch(); } };
    }

    private SessionHistoryViewModel? watched;

    /// <summary>The event list control, for the Windows validation.</summary>
    internal ListBox EventListControl => EventList;

    // The event list is virtualized, so a selected entry that is not in view has no container yet. Weak handlers: the view model outlives the view.
    private void Watch()
    {
        if (History is not { } history || ReferenceEquals(watched, history)) return;
        Unwatch();
        watched = history;
        PropertyChangedEventManager.AddHandler(history, OnHistoryProperty, nameof(SessionHistoryViewModel.SelectedEvent));
        PropertyChangedEventManager.AddHandler(history.Findings, OnChartsRequested, nameof(HistoryFindingsViewModel.ChartsRequest));
        PropertyChangedEventManager.AddHandler(history.Findings, OnSelectedRowChanged, nameof(HistoryFindingsViewModel.SelectedRow));
        CollectionChangedEventManager.AddHandler(history.Events, OnEventsChanged);
    }

    private void Unwatch()
    {
        if (watched is null) return;
        PropertyChangedEventManager.RemoveHandler(watched, OnHistoryProperty, nameof(SessionHistoryViewModel.SelectedEvent));
        PropertyChangedEventManager.RemoveHandler(watched.Findings, OnChartsRequested, nameof(HistoryFindingsViewModel.ChartsRequest));
        PropertyChangedEventManager.RemoveHandler(watched.Findings, OnSelectedRowChanged, nameof(HistoryFindingsViewModel.SelectedRow));
        CollectionChangedEventManager.RemoveHandler(watched.Events, OnEventsChanged);
        watched = null;
    }

    /// <summary>The selection changed in code (marker, incident navigation, filter): bring the entry into view even if its container does not exist yet.</summary>
    private void OnHistoryProperty(object? sender, PropertyChangedEventArgs e)
    {
        if (History?.SelectedEvent is null) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (History?.SelectedEvent is { } item) EventList.ScrollIntoView(item);
        });
    }

    /// <summary>A new list (another recording or filter) without a selection starts at its beginning rather than at a stale offset.</summary>
    private void OnEventsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Reset) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (History?.SelectedEvent is null && EventList.Template?.FindName("EventScroll", EventList) is ScrollViewer viewer) viewer.ScrollToTop();
        });
    }

    private void OnChartsRequested(object? sender, PropertyChangedEventArgs e) => BringChartIntoView();

    /// <summary>A finding chosen on the timeline (or stepped to) may be far down the virtualized list: bring its line into view without moving the page.</summary>
    private void OnSelectedRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (History?.Findings.SelectedRow is { } row && FindingsList.Items.Contains(row)) FindingsList.ScrollIntoView(row);
        });
    }

    private void OnFindingClicked(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListBox list || e.OriginalSource is not DependencyObject source || ItemsControl.ContainerFromElement(list, source) is not ListBoxItem item) return;
        // The chevron button of a group line has its own command; toggling here as well would undo it.
        for (DependencyObject? node = source; node is not null && !ReferenceEquals(node, item); node = node is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D ? System.Windows.Media.VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node))
            if (node is System.Windows.Controls.Button) return;
        // A click on a group's line opens or closes it (the line is selected by the same click); a click elsewhere brings the finding's chart into view.
        if (ReferenceEquals(list, FindingsList) && item.Content is HistoryFindingRow { IsGroup: true } group) History?.Findings.ToggleGroup(group.RuleId);
        else BringChartIntoView();
    }

    /// <summary>On the findings list: Right opens and Left closes the selected group, Enter and Space do either. Moving with Up and Down only selects, so it never opens a group.</summary>
    private void OnGroupKey(object sender, KeyEventArgs e)
    {
        if (History?.Findings is not { SelectedRow: { IsGroup: true } row } findings) return;
        switch (e.Key)
        {
            case Key.Right when !row.IsExpanded: case Key.Left when row.IsExpanded: case Key.Enter: case Key.Space: findings.ToggleGroup(row.RuleId); e.Handled = true; break;
        }
    }

    private void OnFindingKey(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || History is not { } history) return;
        history.Findings.ShowFull();
        e.Handled = true;
    }

    /// <summary>Scrolls the page just far enough to show the chart that carries the selected finding's evidence (the first emphasized chart); nothing happens when no chart is emphasized.</summary>
    internal void BringChartIntoView()
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (History is not { } history) return;
            var row = history.Charts.FirstOrDefault(c => c.IsPrimary) ?? history.Charts.FirstOrDefault(c => c.IsSupporting);
            if (row is not null && ChartList.ItemContainerGenerator.ContainerFromItem(row) is FrameworkElement container) container.BringIntoView();
        });
    }

    /// <summary>The findings controls, for the Windows validation.</summary>
    internal ListBox FindingsListControl => FindingsList;
    internal ListBox CorrelationListControl => CorrelationList;
    internal ItemsControl ChartListControl => ChartList;

    private SessionHistoryViewModel? History => (DataContext as SessionHistoryPage)?.Monitor.History;

    private void OnRange(object? sender, HistoryRangeEventArgs e) => History?.SetRange(e.Start, e.End);

    private void OnHover(object? sender, HoverEventArgs e)
    {
        Overlay.HoverAt = e.At;
        History?.SetHover(e.At);
    }

    private void OnNavigate(object? sender, HistoryNavigationEventArgs e)
    {
        if (History is not { } history) return;
        switch (e.Action)
        {
            case HistoryNavigation.ZoomIn: history.ZoomBy(0.5, e.Anchor); break;
            case HistoryNavigation.ZoomOut: history.ZoomBy(2, e.Anchor); break;
            case HistoryNavigation.Reset: history.ResetZoom(); break;
            case HistoryNavigation.PanLeft: history.PanBy(-0.25); break;
            case HistoryNavigation.PanRight: history.PanBy(0.25); break;
            case HistoryNavigation.PreviousIncident: history.PreviousIncidentCommand.Execute(null); break;
            case HistoryNavigation.NextIncident: history.NextIncidentCommand.Execute(null); break;
        }
    }
}
