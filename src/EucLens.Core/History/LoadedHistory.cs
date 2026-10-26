namespace EucLens.Core;

/// <summary>The values of one metric over the whole recording, one per sample (NaN = no valid value), with what the recording said about its unit.</summary>
public sealed class HistoryChannelData
{
    internal HistoryChannelData(HistoryChannel channel, string unit, double[] values, int valid, string? unavailableReason)
    {
        Channel = channel; Unit = unit; Values = values; Valid = valid; UnavailableReason = unavailableReason;
    }
    public HistoryChannel Channel { get; }
    /// <summary>The unit as recorded; raw counters carry their "raw (...)" label.</summary>
    public string Unit { get; }
    public bool UnitVerified => !HistoryChannels.IsUnverified(Unit);
    internal double[] Values { get; }
    public int Valid { get; }
    public int Missing => Values.Length - Valid;
    /// <summary>For a channel that never had a valid value: the first reason the recording gave.</summary>
    public string? UnavailableReason { get; }
    public bool IsPresent => Valid > 0;
}

/// <summary>The session's own normal range for one metric, used to say whether something stands out in the same recording.</summary>
public sealed record ChannelBaseline(double Minimum, double P05, double P50, double P95, double Maximum, int Valid);

/// <summary>
/// A recording read for analysis: the incident timeline, the user incidents with their recorded context, and compact columns of every metric.
/// Immutable once loaded, so it can be queried from any thread while the UI shows an older view. Raw lines are not kept: about 8 bytes per metric and
/// sample (an eight hour session at 1 Hz is a few MB).
/// </summary>
public sealed class LoadedHistory
{
    private readonly long[] ticks;
    private readonly Dictionary<string, HistoryChannelData> byId;
    private readonly Dictionary<string, Lazy<ChannelBaseline?>> baselines = [];

    internal LoadedHistory(HistoryInfo info, long[] ticks, IReadOnlyList<HistoryChannelData> channels, IReadOnlyList<HistoryEvent> events, IReadOnlyList<HistoryIncident> incidents,
        IReadOnlyList<HistoryGap> gaps, IReadOnlyList<HistoryIssue> issues, TimeSpan gapThreshold)
    {
        Info = info; this.ticks = ticks; AllChannels = channels; Events = events; Incidents = incidents; Gaps = gaps; Issues = issues; GapThreshold = gapThreshold;
        byId = channels.ToDictionary(c => c.Channel.Id);
        foreach (var channel in channels)
        {
            var data = channel;
            baselines[channel.Channel.Id] = new(() => ComputeBaseline(data), LazyThreadSafetyMode.ExecutionAndPublication);
        }
    }

    public HistoryInfo Info { get; }
    public IReadOnlyList<HistoryEvent> Events { get; }
    public IReadOnlyList<HistoryIncident> Incidents { get; }
    public IReadOnlyList<HistoryGap> Gaps { get; }
    public IReadOnlyList<HistoryIssue> Issues { get; }
    /// <summary>Intervals longer than this without a sample are gaps.</summary>
    public TimeSpan GapThreshold { get; }
    public IReadOnlyList<HistoryChannelData> AllChannels { get; }
    /// <summary>Only metrics that hold at least one valid value; everything else stays out of the charts instead of showing zero.</summary>
    public IReadOnlyList<HistoryChannelData> Channels => AllChannels.Where(c => c.IsPresent).ToArray();
    public IReadOnlyList<HistoryChannelData> UnavailableChannels => AllChannels.Where(c => !c.IsPresent).ToArray();
    public int SampleCount => ticks.Length;
    /// <summary>The sample times (UTC ticks, in time order) for the analyzer; read-only by convention, never modified.</summary>
    internal long[] TickArray => ticks;
    public DateTimeOffset FirstSample => ticks.Length == 0 ? Info.StartedUtc : FromTicks(ticks[0]);
    public DateTimeOffset LastSample => ticks.Length == 0 ? Info.EndedUtc : FromTicks(ticks[^1]);
    /// <summary>The span the charts can show: from the first to the last sample (or the recording's own start and end when there are none).</summary>
    public (DateTimeOffset Start, DateTimeOffset End) Span => ticks.Length == 0 ? (Info.StartedUtc, Info.EndedUtc) : (FirstSample, LastSample);
    public long ApproximateBytes => (long)ticks.Length * 8 * (1 + AllChannels.Count);

    internal static DateTimeOffset FromTicks(long t) => new(new DateTime(t, DateTimeKind.Utc));

    public HistoryChannelData? Channel(string id) => byId.GetValueOrDefault(id);

    /// <summary>Sample index range [start, end) for a time range, by binary search.</summary>
    public (int Start, int End) IndexRange(DateTimeOffset from, DateTimeOffset to)
    {
        var a = LowerBound(from.UtcTicks); var b = LowerBound(to.UtcTicks + 1);
        return (a, Math.Max(a, b));
    }

    private int LowerBound(long value)
    {
        int lo = 0, hi = ticks.Length;
        while (lo < hi) { var mid = lo + (hi - lo) / 2; if (ticks[mid] < value) lo = mid + 1; else hi = mid; }
        return lo;
    }

    public SeriesWindow Series(string channelId, DateTimeOffset from, DateTimeOffset to, int maxPoints, IReadOnlyList<DateTimeOffset>? anchors = null)
    {
        var data = byId[channelId];
        var (start, end) = IndexRange(from, to);
        var t = ticks.AsSpan(start, end - start); var v = data.Values.AsSpan(start, end - start);
        var points = PeakPreservingDownsampler.Reduce(t, v, from.UtcTicks, to.UtcTicks, maxPoints, GapThreshold.Ticks, anchors?.Select(a => a.UtcTicks).ToArray(), out var decimated);
        return new(data.Channel, data.Unit, data.UnitVerified, points, decimated, HistoryStatistics.Compute(t, v));
    }

    public RangeStatistics Statistics(string channelId, DateTimeOffset from, DateTimeOffset to)
    {
        var data = byId[channelId];
        var (start, end) = IndexRange(from, to);
        return HistoryStatistics.Compute(ticks.AsSpan(start, end - start), data.Values.AsSpan(start, end - start));
    }

    /// <summary>Every present metric over one range, reduced for drawing. Incident times are forced bucket boundaries.</summary>
    public IReadOnlyList<SeriesWindow> View(DateTimeOffset from, DateTimeOffset to, int maxPoints, CancellationToken cancellationToken = default)
    {
        var anchors = Incidents.Select(i => i.At).ToArray();
        var result = new List<SeriesWindow>();
        foreach (var channel in Channels)
        {
            cancellationToken.ThrowIfCancellationRequested();
            result.Add(Series(channel.Channel.Id, from, to, maxPoints, anchors));
        }
        return result;
    }

    /// <summary>The recorded value closest to an instant, only if a sample lies within the gap threshold of it: the readout never invents a value inside a gap.</summary>
    public (double Value, DateTimeOffset At)? Nearest(string channelId, DateTimeOffset at)
    {
        var data = byId[channelId];
        if (ticks.Length == 0) return null;
        var i = LowerBound(at.UtcTicks);
        long bestDistance = long.MaxValue; var best = -1;
        for (var k = Math.Max(0, i - 3); k <= Math.Min(ticks.Length - 1, i + 2); k++)
        {
            if (double.IsNaN(data.Values[k])) continue;
            var distance = Math.Abs(ticks[k] - at.UtcTicks);
            if (distance < bestDistance) { bestDistance = distance; best = k; }
        }
        return best >= 0 && bestDistance <= GapThreshold.Ticks ? (data.Values[best], FromTicks(ticks[best])) : null;
    }

    /// <summary>The highest (or lowest) valid value in a range and when it was recorded; null when the range has no valid value.</summary>
    public (double Value, DateTimeOffset At)? Extreme(string channelId, DateTimeOffset from, DateTimeOffset to, bool highest)
    {
        var data = byId[channelId];
        var (start, end) = IndexRange(from, to);
        var best = -1;
        for (var i = start; i < end; i++)
        {
            var v = data.Values[i];
            if (double.IsNaN(v)) continue;
            if (best < 0 || (highest ? v > data.Values[best] : v < data.Values[best])) best = i;
        }
        return best < 0 ? null : (data.Values[best], FromTicks(ticks[best]));
    }

    public ChannelBaseline? Baseline(string channelId) => baselines.TryGetValue(channelId, out var lazy) ? lazy.Value : null;

    private static ChannelBaseline? ComputeBaseline(HistoryChannelData data)
    {
        var valid = data.Values.Where(v => !double.IsNaN(v)).ToArray();
        if (valid.Length == 0) return null;
        Array.Sort(valid);
        return new(valid[0], HistoryStatistics.Percentile(valid, 0.05), HistoryStatistics.Percentile(valid, 0.5), HistoryStatistics.Percentile(valid, 0.95), valid[^1], valid.Length);
    }
}
