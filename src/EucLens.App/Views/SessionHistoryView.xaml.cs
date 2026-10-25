using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
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
        CollectionChangedEventManager.AddHandler(history.Events, OnEventsChanged);
    }

    private void Unwatch()
    {
        if (watched is null) return;
        PropertyChangedEventManager.RemoveHandler(watched, OnHistoryProperty, nameof(SessionHistoryViewModel.SelectedEvent));
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
