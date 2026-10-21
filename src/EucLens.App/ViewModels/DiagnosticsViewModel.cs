using System.Collections.ObjectModel;
using System.Globalization;
using EucLens.Core;

namespace EucLens.App.ViewModels;

/// <summary>
/// Presentation of diagnostic reports. It only formats, filters and sorts what the engine produced; it draws no
/// conclusions of its own. Kept apart from collection and rules so another explanation panel can bind to the same
/// report later without changing either.
/// </summary>
public sealed class DiagnosticsViewModel : ObservableObject
{
    private const string InsufficientData = "Insufficient Data";
    private readonly IDesktopServices desktop;
    private DiagnosticThresholds thresholds = DiagnosticThresholds.Defaults;
    private IReadOnlyList<FindingCard> cards = [];
    private FindingFilterItem selectedFilter;
    private FindingItem? selectedFinding;
    private bool userSelected;
    private FindingSortKey sortKey = FindingSortKey.Severity;
    private bool sortDescending;
    private Choice<double> selectedTrendWindow;
    private string headline = "No conclusion yet", headlineState = InsufficientData, headlineDetail = "Waiting for the first sample.",
        observation = "Waiting for samples", copyStatus = "", listEmptyTitle = "", listEmptyDetail = "", evaluatedSummary = "",
        noProblemsTitle = "", noProblemsDetail = "";
    private bool hasActiveProblems;
    private DateTime? lastPoint;

    public DiagnosticsViewModel(IDesktopServices desktop)
    {
        this.desktop = desktop;
        Domains = [new(DiagnosticDomain.Connection, "Connection Health"), new(DiagnosticDomain.Graphics, "Graphics Health"), new(DiagnosticDomain.Guest, "VDI Guest Health")];
        Filters =
        [
            new(FindingFilterKind.All, "All"), new(FindingFilterKind.ActiveProblems, "Active problems"), new(FindingFilterKind.Critical, "Critical"),
            new(FindingFilterKind.Warning, "Warning"), new(FindingFilterKind.Information, "Information"), new(FindingFilterKind.Recovered, "Recovered"),
        ];
        selectedFilter = Filters[0];
        Columns =
        [
            new(FindingSortKey.Severity, "Severity", Sort), new(FindingSortKey.Finding, "Finding", Sort), new(FindingSortKey.Category, "Category", Sort),
            new(FindingSortKey.FirstDetected, "First detected", Sort), new(FindingSortKey.Duration, "Duration", Sort), new(FindingSortKey.State, "State", Sort),
        ];
        Rtt = new("Session RTT", "ms", "Blast round-trip time", minimum: 0);
        Fps = new("Frame rate", "FPS", "Delivered frames per second", minimum: 0);
        Cpu = new("Guest CPU", "%", "Machine-wide guest CPU utilization", minimum: 0, maximum: 100);
        PacketLoss = new("Packet loss (uplink)", "%", "Blast uplink packet loss", minimum: 0);
        Trends = [Rtt, Fps, Cpu, PacketLoss];
        TrendWindows = Windows(thresholds);
        selectedTrendWindow = TrendWindows[1];
        CopyFindingCommand = new(CopyFinding, () => selectedFinding is not null);
        CopySummaryCommand = new(CopySummary);
        UpdateColumns();
        Configure(thresholds);
        Clear();
    }

    public ObservableCollection<DomainHealthItem> Domains { get; }
    public DataQualityItem DataQuality { get; } = new();
    public ObservableCollection<HypothesisCard> Hypotheses { get; } = [];
    public ObservableCollection<FindingItem> Findings { get; } = [];
    public IReadOnlyList<FindingFilterItem> Filters { get; }
    public IReadOnlyList<FindingColumn> Columns { get; }
    public FindingFilterItem SelectedFilter
    {
        get => selectedFilter;
        set { if (value is not null && Set(ref selectedFilter, value)) RefreshFindings(); }
    }
    public FindingSortKey SortKey => sortKey;
    public bool SortDescending => sortDescending;
    /// <summary>Selected finding for the evidence panel. Until the user picks one, the most important finding is shown.</summary>
    public FindingItem? SelectedFinding
    {
        get => selectedFinding;
        set
        {
            // Selectors clear their selection while items move; keep it while the finding is still listed.
            if (value is null && selectedFinding is not null && Findings.Contains(selectedFinding)) { Raise(nameof(SelectedFinding)); return; }
            if (!Set(ref selectedFinding, value)) return;
            userSelected = value is not null;
            SelectionChanged();
        }
    }
    public bool HasSelection => selectedFinding is not null;
    public string Headline { get => headline; private set => Set(ref headline, value); }
    /// <summary>Healthy, Warning, Critical or Insufficient Data; drives only the small status marker.</summary>
    public string HeadlineState { get => headlineState; private set => Set(ref headlineState, value); }
    public string HeadlineDetail { get => headlineDetail; private set => Set(ref headlineDetail, value); }
    public string Observation { get => observation; private set => Set(ref observation, value); }
    public bool HasActiveProblems { get => hasActiveProblems; private set => Set(ref hasActiveProblems, value); }
    public string EvaluatedSummary { get => evaluatedSummary; private set => Set(ref evaluatedSummary, value); }
    public string ListEmptyTitle { get => listEmptyTitle; private set => Set(ref listEmptyTitle, value); }
    public string ListEmptyDetail { get => listEmptyDetail; private set => Set(ref listEmptyDetail, value); }
    public bool IsListEmpty => Findings.Count == 0;
    public bool HasFindings => Findings.Count > 0;
    public bool HasNoSelection => selectedFinding is null;
    /// <summary>Shown above a non-empty list (e.g. information only) when nothing needs attention.</summary>
    public bool ShowNoProblemsBanner => !HasActiveProblems && Findings.Count > 0;
    public string NoProblemsTitle { get => noProblemsTitle; private set => Set(ref noProblemsTitle, value); }
    public string NoProblemsDetail { get => noProblemsDetail; private set => Set(ref noProblemsDetail, value); }
    public string CopyStatus { get => copyStatus; private set => Set(ref copyStatus, value); }
    public RelayCommand CopyFindingCommand { get; }
    public RelayCommand CopySummaryCommand { get; }

    public TrendChart Rtt { get; }
    public TrendChart Fps { get; }
    public TrendChart Cpu { get; }
    public TrendChart PacketLoss { get; }
    public IReadOnlyList<TrendChart> Trends { get; }
    public IReadOnlyList<Choice<double>> TrendWindows { get; private set; }
    public Choice<double> SelectedTrendWindow
    {
        get => selectedTrendWindow;
        set { if (value is not null && Set(ref selectedTrendWindow, value)) UpdateTrendAxes(); }
    }

    private static IReadOnlyList<Choice<double>> Windows(DiagnosticThresholds t) =>
        [new(t.ShortWindowSeconds, Seconds(t.ShortWindowSeconds)), new(t.StandardWindowSeconds, Seconds(t.StandardWindowSeconds)), new(t.LongWindowSeconds, Seconds(t.LongWindowSeconds))];
    private static string Seconds(double s) => string.Create(CultureInfo.InvariantCulture, $"{s:0} s");

    /// <summary>Threshold lines and trend windows follow the configured values; nothing is hard-coded.</summary>
    public void Configure(DiagnosticThresholds value)
    {
        thresholds = value;
        var index = TrendWindows.ToList().IndexOf(selectedTrendWindow);
        TrendWindows = Windows(value);
        Raise(nameof(TrendWindows));
        selectedTrendWindow = TrendWindows[index < 0 ? 1 : index];
        Raise(nameof(SelectedTrendWindow));
        // Full wording in the chart header; a short label on the line itself so it never runs past a narrow plot.
        Rtt.SetReferences(new ReferenceLine("Warning", value.RttWarningMs, $"Warning ≥ {N(value.RttWarningMs)} ms", $"Warning {N(value.RttWarningMs)}"),
            new ReferenceLine("Critical", value.RttCriticalMs, $"Critical ≥ {N(value.RttCriticalMs)} ms", $"Critical {N(value.RttCriticalMs)}"));
        Fps.SetReferences(new ReferenceLine("Warning", value.LowFps, $"Low FPS < {N(value.LowFps)} (active workloads only)", $"Low FPS {N(value.LowFps)}"));
        Cpu.SetReferences(new ReferenceLine("Warning", value.CpuWarningPercent, $"Warning ≥ {N(value.CpuWarningPercent)} %", $"Warning {N(value.CpuWarningPercent)} %"));
        PacketLoss.SetReferences(new ReferenceLine("Warning", value.PacketLossWarningPercent, $"Warning: mean ≥ {N(value.PacketLossWarningPercent)} %", $"Warning {N(value.PacketLossWarningPercent)} %"));
        UpdateTrendAxes();
    }

    private static string N(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>Resets to the no-data state, e.g. after a mode switch; nothing from the previous mode remains.</summary>
    public void Clear()
    {
        cards = [];
        lastPoint = null;
        foreach (var d in Domains)
        {
            d.State = InsufficientData; d.Coverage = "None"; d.Observation = "Waiting for samples"; d.ActiveProblems = "No active warnings";
            d.Summary = "Waiting for samples."; d.Evidence = ""; d.CoverageNote = "";
            d.Measurements.Clear(); d.Details.Clear(); d.Refresh();
        }
        DataQuality.Level = "Insufficient evidence"; DataQuality.Summary = "Waiting for samples."; DataQuality.Rows.Clear(); DataQuality.Details.Clear();
        Hypotheses.Clear();
        foreach (var t in Trends) t.Clear();
        Headline = "No conclusion yet"; HeadlineState = InsufficientData; HeadlineDetail = "Waiting for the first sample.";
        Observation = "Waiting for samples"; EvaluatedSummary = ""; HasActiveProblems = false; CopyStatus = "";
        userSelected = false;
        RefreshFindings();
    }

    public void Apply(DiagnosticReport report, SessionSnapshot sample)
    {
        var assessments = report.Assessments ?? LegacyAssessments(report);
        var findings = report.Findings ?? LegacyFindings(report);
        var inputs = report.Inputs ?? [];
        var active = findings.Where(f => f.IsActive && f.Severity >= Severity.Warning).ToArray();
        var window = assessments.Select(a => a.WindowSeconds).FirstOrDefault(w => w > 0);
        Observation = window > 0 ? $"Last {window:0} s" : "Single sample";
        foreach (var item in Domains)
            if (assessments.FirstOrDefault(a => a.Domain == item.DomainKey) is { } a) ApplyDomain(item, a, active, inputs, sample);
        ApplyDataQuality(assessments, inputs, sample);
        Replace(Hypotheses, (report.Hypotheses ?? []).Select(h => new HypothesisCard(h.Statement, h.Confidence,
            h.VerificationSteps.Select((step, i) => $"{i + 1}. {step}").ToArray())), (a, b) => a.Statement == b.Statement && a.Confidence == b.Confidence && a.Steps.SequenceEqual(b.Steps));
        ApplyHeadline(assessments, active, inputs);
        cards = findings.Select(FindingCard.From).ToArray();
        RefreshFindings();
        AppendTrends(sample);
    }

    // ---------- health overview ----------

    private static readonly Dictionary<string, string> Labels = new(StringComparer.Ordinal)
    {
        ["RTT"] = "RTT", ["packet loss (uplink)"] = "Packet loss (uplink)", ["reconnects"] = "Reconnects", ["FPS"] = "FPS", ["dirty FPS"] = "Dirty FPS",
        ["out queueing time"] = "Out queueing time", ["CPU"] = "CPU", ["RAM"] = "RAM", ["available RAM"] = "Available RAM", ["disk active time"] = "Disk active time",
        ["GPU"] = "GPU", ["VRAM"] = "VRAM", ["encoder"] = "Video encoder",
    };

    private static MeasurementRow Row(AssessmentInput i)
    {
        var name = Labels.GetValueOrDefault(i.Name, i.Name);
        var label = i.Statistic == "total" ? $"{name} (total)" : $"{name} {i.Statistic}";
        var value = i.State switch
        {
            InputState.Evaluated when i.Value is { } v => $"{Value(v)}{(i.Unit.Length > 0 ? " " + i.Unit : "")}",
            InputState.NotCollected => "Not available",
            _ => "Insufficient data",
        };
        return new(label, value, i.Threshold ?? "", SampleNote(i));
    }

    /// <summary>Precision follows magnitude (253 ms, 51.3 %, 1.99 %), so the overview stays scannable.</summary>
    public static string Value(double v) => v.ToString(Math.Abs(v) >= 100 ? "0" : Math.Abs(v) >= 10 ? "0.#" : "0.##", CultureInfo.InvariantCulture);

    private static string SampleNote(AssessmentInput i) => i.State == InputState.NotCollected
        ? $"No valid samples{(i.Reason is null ? "" : $" — {i.Reason}")}"
        : string.Create(CultureInfo.InvariantCulture, $"{i.ValidSamples} valid of {i.TotalSamples} samples · {i.Coverage:P0} of {i.WindowSeconds:0} s covered")
          + (i.StaleSamples + i.FailedSamples > 0 ? $" · {i.StaleSamples} stale, {i.FailedSamples} failed" : "");

    private static void ApplyDomain(DomainHealthItem item, DomainAssessment a, IReadOnlyList<DiagnosticFinding> active, IReadOnlyList<AssessmentInput> inputs, SessionSnapshot sample)
    {
        bool InDomain(DiagnosticFinding f) => f.Domain == a.Domain || (a.Domain == DiagnosticDomain.Guest && f.Domain == DiagnosticDomain.Gpu);
        var problems = active.Where(InDomain).OrderByDescending(f => f.Severity).ToArray();
        var mine = inputs.Where(i => i.Domain == a.Domain && i.State != InputState.UnverifiedUnit).ToArray();
        item.State = a.StateLabel;
        item.Coverage = a.Coverage.ToString();
        item.Observation = a.WindowSeconds > 0 ? $"Last {a.WindowSeconds:0} s" : "Single sample";
        var critical = problems.Count(f => f.Severity == Severity.Critical);
        item.ActiveProblems = problems.Length == 0 ? "No active warnings"
            : $"{problems.Length} active {(problems.Length == 1 ? "problem" : "problems")}{(critical > 0 ? $" ({critical} critical)" : "")}";
        item.Summary = problems.Length > 0 ? problems[0].Title + (problems.Length > 1 ? $" (+{problems.Length - 1} more)" : "")
            : a.State == HealthState.InsufficientData
                ? mine.Where(i => i.Required && i.State != InputState.Evaluated).ToArray() is { Length: > 0 } missing
                    ? "Not assessed: " + string.Join("; ", missing.Select(i => $"{Labels.GetValueOrDefault(i.Name, i.Name)} {(i.State == InputState.NotCollected ? "not available" : "has too few valid samples")}"))
                    : "Not assessed: not enough valid measurements yet."
                : a.Coverage == AssessmentCoverage.Full ? "Within configured thresholds." : "Within configured thresholds; some inputs are unavailable.";
        item.Evidence = a.Evidence;
        item.CoverageNote = a.CoverageNote;
        var rows = mine.Select(Row).ToList();
        if (a.Domain == DiagnosticDomain.Guest && sample.Gpu.HardwareAcceleration is not true)
            rows.Add(sample.Gpu.HardwareAcceleration == false
                ? new("GPU", "Software rendering", "Not applicable", "No hardware GPU detected; GPU diagnostics do not apply.")
                : new("GPU", "Unknown", "", "GPU inventory unavailable."));
        Replace(item.Measurements, rows);
        // Technical detail behind the expander: per-input sufficiency and the engine's coverage notes.
        Replace(item.Details, mine.Select(i => $"{Labels.GetValueOrDefault(i.Name, i.Name)}{(i.Required ? " (required)" : "")}: {SampleNote(i)}")
            .Concat(a.CoverageNote.Split("; ", StringSplitOptions.RemoveEmptyEntries).Select(n => char.ToUpperInvariant(n[0]) + n[1..])));
        item.Refresh();
    }

    private void ApplyDataQuality(IReadOnlyList<DomainAssessment> assessments, IReadOnlyList<AssessmentInput> inputs, SessionSnapshot sample)
    {
        // A metric used by two domains (CPU) counts once.
        var distinct = inputs.GroupBy(i => i.Key).Select(g => g.First()).ToArray();
        var evaluable = distinct.Where(i => i.State != InputState.UnverifiedUnit).ToArray();
        var evaluated = evaluable.Count(i => i.State == InputState.Evaluated);
        var unverified = distinct.Where(i => i.State == InputState.UnverifiedUnit).ToArray();
        var stale = distinct.Sum(i => i.StaleSamples);
        var failed = distinct.Sum(i => i.FailedSamples);
        DataQuality.Level = assessments.Count == 0 || assessments.All(a => a.Coverage == AssessmentCoverage.None) ? "Insufficient evidence"
            : assessments.All(a => a.Coverage == AssessmentCoverage.Full) ? "Full coverage" : "Partial coverage";
        DataQuality.Summary = evaluable.Length == 0 ? "No diagnostic inputs reported by this engine."
            : $"{evaluated} of {evaluable.Length} diagnostic inputs have enough valid samples.";
        var raw = (sample.BlastMetrics?.Values ?? []).Concat((sample.HostMetrics ?? []).Select(e => e.Metric)).ToArray();
        Replace(DataQuality.Rows,
        [
            new("Diagnostic inputs evaluated", $"{evaluated} of {evaluable.Length}", "", "Inputs with ≥ the configured coverage and valid samples in the window."),
            new("Unavailable or insufficient", (evaluable.Length - evaluated).ToString(CultureInfo.InvariantCulture), "",
                string.Join(", ", evaluable.Where(i => i.State != InputState.Evaluated).Select(i => Labels.GetValueOrDefault(i.Name, i.Name)))),
            new("Stale / failed samples", stale + failed == 0 ? "None" : $"{stale} stale · {failed} failed", "", "Excluded from every threshold."),
            new("Unverified units (not evaluated)", unverified.Length == 0 ? "None" : unverified.Length.ToString(CultureInfo.InvariantCulture), "",
                string.Join(", ", unverified.Select(i => i.Name))),
            new("Collector metrics with a value", sample.Session.IsDemo ? "Synthetic (Demo Mode)" : raw.Length == 0 ? "N/A"
                : $"{raw.Count(m => m.Status == MetricStatus.Available)} of {raw.Length}", "", "Raw counters in the latest sample (Advanced Metrics). Availability is not diagnostic coverage."),
        ]);
        var t = thresholds;
        Replace(DataQuality.Details,
        [
            string.Create(CultureInfo.InvariantCulture, $"An input counts as evaluated when valid samples cover ≥ {t.MinimumCoverageFraction:P0} of the {t.StandardWindowSeconds:0} s window and number at least {t.MinimumValidSamples}. Stale and failed samples never count."),
            "Collector availability counts raw counters that returned a value in the latest sample. Many available counters do not mean full diagnostic coverage: only the inputs listed here are used by the rules.",
            .. evaluable.Where(i => i.State != InputState.Evaluated).Select(i => $"{Labels.GetValueOrDefault(i.Name, i.Name)}: {SampleNote(i)}"),
            .. unverified.Select(i => $"{i.Name}: unit not documented by the vendor; shown raw in Advanced Metrics, never compared with a threshold."),
        ]);
    }

    private void ApplyHeadline(IReadOnlyList<DomainAssessment> assessments, IReadOnlyList<DiagnosticFinding> active, IReadOnlyList<AssessmentInput> inputs)
    {
        static string Name(DiagnosticDomain d) => d switch { DiagnosticDomain.Connection => "Connection", DiagnosticDomain.Graphics => "Graphics", _ => "VDI guest" };
        var assessed = assessments.Where(a => a.State != HealthState.InsufficientData).ToArray();
        var notAssessed = assessments.Where(a => a.State == HealthState.InsufficientData).Select(a => Name(a.Domain)).ToArray();
        var window = assessments.Select(a => a.WindowSeconds).FirstOrDefault(w => w > 0);
        HasActiveProblems = active.Count > 0;
        // What was evaluated, from the engine's inputs: the basis for any "no warnings" statement.
        EvaluatedSummary = string.Join(" · ", assessments.Select(a => $"{Name(a.Domain)}: " + (a.State == HealthState.InsufficientData ? "not assessed (insufficient data)"
            : string.Join(", ", inputs.Where(i => i.Domain == a.Domain && i.State == InputState.Evaluated).Select(i => Labels.GetValueOrDefault(i.Name, i.Name))))));
        if (active.Count > 0)
        {
            var worst = active.Max(f => f.Severity);
            Headline = $"{active.Count} active {(active.Count == 1 ? "problem" : "problems")} (warning or critical)";
            HeadlineState = worst == Severity.Critical ? "Critical" : "Warning";
            HeadlineDetail = string.Join(" · ", assessments.Where(a => a.State is HealthState.Warning or HealthState.Critical).Select(a => $"{Name(a.Domain)}: {a.StateLabel}")
                .Append($"Most severe: {active.OrderByDescending(f => f.Severity).First().Title}"));
        }
        else if (assessed.Length == 0)
        {
            Headline = "No conclusion yet: not enough valid measurements";
            HeadlineState = InsufficientData;
            HeadlineDetail = string.Create(CultureInfo.InvariantCulture,
                $"Each domain needs valid samples covering ≥ {thresholds.MinimumCoverageFraction:P0} of the {window:0} s window. Missing data is not treated as a problem.");
        }
        else
        {
            Headline = string.Create(CultureInfo.InvariantCulture, $"No active performance warnings in the last {window:0} s");
            HeadlineState = notAssessed.Length == 0 ? "Healthy" : InsufficientData;
            HeadlineDetail = notAssessed.Length == 0
                ? (assessments.All(a => a.Coverage == AssessmentCoverage.Full) ? "All three domains assessed with full coverage." : "All three domains assessed; some optional inputs are unavailable (see Evidence coverage).")
                : $"Not assessed (insufficient data): {string.Join(", ", notAssessed)}. This is not a statement that those areas are healthy.";
        }
    }

    // ---------- findings ----------

    private void Sort(FindingSortKey key)
    {
        if (key == sortKey) sortDescending = !sortDescending;
        else { sortKey = key; sortDescending = key is FindingSortKey.FirstDetected or FindingSortKey.Duration; }
        UpdateColumns();
        RefreshFindings();
    }

    /// <summary>Sorts by a column; <see cref="FindingSortKey.Severity"/> ascending is the default priority order.</summary>
    public void SortBy(FindingSortKey key, bool descending)
    {
        sortKey = key; sortDescending = descending;
        UpdateColumns();
        RefreshFindings();
    }

    private void UpdateColumns()
    {
        foreach (var c in Columns) c.Glyph = c.Key != sortKey ? "" : sortDescending ? "▼" : "▲";
        Raise(nameof(SortKey)); Raise(nameof(SortDescending));
    }

    private static int StateRank(FindingCard c) => c.State switch { "Active" => 0, "Recovered" => 1, _ => 2 };

    private IEnumerable<FindingCard> Ordered(IEnumerable<FindingCard> source)
    {
        // Ties fall back to the default priority order, then rule identifier, so the list never reshuffles between samples.
        IOrderedEnumerable<FindingCard> ordered = sortKey switch
        {
            FindingSortKey.Finding => By(source, c => c.Title, StringComparer.CurrentCultureIgnoreCase),
            FindingSortKey.Category => By(source, c => c.Domain, StringComparer.CurrentCultureIgnoreCase),
            FindingSortKey.FirstDetected => By(source, c => c.DetectedAt, Comparer<DateTimeOffset>.Default),
            FindingSortKey.Duration => By(source, c => c.Duration, Comparer<TimeSpan>.Default),
            FindingSortKey.State => By(source, StateRank, Comparer<int>.Default),
            _ => By(source, c => c.Rank, Comparer<int>.Default),
        };
        return ordered.ThenBy(c => c.Rank).ThenByDescending(c => c.RecoveredAt ?? DateTimeOffset.MaxValue).ThenByDescending(c => c.DetectedAt)
            .ThenBy(c => c.RuleId, StringComparer.Ordinal);
    }

    private IOrderedEnumerable<FindingCard> By<T>(IEnumerable<FindingCard> source, Func<FindingCard, T> key, IComparer<T> comparer) =>
        sortDescending ? source.OrderByDescending(key, comparer) : source.OrderBy(key, comparer);

    private void RefreshFindings()
    {
        foreach (var f in Filters) f.Count = cards.Count(f.Matches);
        var previous = selectedFinding;
        Sync(Ordered(cards.Where(selectedFilter.Matches)).ToArray());
        if (selectedFinding is not null && !Findings.Contains(selectedFinding)) { selectedFinding = null; userSelected = false; }
        // Until the user chooses, the panel follows the most important finding.
        if (!userSelected) selectedFinding = Findings.FirstOrDefault();
        if (!ReferenceEquals(previous, selectedFinding)) SelectionChanged();
        // Never "no warnings" without saying what was evaluated, and never when nothing could be evaluated.
        (NoProblemsTitle, NoProblemsDetail) = Domains.All(d => d.State == InsufficientData)
            ? ("No findings yet.", "Diagnostics need a full observation window of valid samples before any rule can conclude. Missing data is not treated as a problem.")
            : ("No active performance warnings detected in the selected observation window.", "Evaluated — " + EvaluatedSummary);
        (ListEmptyTitle, ListEmptyDetail) = selectedFilter.Kind switch
        {
            FindingFilterKind.Information => ("No informational observations.", ""),
            FindingFilterKind.Recovered => ("No recovered findings in this monitoring period.", "The 50 most recent recovered findings are kept until the mode or thresholds change."),
            FindingFilterKind.Critical when HasActiveProblems => ("No critical findings.", "Other active problems are listed under Active problems."),
            FindingFilterKind.Warning when HasActiveProblems => ("No warning findings.", "Other active problems are listed under Active problems."),
            _ => (NoProblemsTitle, NoProblemsDetail),
        };
        Raise(nameof(IsListEmpty)); Raise(nameof(HasFindings)); Raise(nameof(ShowNoProblemsBanner));
    }

    private void SelectionChanged()
    {
        Raise(nameof(SelectedFinding)); Raise(nameof(HasSelection)); Raise(nameof(HasNoSelection));
        CopyFindingCommand.Refresh();
        CopyStatus = "";
    }

    /// <summary>Updates items in place by key; only new or vanished findings change the collection.</summary>
    private void Sync(FindingCard[] items)
    {
        for (var i = 0; i < items.Length; i++)
        {
            var key = FindingItem.Make(items[i]);
            var existing = -1;
            for (var j = i; j < Findings.Count; j++) if (Findings[j].Key == key) { existing = j; break; }
            if (existing < 0) { Findings.Insert(i, new FindingItem(items[i])); continue; }
            if (existing != i) Findings.Move(existing, i);
            Findings[i].Card = items[i];
        }
        while (Findings.Count > items.Length) Findings.RemoveAt(Findings.Count - 1);
    }

    private void CopyFinding()
    {
        if (selectedFinding is null) return;
        CopyStatus = desktop.CopyText(selectedFinding.Card.ToClipboardText()) ? "Finding copied to the clipboard." : "Clipboard unavailable.";
    }

    public string SummaryText()
    {
        var b = new System.Text.StringBuilder();
        b.AppendLine($"EUC Lens diagnostics — {Observation}");
        b.AppendLine($"{Headline}. {HeadlineDetail}");
        b.AppendLine();
        foreach (var d in Domains)
        {
            b.AppendLine($"{d.Domain}: {d.State} (coverage {d.Coverage}) — {d.Summary}");
            foreach (var m in d.Measurements) b.AppendLine($"  {m.Label}: {m.Value}{(m.Threshold.Length > 0 ? $" (threshold {m.Threshold})" : "")}");
        }
        b.AppendLine($"Evidence coverage: {DataQuality.Level}. {DataQuality.Summary}");
        if (Hypotheses.Count > 0) { b.AppendLine().AppendLine("Assessment:"); foreach (var h in Hypotheses) b.AppendLine($"- {h.Statement} ({h.Confidence})"); }
        var list = cards.Where(c => c.IsActive).OrderBy(c => c.Rank).ToArray();
        if (list.Length > 0) { b.AppendLine().AppendLine("Active findings:"); foreach (var c in list) b.AppendLine($"- [{c.SeverityLabel}] {c.Title} ({c.Domain}, since {c.FirstDetected})"); }
        return b.ToString();
    }

    private void CopySummary() => CopyStatus = desktop.CopyText(SummaryText()) ? "Diagnostic summary copied to the clipboard." : "Clipboard unavailable.";

    // ---------- trends ----------

    private void AppendTrends(SessionSnapshot s)
    {
        var at = s.Timestamp.LocalDateTime;
        // A gap longer than the engine's maximum sample gap stays visible as a break in every line.
        var gap = lastPoint is { } previous && (at - previous).TotalSeconds > thresholds.MaximumSampleGapSeconds;
        foreach (var (chart, metric) in new[] { (Rtt, s.Connection.RoundTripTime), (Fps, s.Graphics.FramesPerSecond), (Cpu, s.System.CpuUtilization), (PacketLoss, s.Connection.PacketLoss) })
        {
            if (gap) chart.Add(lastPoint!.Value.AddSeconds(1), null);
            chart.Add(at, metric.Status == MetricStatus.Available ? metric.Value : null);
            chart.Trim(at.AddSeconds(-thresholds.LongWindowSeconds - 2));
            chart.Latest = metric.Status == MetricStatus.Available && metric.Value is { } v ? $"{v.ToString("0.#", CultureInfo.InvariantCulture)} {chart.Unit}" : "N/A";
        }
        lastPoint = at;
        UpdateTrendAxes();
    }

    /// <summary>All four charts share one time range so events line up vertically.</summary>
    private void UpdateTrendAxes()
    {
        if (lastPoint is not { } end) { foreach (var t in Trends) t.SetRange(null, null); return; }
        var start = end.AddSeconds(-selectedTrendWindow.Value);
        foreach (var t in Trends) t.SetRange(start, end);
    }

    // ---------- helpers ----------

    private static IReadOnlyList<DomainAssessment> LegacyAssessments(DiagnosticReport report)
    {
        static HealthState Map(HealthStatus h) => h switch
        {
            HealthStatus.Healthy => HealthState.Healthy, HealthStatus.Warning => HealthState.Warning, HealthStatus.Critical => HealthState.Critical, _ => HealthState.InsufficientData
        };
        return
        [
            new(DiagnosticDomain.Connection, Map(report.ConnectionHealth), AssessmentCoverage.Partial, "Legacy single-sample rules.", "", 0),
            new(DiagnosticDomain.Graphics, HealthState.InsufficientData, AssessmentCoverage.None, "Not evaluated by this engine.", "", 0),
            new(DiagnosticDomain.Guest, Map(report.HostHealth), AssessmentCoverage.Partial, "Legacy host rules.", "", 0),
        ];
    }

    private static IReadOnlyList<DiagnosticFinding> LegacyFindings(DiagnosticReport report) => report.Results.Select(r => new DiagnosticFinding(r.RuleId,
        r.Area == DiagnosticArea.Host ? DiagnosticDomain.Guest : DiagnosticDomain.Connection, r.Status == MetricStatus.Available ? r.Severity : Severity.Info,
        r.Title, r.Description, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, null, 0, 0, 0,
        r.SupportingMetrics.Select(p => new DiagnosticEvidence(p.Key, p.Value)).ToArray(), "Legacy rule.", [r.PossibleCause], [r.RecommendedAction], "")).ToArray();

    /// <summary>Value-equal items are left alone so WPF does not re-render unchanged rows.</summary>
    public static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> values, Func<T, T, bool>? equal = null)
    {
        var items = values as IList<T> ?? values.ToArray();
        for (var i = 0; i < items.Count; i++)
        {
            if (i >= target.Count) target.Add(items[i]);
            else if (!(equal?.Invoke(target[i], items[i]) ?? Equals(target[i], items[i]))) target[i] = items[i];
        }
        while (target.Count > items.Count) target.RemoveAt(target.Count - 1);
    }
}

/// <summary>One diagnostic trend: its data (rendered by the view's own chart objects), latest value and threshold labels.</summary>
public sealed class TrendChart : ObservableObject
{
    private const int Capacity = 720;
    private readonly double? maximum;
    private string latest = "N/A", reference = "";
    private bool hasData;

    public TrendChart(string title, string unit, string description, double? minimum = null, double? maximum = null)
    {
        Title = title; Unit = unit; Description = description; this.maximum = maximum;
        Data = new ChartData(title, unit, Capacity, yMin: minimum, yMax: maximum, yMinStep: unit == "%" && maximum is null ? 0.5 : 1, xStepSeconds: 15);
    }

    public string Title { get; }
    public string Unit { get; }
    public string Description { get; }
    public ChartData Data { get; }
    public IReadOnlyList<TimeValue> Points => Data.Points;
    public string AutomationName => $"{Title} trend, {Unit}. Latest {Latest}. {Reference}";
    public string Latest { get => latest; set { if (Set(ref latest, value)) Raise(nameof(AutomationName)); } }
    public string Reference { get => reference; private set => Set(ref reference, value); }
    public bool HasData { get => hasData; private set { if (Set(ref hasData, value)) Raise(nameof(ShowNoData)); } }
    public bool ShowNoData => !hasData;

    public void SetReferences(params ReferenceLine[] references)
    {
        Reference = string.Join(" · ", references.Select(r => r.Label));
        Data.SetReferences(references);
    }

    public void Add(DateTime at, double? value) => Data.Add(at, value);
    public void Trim(DateTime before) => Data.TrimBefore(before);

    public void Clear()
    {
        Data.Clear();
        Latest = "N/A";
        SetRange(null, null);
    }

    /// <summary>Shared time range; the value axis always includes the warning line so headroom stays visible.</summary>
    public void SetRange(DateTime? start, DateTime? end)
    {
        Data.SetXRange(start, end);
        var visible = Data.Points.Where(p => p.Value is not null && (start is null || p.At >= start)).Select(p => p.Value!.Value).ToArray();
        HasData = visible.Length > 0;
        if (maximum is not null) return;
        var lines = Data.References;
        var top = Math.Max(visible.Length == 0 ? 0 : visible.Max(), lines.Count == 0 ? 0 : lines[0].Value);
        var yMax = top <= 0 ? (double?)null : top * 1.15;
        Data.SetYMax(yMax);
        // Lines above the visible range are hidden rather than drawn outside the plot.
        Data.SetReferences(lines.Select(r => r with { Visible = r.Value <= (yMax ?? 0) }).ToArray());
    }
}
