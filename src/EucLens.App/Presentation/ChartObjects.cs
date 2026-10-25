using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using EucLens.App.ViewModels;
using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.Kernel;
using LiveChartsCore.SkiaSharpView;

namespace EucLens.App.Presentation;

/// <summary>
/// The LiveCharts objects of one chart view: its own series, axes, sections and point copies, kept in step with a
/// view model's <see cref="ChartData"/> through weak subscriptions.
/// <para>
/// LiveCharts 2.0 adds every chart that fetches a series to <c>Series.subscribedTo</c> and never removes it, and each
/// point records a per-chart visual. Objects shared with long-lived view models therefore kept every chart (and its
/// page) that ever displayed them. Owning them per view means nothing long-lived references a chart, so a closed
/// page is collected without any unload hook; a chart that is unloaded and loaded again keeps working and resyncs.
/// </para>
/// WPF-free so the ownership and mirroring rules are unit-tested with real LiveCharts rendering.
/// </summary>
public sealed class ChartObjects
{
    private readonly ChartData data;
    private readonly ObservableCollection<DateTimePoint> values = [];

    public ChartObjects(ChartData data)
    {
        ChartsRuntime.EnsureInitialized();
        this.data = data;
        var unit = data.Unit;
        Line = new LineSeries<DateTimePoint>
        {
            Values = values, Name = data.Name, GeometrySize = 0, LineSmoothness = data.Smoothness, Stroke = null, Fill = null,
            XToolTipLabelFormatter = p => p.Model is { } model ? (data.TooltipTimeLabel ?? data.TimeLabel)?.Invoke(model.DateTime) ?? model.DateTime.ToString("HH:mm:ss", CultureInfo.InvariantCulture) : "",
            YToolTipLabelFormatter = p => $"{p.Coordinate.PrimaryValue.ToString("0.##", CultureInfo.InvariantCulture)} {unit}",
        };
        Series = [Line];
        XAxes = [new Axis
        {
            Labeler = v => v > 0 && v <= DateTime.MaxValue.Ticks
                ? data.TimeLabel is { } label ? label(new DateTime((long)v, DateTimeKind.Utc)) : new DateTime((long)v).ToString("HH:mm:ss", CultureInfo.InvariantCulture)
                : "",
            UnitWidth = TimeSpan.TicksPerSecond, MinStep = TimeSpan.TicksPerSecond * data.XStepSeconds, TextSize = data.LabelSize, SeparatorsPaint = null,
        }];
        YAxes = [new Axis { Name = data.AxisName, MinLimit = data.YMin, TextSize = data.LabelSize, MinStep = data.YMinStep }];
        Resync();
        // Weak: the view model never keeps a chart view alive.
        WeakSubscription.Collection(data.Points, this, static (self, e) => self.OnPoints(e));
        WeakSubscription.Property(data, this, static (self, name) => self.OnData(name));
    }

    public LineSeries<DateTimePoint> Line { get; }
    public ISeries[] Series { get; }
    public Axis[] XAxes { get; }
    public Axis[] YAxes { get; }
    /// <summary>Threshold lines; <see cref="CoreSection.Tag"/> holds the level so the theme can colour them.</summary>
    public IChartElement[] Sections { get; private set; } = [];
    public IReadOnlyList<DateTimePoint> Values => values;
    /// <summary>Raised when the number of reference lines changes and <see cref="Sections"/> is a new array.</summary>
    public event Action? SectionsReplaced;

    /// <summary>Copies the current data; used on creation and whenever the chart is loaded again.</summary>
    public void Resync()
    {
        values.Clear();
        foreach (var p in data.Points) values.Add(new(p.At, p.Value));
        XAxes[0].MinLimit = data.XMin; XAxes[0].MaxLimit = data.XMax;
        YAxes[0].MaxLimit = data.YMax;
        UpdateSections();
    }

    private void OnPoints(NotifyCollectionChangedEventArgs e)
    {
        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Add when e.NewStartingIndex == values.Count && e.NewItems is { } added:
                foreach (TimeValue p in added) values.Add(new(p.At, p.Value));
                break;
            case NotifyCollectionChangedAction.Remove when e.OldStartingIndex >= 0 && e.OldItems is { Count: var n } && e.OldStartingIndex + n <= values.Count:
                for (var i = 0; i < n; i++) values.RemoveAt(e.OldStartingIndex);
                break;
            default:
                Resync();
                break;
        }
    }

    private void OnData(string? name)
    {
        switch (name)
        {
            case nameof(ChartData.XMin): XAxes[0].MinLimit = data.XMin; break;
            case nameof(ChartData.XMax): XAxes[0].MaxLimit = data.XMax; break;
            case nameof(ChartData.YMax): YAxes[0].MaxLimit = data.YMax; break;
            case nameof(ChartData.References): UpdateSections(); break;
            default: Resync(); break;
        }
    }

    private void UpdateSections()
    {
        var lines = data.References;
        // Same count: update in place so the theme's paints stay attached.
        if (Sections.Length == lines.Count)
        {
            for (var i = 0; i < lines.Count; i++)
            {
                var s = (RectangularSection)Sections[i];
                s.Yi = s.Yj = lines[i].Value; s.Tag = lines[i].Level; s.Label = lines[i].ChartLabel; s.IsVisible = lines[i].Visible;
            }
            return;
        }
        Sections = lines.Select(r => (IChartElement)new RectangularSection
        {
            Yi = r.Value, Yj = r.Value, Tag = r.Level, Label = r.ChartLabel, LabelSize = data.LabelSize, Fill = null, IsVisible = r.Visible,
        }).ToArray();
        SectionsReplaced?.Invoke();
    }
}

/// <summary>Event subscriptions that do not keep the subscriber alive; a dead subscriber is removed on the next event.</summary>
public static class WeakSubscription
{
    public static void Collection<T>(INotifyCollectionChanged source, T target, Action<T, NotifyCollectionChangedEventArgs> handler) where T : class
    {
        var weak = new WeakReference<T>(target);
        NotifyCollectionChangedEventHandler? forward = null;
        forward = (_, e) => { if (weak.TryGetTarget(out var t)) handler(t, e); else source.CollectionChanged -= forward; };
        source.CollectionChanged += forward;
    }

    public static void Property<T>(INotifyPropertyChanged source, T target, Action<T, string?> handler) where T : class
    {
        var weak = new WeakReference<T>(target);
        PropertyChangedEventHandler? forward = null;
        forward = (_, e) => { if (weak.TryGetTarget(out var t)) handler(t, e.PropertyName); else source.PropertyChanged -= forward; };
        source.PropertyChanged += forward;
    }
}
