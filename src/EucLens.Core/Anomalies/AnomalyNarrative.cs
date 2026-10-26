using System.Globalization;

namespace EucLens.Core;

/// <summary>
/// The wording of observations and correlations, kept apart from the statistics: the engine decides from numbers, this class only describes what it decided. Every sentence
/// is built from recorded values and times (UTC); none names a cause. Rule-specific cautions are listed once here so they can be reviewed in one place.
/// </summary>
internal static class AnomalyNarrative
{
    public const string NoCause = "The recording does not establish the underlying cause.";

    private static readonly HashSet<string> BlastChannels = ["rtt", "packetLoss", "fps", "dirtyFps", "droppedFrames", "outQueueing", "bandwidthConsumption"];

    public static string Number(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    public static string Label(string channelId, string title, bool blast)
    {
        if (!blast || !BlastChannels.Contains(channelId) || title.Length == 0) return title;
        return channelId == "packetLoss" ? "Blast packet loss (uplink counter)" : "Blast " + char.ToLowerInvariant(title[0]) + title[1..];
    }

    private static string Cap(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    public static string Time(DateTimeOffset utc) => utc.UtcDateTime.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
    private static string DateTimeText(DateTimeOffset utc) => utc.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    /// <summary>"between 14:32:10 and 14:32:45 UTC on 2026-03-04" (the date is part of both ends when they differ).</summary>
    public static string Interval(DateTimeOffset start, DateTimeOffset end) =>
        start.UtcDateTime.Date == end.UtcDateTime.Date
            ? $"between {Time(start)} and {Time(end)} UTC on {start.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}"
            : $"between {DateTimeText(start)} and {DateTimeText(end)} UTC";

    /// <summary>Culture-independent text for a span ("59.9 s", "2 min 05 s", "1 h 30 min"): a result must read the same on every machine.</summary>
    public static string Duration(TimeSpan span)
    {
        span = span.Duration();
        if (span.TotalHours >= 1) return $"{(int)span.TotalHours} h {span.Minutes:00} min";
        if (span.TotalMinutes >= 1) return $"{(int)span.TotalMinutes} min {span.Seconds:00} s";
        return span.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture) + " s";
    }

    public static string Count(long value) => value.ToString("N0", CultureInfo.InvariantCulture);
    public static string Percent(double fraction) => (fraction * 100).ToString("0", CultureInfo.InvariantCulture) + "%";

    public static string Comparison(AnomalyRule rule, ThresholdBasis basis, double? absolute, double? baselineLimit, MetricBaseline? baseline, string unit)
    {
        var u = unit.Length == 0 ? "" : " " + unit;
        string Abs()
        {
            if (rule.RatioOfChannelId is not null) return $"consumption at or above {Number(absolute!.Value * 100)} % of the available bandwidth";
            return rule.HigherIsWorse ? $"at or above {Number(absolute!.Value)}{u}" : $"below {Number(absolute!.Value)}{u}";
        }
        string Base() => $"more than {Number(rule.Baseline!.Deviations)} robust deviations {(rule.HigherIsWorse ? "above" : "below")} the session baseline median {Number(baseline!.Median)}{u} (limit {Number(baselineLimit!.Value)}{u})";
        return basis switch
        {
            ThresholdBasis.Absolute => Abs(),
            ThresholdBasis.BaselineDeviation => Base(),
            _ => rule.Criterion == RuleCriterion.Either ? $"{Abs()} or {Base()}" : $"{Abs()} and {Base()}"
        };
    }

    public static string Explain(AnomalyRule rule, ObservationKind kind, string label, MetricEvidence primary, DateTimeOffset start, DateTimeOffset end, TimeSpan span, int matching, int valid,
        string comparison, ObservationEnd ending, DateTimeOffset? recoveredAt, MetricEvidence? activity)
    {
        var worst = rule.HigherIsWorse ? primary.Maximum : primary.Minimum;
        var unit = primary.Unit.Length == 0 ? "" : " " + primary.Unit;
        var parts = new List<string>
        {
            $"{Cap(label)} {(rule.HigherIsWorse ? "reached" : "fell to")} {Number(worst)}{unit} {Interval(start, end)}.",
            kind switch
            {
                ObservationKind.Sustained => $"The condition lasted {Duration(span)}: {matching} of {valid} valid samples met the criterion ({comparison}); median {Number(primary.P50)}{unit}.",
                ObservationKind.Spike => $"This was a short excursion of {Duration(span)}: {matching} valid sample(s) met the criterion ({comparison}); median {Number(primary.P50)}{unit}.",
                ObservationKind.Burst => $"This was a short burst of {Duration(span)}: {matching} valid samples met the criterion ({comparison}); median {Number(primary.P50)}{unit}.",
                _ => $"Repeated excursions over {Duration(span)}: {matching} of {valid} valid samples met the criterion ({comparison}), too sparse to count as sustained; median {Number(primary.P50)}{unit}."
            }
        };
        if (activity is not null) parts.Add($"Display activity was present: recorded Dirty FPS had a median of {Number(activity.P50)} {activity.Unit} in this interval.");
        parts.Add(ending switch
        {
            ObservationEnd.Recovered => $"Valid values were back within the normal criterion at {Time(recoveredAt!.Value)} UTC.",
            ObservationEnd.ContextEnded => "The display activity this measurement depends on stopped after this interval; that is not a recovery.",
            _ => "Measurements ended or were missing after this interval, so recovery is not confirmed."
        });
        parts.Add(NoCause);
        return string.Join(" ", parts);
    }

    /// <summary>Cautions that belong to a rule, whatever the data says.</summary>
    public static IEnumerable<string> RuleNotes(AnomalyRule rule, bool blast)
    {
        switch (rule.Id)
        {
            case "conn.loss.sustained": case "conn.loss.burst":
                yield return blast
                    ? "Only the uplink packet-loss counter is recorded; loss in the other direction was not measured."
                    : "The recording does not state the direction of the measured packet loss.";
                break;
            case "gfx.fps.low": case "gfx.dropped.high":
                yield return "Display activity was established from the recorded Dirty FPS counter only; the user-selected workload type is not evidence of activity.";
                break;
            case "guest.cpu.saturation": case "guest.cpu.spike":
                yield return "High guest CPU utilization is a guest-resource observation; it is not attributed to the remote-display protocol or to any process.";
                break;
            case "guest.ram.high":
                yield return "Utilization shows how much memory was in use, not whether paging or allocation failures occurred.";
                break;
            case "guest.gpu.saturation":
                yield return "GPU utilization is used only as recorded; the recording does not say which engine of the GPU was busy.";
                break;
            case "guest.disk.throughput-spike":
                yield return "Throughput measures activity; it does not show disk latency or a disk problem.";
                break;
            case "conn.queueing.increase":
                yield return "Out-queueing time is a Horizon Blast counter; it describes delay inside the display pipeline and does not locate the cause.";
                break;
        }
    }

    // ----- correlations -----

    public static string Describe(AnomalyObservation o)
    {
        var p = o.Primary;
        var lower = IsLowerWorse(o);
        return $"{p.Title.ToLowerInvariant()} {(lower ? "fell to" : "reached")} {Number(lower ? p.Minimum : p.Maximum)} {p.Unit} ({Time(o.Start)}–{Time(o.End)} UTC)".Replace("  ", " ");
    }

    private static bool IsLowerWorse(AnomalyObservation o) => HistoryChannels.Find(o.Primary.ChannelId) is { HigherIsWorse: false };

    public static string Correlation(CorrelationPattern pattern, IReadOnlyList<AnomalyObservation> parts, DateTimeOffset overlapStart, DateTimeOffset overlapEnd, bool touching)
    {
        var overlap = touching ? "intervals within the correlation tolerance of each other" : $"overlapping intervals ({Interval(overlapStart, overlapEnd)})";
        var details = string.Join("; ", parts.Select(Describe));
        return pattern switch
        {
            CorrelationPattern.RttAndPacketLoss => $"RTT and packet loss degraded during {overlap}. {details}. Both measurements indicate connection-quality degradation during this period. The recording does not establish whether the endpoint, the network, the host or a gateway is involved.",
            CorrelationPattern.GraphicsAndConnection => $"Frame delivery degraded while connection measurements were degraded, in {overlap}, with display activity established. {details}. The timing is correlated; the recording does not show that one caused the other.",
            CorrelationPattern.FpsAndGuestCpu => $"Frame rate was low during display activity while guest CPU utilization was sustained at a high level, in {overlap}. {details}. The recording does not show that CPU was the cause.",
            CorrelationPattern.MultipleGuestResources => $"Several guest resource measurements were degraded in {overlap}. {details}. The recording does not show which of them, if any, drove the others.",
            _ => $"{details}. This observation lies next to missing data or a recorded session change; the recording does not show what happened during the gap, and observations on either side are not treated as one continuous event."
        };
    }
}
