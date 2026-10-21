using System.Collections.ObjectModel;
using System.Globalization;
using EucLens.Core;

namespace EucLens.App.ViewModels;

public enum FindingFilterKind { All, ActiveProblems, Critical, Warning, Information, Recovered }
public enum FindingSortKey { Severity, Finding, Category, FirstDetected, Duration, State }

/// <summary>One row of a finding's evidence table. <see cref="Threshold"/> is empty when the engine defines none.</summary>
public sealed record EvidenceRow(string Measurement, string Observed, string Threshold, string Note)
{
    public bool HasNote => Note.Length > 0;
    /// <summary>Spells out which statistic a generic engine label refers to.</summary>
    public static EvidenceRow From(DiagnosticEvidence e) => new(e.Metric switch
    {
        "Window mean" => "Mean over window",
        "Window P95" => "95th percentile (P95)",
        "Window min / max" => "Min / max over window",
        "Latest" => "Latest sample",
        _ => e.Metric,
    }, e.Value, e.Threshold ?? "", e.Detail ?? "");
}

/// <summary>Label, value and configured threshold of one supporting measurement; <see cref="Note"/> carries sample sufficiency.</summary>
public sealed record MeasurementRow(string Label, string Value, string Threshold, string Note)
{
    public bool HasThreshold => Threshold.Length > 0;
}

/// <summary>Display form of a finding. Everything shown comes from the <see cref="DiagnosticFinding"/>; nothing is inferred here.</summary>
public sealed record FindingCard(string RuleId, string DetectedKey, string Severity, Severity Level, string SeverityLabel, string State, bool IsActive,
    bool IsProblem, string Title, string Domain, string Description, DateTimeOffset DetectedAt, DateTimeOffset? RecoveredAt, string FirstDetected,
    TimeSpan Duration, string DurationLabel, string Observation, string Timeline, string Source, IReadOnlyList<EvidenceRow> Evidence,
    string DecisionRule, IReadOnlyList<string> Causes, IReadOnlyList<string> Actions, string Limitations)
{
    /// <summary>Default order: active critical, active warning, recovered, information.</summary>
    public int Rank => !IsActive ? 2 : Level switch { Core.Severity.Critical => 0, Core.Severity.Warning => 1, _ => 3 };

    public static string Time(DateTimeOffset t) => t.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);
    public static string FormatDuration(TimeSpan d) => d.TotalSeconds < 60 ? $"{Math.Max(0, d.TotalSeconds):0} s"
        : d.TotalHours < 1 ? $"{(int)d.TotalMinutes} min {d.Seconds:00} s" : $"{(int)d.TotalHours} h {d.Minutes:00} min";

    public static string DomainLabel(DiagnosticDomain d) => d switch
    {
        DiagnosticDomain.Guest => "VDI guest", DiagnosticDomain.Gpu => "GPU", DiagnosticDomain.DataQuality => "Data quality", _ => d.ToString()
    };

    public static FindingCard From(DiagnosticFinding f)
    {
        var badge = f.IsActive ? f.Severity switch { Core.Severity.Critical => "CRITICAL", Core.Severity.Warning => "WARNING", _ => "INFO" } : "RECOVERED";
        var level = f.Severity switch { Core.Severity.Critical => "Critical", Core.Severity.Warning => "Warning", _ => "Info" };
        var state = !f.IsActive ? "Recovered" : f.Severity >= Core.Severity.Warning ? "Active" : "Informational";
        var end = f.RecoveredAt ?? f.LastConfirmedAt;
        var duration = end - f.DetectedAt;
        var observationRow = f.Evidence.FirstOrDefault(e => e.Metric == "Observation");
        var observation = observationRow is not null ? observationRow.Value + (observationRow.Detail is null ? "" : $" · {observationRow.Detail}")
            : f.ObservationSeconds > 0 ? $"{f.ObservationSeconds:0} s window · {f.ValidSamples} valid of {f.TotalSamples} samples" : "Inventory-based (no time window)";
        var timeline = f.IsActive ? $"Detected {Time(f.DetectedAt)} · last confirmed {Time(f.LastConfirmedAt)}"
            : $"Detected {Time(f.DetectedAt)} · ended {Time(f.RecoveredAt!.Value)} — {f.RecoveryReason}";
        // Observation and source are shown in the panel header rather than as table rows.
        var rows = f.Evidence.Where(e => e.Metric is not ("Observation" or "Source")).Select(EvidenceRow.From)
            .Select(r => r.Measurement == "Adapter" ? r with { Observed = GpuText.Adapters(r.Observed) } : r).ToArray();
        return new(f.RuleId, f.DetectedAt.UtcTicks.ToString(CultureInfo.InvariantCulture), badge, f.Severity, level, state, f.IsActive,
            f.IsActive && f.Severity >= Core.Severity.Warning, f.Title, DomainLabel(f.Domain), f.Description, f.DetectedAt, f.RecoveredAt,
            Time(f.DetectedAt), duration, FormatDuration(duration), observation, timeline, f.Evidence.FirstOrDefault(e => e.Metric == "Source")?.Value ?? "",
            rows, f.DecisionRule, f.PossibleCauses, f.RecommendedActions, f.Limitations);
    }

    /// <summary>Plain-text form for the clipboard: evidence, thresholds and recommendations exactly as the engine stated them.</summary>
    public string ToClipboardText()
    {
        var b = new System.Text.StringBuilder();
        b.AppendLine($"EUC Lens finding: {Title}");
        b.AppendLine($"Severity: {SeverityLabel} · Category: {Domain} · State: {State} · Rule: {RuleId}");
        b.AppendLine(Timeline);
        b.AppendLine($"Observation: {Observation}");
        if (Source.Length > 0) b.AppendLine($"Source: {Source}");
        b.AppendLine().AppendLine(Description).AppendLine().AppendLine("Supporting measurements:");
        foreach (var e in Evidence)
            b.AppendLine($"- {e.Measurement}: {e.Observed}{(e.Threshold.Length > 0 ? $" (threshold {e.Threshold})" : "")}{(e.Note.Length > 0 ? $" [{e.Note}]" : "")}");
        b.AppendLine().AppendLine($"Decision rule: {DecisionRule}");
        if (Causes.Count > 0) { b.AppendLine().AppendLine("Possible contributing factors:"); foreach (var c in Causes) b.AppendLine($"- {c}"); }
        if (Actions.Count > 0) { b.AppendLine().AppendLine("Recommended investigation:"); for (var i = 0; i < Actions.Count; i++) b.AppendLine($"{i + 1}. {Actions[i]}"); }
        if (Limitations.Length > 0) b.AppendLine().AppendLine($"Limitations: {Limitations}");
        return b.ToString();
    }
}

/// <summary>Stable list item for a finding: per-second updates replace values in place, so selection and expansion survive.</summary>
public sealed class FindingItem : ObservableObject
{
    private FindingCard card;
    private bool expanded;
    public FindingItem(FindingCard card) { this.card = card; Key = Make(card); Sync(); }
    public static string Make(FindingCard c) => c.RuleId + "|" + c.DetectedKey;
    public string Key { get; }
    public FindingCard Card { get => card; set { if (!Set(ref card, value)) return; Sync(); Raise(string.Empty); } }
    public bool IsExpanded { get => expanded; set => Set(ref expanded, value); }
    public ObservableCollection<EvidenceRow> Evidence { get; } = [];
    public ObservableCollection<string> Causes { get; } = [];
    /// <summary>Numbered as stated by the engine; the order is the engine's.</summary>
    public ObservableCollection<string> Actions { get; } = [];
    public string Severity => card.Severity;
    public string SeverityLabel => card.SeverityLabel;
    /// <summary>Colour key: only active warnings and critical findings are coloured; information and recovered stay neutral.</summary>
    public string Marker => card.IsProblem ? card.SeverityLabel : "Neutral";
    public string State => card.State;
    public bool IsProblem => card.IsProblem;
    public bool IsActive => card.IsActive;
    public string Title => card.Title;
    public string Domain => card.Domain;
    public string Description => card.Description;
    public string FirstDetected => card.FirstDetected;
    public string DurationLabel => card.DurationLabel;
    public string Observation => card.Observation;
    public string Timeline => card.Timeline;
    public string Source => card.Source;
    public bool HasSource => card.Source.Length > 0;
    public string DecisionRule => card.DecisionRule;
    public string Limitations => card.Limitations;
    public bool HasCauses => card.Causes.Count > 0;
    public bool HasActions => card.Actions.Count > 0;
    public string RuleId => card.RuleId;
    public string AutomationName => $"{card.SeverityLabel}, {card.State}: {card.Title}. {card.Domain}. Detected {card.FirstDetected}, duration {card.DurationLabel}.";
    private void Sync()
    {
        DiagnosticsViewModel.Replace(Evidence, card.Evidence);
        DiagnosticsViewModel.Replace(Causes, card.Causes);
        DiagnosticsViewModel.Replace(Actions, card.Actions.Select((a, i) => $"{i + 1}.  {a}"));
    }
}

public sealed class FindingFilterItem(FindingFilterKind kind, string label) : ObservableObject
{
    private int count;
    public FindingFilterKind Kind { get; } = kind;
    public string Label { get; } = label;
    public int Count { get => count; set { if (Set(ref count, value)) Raise(nameof(Display)); } }
    public string Display => string.Create(CultureInfo.InvariantCulture, $"{Label}  {Count}");
    public bool Matches(FindingCard c) => Kind switch
    {
        FindingFilterKind.ActiveProblems => c.IsProblem,
        FindingFilterKind.Critical => c.IsActive && c.Level == Severity.Critical,
        FindingFilterKind.Warning => c.IsActive && c.Level == Severity.Warning,
        FindingFilterKind.Information => c.IsActive && c.Level == Severity.Info,
        FindingFilterKind.Recovered => !c.IsActive,
        _ => true,
    };
}

/// <summary>Sortable column header: label plus the current direction glyph.</summary>
public sealed class FindingColumn : ObservableObject
{
    private string glyph = "";
    public FindingColumn(FindingSortKey key, string label, Action<FindingSortKey> sort) { Key = key; Label = label; SortCommand = new(() => sort(key)); }
    public FindingSortKey Key { get; }
    public string Label { get; }
    public RelayCommand SortCommand { get; }
    public string Glyph { get => glyph; set { if (Set(ref glyph, value)) Raise(nameof(AutomationName)); } }
    public string AutomationName => $"Sort findings by {Label}" + (glyph.Length == 0 ? "" : glyph == "▲" ? ", currently ascending" : ", currently descending");
}

/// <summary>One of the three independent health domains. Values update in place every sample.</summary>
public sealed class DomainHealthItem(DiagnosticDomain domain, string title) : ObservableObject
{
    private string state = "Insufficient Data", coverage = "None", observation = "Waiting for samples", problems = "No active warnings", summary = "Waiting for samples.",
        evidence = "", coverageNote = "";
    private bool expanded;
    public DiagnosticDomain DomainKey { get; } = domain;
    /// <summary>Display name, e.g. "Connection Health".</summary>
    public string Domain { get; } = title;
    public string State { get => state; set => Set(ref state, value); }
    public string Coverage { get => coverage; set => Set(ref coverage, value); }
    public string Observation { get => observation; set => Set(ref observation, value); }
    public string ActiveProblems { get => problems; set => Set(ref problems, value); }
    /// <summary>One-line technical explanation of the state.</summary>
    public string Summary { get => summary; set => Set(ref summary, value); }
    /// <summary>The engine's own evidence and coverage text, kept for tooltips and the details section.</summary>
    public string Evidence { get => evidence; set => Set(ref evidence, value); }
    public string CoverageNote { get => coverageNote; set => Set(ref coverageNote, value); }
    public bool IsExpanded { get => expanded; set => Set(ref expanded, value); }
    public ObservableCollection<MeasurementRow> Measurements { get; } = [];
    public ObservableCollection<string> Details { get; } = [];
    public string AutomationName => $"{Domain}: {State}. Coverage {Coverage}. {ActiveProblems}. {Summary}";
    public void Refresh() => Raise(nameof(AutomationName));
}

/// <summary>Difference between what the collectors returned and what the rules could actually use.</summary>
public sealed class DataQualityItem : ObservableObject
{
    private string level = "Insufficient evidence", summary = "Waiting for samples.";
    private bool expanded;
    public string Level { get => level; set => Set(ref level, value); }
    public string Summary { get => summary; set => Set(ref summary, value); }
    public bool IsExpanded { get => expanded; set => Set(ref expanded, value); }
    public ObservableCollection<MeasurementRow> Rows { get; } = [];
    public ObservableCollection<string> Details { get; } = [];
}

/// <summary>An engine hypothesis with its verification steps, numbered in the engine's order.</summary>
public sealed record HypothesisCard(string Statement, string Confidence, IReadOnlyList<string> Steps)
{
    public bool HasSteps => Steps.Count > 0;
    public string Verification => Steps.Count == 0 ? "" : "Verify: " + string.Join(" ", Steps);
}

/// <summary>Presentation-only clean-up of adapter text; GPU collection is unchanged.</summary>
public static class GpuText
{
    /// <summary>"No hardware GPU: Microsoft Basic Render Driver ×2 (DXGI software adapter (Microsoft Basic Render Driver); CPU rendering.)"
    /// becomes "Microsoft Basic Render Driver ×2 — CPU rendering".</summary>
    public static string Adapters(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "N/A";
        var body = text.StartsWith("No hardware GPU: ", StringComparison.Ordinal) ? text["No hardware GPU: ".Length..] : text;
        return string.Join("; ", Split(body).Select(Entry));
        // Adapters are separated by "; " outside parentheses; reasons may contain "; " themselves.
        static IEnumerable<string> Split(string value)
        {
            int depth = 0, start = 0;
            for (var i = 0; i < value.Length; i++)
            {
                if (value[i] == '(') depth++;
                else if (value[i] == ')') depth--;
                else if (depth == 0 && value[i] == ';' && i + 1 < value.Length && value[i + 1] == ' ') { yield return value[start..i]; start = i + 2; }
            }
            if (start < value.Length) yield return value[start..];
        }
        static string Entry(string entry)
        {
            var open = entry.IndexOf(" (", StringComparison.Ordinal);
            if (open < 0 || !entry.EndsWith(')')) return entry;
            var name = entry[..open];
            var reason = entry[(open + 2)..^1];
            var core = name.Split(" ×")[0];
            // Drop the parenthetical when it only repeats the adapter name; keep any remaining explanation.
            if (!reason.Contains(core, StringComparison.Ordinal)) return entry;
            var tail = reason.Split(';', 2) is [_, var rest] ? rest.Trim().TrimEnd('.') : "";
            return tail.Length == 0 ? name : $"{name} — {tail}";
        }
    }
}
