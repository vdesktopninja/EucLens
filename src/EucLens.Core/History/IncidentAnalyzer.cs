using System.Globalization;

namespace EucLens.Core;

/// <summary>
/// Describes the recording around one instant without interpreting it. Every sentence is built from recorded values or recorded findings: a metric is
/// reported only if, inside the examined interval, it goes clearly beyond the same session's own normal range (above its 95th percentile by at least twice
/// the distance from the median, or below the 5th percentile by the same rule for frame rate). Nothing is compared with thresholds that did not exist when
/// the recording was made, no cause is named, and "nothing stood out" is a legitimate result.
/// </summary>
public static class IncidentAnalyzer
{
    public const int MinimumSessionSamples = 30;
    public const int MinimumWindowSamples = 3;
    public static readonly TimeSpan DefaultContext = TimeSpan.FromMinutes(2);

    private static string Number(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    public static IncidentAnalysis Analyze(LoadedHistory history, HistoryIncident incident, TimeSpan context, TimeZoneInfo? zone = null) =>
        Analyze(history, incident.At, context, zone, "User reported a performance issue");

    public static IncidentAnalysis Analyze(LoadedHistory history, DateTimeOffset at, TimeSpan context, TimeZoneInfo? zone = null, string subject = "Selected time")
    {
        if (context <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(context));
        var from = at - context; var to = at + context;
        var changes = new List<ObservedChange>(); var stable = new List<string>(); var missing = new List<string>();
        var notCompared = new List<string>();

        foreach (var channel in history.Channels)
        {
            var id = channel.Channel.Id;
            var baseline = history.Baseline(id);
            var stats = history.Statistics(id, from, to);
            if (stats.Missing > 0 && stats.Samples > 0) missing.Add($"{channel.Channel.Title}: {stats.Missing} of {stats.Samples} samples had no valid value");
            if (baseline is null || baseline.Valid < MinimumSessionSamples || stats.Valid < MinimumWindowSamples) { notCompared.Add(channel.Channel.Title); continue; }

            bool abnormal; (double Value, DateTimeOffset At)? extreme; double reference; string referenceName;
            if (channel.Channel.HigherIsWorse)
            {
                extreme = history.Extreme(id, from, to, highest: true);
                var limit = baseline.P95 > baseline.P50 ? baseline.P50 + 2 * (baseline.P95 - baseline.P50) : baseline.P95;
                abnormal = extreme is { } e && e.Value > baseline.P95 && e.Value >= limit;
                reference = baseline.P95; referenceName = "95th percentile";
            }
            else
            {
                extreme = history.Extreme(id, from, to, highest: false);
                var limit = baseline.P50 > baseline.P05 ? baseline.P50 - 2 * (baseline.P50 - baseline.P05) : baseline.P05;
                abnormal = extreme is { } e && e.Value < baseline.P05 && e.Value <= limit;
                reference = baseline.P05; referenceName = "5th percentile";
            }
            var unit = channel.Unit.Length == 0 ? "" : " " + channel.Unit;
            if (abnormal && extreme is { } x)
            {
                var verb = channel.Channel.HigherIsWorse ? "reached" : "fell to";
                changes.Add(new(id, channel.Channel.Title, channel.Unit, channel.UnitVerified, x.Value, x.At, baseline.P50, reference, referenceName,
                    $"{channel.Channel.Title} {verb} {Number(x.Value)}{unit} at {HistoryTime.Format(x.At, zone)} (this session: median {Number(baseline.P50)}{unit}, {referenceName} {Number(reference)}{unit})."));
            }
            else if (extreme is { } y)
                stable.Add($"{channel.Channel.Title} stayed within this session's normal range ({(channel.Channel.HigherIsWorse ? "maximum" : "minimum")} {Number(y.Value)}{unit}, {referenceName} {Number(reference)}{unit})");
        }

        var findings = history.Events.Where(e => e.IsFinding && e.At >= from && e.At <= to).ToArray();
        var gaps = history.Gaps.Where(g => g.End >= from && g.Start <= to).ToArray();

        var lines = new List<string>
        {
            $"{subject} at {HistoryTime.Format(at, zone)}.",
            $"Interval examined: {HistoryTime.Format(from, zone)} to {HistoryTime.Format(to, zone)} ({HistoryTime.FormatSpan(context * 2)})."
        };
        if (changes.Count > 0) lines.Add("Observed in the recording: " + string.Join(" ", changes.Select(c => c.Statement)));
        else if (stable.Count > 0) lines.Add("No measurement stood out in this interval. " + string.Join("; ", stable) + ".");
        else lines.Add("There was not enough valid data in this interval, or in this session, to compare measurements.");
        if (changes.Count > 0 && stable.Count > 0) lines.Add("Without a clear change: " + string.Join("; ", stable.Select(s => s[..s.IndexOf(" stayed", StringComparison.Ordinal)])) + ".");
        lines.Add(findings.Length == 0
            ? "No diagnostic findings were recorded in this interval."
            : $"{findings.Length} diagnostic finding event(s) were recorded in this interval: {string.Join("; ", findings.Take(5).Select(f => $"{f.Title} at {HistoryTime.Format(f.At, zone)}"))}{(findings.Length > 5 ? "; …" : "")}.");
        if (gaps.Length > 0) lines.Add($"Measurements are missing for {string.Join(", ", gaps.Select(g => $"{HistoryTime.FormatSpan(g.Length)} ({g.Reason.ToLowerInvariant()})"))} in this interval.");
        if (notCompared.Count > 0 && changes.Count == 0 && stable.Count > 0) lines.Add($"Not compared (too few valid samples): {string.Join(", ", notCompared)}.");
        lines.Add("This describes recorded values only. A change that coincides with a report is an observation, not proof of its cause.");
        return new(at, from, to, changes, findings, missing, stable, gaps, string.Join(" ", lines));
    }
}
