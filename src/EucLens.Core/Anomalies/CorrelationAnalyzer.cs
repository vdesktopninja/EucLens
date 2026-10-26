using System.Security.Cryptography;
using System.Text;

namespace EucLens.Core;

/// <summary>
/// Conservative cross-metric correlation of observations. Two observations are linked only when they lie on the same continuous stretch of data, their intervals overlap (or
/// lie within the correlation tolerance) over at least the minimum number of samples, and both are confirmed observations. Linked observations form one correlation per cluster,
/// so one continuing problem is reported once. A correlation describes timing; it never says one thing caused another.
/// </summary>
internal static class CorrelationAnalyzer
{
    private static readonly HashSet<string> RttRules = ["conn.rtt.high", "conn.rtt.deviation"];
    private static readonly HashSet<string> LossRules = ["conn.loss.sustained", "conn.loss.burst"];
    private static readonly HashSet<string> GraphicsRules = ["gfx.fps.low", "gfx.dropped.high"];
    private static readonly HashSet<string> ConnectionRules = ["conn.rtt.high", "conn.rtt.deviation", "conn.loss.sustained", "conn.loss.burst", "conn.queueing.increase", "conn.bandwidth.pressure"];
    private static readonly HashSet<string> GuestRules = ["guest.cpu.saturation", "guest.ram.high", "guest.disk.stall"];
    private static readonly HashSet<HistoryEventKind> AvailabilityKinds =
    [
        HistoryEventKind.RemoteDisconnected, HistoryEventKind.RemoteReconnected, HistoryEventKind.DataLoss, HistoryEventKind.MonitoringPaused, HistoryEventKind.MonitoringResumed, HistoryEventKind.SessionChanged
    ];

    public static IReadOnlyList<AnomalyCorrelation> Analyze(IReadOnlyList<AnomalyObservation> observations, AnomalyTimeline timeline, AnomalyInput input, AnomalyOptions options, bool blast, CancellationToken token)
    {
        var confirmed = observations.Where(o => o.Status == AnalysisStatus.ConfirmedObservation).ToList();
        var result = new List<AnomalyCorrelation>();
        var ctx = new Context(timeline, input, options, token);

        result.AddRange(Linked(ctx, confirmed, o => RttRules.Contains(o.RuleId), o => LossRules.Contains(o.RuleId), CorrelationPattern.RttAndPacketLoss));
        // A single-sample spike next to a long frame-rate problem is too weak a link: the connection side must be at least a sustained, repeated or burst condition.
        result.AddRange(Linked(ctx, confirmed, o => GraphicsRules.Contains(o.RuleId), o => ConnectionRules.Contains(o.RuleId) && o.Kind != ObservationKind.Spike, CorrelationPattern.GraphicsAndConnection));
        result.AddRange(Linked(ctx, confirmed, o => o.RuleId == "gfx.fps.low", o => o.RuleId == "guest.cpu.saturation", CorrelationPattern.FpsAndGuestCpu));
        result.AddRange(Linked(ctx, confirmed, o => GuestRules.Contains(o.RuleId), null, CorrelationPattern.MultipleGuestResources));
        result.AddRange(Availability(ctx, observations));

        result.Sort((a, b) =>
        {
            var c = a.Start.CompareTo(b.Start);
            if (c != 0) return c;
            c = a.End.CompareTo(b.End);
            if (c != 0) return c;
            c = a.Pattern.CompareTo(b.Pattern);
            return c != 0 ? c : string.CompareOrdinal(a.Id, b.Id);
        });
        return result;
    }

    private sealed record Context(AnomalyTimeline Timeline, AnomalyInput Input, AnomalyOptions Options, CancellationToken Token);

    private static bool Near(Context c, AnomalyObservation a, AnomalyObservation b)
    {
        var half = TimeSpan.FromTicks(c.Options.CorrelationTolerance.Ticks / 2);
        var lo = (a.Start > b.Start ? a.Start : b.Start) - half;
        var hi = (a.End < b.End ? a.End : b.End) + half;
        if (hi < lo) return false;
        if (c.Timeline.SegmentAt(a.Start.UtcTicks) != c.Timeline.SegmentAt(b.Start.UtcTicks)) return false;
        return c.Timeline.SamplesBetween(lo.UtcTicks, hi.UtcTicks) >= c.Options.MinimumOverlapSamples;
    }

    /// <summary>Observations of group A linked to group B (or, with no group B, of different metrics within group A), clustered.</summary>
    private static IEnumerable<AnomalyCorrelation> Linked(Context c, List<AnomalyObservation> all, Func<AnomalyObservation, bool> inA, Func<AnomalyObservation, bool>? inB, CorrelationPattern pattern)
    {
        var a = all.Where(inA).OrderBy(o => o.Start).ThenBy(o => o.Id, StringComparer.Ordinal).ToList();
        var b = inB is null ? a : all.Where(inB).OrderBy(o => o.Start).ThenBy(o => o.Id, StringComparer.Ordinal).ToList();
        if (a.Count == 0 || b.Count == 0) yield break;

        var nodes = inB is null ? a : a.Concat(b).ToList();
        var parent = Enumerable.Range(0, nodes.Count).ToArray();
        var linked = new bool[nodes.Count];
        var edges = new List<(int First, int Second)>();
        int Find(int x) { while (parent[x] != x) x = parent[x] = parent[parent[x]]; return x; }
        var aCount = a.Count;
        for (var i = 0; i < aCount; i++)
        {
            c.Token.ThrowIfCancellationRequested();
            for (var j = inB is null ? i + 1 : aCount; j < nodes.Count; j++)
            {
                var other = nodes[j]; var self = nodes[i];
                if (inB is null && other.Primary.ChannelId == self.Primary.ChannelId) continue;
                if (!Near(c, self, other)) continue;
                parent[Find(i)] = Find(j); linked[i] = linked[j] = true; edges.Add((i, j));
            }
        }
        foreach (var group in Enumerable.Range(0, nodes.Count).Where(i => linked[i]).GroupBy(Find).OrderBy(g => g.Min()))
        {
            var parts = group.Select(i => nodes[i]).OrderBy(o => o.Start).ThenBy(o => o.RuleId, StringComparer.Ordinal).ThenBy(o => o.Id, StringComparer.Ordinal).ToList();
            var members = group.ToHashSet();
            var links = edges.Where(e => members.Contains(e.First)).Select(e => (nodes[e.First], nodes[e.Second])).ToList();
            yield return Build(c, pattern, parts, links);
        }
    }

    /// <summary>The linked pair as a link record: the true overlap of the two intervals when there is one, otherwise the gap between them.</summary>
    private static CorrelationLink LinkOf(AnomalyObservation x, AnomalyObservation y, IReadOnlyList<AnomalyObservation> order)
    {
        var (first, second) = order.ToList().IndexOf(x) <= order.ToList().IndexOf(y) ? (x, y) : (y, x);
        var s = first.Start > second.Start ? first.Start : second.Start; var e = first.End < second.End ? first.End : second.End;
        return s <= e ? new(first.Id, second.Id, s, e, TimeSpan.Zero) : new(first.Id, second.Id, null, null, s - e);
    }

    private static AnomalyCorrelation Build(Context c, CorrelationPattern pattern, List<AnomalyObservation> parts, List<(AnomalyObservation, AnomalyObservation)> pairs)
    {
        var start = parts.Min(p => p.Start); var end = parts.Max(p => p.End);
        // Only an interval in which every component was present is reported as common to them; chains of pairwise overlaps usually have none.
        var commonStart = parts.Max(p => p.Start); var commonEnd = parts.Min(p => p.End);
        var common = commonStart <= commonEnd;
        var timing = common ? CorrelationTiming.CommonOverlap : parts.Count == 2 ? CorrelationTiming.WithinTolerance : CorrelationTiming.Chained;
        var links = pairs.Select(p => LinkOf(p.Item1, p.Item2, parts)).OrderBy(l => parts.FindIndex(p => p.Id == l.FirstId)).ThenBy(l => parts.FindIndex(p => p.Id == l.SecondId)).ToList();
        var severity = parts.Max(p => p.Severity);
        var channels = parts.Select(p => p.Primary.ChannelId).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        var limitations = new List<string>
        {
            "A correlation describes timing only; it does not show that one measurement caused another.",
            $"Components are linked when their intervals overlap or lie within {AnomalyNarrative.Number(c.Options.CorrelationTolerance.TotalSeconds)} s of each other, on one continuous stretch of data."
        };
        if (timing == CorrelationTiming.Chained) limitations.Add("No instant is common to all components: they are linked pair by pair, so the correlation interval is not an interval in which all of them were present.");
        if (timing == CorrelationTiming.WithinTolerance) limitations.Add("The two components do not overlap; they lie within the correlation tolerance of each other.");
        if (parts.Any(p => p.Ending == ObservationEnd.CoverageEnded)) limitations.Add("At least one component ended because measurements ended or were missing; recovery is not confirmed.");
        if (parts.Any(p => p.Severity == Severity.Info)) limitations.Add("At least one component is an informational observation.");
        var title = pattern switch
        {
            CorrelationPattern.RttAndPacketLoss => "Round-trip time and packet loss degraded together",
            CorrelationPattern.GraphicsAndConnection => "Frame delivery and connection measurements degraded together",
            CorrelationPattern.FpsAndGuestCpu => "Low frame rate and sustained guest CPU utilization together",
            _ => "Several guest resource measurements degraded together"
        };
        var explanation = AnomalyNarrative.Correlation(pattern, parts, timing, common ? commonStart : null, common ? commonEnd : null, links);
        return new(Id(c, pattern, parts), pattern, severity, title, start, end, timing, common ? commonStart : null, common ? commonEnd : null, links, parts.Select(p => p.Id).ToList(), channels, explanation, limitations);
    }

    private static string Id(Context c, CorrelationPattern pattern, IEnumerable<AnomalyObservation> parts)
    {
        var text = c.Input.RecordingKey + "|" + pattern + "|" + string.Join(",", parts.Select(p => p.Id).Order(StringComparer.Ordinal));
        return "cor-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)), 0, 8).ToLowerInvariant();
    }

    // ----- pattern 5: next to missing data or a session change -----

    private static IEnumerable<AnomalyCorrelation> Availability(Context c, IReadOnlyList<AnomalyObservation> observations)
    {
        var tolerance = c.Options.CorrelationTolerance;
        foreach (var o in observations)
        {
            c.Token.ThrowIfCancellationRequested();
            var near = new List<string>();
            var events = c.Input.Events; int lo = 0, hi = events.Count; var from = o.Start - tolerance;
            while (lo < hi) { var mid = lo + (hi - lo) / 2; if (events[mid].At < from) lo = mid + 1; else hi = mid; }
            for (var i = lo; i < events.Count && events[i].At <= o.End + tolerance; i++)
                if (AvailabilityKinds.Contains(events[i].Kind)) near.Add($"{events[i].Title} at {AnomalyNarrative.Time(events[i].At)} UTC");
            var gaps = c.Timeline.Gaps;
            int gl = 0, gh = gaps.Count;
            while (gl < gh) { var mid = gl + (gh - gl) / 2; if (gaps[mid].End < (o.Start - tolerance).UtcTicks) gl = mid + 1; else gh = mid; }
            for (var i = gl; i < gaps.Count && gaps[i].Start <= (o.End + tolerance).UtcTicks; i++)
                near.Add($"no samples for {AnomalyNarrative.Duration(TimeSpan.FromTicks(gaps[i].End - gaps[i].Start))} from {AnomalyNarrative.Time(new DateTimeOffset(new DateTime(gaps[i].Start, DateTimeKind.Utc)))} UTC");
            if (near.Count == 0) continue;
            var distinct = near.Distinct().Take(5).ToList();
            var parts = new List<AnomalyObservation> { o };
            var explanation = $"{AnomalyNarrative.Describe(o)} lies next to missing data or a session change: {string.Join("; ", distinct)}{(near.Distinct().Count() > 5 ? "; …" : "")}. " +
                "The recording does not show what happened during the gap, and observations on either side of it are not treated as one continuous event.";
            yield return new(Id(c, CorrelationPattern.AvailabilityOverlap, parts), CorrelationPattern.AvailabilityOverlap, Severity.Info, "Observation next to missing data or a session change",
                o.Start, o.End, CorrelationTiming.SingleObservation, null, null, [], [o.Id], [o.Primary.ChannelId], explanation,
                ["Availability information describes the recording, not the cause of the interruption.", "The observation's own limitations also apply."]);
        }
    }
}
