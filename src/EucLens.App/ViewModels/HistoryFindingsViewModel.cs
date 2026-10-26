using System.Globalization;
using EucLens.Core;

namespace EucLens.App.ViewModels;

public enum FindingSeverityFilter { All, Critical, Warning, Info }
public enum FindingDomainFilter { All, Connection, Graphics, Guest }

/// <summary>How a chart relates to the selected finding: the measurement that supports it, a measurement that adds context, or neither.</summary>
public enum ChartEmphasis { None, Primary, Supporting }

/// <summary>A filter option with the number of findings it would show; the label is rebuilt whenever the analysis changes.</summary>
public sealed class FindingFilterChoice<T>(T value, string name) : ObservableObject
{
    private string label = name;
    public T Value { get; } = value;
    public string Name { get; } = name;
    public int Count { get; private set; }
    public string Label { get => label; private set => Set(ref label, value); }
    internal void SetCount(int count) { Count = count; Label = $"{Name} ({count.ToString(CultureInfo.InvariantCulture)})"; Raise(nameof(Count)); }
}

/// <summary>
/// Where the history page should go for a finding: a real interval (recorded UTC times), the time of the measured extreme, and which charts carry the evidence.
/// <see cref="HighlightStart"/>/<see cref="HighlightEnd"/> are set only for an interval that is real for everything selected: one observation, or the interval common to
/// every component of a correlation. A chained correlation gets its span as the view but no highlighted interval.
/// </summary>
public sealed record FindingTarget(DateTimeOffset Start, DateTimeOffset End, DateTimeOffset? Peak, IReadOnlyList<string> PrimaryChannels, IReadOnlyList<string> SupportingChannels,
    DateTimeOffset? HighlightStart, DateTimeOffset? HighlightEnd);

/// <summary>One metric of an observation as measured inside its interval, formatted for reading (the recorded unit, never a converted one).</summary>
public sealed record HistoryMetricRow(string Role, string Metric, string Unit, string Minimum, string P50, string P95, string Maximum, string WorstAt, string ValidSamples, string Summary);

/// <summary>The structured evidence of one observation: what the engine saw, the measured values, the criterion that matched and what limits the evidence.</summary>
public sealed class HistoryObservationDetail
{
    internal HistoryObservationDetail(AnomalyObservation o, TimeZoneInfo zone)
    {
        Explanation = o.Explanation;
        var worstIsLow = HistoryChannels.Find(o.Primary.ChannelId) is { HigherIsWorse: false };
        Metrics = o.Metrics.Select(m => Row(m, zone)).ToArray();
        var p = o.Primary;
        KeyValues = p.ValidSamples == 0 ? "No valid measurement inside this interval."
            : $"{(worstIsLow ? "Lowest" : "Highest")} {FindingText.Value(worstIsLow ? p.Minimum : p.Maximum, p.Unit)} at {HistoryTime.Format(p.WorstAt, zone)} · " +
              $"{FindingText.Count(o.MatchingSamples)} matching of {FindingText.Count(o.SampleCount)} valid samples · coverage {FindingText.Percent(o.Coverage)}";
        Utc = $"UTC {HistoryTime.Format(o.Start, TimeZoneInfo.Utc)}–{HistoryTime.Format(o.End, TimeZoneInfo.Utc)}";
        var lines = new List<string>
        {
            $"Criterion: {o.Threshold.Comparison}.",
            "Basis: " + o.Threshold.Basis switch
            {
                ThresholdBasis.Absolute => "a fixed threshold.",
                ThresholdBasis.BaselineDeviation => "deviation from this session's own baseline.",
                _ => "a fixed threshold together with deviation from this session's own baseline."
            }
        };
        if (o.Threshold.Baseline is { } b)
            lines.Add(b.Reliable
                ? $"Session baseline: median {FindingText.Value(b.Median, b.Unit)}, robust deviation {FindingText.Value(b.Sigma, b.Unit)}, from {FindingText.Count(b.Samples)} valid samples over {FindingText.Span(b.Span)}."
                : $"Session baseline not used: {b.Reason}");
        Threshold = lines;
        Ending = o.Ending switch
        {
            ObservationEnd.Recovered when o.RecoveredAt is { } at => $"Ended: valid values were back within the normal criterion at {HistoryTime.Format(at, zone)}.",
            ObservationEnd.ContextEnded => "Ended: the display activity this measurement depends on stopped. That is not a recovery.",
            _ => "Ended: measurements stopped or were missing after this interval. Recovery is not confirmed."
        };
        Limitations = o.Limitations.ToArray();
        Events = o.AssociatedEvents.Select(e => $"{HistoryTime.Format(e.At, zone)}  {e.Title}").Concat(
            o.AssociatedEventCount > o.AssociatedEvents.Count ? [$"+ {FindingText.Count(o.AssociatedEventCount - o.AssociatedEvents.Count)} more recorded events nearby"] : []).ToArray();
    }

    private static HistoryMetricRow Row(MetricEvidence m, TimeZoneInfo zone)
    {
        var none = m.ValidSamples == 0;
        string N(double v) => none ? "no valid value" : FindingText.Number(v);
        return new(m.Role, m.Title, m.Unit.Length == 0 ? "no unit recorded" : m.Unit, N(m.Minimum), N(m.P50), N(m.P95), N(m.Maximum),
            none ? "—" : HistoryTime.Format(m.WorstAt, zone), FindingText.Count(m.ValidSamples),
            none ? $"{m.Title}: no valid measurement in this interval"
                : $"{m.Title} ({m.Unit}): min {N(m.Minimum)}, P50 {N(m.P50)}, P95 {N(m.P95)}, max {N(m.Maximum)}; {FindingText.Count(m.ValidSamples)} valid samples");
    }

    public string Explanation { get; }
    public string KeyValues { get; }
    public string Utc { get; }
    public IReadOnlyList<HistoryMetricRow> Metrics { get; }
    public IReadOnlyList<string> Threshold { get; }
    public string Ending { get; }
    public IReadOnlyList<string> Limitations { get; }
    public IReadOnlyList<string> Events { get; }
    public bool HasLimitations => Limitations.Count > 0 || Events.Count > 0;
    public string LimitationsHeader => $"Recording limitations and nearby events ({(Limitations.Count + Events.Count).ToString(CultureInfo.InvariantCulture)})";
}

/// <summary>Formatting shared by the findings presentation: invariant, so a result reads the same on every machine.</summary>
internal static class FindingText
{
    public static string Number(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);
    public static string Count(long value) => value.ToString("N0", CultureInfo.InvariantCulture);
    public static string Percent(double fraction) => (fraction * 100).ToString("0.0", CultureInfo.InvariantCulture) + "%";
    public static string Value(double value, string unit) => !double.IsFinite(value) ? "no valid value" : unit.Length == 0 ? Number(value) : unit == "%" ? Number(value) + "%" : $"{Number(value)} {unit}";
    public static string Plural(long n, string singular, string plural) => $"{Count(n)} {(n == 1 ? singular : plural)}";

    /// <summary>"2 min 03 s", "59.9 s", "1 h 30 min".</summary>
    public static string Span(TimeSpan span)
    {
        span = span.Duration();
        if (span.TotalHours >= 1) return $"{(int)span.TotalHours} h {span.Minutes:00} min";
        if (span.TotalMinutes >= 1) return $"{(int)span.TotalMinutes} min {span.Seconds:00} s";
        return span.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture) + " s";
    }
}

/// <summary>One row of the findings list: severity, title, the measured value that matters, when and for how long. The evidence stays attached to the row.</summary>
public sealed class HistoryObservationItem
{
    private readonly TimeZoneInfo zone;
    private HistoryObservationDetail? detail;

    public HistoryObservationItem(AnomalyObservation observation, TimeZoneInfo zone, bool withDate)
    {
        Observation = observation; this.zone = zone;
        Domain = HistoryChannels.GroupTitle(observation.Domain);
        Time = HistoryTime.Format(observation.Start, zone, withDate) + "–" + HistoryTime.Format(observation.End, zone, withDate);
        Duration = FindingText.Span(observation.Duration);
        var p = observation.Primary;
        var lowerIsWorse = HistoryChannels.Find(p.ChannelId) is { HigherIsWorse: false };
        Measurement = p.ValidSamples == 0 || !double.IsFinite(p.Maximum)
            ? $"{p.Title}: no valid measurement in this interval"
            : lowerIsWorse ? $"{p.Title}: lowest {FindingText.Value(p.Minimum, p.Unit)} · P50 {FindingText.Value(p.P50, p.Unit)}"
            : $"{p.Title}: P95 {FindingText.Value(p.P95, p.Unit)} · max {FindingText.Value(p.Maximum, p.Unit)}";
        StatusBadge = observation.Status == AnalysisStatus.QualifiedObservation ? $"Qualified: {FindingText.Percent(observation.Coverage)} usable samples"
            : observation.Ending == ObservationEnd.CoverageEnded ? "Ended when data stopped"
            : observation.Ending == ObservationEnd.ContextEnded ? "Ended when display activity stopped" : "";
    }

    public AnomalyObservation Observation { get; }
    public string Id => Observation.Id;
    public string Title => Observation.Title;
    public string Domain { get; }
    /// <summary>Time of the interval in the selected time zone.</summary>
    public string Time { get; }
    public string Duration { get; }
    /// <summary>The primary metric and the recorded value that describes the finding best, in the recorded unit.</summary>
    public string Measurement { get; }
    /// <summary>Empty for a complete, recovered observation; otherwise why the evidence is qualified or incomplete.</summary>
    public string StatusBadge { get; }
    public bool HasStatusBadge => StatusBadge.Length > 0;
    public string SeverityLabel => Observation.Severity.ToString();
    /// <summary>The tag of the severity icon style.</summary>
    public string SeverityTag => Observation.Severity.ToString();
    /// <summary>What the status of the observation means; a measured condition is never a confirmed cause.</summary>
    public string Status => Observation.Status == AnalysisStatus.QualifiedObservation
        ? "Qualified: the evidence is incomplete" : "Measured observation, not a confirmed cause";
    public HistoryObservationDetail Detail => detail ??= new(Observation, zone);
    public string AutomationName => $"{SeverityLabel}: {Title}. {Measurement}. {Time}, {Duration}." + (HasStatusBadge ? " " + StatusBadge + "." : "");
}

/// <summary>A correlation is a timing relationship between observations. Only a true common overlap is presented as shared time; chained and tolerance-linked ones say so.</summary>
public sealed class HistoryCorrelationItem
{
    internal HistoryCorrelationItem(AnomalyCorrelation correlation, TimeZoneInfo zone, IReadOnlyDictionary<string, HistoryObservationItem> observations, bool withDate)
    {
        Correlation = correlation;
        IsPerformance = correlation.Pattern != CorrelationPattern.AvailabilityOverlap;
        Components = correlation.ComponentIds.Select(id => observations.GetValueOrDefault(id)).OfType<HistoryObservationItem>().ToArray();
        string Name(string id) => observations.TryGetValue(id, out var o) ? o.Observation.Primary.Title : "a measurement";
        Summary = string.Join(" + ", Components.Select(c => c.Observation.Primary.Title).Distinct(StringComparer.Ordinal));
        Time = HistoryTime.Format(correlation.Start, zone, withDate) + "–" + HistoryTime.Format(correlation.End, zone, withDate);
        Timing = correlation.Timing switch
        {
            CorrelationTiming.CommonOverlap when correlation.CommonOverlapStart is { } s && correlation.CommonOverlapEnd is { } e =>
                $"Present together {HistoryTime.Format(s, zone, withDate)}–{HistoryTime.Format(e, zone, withDate)}",
            CorrelationTiming.WithinTolerance => "Close together in time, not overlapping",
            CorrelationTiming.Chained => "Linked in a chain: no single time interval contains every component",
            _ => "Next to missing data or a session change"
        };
        Links = correlation.Links.Select(l => $"{Name(l.FirstId)} and {Name(l.SecondId)}: " +
            (l.OverlapStart is { } s && l.OverlapEnd is { } e
                ? $"overlap {HistoryTime.Format(s, zone, withDate)}–{HistoryTime.Format(e, zone, withDate)}"
                : $"{FindingText.Span(l.Gap)} apart, no overlap")).ToArray();
        // Never an interval that is not shared: only a true common overlap is highlighted; every other timing is navigated as its span.
        CommonStart = correlation.Timing == CorrelationTiming.CommonOverlap ? correlation.CommonOverlapStart : null;
        CommonEnd = correlation.Timing == CorrelationTiming.CommonOverlap ? correlation.CommonOverlapEnd : null;
    }

    public AnomalyCorrelation Correlation { get; }
    public bool IsPerformance { get; }
    public string Title => Correlation.Title;
    public string SeverityTag => Correlation.Severity.ToString();
    public string Summary { get; }
    public string Time { get; }
    public string Timing { get; }
    public DateTimeOffset? CommonStart { get; }
    public DateTimeOffset? CommonEnd { get; }
    public IReadOnlyList<HistoryObservationItem> Components { get; }
    public string Explanation => Correlation.Explanation;
    public IReadOnlyList<string> Links { get; }
    public IReadOnlyList<string> Limitations => Correlation.Limitations;
    public bool HasLinks => Links.Count > 0;
    public string AutomationName => $"{Title}: {Summary}. {Timing}.";
}

/// <summary>One kind of "what could not be said", with its own heading: the states are never presented as equivalent.</summary>
public sealed record QualityGroup(string Heading, string Note, IReadOnlyList<string> Lines)
{
    public string HeadingWithCount => $"{Heading} ({Lines.Count.ToString(CultureInfo.InvariantCulture)})";
}

/// <summary>
/// Presentation-only projection of the deterministic M1 analysis. Owns no recording or chart data; clearing it releases all findings. The history page owns
/// cancellation and the loading lifecycle. Navigation goes through delegates, so nothing here knows about charts or windows.
/// </summary>
public sealed class HistoryFindingsViewModel : ObservableObject
{
    private const string Idle = "Open a recording to analyze its measured performance.";
    private static readonly IReadOnlyDictionary<string, string> RuleTitles = AnomalyRules.Default.ToDictionary(r => r.Id, r => r.Title, StringComparer.Ordinal);

    private readonly Action<FindingTarget> navigate;
    private readonly Action clearEmphasis;
    private readonly Action showFullRecording;
    private TimeZoneInfo zone;
    private string status = Idle, summary = "", snapshotNote = "", dataQuality = "";
    private HistoryObservationItem? selectedObservation;
    private HistoryCorrelationItem? selectedCorrelation, selectedContext;
    private FindingFilterChoice<FindingSeverityFilter> severity;
    private FindingFilterChoice<FindingDomainFilter> domain;
    private IReadOnlyList<HistoryObservationItem> all = [];
    private DateTimeOffset? highlightStart, highlightEnd, peakAt;
    private bool applying;

    public HistoryFindingsViewModel(TimeZoneInfo zone, Action<FindingTarget> navigate, Action? clearEmphasis = null, Action? showFullRecording = null)
    {
        this.zone = zone; this.navigate = navigate; this.clearEmphasis = clearEmphasis ?? (() => { }); this.showFullRecording = showFullRecording ?? (() => { });
        SeverityFilters = [new(FindingSeverityFilter.All, "All"), new(FindingSeverityFilter.Critical, "Critical"), new(FindingSeverityFilter.Warning, "Warning"), new(FindingSeverityFilter.Info, "Info")];
        DomainFilters = [new(FindingDomainFilter.All, "All areas"), new(FindingDomainFilter.Connection, "Connection"), new(FindingDomainFilter.Graphics, "Graphics"), new(FindingDomainFilter.Guest, "VDI Guest")];
        severity = SeverityFilters[0]; domain = DomainFilters[0];
        ShowFullRecordingCommand = new(ShowFull, () => selectedObservation is not null || selectedCorrelation is not null || selectedContext is not null || highlightStart is not null);
        ShowInChartsCommand = new(RequestCharts, () => HasSelection);
        RefreshCounts();
    }

    private void RequestCharts() { ChartsRequest++; Raise(nameof(ChartsRequest)); }

    // ---------- results ----------

    public IReadOnlyList<FindingFilterChoice<FindingSeverityFilter>> SeverityFilters { get; }
    public IReadOnlyList<FindingFilterChoice<FindingDomainFilter>> DomainFilters { get; }
    /// <summary>Every observation of the analysis, most severe first, then in time order.</summary>
    public IReadOnlyList<HistoryObservationItem> Observations => all;
    /// <summary>What the filters let through; replaced as one change.</summary>
    public BulkObservableCollection<HistoryObservationItem> VisibleObservations { get; } = [];
    public IReadOnlyList<HistoryCorrelationItem> PerformanceCorrelations { get; private set; } = [];
    public IReadOnlyList<HistoryCorrelationItem> ContextCorrelations { get; private set; } = [];
    public IReadOnlyList<HistoryCorrelationItem> Correlations => [.. PerformanceCorrelations, .. ContextCorrelations];
    public IReadOnlyList<QualityGroup> QualityGroups { get; private set; } = [];
    public IReadOnlyList<string> Limitations { get; private set; } = [];

    public bool HasObservations => all.Count > 0;
    public bool HasVisibleObservations => VisibleObservations.Count > 0;
    public bool HasPerformanceCorrelations => PerformanceCorrelations.Count > 0;
    public bool HasContextCorrelations => ContextCorrelations.Count > 0;
    public string ContextHeader => $"Recording and availability context ({ContextCorrelations.Count.ToString(CultureInfo.InvariantCulture)})";
    public string QualityHeader { get; private set; } = "Data quality and what could not be evaluated";
    public bool HasQualityGroups => QualityGroups.Count > 0;

    /// <summary>The load state: waiting, analyzing, or why the analysis is unavailable.</summary>
    public string Status { get => status; private set => Set(ref status, value); }
    /// <summary>"6 observations · 1 warning · 5 informational · 2 performance correlations", from the actual analysis.</summary>
    public string Summary { get => summary; private set => Set(ref summary, value); }
    /// <summary>"Data coverage: 98.5% · 1 gap · 14.8 s missing", from the recording's own metadata.</summary>
    public string DataQuality { get => dataQuality; private set { if (Set(ref dataQuality, value)) Raise(nameof(HasDataQuality)); } }
    public bool HasDataQuality => dataQuality.Length > 0;
    /// <summary>Set for a recording that is still being written, was interrupted, or was analyzed only in part: what the findings do and do not cover.</summary>
    public string SnapshotNote { get => snapshotNote; private set { if (Set(ref snapshotNote, value)) Raise(nameof(HasSnapshotNote)); } }
    public bool HasSnapshotNote => snapshotNote.Length > 0;
    /// <summary>"Showing 3 of 6" when a filter hides findings.</summary>
    public string FilterNote => all.Count == 0 ? "" : VisibleObservations.Count == all.Count ? "" : $"Showing {VisibleObservations.Count.ToString(CultureInfo.InvariantCulture)} of {all.Count.ToString(CultureInfo.InvariantCulture)} findings";
    public string EmptyNote =>
        all.Count == 0 ? (analyzed ? "No observation: no rule that could be evaluated detected a condition. That is not proof the session was healthy; see data quality below." : "")
        : VisibleObservations.Count == 0 ? "No finding matches these filters." : "";
    private bool analyzed;
    private string exportMessage = "";
    /// <summary>The analysis behind the list, kept so it can be exported; released with everything else.</summary>
    internal AnomalyAnalysis? CurrentAnalysis { get; private set; }
    public bool HasAnalysis => CurrentAnalysis is not null;
    /// <summary>The outcome of the last export of the findings (saved where, cancelled, failed and why); empty until one is attempted.</summary>
    public string ExportMessage { get => exportMessage; internal set => Set(ref exportMessage, value); }

    // ---------- filters ----------

    public FindingFilterChoice<FindingSeverityFilter> SelectedSeverity
    {
        get => severity;
        set { if (value is not null && Set(ref severity, value)) ApplyFilters(); }
    }

    public FindingFilterChoice<FindingDomainFilter> SelectedDomain
    {
        get => domain;
        set { if (value is not null && Set(ref domain, value)) ApplyFilters(); }
    }

    private static int Rank(Severity s) => s switch { EucLens.Core.Severity.Critical => 0, EucLens.Core.Severity.Warning => 1, _ => 2 };

    private bool Passes(HistoryObservationItem item) =>
        (severity.Value == FindingSeverityFilter.All || item.Observation.Severity.ToString() == severity.Value.ToString())
        && (domain.Value == FindingDomainFilter.All || item.Observation.Domain.ToString() == domain.Value.ToString());

    private void RefreshCounts()
    {
        foreach (var option in SeverityFilters)
            option.SetCount(option.Value == FindingSeverityFilter.All ? all.Count : all.Count(i => i.Observation.Severity.ToString() == option.Value.ToString()));
        foreach (var option in DomainFilters)
            option.SetCount(option.Value == FindingDomainFilter.All ? all.Count : all.Count(i => i.Observation.Domain.ToString() == option.Value.ToString()));
    }

    private void ApplyFilters()
    {
        applying = true;
        VisibleObservations.ReplaceAll(all.Where(Passes));
        applying = false;
        // A selected finding that the filter now hides is no longer shown as selected; the charts keep their range.
        if (selectedObservation is not null && !VisibleObservations.Contains(selectedObservation)) { selectedObservation = null; Raise(nameof(SelectedObservation)); Raise(nameof(HasSelectedObservation)); Raise(nameof(HasSelection)); ClearHighlight(); clearEmphasis(); }
        // The list drops its selection when its items are replaced; a finding that is still shown is selected in it again.
        else if (selectedObservation is not null) Raise(nameof(SelectedObservation));
        Raise(nameof(HasVisibleObservations)); Raise(nameof(FilterNote)); Raise(nameof(EmptyNote));
        RefreshSelectionCommands();
    }

    // ---------- selection ----------

    public HistoryObservationItem? SelectedObservation
    {
        get => selectedObservation;
        set
        {
            if (applying || !Set(ref selectedObservation, value)) return;
            Raise(nameof(HasSelectedObservation)); Raise(nameof(HasSelection));
            if (value is null) { RefreshSelectionCommands(); return; }
            ClearCorrelationSelection();
            Go(new(value.Observation.Start, value.Observation.End, value.Observation.Primary.ValidSamples > 0 ? value.Observation.Primary.WorstAt : null,
                [value.Observation.Primary.ChannelId], value.Observation.Metrics.Skip(1).Select(m => m.ChannelId).ToArray(), value.Observation.Start, value.Observation.End));
        }
    }

    /// <summary>A performance correlation selected in its list.</summary>
    public HistoryCorrelationItem? SelectedCorrelation
    {
        get => selectedCorrelation;
        set
        {
            if (!Set(ref selectedCorrelation, value)) return;
            Raise(nameof(ActiveCorrelation)); Raise(nameof(HasSelectedCorrelation)); Raise(nameof(HasSelection));
            if (value is null) { RefreshSelectionCommands(); return; }
            ClearObservationSelection(); ClearContextSelection();
            GoCorrelation(value);
        }
    }

    /// <summary>A recording-and-availability context note selected in its own list.</summary>
    public HistoryCorrelationItem? SelectedContextCorrelation
    {
        get => selectedContext;
        set
        {
            if (!Set(ref selectedContext, value)) return;
            Raise(nameof(ActiveCorrelation)); Raise(nameof(HasSelectedCorrelation)); Raise(nameof(HasSelection));
            if (value is null) { RefreshSelectionCommands(); return; }
            ClearObservationSelection(); ClearPerformanceSelection();
            GoCorrelation(value);
        }
    }

    /// <summary>The correlation whose detail is shown, from either list.</summary>
    public HistoryCorrelationItem? ActiveCorrelation => selectedCorrelation ?? selectedContext;
    public bool HasSelectedObservation => selectedObservation is not null;
    public bool HasSelectedCorrelation => ActiveCorrelation is not null;
    public bool HasSelection => HasSelectedObservation || HasSelectedCorrelation;
    private void RefreshSelectionCommands() { ShowFullRecordingCommand.Refresh(); ShowInChartsCommand.Refresh(); }

    /// <summary>A component of the shown correlation chosen for inspection: selects that observation (clearing the correlation) and navigates to it.</summary>
    public HistoryObservationItem? SelectedComponent
    {
        get => null;
        set
        {
            if (value is null) return;
            // The component may be hidden by a filter: show everything so the selection is visible in the list.
            if (!VisibleObservations.Contains(value)) { severity = SeverityFilters[0]; domain = DomainFilters[0]; Raise(nameof(SelectedSeverity)); Raise(nameof(SelectedDomain)); ApplyFilters(); }
            SelectedObservation = value;
            Raise(nameof(SelectedComponent));
        }
    }

    private void GoCorrelation(HistoryCorrelationItem item)
    {
        var parts = item.Components;
        var primary = parts.Select(p => p.Observation.Primary.ChannelId).Distinct(StringComparer.Ordinal).ToArray();
        var supporting = parts.SelectMany(p => p.Observation.Metrics.Skip(1).Select(m => m.ChannelId)).Distinct(StringComparer.Ordinal).Except(primary).ToArray();
        // The view covers the real span of what is linked. A shared interval exists only for a true common overlap, and only then is it highlighted.
        Go(new(item.Correlation.Start, item.Correlation.End, null, primary, supporting, item.CommonStart, item.CommonEnd));
    }

    private void Go(FindingTarget target)
    {
        highlightStart = target.HighlightStart; highlightEnd = target.HighlightEnd; peakAt = target.Peak;
        RaiseHighlight();
        RefreshSelectionCommands();
        navigate(target);
    }

    private void ClearObservationSelection() { if (selectedObservation is null) return; selectedObservation = null; Raise(nameof(SelectedObservation)); Raise(nameof(HasSelectedObservation)); }
    private void ClearPerformanceSelection() { if (selectedCorrelation is null) return; selectedCorrelation = null; Raise(nameof(SelectedCorrelation)); }
    private void ClearContextSelection() { if (selectedContext is null) return; selectedContext = null; Raise(nameof(SelectedContextCorrelation)); }
    private void ClearCorrelationSelection() { ClearPerformanceSelection(); ClearContextSelection(); Raise(nameof(ActiveCorrelation)); Raise(nameof(HasSelectedCorrelation)); }

    private void ClearHighlight() { highlightStart = highlightEnd = peakAt = null; RaiseHighlight(); }
    private void RaiseHighlight() { Raise(nameof(HighlightStart)); Raise(nameof(HighlightEnd)); Raise(nameof(PeakAt)); }

    /// <summary>The interval every selected component shares, drawn on the charts and the strip; null when nothing selected has one.</summary>
    public DateTimeOffset? HighlightStart => highlightStart;
    public DateTimeOffset? HighlightEnd => highlightEnd;
    /// <summary>When the primary metric reached its recorded extreme inside the selected observation.</summary>
    public DateTimeOffset? PeakAt => peakAt;
    public RelayCommand ShowFullRecordingCommand { get; }
    /// <summary>Asks the page to bring the emphasized chart into view (a button, or a click on a finding). Selecting with the arrow keys never moves the page.</summary>
    public RelayCommand ShowInChartsCommand { get; }
    public int ChartsRequest { get; private set; }

    /// <summary>Back to the whole recording: nothing selected, nothing highlighted, every chart on the full range.</summary>
    public void ShowFull()
    {
        ClearObservationSelection(); ClearCorrelationSelection(); ClearHighlight();
        Raise(nameof(HasSelection));
        RefreshSelectionCommands();
        showFullRecording();
    }

    public void SetTimeZone(TimeZoneInfo next) => zone = next;

    // ---------- lifecycle ----------

    public void Clear()
    {
        selectedObservation = null; selectedCorrelation = null; selectedContext = null; highlightStart = highlightEnd = peakAt = null;
        all = []; analyzed = false; CurrentAnalysis = null; exportMessage = ""; Raise(nameof(ExportMessage)); Raise(nameof(HasAnalysis)); applying = true; VisibleObservations.ReplaceAll([]); applying = false;
        PerformanceCorrelations = []; ContextCorrelations = []; QualityGroups = []; Limitations = [];
        severity = SeverityFilters[0]; domain = DomainFilters[0];
        RefreshCounts();
        Summary = ""; DataQuality = ""; SnapshotNote = ""; QualityHeader = "Data quality and what could not be evaluated";
        RaiseAll();
        Status = Idle;
        RefreshSelectionCommands();
    }

    public void Loading()
    {
        Clear();
        Status = "Analyzing recorded metrics locally…";
    }

    public void Failed(string message)
    {
        Clear();
        Status = $"Analysis unavailable: {message}. The recording and charts remain usable.";
    }

    public void Show(AnomalyAnalysis analysis, HistoryInfo info)
    {
        var multiDay = analysis.From is { } from && analysis.To is { } to && HistoryTime.ToLocal(from, zone).Date != HistoryTime.ToLocal(to, zone).Date;
        all = analysis.Observations.OrderBy(o => Rank(o.Severity)).ThenBy(o => o.Start).ThenBy(o => o.Id, StringComparer.Ordinal)
            .Select(o => new HistoryObservationItem(o, zone, multiDay)).ToArray();
        var byId = all.ToDictionary(i => i.Id, StringComparer.Ordinal);
        var correlations = analysis.Correlations.Select(c => new HistoryCorrelationItem(c, zone, byId, multiDay)).ToArray();
        PerformanceCorrelations = correlations.Where(c => c.IsPerformance).ToArray();
        ContextCorrelations = correlations.Where(c => !c.IsPerformance).ToArray();
        Limitations = analysis.Limitations.ToArray();
        analyzed = true; CurrentAnalysis = analysis; Raise(nameof(HasAnalysis));
        RefreshCounts();
        selectedObservation = null; selectedCorrelation = null; selectedContext = null; highlightStart = highlightEnd = peakAt = null;
        applying = true; VisibleObservations.ReplaceAll(all.Where(Passes)); applying = false;
        Summary = BuildSummary(analysis.Observations, PerformanceCorrelations.Count, ContextCorrelations.Count);
        DataQuality = BuildDataQuality(info);
        SnapshotNote = BuildSnapshotNote(info, analysis);
        QualityGroups = BuildQualityGroups(analysis, info);
        QualityHeader = BuildQualityHeader(QualityGroups);
        RaiseAll();
        Status = analysis.ReducedConfidence
            ? "Reduced-confidence analysis: read the limitations before relying on a finding."
            : "Measured conditions from the recorded samples, not proven root causes.";
        RefreshSelectionCommands();
    }

    // ---------- text ----------

    private static string BuildSummary(IReadOnlyList<AnomalyObservation> observations, int performance, int context)
    {
        var parts = new List<string> { FindingText.Plural(observations.Count, "observation", "observations") };
        var critical = observations.Count(o => o.Severity == EucLens.Core.Severity.Critical);
        var warning = observations.Count(o => o.Severity == EucLens.Core.Severity.Warning);
        var info = observations.Count(o => o.Severity == EucLens.Core.Severity.Info);
        if (critical > 0) parts.Add($"{FindingText.Count(critical)} critical");
        if (warning > 0) parts.Add($"{FindingText.Count(warning)} {(warning == 1 ? "warning" : "warnings")}");
        if (info > 0) parts.Add($"{FindingText.Count(info)} informational");
        parts.Add(FindingText.Plural(performance, "performance correlation", "performance correlations"));
        if (context > 0) parts.Add(FindingText.Plural(context, "availability note", "availability notes"));
        return string.Join(" · ", parts);
    }

    private static string BuildDataQuality(HistoryInfo info)
    {
        var parts = new List<string> { $"Data coverage: {FindingText.Percent(info.DataCoverage)}",
            info.Gaps == 0 ? "no gaps" : $"{FindingText.Plural(info.Gaps, "gap", "gaps")} · {FindingText.Span(info.GapTime)} missing" };
        if (info.LostSamples > 0) parts.Add($"{FindingText.Plural(info.LostSamples, "sample", "samples")} lost while recording");
        return string.Join(" · ", parts);
    }

    private static string BuildSnapshotNote(HistoryInfo info, AnomalyAnalysis analysis) => info.State switch
    {
        RecordingCompleteness.InProgress => "Snapshot of a recording that is still being written. The findings describe the data on disk now; the end of the session is not known, and an observation that is still open ends where the data ends, not where the problem ended.",
        RecordingCompleteness.Interrupted => "Interrupted recording: no end was written. The findings describe the data up to the last complete line; the real end of the session is not known.",
        RecordingCompleteness.Partial => "Partial analysis: only the beginning of this recording was analyzed. Nothing here describes the rest of the session.",
        _ => ""
    };

    private static string RuleTitle(string id) => RuleTitles.GetValueOrDefault(id, id);

    private static IReadOnlyList<QualityGroup> BuildQualityGroups(AnomalyAnalysis analysis, HistoryInfo info)
    {
        var groups = new List<QualityGroup>();
        void Add(string heading, string note, IEnumerable<string> lines)
        {
            var list = lines.Distinct(StringComparer.Ordinal).ToArray();
            if (list.Length > 0) groups.Add(new(heading, note, list));
        }

        var incomplete = new List<string>();
        if (info.State != RecordingCompleteness.Complete) incomplete.Add(info.State switch
        {
            RecordingCompleteness.InProgress => "The recording is still in progress; its end is not known.",
            RecordingCompleteness.Interrupted => "The recording was interrupted; no end of recording was written.",
            _ => "Only the beginning of the recording was analyzed."
        });
        if (analysis.ReducedConfidence) incomplete.Add("The analysis has reduced confidence.");
        incomplete.AddRange(analysis.Limitations);
        Add("Recording incomplete", "What was missing or cut off in the recording itself.", incomplete);

        Add("No usable measurement", "The metric was recorded as unavailable or never had a valid value. It is not shown as zero.",
            analysis.Coverage.Where(c => !c.Usable && c.ValidSamples == 0 && !HistoryChannels.IsUnverified(c.Unit)).Select(c => $"{c.Title}: {c.Reason ?? "no valid value"}"));
        Add("Unverified or unsupported units", "Recorded as raw counters or in units the analyzer does not interpret; shown as recorded, never converted or judged.",
            analysis.Coverage.Where(c => !c.Usable && c.ValidSamples > 0 || HistoryChannels.IsUnverified(c.Unit)).Select(c => $"{c.Title}: {c.Reason ?? "unit not interpreted"}"));
        Add("Partly missing measurements", "Usable, but some samples had no value; findings on these metrics are labelled when that affects them.",
            analysis.Coverage.Where(c => c.Usable && c.ValidSamples < c.Samples).Select(c => $"{c.Title}: {FindingText.Percent(c.Coverage)} of samples had a value"));
        Add("Insufficient evidence", "The rule could not be evaluated; it says nothing about the session.",
            analysis.Rules.Where(r => r.State == RuleState.InsufficientData).Select(r => $"{RuleTitle(r.RuleId)}: {r.Reason ?? "no verified measurement available"}"));
        Add("Rule not applicable", "The rule does not apply to this recording (for example a Horizon-only counter on another protocol, or a static screen).",
            analysis.Rules.Where(r => r.State == RuleState.NotApplicable).Select(r => $"{RuleTitle(r.RuleId)}: {r.Reason ?? "not applicable"}"));
        Add("No anomaly detected", "These rules ran on usable data and matched nothing. That is not proof the session was healthy.",
            analysis.Rules.Where(r => r.State == RuleState.Evaluated && r.Observations == 0).Select(r => RuleTitle(r.RuleId)));
        return groups;
    }

    /// <summary>Short enough to stay on one line at any width; the headings and counts are inside the section.</summary>
    private static string BuildQualityHeader(IReadOnlyList<QualityGroup> groups) =>
        groups.Count == 0 ? "Data quality and what could not be evaluated" : $"Data quality and what could not be evaluated ({groups.Count.ToString(CultureInfo.InvariantCulture)} kinds of note)";

    private void RaiseAll()
    {
        Raise(nameof(Observations)); Raise(nameof(PerformanceCorrelations)); Raise(nameof(ContextCorrelations)); Raise(nameof(Correlations)); Raise(nameof(Limitations));
        Raise(nameof(QualityGroups)); Raise(nameof(QualityHeader)); Raise(nameof(HasQualityGroups)); Raise(nameof(ContextHeader));
        Raise(nameof(HasObservations)); Raise(nameof(HasVisibleObservations)); Raise(nameof(HasPerformanceCorrelations)); Raise(nameof(HasContextCorrelations));
        Raise(nameof(FilterNote)); Raise(nameof(EmptyNote));
        Raise(nameof(SelectedObservation)); Raise(nameof(SelectedCorrelation)); Raise(nameof(SelectedContextCorrelation)); Raise(nameof(ActiveCorrelation));
        Raise(nameof(HasSelectedObservation)); Raise(nameof(HasSelectedCorrelation)); Raise(nameof(HasSelection));
        Raise(nameof(SelectedSeverity)); Raise(nameof(SelectedDomain));
        RaiseHighlight();
    }
}
