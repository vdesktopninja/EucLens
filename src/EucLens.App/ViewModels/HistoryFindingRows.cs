using System.Globalization;
using EucLens.Core;

namespace EucLens.App.ViewModels;

public enum FindingRowKind
{
    /// <summary>A rule that fired once: one row, nothing to expand.</summary>
    Single,
    /// <summary>The header of a rule that fired more than once; its occurrences follow while it is expanded.</summary>
    Group,
    /// <summary>One occurrence of a rule that fired more than once, shown under its group header.</summary>
    Occurrence
}

/// <summary>
/// One line of the findings list. A line is either one observation of the analysis or the header of several observations of the same rule. The header only describes its
/// occurrences: every occurrence keeps its own observation, identifier, interval, evidence and export row, and is selected, shown on the charts and exported on its own.
/// </summary>
public sealed class HistoryFindingRow : ObservableObject
{
    private readonly Action<string, bool>? setExpanded;
    private bool expanded, previewed;

    internal HistoryFindingRow(FindingRowKind kind, string ruleId, string title, string severityTag, string domain, string key, string keyLabel, string keyMetric, string time, string duration,
        string count, string total, string badge, HistoryObservationItem? item, IReadOnlyList<HistoryObservationItem> occurrences, bool isExpanded, Action<string, bool>? setExpanded, string? groupSeverityTag = null)
    {
        GroupSeverityTag = groupSeverityTag ?? severityTag;
        Kind = kind; RuleId = ruleId; Title = title; SeverityTag = severityTag; Domain = domain; KeyValue = key; KeyLabel = keyLabel; KeyMetric = keyMetric; Time = time; Duration = duration;
        CountText = count; TotalText = total; StatusBadge = badge; Item = item; Occurrences = occurrences; expanded = isExpanded; this.setExpanded = setExpanded;
        SeverityLabel = severityTag;
        ToggleCommand = new(() => IsExpanded = !IsExpanded, () => IsGroup);
    }

    /// <summary>Opens or closes the occurrences of a group header: what the chevron button of the line invokes, for the mouse and for assistive technology.</summary>
    public RelayCommand ToggleCommand { get; }
    public string ToggleName => $"{(expanded ? "Close" : "Open")} the {CountText} of {Title}";

    public FindingRowKind Kind { get; }
    public string RuleId { get; }
    public string Title { get; }
    public string SeverityTag { get; }
    public string SeverityLabel { get; }
    public string Domain { get; }
    /// <summary>The measured value that matters, in its recorded unit ("99%", "420 ms"); for a group the most extreme value of any occurrence.</summary>
    public string KeyValue { get; }
    /// <summary>"Highest" or "Lowest": which end of the metric is the bad one.</summary>
    public string KeyLabel { get; }
    public string KeyMetric { get; }
    public string Time { get; }
    public string Duration { get; }
    /// <summary>"3 occurrences" on a group header; empty elsewhere.</summary>
    public string CountText { get; }
    /// <summary>"Total affected 4 min 10 s" on a group header: the length of the union of its intervals, so overlapping occurrences are never counted twice.</summary>
    public string TotalText { get; }
    public string StatusBadge { get; }
    public bool HasStatusBadge => StatusBadge.Length > 0;
    /// <summary>What a screen reader says for the line; a group says whether it is open and how to change that.</summary>
    public string AutomationName => Kind == FindingRowKind.Group
        ? $"{SeverityTag}: {Title}, {CountText}. {KeyLabel} {KeyValue}. {Time}. {TotalText}. {(expanded ? "Expanded" : "Collapsed")}: press Enter to {(expanded ? "collapse" : "expand")} the occurrences."
        : $"{(Kind == FindingRowKind.Occurrence ? "Occurrence: " : "")}{SeverityTag}: {Title}. {KeyLabel} {KeyValue}. {Time}, {Duration}." + (StatusBadge.Length > 0 ? " " + StatusBadge + "." : "");
    /// <summary>The observation behind a single row or an occurrence; null on a group header.</summary>
    public HistoryObservationItem? Item { get; }
    /// <summary>Every observation of the group (a header), the one observation of a single row, or the group's siblings are not listed here.</summary>
    public IReadOnlyList<HistoryObservationItem> Occurrences { get; }
    public bool IsGroup => Kind == FindingRowKind.Group;
    public bool IsOccurrence => Kind == FindingRowKind.Occurrence;
    public bool IsSingle => Kind == FindingRowKind.Single;
    public bool IsSelectable => Kind != FindingRowKind.Group;
    /// <summary>For an occurrence: the worst severity of its group, so the line names its own severity only when it differs from the header's.</summary>
    public string GroupSeverityTag { get; }
    public bool ShowSeverityWord => !IsOccurrence || SeverityTag != GroupSeverityTag;
    /// <summary>"Duration" for one finding, "Total affected" for a group (the union of its intervals).</summary>
    public string AffectedLabel => IsGroup ? "Total affected" : "Duration";
    public string AffectedValue => IsGroup ? TotalText["Total affected ".Length..] : Duration;
    /// <summary>Names of the key figures for assistive technology: label and value together.</summary>
    public string KeyAutomation => $"{KeyLabel} {KeyMetric}: {KeyValue}";
    public string AffectedAutomation => $"{AffectedLabel}: {AffectedValue}";
    public string WhenAutomation => IsGroup ? $"First to last: {Time}" : $"When: {Time}";
    /// <summary>True for the line whose finding the details pane shows as a preview while nothing is selected: drawn with a quiet marker, never as a selection.</summary>
    public bool IsPreviewed { get => previewed; internal set => Set(ref previewed, value); }

    public bool IsExpanded
    {
        get => expanded;
        set { if (IsGroup && expanded != value) { expanded = value; Raise(nameof(IsExpanded)); Raise(nameof(AutomationName)); Raise(nameof(ToggleName)); setExpanded?.Invoke(RuleId, value); } }
    }
}

/// <summary>Builds the lines of the findings list from the observations the filters let through. Pure: the same observations always give the same lines in the same order.</summary>
internal static class FindingGrouping
{
    private static int Rank(Severity s) => s switch { Severity.Critical => 0, Severity.Warning => 1, _ => 2 };

    /// <summary>
    /// A group exists for a rule with two or more observations. Groups and single observations are ordered like the observations themselves: worst severity first, then by time.
    /// The occurrences of a group are in time order, each with its own severity.
    /// </summary>
    public static IReadOnlyList<HistoryFindingRow> Build(IReadOnlyList<HistoryObservationItem> items, Func<string, bool> isExpanded, Action<string, bool>? setExpanded)
    {
        var rows = new List<(int Rank, DateTimeOffset Start, string Key, IReadOnlyList<HistoryFindingRow> Lines)>();
        foreach (var group in items.GroupBy(i => i.Observation.RuleId, StringComparer.Ordinal))
        {
            var ordered = group.OrderBy(i => i.Observation.Start).ThenBy(i => i.Id, StringComparer.Ordinal).ToArray();
            var worst = ordered.MinBy(i => Rank(i.Observation.Severity))!;
            var first = ordered[0];
            if (ordered.Length == 1)
            {
                rows.Add((Rank(first.Observation.Severity), first.Observation.Start, first.Id, [Line(FindingRowKind.Single, first)]));
                continue;
            }
            var open = isExpanded(group.Key);
            var lines = new List<HistoryFindingRow> { Header(ordered, worst, open, setExpanded) };
            if (open) lines.AddRange(ordered.Select(o => Line(FindingRowKind.Occurrence, o, worst.SeverityTag)));
            rows.Add((Rank(worst.Observation.Severity), first.Observation.Start, group.Key, lines));
        }
        return rows.OrderBy(r => r.Rank).ThenBy(r => r.Start).ThenBy(r => r.Key, StringComparer.Ordinal).SelectMany(r => r.Lines).ToArray();
    }

    /// <summary>The occurrence lines of a group, in time order.</summary>
    public static IReadOnlyList<HistoryFindingRow> OccurrenceRows(IEnumerable<HistoryObservationItem> items)
    {
        var ordered = items.OrderBy(i => i.Observation.Start).ThenBy(i => i.Id, StringComparer.Ordinal).ToArray();
        var worst = ordered.Length == 0 ? null : ordered.MinBy(i => Rank(i.Observation.Severity))!.SeverityTag;
        return ordered.Select(i => Line(FindingRowKind.Occurrence, i, worst)).ToArray();
    }

    private static HistoryFindingRow Line(FindingRowKind kind, HistoryObservationItem i, string? groupSeverity = null) =>
        new(kind, i.Observation.RuleId, i.Title, i.SeverityTag, i.Domain, i.KeyValue, i.KeyLabel, i.KeyMetric, i.Time, i.Duration, "", "", i.StatusBadge, i, [i], false, null, groupSeverity);

    private static HistoryFindingRow Header(IReadOnlyList<HistoryObservationItem> ordered, HistoryObservationItem worst, bool open, Action<string, bool>? setExpanded)
    {
        var first = ordered[0];
        var lowerIsWorse = first.LowerIsWorse;
        // The most extreme measured value of any occurrence, from occurrences that have a valid measurement; none when no occurrence has one.
        var measured = ordered.Where(o => o.Observation.Primary.ValidSamples > 0 && double.IsFinite(lowerIsWorse ? o.Observation.Primary.Minimum : o.Observation.Primary.Maximum)).ToArray();
        var extreme = measured.Length == 0 ? null : lowerIsWorse ? measured.MinBy(o => o.Observation.Primary.Minimum) : measured.MaxBy(o => o.Observation.Primary.Maximum);
        var key = extreme?.KeyValue ?? "no valid value";
        var last = ordered.MaxBy(o => o.Observation.End)!;
        var affected = AffectedTime(ordered.Select(o => (o.Observation.Start, o.Observation.End)));
        var qualified = ordered.Count(o => o.Observation.Status == AnalysisStatus.QualifiedObservation);
        return new(FindingRowKind.Group, first.Observation.RuleId, first.Title, worst.SeverityTag, first.Domain, key, first.KeyLabel, first.KeyMetric,
            first.StartLabel + "–" + last.EndLabel, "", FindingText.Plural(ordered.Count, "occurrence", "occurrences"),
            "Total affected " + FindingText.Span(affected), qualified > 0 ? $"{FindingText.Count(qualified)} of {FindingText.Count(ordered.Count)} qualified" : "", null, ordered, open, setExpanded);
    }

    /// <summary>The length of the union of the intervals: time covered by two overlapping occurrences counts once.</summary>
    public static TimeSpan AffectedTime(IEnumerable<(DateTimeOffset Start, DateTimeOffset End)> intervals)
    {
        var total = TimeSpan.Zero; DateTimeOffset? from = null, to = null;
        foreach (var (start, end) in intervals.OrderBy(i => i.Start).ThenBy(i => i.End))
        {
            if (from is null) { from = start; to = end; continue; }
            if (start <= to) { if (end > to) to = end; continue; }
            total += to!.Value - from.Value; from = start; to = end;
        }
        if (from is not null) total += to!.Value - from.Value;
        return total;
    }
}

/// <summary>One finding on the performance timeline: exactly the observation's recorded interval, drawn on the track of its area.</summary>
public sealed record FindingBar(string Id, string RuleId, string Title, HistoryGroup Domain, Severity Severity, DateTimeOffset Start, DateTimeOffset End, string Summary);

/// <summary>A bar with its place on the track: pixel extent and the lane (row) it was given inside the track so that overlapping findings stay separate.</summary>
public sealed record PlacedBar(FindingBar Bar, double Left, double Right, int Lane);

/// <summary>Deterministic lane assignment for a track: bars are placed in start order, each in the first lane where it does not touch the previous bar.</summary>
public static class FindingsTimelineLayout
{
    /// <summary>
    /// <paramref name="x"/> maps a time to a pixel. A bar is never narrower than <paramref name="minimumWidth"/> (a 3-second finding in an 8-hour recording must still be visible) and a lane keeps
    /// <paramref name="spacing"/> pixels between two bars, so findings that would overlap on screen, even only because of that minimum width, go to different lanes. The extent of a bar is its true
    /// interval; only the pixels that make it visible are added.
    /// </summary>
    public static IReadOnlyList<PlacedBar> Pack(IEnumerable<FindingBar> bars, Func<DateTimeOffset, double> x, double minimumWidth, double spacing, out int lanes)
    {
        var placed = new List<PlacedBar>(); var laneRight = new List<double>();
        foreach (var bar in bars.OrderBy(b => b.Start).ThenByDescending(b => b.End).ThenBy(b => b.Id, StringComparer.Ordinal))
        {
            double left = x(bar.Start), right = Math.Max(x(bar.End), left + minimumWidth);
            var lane = laneRight.FindIndex(r => r + spacing <= left);
            if (lane < 0) { lane = laneRight.Count; laneRight.Add(right); } else laneRight[lane] = right;
            placed.Add(new(bar, left, right, lane));
        }
        lanes = laneRight.Count;
        return placed;
    }

    /// <summary>The tracks of the timeline, in the order of the history charts.</summary>
    public static readonly IReadOnlyList<HistoryGroup> Tracks = [HistoryGroup.Connection, HistoryGroup.Graphics, HistoryGroup.Guest];

    /// <summary>Text of a bar for the tooltip and the screen reader.</summary>
    public static string Describe(PlacedBar placed) => $"{placed.Bar.Severity}: {placed.Bar.Title}. {placed.Bar.Summary}";

    internal static string Culture(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);
}
