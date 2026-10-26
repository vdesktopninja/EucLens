using System.Globalization;
using EucLens.Core;

namespace EucLens.App.ViewModels;

/// <summary>One read-only anomaly result. The actual evidence and rule identity remain attached to the row.</summary>
public sealed class HistoryObservationItem
{
    public HistoryObservationItem(AnomalyObservation observation, TimeZoneInfo zone, bool showGroup)
    {
        Observation = observation;
        Group = HistoryChannels.GroupTitle(observation.Domain);
        ShowGroup = showGroup;
        Time = HistoryTime.Format(observation.Start, zone, withDate: true) + " — " +
               HistoryTime.Format(observation.End, zone, withDate: true);
        Evidence = observation.Metrics.Select(m =>
            $"{m.Title}: {m.ValidSamples.ToString("N0", CultureInfo.InvariantCulture)} valid · " +
            $"min {Number(m.Minimum)}, P50 {Number(m.P50)}, P95 {Number(m.P95)}, max {Number(m.Maximum)} " +
            $"{m.Unit} · extreme at {HistoryTime.Format(m.WorstAt, zone, withDate: true)}").ToArray();
        Limitations = observation.Limitations;
    }

    private static string Number(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);
    public AnomalyObservation Observation { get; }
    public string Id => Observation.Id;
    public string Group { get; }
    public bool ShowGroup { get; }
    public string Title => Observation.Title;
    public string Time { get; }
    public string Severity => Observation.Severity.ToString();
    public string Status => Observation.Status == AnalysisStatus.QualifiedObservation
        ? "Qualified — incomplete evidence" : "Measured observation, not a confirmed cause";
    public string Explanation => Observation.Explanation;
    public string Coverage => $"{Observation.Coverage.ToString("P1", CultureInfo.InvariantCulture)} sample coverage; " +
                              $"{Observation.MatchingSamples.ToString("N0", CultureInfo.InvariantCulture)} matching samples";
    public string Threshold => Observation.Threshold.Comparison;
    public IReadOnlyList<string> Evidence { get; }
    public IReadOnlyList<string> Limitations { get; }
    public string AutomationName => $"{Severity}: {Title}, {Time}. {Status}";
}

/// <summary>A correlation is a timing relationship. Chained and tolerance-linked cases explicitly have no common overlap.</summary>
public sealed class HistoryCorrelationItem
{
    public HistoryCorrelationItem(AnomalyCorrelation correlation, TimeZoneInfo zone)
    {
        Correlation = correlation;
        Time = HistoryTime.Format(correlation.Start, zone, withDate: true) + " — " +
               HistoryTime.Format(correlation.End, zone, withDate: true);
        Timing = correlation.Timing switch
        {
            CorrelationTiming.CommonOverlap when correlation.CommonOverlapStart is { } start && correlation.CommonOverlapEnd is { } end =>
                "Common measured interval: " + HistoryTime.Format(start, zone, withDate: true) + " — " +
                HistoryTime.Format(end, zone, withDate: true),
            CorrelationTiming.WithinTolerance => "Measurements occurred close together, not simultaneously.",
            CorrelationTiming.Chained => "Chained timing only: there is no interval common to every component.",
            _ => "Adjacent to a data gap or a session change."
        };
        Links = correlation.Links.Select(link =>
            $"{link.FirstId} / {link.SecondId}: " +
            (link.OverlapStart is { } start && link.OverlapEnd is { } end
                ? $"overlap {HistoryTime.Format(start, zone)} — {HistoryTime.Format(end, zone)}"
                : $"gap {link.Gap.TotalSeconds.ToString("0.##", CultureInfo.InvariantCulture)} s")).ToArray();
    }

    public AnomalyCorrelation Correlation { get; }
    public string Title => Correlation.Title;
    public string Severity => Correlation.Severity.ToString();
    public string Time { get; }
    public string Timing { get; }
    public string Explanation => Correlation.Explanation;
    public IReadOnlyList<string> Links { get; }
    public IReadOnlyList<string> Limitations => Correlation.Limitations;
    public string AutomationName => $"{Severity}: {Title}. {Timing}";
}

/// <summary>
/// Presentation-only projection of the deterministic M1 analysis. Owns no recording or chart data;
/// clearing this model releases all findings. The history page owns cancellation and the loading lifecycle.
/// </summary>
public sealed class HistoryFindingsViewModel : ObservableObject
{
    private readonly Action<DateTimeOffset, DateTimeOffset, DateTimeOffset?> navigate;
    private TimeZoneInfo zone;
    private string status = "Open a recording to analyze its measured performance.";
    private HistoryObservationItem? selectedObservation;
    private HistoryCorrelationItem? selectedCorrelation;

    public HistoryFindingsViewModel(TimeZoneInfo zone, Action<DateTimeOffset, DateTimeOffset, DateTimeOffset?> navigate)
    {
        this.zone = zone;
        this.navigate = navigate;
    }

    public IReadOnlyList<HistoryObservationItem> Observations { get; private set; } = [];
    public IReadOnlyList<HistoryCorrelationItem> Correlations { get; private set; } = [];
    public IReadOnlyList<string> Limitations { get; private set; } = [];
    public IReadOnlyList<string> InsufficientData { get; private set; } = [];
    public IReadOnlyList<string> Coverage { get; private set; } = [];
    public bool HasObservations => Observations.Count > 0;
    public bool HasCorrelations => Correlations.Count > 0;
    public bool HasSelectedObservation => selectedObservation is not null;
    public bool HasSelectedCorrelation => selectedCorrelation is not null;
    public string Status { get => status; private set => Set(ref status, value); }

    public HistoryObservationItem? SelectedObservation
    {
        get => selectedObservation;
        set
        {
            if (!Set(ref selectedObservation, value)) return;
            Raise(nameof(HasSelectedObservation));
            if (value is null) return;
            selectedCorrelation = null;
            Raise(nameof(SelectedCorrelation)); Raise(nameof(HasSelectedCorrelation));
            navigate(value.Observation.Start, value.Observation.End, value.Observation.Primary.WorstAt);
        }
    }

    public HistoryCorrelationItem? SelectedCorrelation
    {
        get => selectedCorrelation;
        set
        {
            if (!Set(ref selectedCorrelation, value)) return;
            Raise(nameof(HasSelectedCorrelation));
            if (value is null) return;
            selectedObservation = null;
            Raise(nameof(SelectedObservation)); Raise(nameof(HasSelectedObservation));
            // Only a true common overlap can be represented as shared timing. Chained results show their full interval.
            navigate(value.Correlation.CommonOverlapStart ?? value.Correlation.Start,
                     value.Correlation.CommonOverlapEnd ?? value.Correlation.End, null);
        }
    }

    public void SetTimeZone(TimeZoneInfo next) => zone = next;

    public void Clear()
    {
        selectedObservation = null; selectedCorrelation = null;
        Observations = []; Correlations = []; Limitations = []; Coverage = []; InsufficientData = [];
        RaiseAll();
        Status = "Open a recording to analyze its measured performance.";
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

    public void Show(AnomalyAnalysis analysis)
    {
        var previous = (HistoryGroup?)null;
        Observations = analysis.Observations.Select(o =>
        {
            var row = new HistoryObservationItem(o, zone, o.Domain != previous);
            previous = o.Domain;
            return row;
        }).ToArray();
        Correlations = analysis.Correlations.Select(c => new HistoryCorrelationItem(c, zone)).ToArray();
        Limitations = analysis.Limitations.ToArray();
        if (analysis.ReducedConfidence && Limitations.Count == 0)
            Limitations = ["The recording was analyzed with reduced confidence."];
        InsufficientData = analysis.Rules.Where(r => r.State == RuleState.InsufficientData)
            .Select(r => $"{r.RuleId}: {r.Reason ?? "No verified measurement available."}").ToArray();
        Coverage = analysis.Coverage.Where(c => !c.Usable || c.ValidSamples < c.Samples)
            .Select(c => $"{c.Title}: {(c.Usable ? c.Coverage.ToString("P1", CultureInfo.InvariantCulture) + " usable" : "unavailable")}" +
                         (string.IsNullOrWhiteSpace(c.Reason) ? "" : $" — {c.Reason}")).ToArray();
        selectedObservation = null; selectedCorrelation = null;
        RaiseAll();
        Status = $"{Observations.Count.ToString("N0", CultureInfo.InvariantCulture)} measured observation(s), " +
                 $"{Correlations.Count.ToString("N0", CultureInfo.InvariantCulture)} timing correlation(s). " +
                 (analysis.ReducedConfidence ? "Partial / reduced-confidence analysis; read limitations." :
                  "No observations means no rule detected an issue in the available metrics, not proof that the session was healthy.");
    }

    private void RaiseAll()
    {
        Raise(nameof(Observations)); Raise(nameof(Correlations)); Raise(nameof(Limitations));
        Raise(nameof(InsufficientData)); Raise(nameof(Coverage));
        Raise(nameof(HasObservations)); Raise(nameof(HasCorrelations));
        Raise(nameof(SelectedObservation)); Raise(nameof(SelectedCorrelation));
        Raise(nameof(HasSelectedObservation)); Raise(nameof(HasSelectedCorrelation));
    }
}
