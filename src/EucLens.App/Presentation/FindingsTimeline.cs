using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Input;
using System.Windows.Media;
using EucLens.App.ViewModels;
using EucLens.Core;

namespace EucLens.App.Presentation;

public sealed class FindingActivatedEventArgs(string id) : EventArgs
{
    public string Id { get; } = id;
}

public sealed class FindingStepEventArgs(int direction) : EventArgs
{
    /// <summary>-1 for the previous finding in time, +1 for the next.</summary>
    public int Direction { get; } = direction;
}

/// <summary>
/// The performance findings of a recording on its own time axis: one track each for Connection, Graphics and VDI Guest, every finding as a bar over exactly its recorded interval
/// (nothing is interpolated or smoothed), overlapping findings in separate lanes, and the stretches without samples in a row of their own. The height of a bar says how severe the
/// finding is (critical full height, warning three quarters, info half) next to its colour, so severity never depends on colour alone. A click selects a finding, Ctrl+Left and Ctrl+Right
/// step through them in time order, a drag selects a range like the strip below. Owns no data: the bars arrive through <see cref="Bars"/> and a selection leaves through an event.
/// </summary>
public sealed class FindingsTimeline : HistoryTimeElement
{
    public static readonly DependencyProperty BarsProperty = DependencyProperty.Register(nameof(Bars), typeof(IReadOnlyList<FindingBar>), typeof(FindingsTimeline),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty SelectedIdProperty = DependencyProperty.Register(nameof(SelectedId), typeof(string), typeof(FindingsTimeline),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty HighlightIdsProperty = DependencyProperty.Register(nameof(HighlightIds), typeof(IReadOnlyList<string>), typeof(FindingsTimeline),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<FindingBar>? Bars { get => (IReadOnlyList<FindingBar>?)GetValue(BarsProperty); set => SetValue(BarsProperty, value); }
    public string? SelectedId { get => (string?)GetValue(SelectedIdProperty); set => SetValue(SelectedIdProperty, value); }
    /// <summary>Findings to outline (the components of the selected correlation).</summary>
    public IReadOnlyList<string>? HighlightIds { get => (IReadOnlyList<string>?)GetValue(HighlightIdsProperty); set => SetValue(HighlightIdsProperty, value); }

    public event EventHandler<FindingActivatedEventArgs>? FindingActivated;
    public event EventHandler<FindingStepEventArgs>? FindingStepRequested;

    // Geometry in DIPs. The label column holds the track names; the axis starts after it.
    internal const double LabelColumn = 92, RightMargin = 10, AxisBand = 18, TrackGap = 4, GapRowHeight = 14, MinimumBar = 4, BarSpacing = 2, MinimumLane = 6, MaximumLane = 12, MaximumTrackLanesHeight = 52;

    public FindingsTimeline() { MinWidth = 320; Cursor = Cursors.Arrow; }

    protected override (double Left, double Right) AxisExtent => (LabelColumn, Math.Max(LabelColumn + 1, ActualWidth - RightMargin));
    protected override (DateTimeOffset Start, DateTimeOffset End) AxisRange => (SessionStart, SessionEnd);

    // ---- layout of the bars ----

    private sealed record TrackLayout(HistoryGroup Domain, IReadOnlyList<PlacedBar> Bars, int Lanes, double LaneHeight, double Top, double Height);
    private sealed record Layout(IReadOnlyList<TrackLayout> Tracks, double GapTop, double Height);

    private Layout? cached;
    private (object? Bars, double Width, DateTimeOffset Start, DateTimeOffset End) cachedKey;

    private Layout Compute(double width)
    {
        var bars = Bars;
        (object? Bars, double Width, DateTimeOffset Start, DateTimeOffset End) key = (bars, width, SessionStart, SessionEnd);
        if (cached is not null && cachedKey.Equals(key)) return cached;
        var (left, right) = (LabelColumn, Math.Max(LabelColumn + 1, width - RightMargin));
        var span = (SessionEnd - SessionStart).Ticks;
        double X(DateTimeOffset at) => span <= 0 ? left : left + (at - SessionStart).Ticks / (double)span * (right - left);
        var tracks = new List<TrackLayout>(); var top = AxisBand;
        foreach (var domain in FindingsTimelineLayout.Tracks)
        {
            var placed = FindingsTimelineLayout.Pack((bars ?? []).Where(b => b.Domain == domain), X, MinimumBar, BarSpacing, out var lanes);
            var laneHeight = lanes <= 1 ? MaximumLane : Math.Clamp(MaximumTrackLanesHeight / lanes, MinimumLane, MaximumLane);
            var height = Math.Max(22, Math.Max(1, lanes) * laneHeight + 8);
            tracks.Add(new(domain, placed, lanes, laneHeight, top, height));
            top += height + TrackGap;
        }
        cachedKey = key;
        return cached = new(tracks, top, top + GapRowHeight + 4);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsInfinity(availableSize.Width) ? 640 : availableSize.Width;
        return new Size(width, Compute(width).Height);
    }

    private static double BarHeight(Severity severity, double lane) => lane * severity switch { Severity.Critical => 0.92, Severity.Warning => 0.72, _ => 0.5 };

    private Rect BarRect(TrackLayout track, PlacedBar placed)
    {
        var height = BarHeight(placed.Bar.Severity, track.LaneHeight - 1.5);
        var laneTop = track.Top + 4 + placed.Lane * track.LaneHeight;
        return new Rect(placed.Left, laneTop + (track.LaneHeight - 1.5 - height) / 2, Math.Max(MinimumBar, placed.Right - placed.Left), height);
    }

    private Brush SeverityFill(Severity severity) => severity switch { Severity.Critical => CriticalBrush, Severity.Warning => WarningBrush, _ => AccentBrush };

    // ---- drawing ----

    protected override void OnRender(DrawingContext dc)
    {
        var bounds = new Rect(0, 0, ActualWidth, ActualHeight);
        dc.DrawRectangle(Brushes.Transparent, null, bounds);
        var layout = Compute(ActualWidth);
        var (left, right) = AxisExtent;
        if (HasRange) DrawTicks(dc, left, right);
        var any = false;
        foreach (var track in layout.Tracks)
        {
            var area = new Rect(left, track.Top, right - left, track.Height);
            dc.PushOpacity(0.35); dc.DrawRoundedRectangle(GridBrush, null, area, 3, 3); dc.Pop();
            var label = Text(HistoryChannels.GroupTitle(track.Domain), 12, TextBrush);
            dc.DrawText(label, new Point(4, track.Top + Math.Max(2, (track.Height - label.Height) / 2)));
            if (track.Bars.Count > 0) { var count = Text(track.Bars.Count.ToString("N0", System.Globalization.CultureInfo.CurrentCulture), 10, MutedBrush); dc.DrawText(count, new Point(LabelColumn - 8 - count.Width, track.Top + Math.Max(2, (track.Height - count.Height) / 2))); }
            any |= track.Bars.Count > 0;
        }
        if (HasRange)
        {
            // The stretches without samples, in their own row: never mixed with findings.
            var gapLabel = Text("Gaps", 12, TextBrush);
            dc.DrawText(gapLabel, new Point(4, layout.GapTop + (GapRowHeight - gapLabel.Height) / 2));
            dc.PushOpacity(0.35); dc.DrawRoundedRectangle(GridBrush, null, new Rect(left, layout.GapTop, right - left, GapRowHeight), 3, 3); dc.Pop();
            foreach (var gap in Gaps ?? [])
            {
                var x1 = Math.Clamp(XOf(gap.Start), left, right); var x2 = Math.Clamp(XOf(gap.End), left, right);
                var rect = new Rect(x1, layout.GapTop + 1, Math.Max(3, x2 - x1), GapRowHeight - 2);
                dc.DrawRectangle(MutedBrush, null, rect);
                if (rect.Width > 8) for (var x = rect.Left + 2; x < rect.Right; x += 5) dc.DrawLine(new Pen(GridBrush, 1.2), new Point(x, rect.Bottom), new Point(Math.Min(rect.Right, x + 4), rect.Top));
            }
            // What the charts below show.
            var viewLeft = Math.Clamp(XOf(ViewStart), left, right); var viewRight = Math.Clamp(Math.Max(XOf(ViewStart) + 3, XOf(ViewEnd)), left, right);
            if (viewRight - viewLeft < right - left - 2)
                dc.DrawRectangle(AccentSurfaceBrush, new Pen(AccentBrush, 1.25), new Rect(viewLeft, AxisBand - 3, Math.Max(3, viewRight - viewLeft), layout.GapTop - AxisBand + GapRowHeight + 6));
        }
        var selected = SelectedId; var highlighted = HighlightIds;
        foreach (var track in layout.Tracks)
            foreach (var placed in track.Bars)
            {
                var rect = BarRect(track, placed);
                var isSelected = selected is not null && string.Equals(placed.Bar.Id, selected, StringComparison.Ordinal);
                dc.DrawRectangle(SeverityFill(placed.Bar.Severity), new Pen(TextBrush, isSelected ? 0 : 0.6), rect);
            }
        // Emphasis on top of every bar, so a selected bar is never covered by a neighbour.
        foreach (var track in layout.Tracks)
            foreach (var placed in track.Bars)
            {
                var isSelected = selected is not null && string.Equals(placed.Bar.Id, selected, StringComparison.Ordinal);
                var isLinked = highlighted is not null && highlighted.Contains(placed.Bar.Id, StringComparer.Ordinal);
                if (!isSelected && !isLinked) continue;
                var rect = Rect.Inflate(BarRect(track, placed), 2, 2);
                dc.DrawRectangle(null, new Pen(isSelected ? TextBrush : AccentBrush, isSelected ? 2 : 1.5) { DashStyle = isSelected ? DashStyles.Solid : DashStyles.Dash }, rect);
            }
        if (!any)
        {
            var empty = Text("No findings in this recording", 12, MutedBrush);
            dc.DrawText(empty, new Point(left + 12, layout.Tracks[0].Top + (layout.Tracks[^1].Top + layout.Tracks[^1].Height - layout.Tracks[0].Top - empty.Height) / 2));
        }
        if (DragFrom is { } from && DragTo is { } to)
            dc.DrawRectangle(AccentSurfaceBrush, new Pen(AccentBrush, 1) { DashStyle = DashStyles.Dash }, new Rect(Math.Min(from, to), AxisBand - 3, Math.Abs(to - from), layout.Height - AxisBand));
        DrawFocus(dc, bounds);
        // The range of the session or the width changed without a new measure pass: ask for one when the lanes need another height.
        if (Math.Abs(layout.Height - DesiredSize.Height) > 0.5) Dispatcher.BeginInvoke(InvalidateMeasure, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void DrawTicks(DrawingContext dc, double left, double right)
    {
        var count = Math.Clamp((int)((right - left) / 130), 2, 8);
        for (var i = 0; i <= count; i++)
        {
            var at = SessionStart + TimeSpan.FromTicks((SessionEnd - SessionStart).Ticks * i / count);
            var text = Text(Label(at, seconds: (SessionEnd - SessionStart) < TimeSpan.FromHours(2)), 11, MutedBrush);
            var x = XOf(at);
            dc.DrawText(text, new Point(Math.Clamp(x - text.Width / 2, 0, Math.Max(0, ActualWidth - text.Width)), 0));
        }
    }

    // ---- pointer and keyboard ----

    private PlacedBar? BarAt(Point point)
    {
        var layout = Compute(ActualWidth);
        // The front-most (last drawn) bar wins; a few pixels of tolerance make a 4-pixel bar hittable.
        foreach (var track in layout.Tracks.Reverse())
        {
            if (point.Y < track.Top - 2 || point.Y > track.Top + track.Height + 2) continue;
            foreach (var placed in track.Bars.Reverse())
                if (Rect.Inflate(BarRect(track, placed), 2, 3).Contains(point)) return placed;
        }
        return null;
    }

    private string? hoverId;

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var hit = BarAt(Pointer(e));
        var id = hit?.Bar.Id;
        if (string.Equals(id, hoverId, StringComparison.Ordinal)) return;
        hoverId = id;
        ToolTip = hit is null ? null : FindingsTimelineLayout.Describe(hit);
        Cursor = hit is null ? Cursors.Cross : Cursors.Hand;
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        hoverId = null; ToolTip = null;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        var moved = DragFrom is { } from && DragTo is { } to && Math.Abs(to - from) > 4;
        var hit = moved ? null : BarAt(Pointer(e));
        base.OnMouseLeftButtonUp(e);
        if (hit is not null) FindingActivated?.Invoke(this, new(hit.Bar.Id));
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        var control = ((ModifierSource?.Invoke() ?? Keyboard.Modifiers) & ModifierKeys.Control) != 0;
        if (control && e.Key is Key.Left or Key.Right)
        {
            FindingStepRequested?.Invoke(this, new(e.Key == Key.Left ? -1 : 1));
            e.Handled = true;
            return;
        }
        base.OnKeyDown(e);
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new FrameworkElementAutomationPeer(this);

    // ---- facts for the Windows validation ----

    /// <summary>Where each bar is drawn (the front-most first is not needed: tests read all of them).</summary>
    internal IReadOnlyList<(string Id, HistoryGroup Domain, int Lane, Rect Rect)> DrawnBars()
    {
        var layout = Compute(ActualWidth);
        return layout.Tracks.SelectMany(t => t.Bars.Select(p => (p.Bar.Id, t.Domain, p.Lane, BarRect(t, p)))).ToArray();
    }

    internal IReadOnlyList<(HistoryGroup Domain, double Top, double Height)> DrawnTracks() => Compute(ActualWidth).Tracks.Select(t => (t.Domain, t.Top, t.Height)).ToArray();
}
