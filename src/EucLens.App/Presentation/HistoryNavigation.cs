using System.Globalization;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Input;
using System.Windows.Media;
using EucLens.App.ViewModels;
using EucLens.Core;

namespace EucLens.App.Presentation;

/// <summary>A request from the timeline strip or the chart overlay; the view translates it into calls on the history view model.</summary>
public enum HistoryNavigation { ZoomIn, ZoomOut, Reset, PanLeft, PanRight, PreviousIncident, NextIncident }

public sealed class HistoryRangeEventArgs(DateTimeOffset start, DateTimeOffset end) : EventArgs
{
    public DateTimeOffset Start { get; } = start;
    public DateTimeOffset End { get; } = end;
}

public sealed class HistoryNavigationEventArgs(HistoryNavigation action, DateTimeOffset? anchor = null) : EventArgs
{
    public HistoryNavigation Action { get; } = action;
    public DateTimeOffset? Anchor { get; } = anchor;
}

public sealed class HistoryMarkerEventArgs(DateTimeOffset at) : EventArgs
{
    public DateTimeOffset At { get; } = at;
}

/// <summary>
/// Shared behaviour of the two elements that draw on the history time axis: the dependency properties of the visible range, the theme brushes (referenced
/// dynamically, so a theme change redraws them), pointer selection of a range and the keyboard commands. Drawing and the time-to-pixel mapping are in the
/// derived classes. Holds no chart and no view model: everything it knows arrives through properties and leaves through events.
/// </summary>
public abstract class HistoryTimeElement : FrameworkElement
{
    private static DependencyProperty Time(string name, DateTimeOffset value = default) =>
        DependencyProperty.Register(name, typeof(DateTimeOffset), typeof(HistoryTimeElement), new FrameworkPropertyMetadata(value, FrameworkPropertyMetadataOptions.AffectsRender));
    private static DependencyProperty Brush(string name) =>
        DependencyProperty.Register(name + "Fill", typeof(Brush), typeof(HistoryTimeElement), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ViewStartProperty = Time(nameof(ViewStart));
    public static readonly DependencyProperty ViewEndProperty = Time(nameof(ViewEnd));
    public static readonly DependencyProperty SessionStartProperty = Time(nameof(SessionStart));
    public static readonly DependencyProperty SessionEndProperty = Time(nameof(SessionEnd));
    public static readonly DependencyProperty SelectedAtProperty = DependencyProperty.Register(nameof(SelectedAt), typeof(DateTimeOffset?), typeof(HistoryTimeElement),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty MarkersProperty = DependencyProperty.Register(nameof(Markers), typeof(IReadOnlyList<TimelineMarker>), typeof(HistoryTimeElement),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty GapsProperty = DependencyProperty.Register(nameof(Gaps), typeof(IReadOnlyList<HistoryGap>), typeof(HistoryTimeElement),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ZoneProperty = DependencyProperty.Register(nameof(Zone), typeof(TimeZoneInfo), typeof(HistoryTimeElement),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty AccentFillProperty = Brush("Accent");
    public static readonly DependencyProperty AccentSurfaceFillProperty = Brush("AccentSurface");
    public static readonly DependencyProperty MutedFillProperty = Brush("Muted");
    public static readonly DependencyProperty TextFillProperty = Brush("Text");
    public static readonly DependencyProperty GridFillProperty = Brush("Grid");
    public static readonly DependencyProperty WarningFillProperty = Brush("Warning");
    public static readonly DependencyProperty CriticalFillProperty = Brush("Critical");
    public static readonly DependencyProperty SuccessFillProperty = Brush("Success");
    public static readonly DependencyProperty BorderFillProperty = Brush("Border");

    protected HistoryTimeElement()
    {
        Focusable = true; FocusVisualStyle = null; SnapsToDevicePixels = true; Cursor = Cursors.Cross;
        SetResourceReference(AccentFillProperty, "AccentBrush"); SetResourceReference(AccentSurfaceFillProperty, "AccentSurfaceBrush");
        SetResourceReference(MutedFillProperty, "MutedBrush"); SetResourceReference(TextFillProperty, "TextBrush"); SetResourceReference(GridFillProperty, "ChartGridBrush");
        SetResourceReference(WarningFillProperty, "WarningBrush"); SetResourceReference(CriticalFillProperty, "CriticalBrush"); SetResourceReference(SuccessFillProperty, "SuccessBrush");
        SetResourceReference(BorderFillProperty, "BorderBrush");
    }

    public DateTimeOffset ViewStart { get => (DateTimeOffset)GetValue(ViewStartProperty); set => SetValue(ViewStartProperty, value); }
    public DateTimeOffset ViewEnd { get => (DateTimeOffset)GetValue(ViewEndProperty); set => SetValue(ViewEndProperty, value); }
    public DateTimeOffset SessionStart { get => (DateTimeOffset)GetValue(SessionStartProperty); set => SetValue(SessionStartProperty, value); }
    public DateTimeOffset SessionEnd { get => (DateTimeOffset)GetValue(SessionEndProperty); set => SetValue(SessionEndProperty, value); }
    public DateTimeOffset? SelectedAt { get => (DateTimeOffset?)GetValue(SelectedAtProperty); set => SetValue(SelectedAtProperty, value); }
    public IReadOnlyList<TimelineMarker>? Markers { get => (IReadOnlyList<TimelineMarker>?)GetValue(MarkersProperty); set => SetValue(MarkersProperty, value); }
    public IReadOnlyList<HistoryGap>? Gaps { get => (IReadOnlyList<HistoryGap>?)GetValue(GapsProperty); set => SetValue(GapsProperty, value); }
    public TimeZoneInfo? Zone { get => (TimeZoneInfo?)GetValue(ZoneProperty); set => SetValue(ZoneProperty, value); }

    protected Brush AccentBrush => (Brush)GetValue(AccentFillProperty) ?? Brushes.DodgerBlue;
    protected Brush AccentSurfaceBrush => (Brush)GetValue(AccentSurfaceFillProperty) ?? Brushes.Transparent;
    protected Brush MutedBrush => (Brush)GetValue(MutedFillProperty) ?? Brushes.Gray;
    protected Brush TextBrush => (Brush)GetValue(TextFillProperty) ?? Brushes.Black;
    protected Brush GridBrush => (Brush)GetValue(GridFillProperty) ?? Brushes.LightGray;
    protected Brush WarningBrush => (Brush)GetValue(WarningFillProperty) ?? Brushes.Orange;
    protected Brush CriticalBrush => (Brush)GetValue(CriticalFillProperty) ?? Brushes.Red;
    protected Brush SuccessBrush => (Brush)GetValue(SuccessFillProperty) ?? Brushes.Green;
    protected Brush BorderLineBrush => (Brush)GetValue(BorderFillProperty) ?? Brushes.Gray;

    public event EventHandler<HistoryRangeEventArgs>? RangeRequested;
    public event EventHandler<HistoryNavigationEventArgs>? NavigationRequested;
    public event EventHandler<HistoryMarkerEventArgs>? MarkerActivated;

    // ---- time axis ----

    /// <summary>Horizontal extent of the time axis in this element's coordinates.</summary>
    protected abstract (double Left, double Right) AxisExtent { get; }
    /// <summary>The time range the extent represents.</summary>
    protected abstract (DateTimeOffset Start, DateTimeOffset End) AxisRange { get; }

    public double XOf(DateTimeOffset at)
    {
        var (left, right) = AxisExtent; var (start, end) = AxisRange;
        var span = (end - start).Ticks;
        return span <= 0 ? left : left + (at - start).Ticks / (double)span * (right - left);
    }

    public DateTimeOffset TimeAt(double x)
    {
        var (left, right) = AxisExtent; var (start, end) = AxisRange;
        var ratio = right <= left ? 0 : Math.Clamp((x - left) / (right - left), 0, 1);
        return start + TimeSpan.FromTicks((long)((end - start).Ticks * ratio));
    }

    protected bool HasRange => AxisRange.End > AxisRange.Start;

    // ---- text ----

    protected FormattedText Text(string text, double size, Brush brush)
    {
        var family = (FontFamily)GetValue(System.Windows.Documents.TextElement.FontFamilyProperty);
        return new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface(family, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal), size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
    }

    protected string Label(DateTimeOffset at, bool seconds) => HistoryTime.AxisLabel(at.UtcTicks, Zone, seconds);

    // ---- geometry of markers ----

    protected Geometry? GlyphGeometry(string tag)
    {
        var key = tag.StartsWith("Finding", StringComparison.Ordinal) ? "SeverityGeometry" + tag["Finding".Length..] : "EventGeometry" + tag;
        return TryFindResource(key) as Geometry;
    }

    protected Brush GlyphBrush(string tag) => tag switch
    {
        "Incident" or "FindingInfo" => AccentBrush,
        "FindingWarning" or "Disconnect" => WarningBrush,
        "FindingCritical" or "DataLoss" => CriticalBrush,
        "Reconnect" => SuccessBrush,
        _ => MutedBrush
    };

    protected void DrawGlyph(DrawingContext dc, string tag, double centerX, double centerY, double size)
    {
        if (GlyphGeometry(tag) is not { } geometry) return;
        var scale = size / 16.0;
        var transform = new TransformGroup { Children = { new ScaleTransform(scale, scale), new TranslateTransform(centerX - size / 2, centerY - size / 2) } };
        dc.PushTransform(transform);
        dc.DrawGeometry(GlyphBrush(tag), null, geometry);
        dc.Pop();
    }

    // ---- pointer ----

    /// <summary>
    /// Validation seam: the Windows smoke test raises real routed mouse events at the element and supplies the pointer position (a synthesized event carries
    /// none) and the modifier keys. Null in the application, where the real pointer and keyboard are used.
    /// </summary>
    internal Func<MouseEventArgs, Point>? PointerSource { get; set; }
    internal Func<ModifierKeys>? ModifierSource { get; set; }
    protected Point Pointer(MouseEventArgs e) => PointerSource?.Invoke(e) ?? e.GetPosition(this);

    private Point? pressed;
    private bool dragging;
    protected double? DragFrom { get; private set; }
    protected double? DragTo { get; private set; }

    /// <summary>The marker closest to a pixel position within <paramref name="tolerance"/>, preferring user incidents.</summary>
    protected virtual TimelineMarker? MarkerNear(double x, double tolerance)
    {
        TimelineMarker? best = null; var bestDistance = tolerance;
        foreach (var marker in Markers ?? [])
        {
            var distance = Math.Abs(XOf(marker.At) - x);
            if (distance > bestDistance + (marker.Kind == HistoryEventKind.UserIncident ? 2 : 0)) continue;
            if (best is null || distance < bestDistance || marker.Kind == HistoryEventKind.UserIncident && best.Kind != HistoryEventKind.UserIncident) { best = marker; bestDistance = distance; }
        }
        return best;
    }

    protected virtual bool SelectsMarkers => false;

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
        if (!HasRange) return;
        pressed = Pointer(e); dragging = false; DragFrom = pressed.Value.X; DragTo = DragFrom;
        CaptureMouse();
        e.Handled = true;
        if (e.ClickCount == 2) { pressed = null; ReleaseMouseCapture(); NavigationRequested?.Invoke(this, new(HistoryNavigation.Reset)); }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (pressed is not { } start) return;
        var position = Pointer(e);
        if (!dragging && Math.Abs(position.X - start.X) > 4) dragging = true;
        if (dragging) { DragTo = position.X; InvalidateVisual(); }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (pressed is not { } start) return;
        var end = Pointer(e);
        var wasDragging = dragging;
        pressed = null; dragging = false; DragFrom = DragTo = null;
        ReleaseMouseCapture();
        InvalidateVisual();
        if (wasDragging && Math.Abs(end.X - start.X) > 4)
        {
            var a = TimeAt(Math.Min(start.X, end.X)); var b = TimeAt(Math.Max(start.X, end.X));
            RangeRequested?.Invoke(this, new(a, b));
        }
        else if (SelectsMarkers && MarkerNear(end.X, 7) is { } marker) MarkerActivated?.Invoke(this, new(marker.At));
        else if (SelectsMarkers)
        {
            // A click on the bare strip moves the view there, keeping its width.
            var half = (ViewEnd - ViewStart) / 2; var at = TimeAt(end.X);
            RangeRequested?.Invoke(this, new(at - half, at + half));
        }
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (pressed is null) return;
        pressed = null; dragging = false; DragFrom = DragTo = null; InvalidateVisual();
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        // Ctrl + wheel zooms around the pointer; the plain wheel keeps scrolling the page.
        if (!HasRange || ((ModifierSource?.Invoke() ?? Keyboard.Modifiers) & ModifierKeys.Control) == 0) { base.OnMouseWheel(e); return; }
        NavigationRequested?.Invoke(this, new(e.Delta > 0 ? HistoryNavigation.ZoomIn : HistoryNavigation.ZoomOut, TimeAt(Pointer(e).X)));
        e.Handled = true;
    }

    // ---- keyboard ----

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (!HasRange) return;
        HistoryNavigation? action = e.Key switch
        {
            Key.Left => HistoryNavigation.PanLeft, Key.Right => HistoryNavigation.PanRight,
            Key.Up or Key.OemPlus or Key.Add => HistoryNavigation.ZoomIn, Key.Down or Key.OemMinus or Key.Subtract => HistoryNavigation.ZoomOut,
            Key.Home or Key.Escape => HistoryNavigation.Reset,
            Key.PageUp => HistoryNavigation.PreviousIncident, Key.PageDown => HistoryNavigation.NextIncident,
            _ => null
        };
        if (action is null) return;
        NavigationRequested?.Invoke(this, new(action.Value));
        e.Handled = true;
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new FrameworkElementAutomationPeer(this);

    protected void DrawFocus(DrawingContext dc, Rect bounds)
    {
        if (!IsKeyboardFocused) return;
        dc.DrawRectangle(null, new Pen(AccentBrush, 2), Rect.Inflate(bounds, -1, -1));
    }
}

/// <summary>
/// The whole session on one line: gaps without samples, every timeline event as its own glyph, the visible range, and the selected incident. Drag to select a
/// range, click an event to select it, click elsewhere to move the view; arrow keys pan, plus and minus zoom, Home resets, Page Up and Page Down step through incidents.
/// </summary>
public sealed class HistoryTimelineStrip : HistoryTimeElement
{
    private const double LabelBand = 16, GlyphBand = 26;

    public HistoryTimelineStrip() { Height = LabelBand + GlyphBand + 6; MinWidth = 200; Cursor = Cursors.Hand; }

    protected override bool SelectsMarkers => true;
    protected override (double Left, double Right) AxisExtent => (8, Math.Max(9, ActualWidth - 8));
    protected override (DateTimeOffset Start, DateTimeOffset End) AxisRange => (SessionStart, SessionEnd);

    protected override void OnRender(DrawingContext dc)
    {
        var bounds = new Rect(0, 0, ActualWidth, ActualHeight);
        dc.DrawRectangle(Brushes.Transparent, null, bounds);
        var (left, right) = AxisExtent;
        var track = new Rect(left, LabelBand + 4, right - left, GlyphBand - 8);
        dc.DrawRoundedRectangle(GridBrush, null, track, 3, 3);
        if (HasRange)
        {
            foreach (var gap in Gaps ?? [])
            {
                var x1 = XOf(gap.Start); var x2 = Math.Max(x1 + 2, XOf(gap.End));
                dc.DrawRectangle(MutedBrush, null, new Rect(x1, track.Top, Math.Min(x2, right) - x1, track.Height));
            }
            var viewLeft = XOf(ViewStart); var viewRight = Math.Max(viewLeft + 3, XOf(ViewEnd));
            var view = new Rect(viewLeft, 2 + LabelBand, Math.Min(viewRight, right) - viewLeft, GlyphBand + 2);
            dc.DrawRectangle(AccentSurfaceBrush, new Pen(AccentBrush, 1.5), view);
            DrawTicks(dc);
            // Findings and other events first, user incidents on top and larger.
            foreach (var marker in (Markers ?? []).Where(m => m.Kind != HistoryEventKind.UserIncident && m.Kind is not (HistoryEventKind.RecordingStarted or HistoryEventKind.RecordingEnded)))
                DrawGlyph(dc, marker.IconTag, XOf(marker.At), LabelBand + GlyphBand / 2, 10);
            foreach (var marker in (Markers ?? []).Where(m => m.Kind == HistoryEventKind.UserIncident))
                DrawGlyph(dc, marker.IconTag, XOf(marker.At), LabelBand + GlyphBand / 2, 16);
            if (SelectedAt is { } selected)
            {
                var x = XOf(selected);
                dc.DrawLine(new Pen(AccentBrush, 2), new Point(x, LabelBand), new Point(x, LabelBand + GlyphBand + 4));
            }
            if (DragFrom is { } from && DragTo is { } to)
                dc.DrawRectangle(AccentSurfaceBrush, new Pen(AccentBrush, 1) { DashStyle = DashStyles.Dash }, new Rect(Math.Min(from, to), LabelBand, Math.Abs(to - from), GlyphBand + 4));
        }
        DrawFocus(dc, bounds);
    }

    private void DrawTicks(DrawingContext dc)
    {
        var (left, right) = AxisExtent;
        var count = Math.Clamp((int)((right - left) / 120), 2, 8);
        for (var i = 0; i <= count; i++)
        {
            var at = SessionStart + TimeSpan.FromTicks((SessionEnd - SessionStart).Ticks * i / count);
            var text = Text(Label(at, seconds: (SessionEnd - SessionStart) < TimeSpan.FromHours(2)), 11, MutedBrush);
            var x = XOf(at);
            var origin = Math.Clamp(x - text.Width / 2, 0, Math.Max(0, ActualWidth - text.Width));
            dc.DrawText(text, new Point(origin, 0));
        }
    }
}

/// <summary>
/// Drawn over the stack of charts: one shared cursor, incident lines, shaded gaps, and the range being selected. It maps time to pixels with the same plot margins
/// the charts use, so every chart and this layer agree on every instant. The pointer position is reported as a time; the view model answers with the recorded values.
/// </summary>
public sealed class HistoryChartOverlay : HistoryTimeElement
{
    public static readonly DependencyProperty PlotLeftProperty = DependencyProperty.Register(nameof(PlotLeft), typeof(double), typeof(HistoryChartOverlay),
        new FrameworkPropertyMetadata(64d, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty PlotRightProperty = DependencyProperty.Register(nameof(PlotRight), typeof(double), typeof(HistoryChartOverlay),
        new FrameworkPropertyMetadata(16d, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty HoverAtProperty = DependencyProperty.Register(nameof(HoverAt), typeof(DateTimeOffset?), typeof(HistoryChartOverlay),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public double PlotLeft { get => (double)GetValue(PlotLeftProperty); set => SetValue(PlotLeftProperty, value); }
    public double PlotRight { get => (double)GetValue(PlotRightProperty); set => SetValue(PlotRightProperty, value); }
    /// <summary>Time under the pointer; set by the overlay and cleared when the pointer leaves.</summary>
    public DateTimeOffset? HoverAt { get => (DateTimeOffset?)GetValue(HoverAtProperty); set => SetValue(HoverAtProperty, value); }
    public event EventHandler<HoverEventArgs>? HoverChanged;

    protected override (double Left, double Right) AxisExtent => (PlotLeft, Math.Max(PlotLeft + 1, ActualWidth - PlotRight));
    protected override (DateTimeOffset Start, DateTimeOffset End) AxisRange => (ViewStart, ViewEnd);

    protected override void OnMouseMove(System.Windows.Input.MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!HasRange) return;
        var x = Pointer(e).X;
        var (left, right) = AxisExtent;
        DateTimeOffset? at = x >= left && x <= right ? TimeAt(x) : null;
        HoverChanged?.Invoke(this, new(at));
    }

    protected override void OnMouseLeave(System.Windows.Input.MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        HoverChanged?.Invoke(this, new(null));
    }

    protected override void OnRender(DrawingContext dc)
    {
        var bounds = new Rect(0, 0, ActualWidth, ActualHeight);
        dc.DrawRectangle(Brushes.Transparent, null, bounds);
        if (HasRange)
        {
            var (left, right) = AxisExtent;
            foreach (var gap in Gaps ?? [])
            {
                var x1 = Math.Clamp(XOf(gap.Start), left, right); var x2 = Math.Clamp(XOf(gap.End), left, right);
                if (x2 - x1 < 1) continue;
                dc.PushOpacity(0.14);
                dc.DrawRectangle(MutedBrush, null, new Rect(x1, 0, x2 - x1, ActualHeight));
                dc.Pop();
            }
            var dashed = new Pen(AccentBrush, 1.25) { DashStyle = new DashStyle([4, 3], 0) };
            foreach (var marker in (Markers ?? []).Where(m => m.Kind == HistoryEventKind.UserIncident))
            {
                var x = XOf(marker.At);
                if (x >= left && x <= right) dc.DrawLine(dashed, new Point(x, 0), new Point(x, ActualHeight));
            }
            if (SelectedAt is { } selected && XOf(selected) is var sx && sx >= left && sx <= right)
                dc.DrawLine(new Pen(AccentBrush, 2.5), new Point(sx, 0), new Point(sx, ActualHeight));
            if (HoverAt is { } hover && XOf(hover) is var hx && hx >= left && hx <= right)
                dc.DrawLine(new Pen(TextBrush, 1) { DashStyle = DashStyles.Solid }, new Point(hx, 0), new Point(hx, ActualHeight));
            if (DragFrom is { } from && DragTo is { } to)
                dc.DrawRectangle(AccentSurfaceBrush, new Pen(AccentBrush, 1) { DashStyle = DashStyles.Dash }, new Rect(Math.Min(from, to), 0, Math.Abs(to - from), ActualHeight));
        }
        DrawFocus(dc, bounds);
    }
}

public sealed class HoverEventArgs(DateTimeOffset? at) : EventArgs
{
    public DateTimeOffset? At { get; } = at;
}
