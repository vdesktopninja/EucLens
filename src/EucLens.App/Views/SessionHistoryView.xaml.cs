using System.Windows;
using System.Windows.Controls;
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
