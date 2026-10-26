using System.Globalization;

namespace EucLens.Core;

/// <summary>One metric over a recording: one value per sample, NaN where the recording has no valid value (a missing value is never a zero).</summary>
/// <param name="Unit">The unit exactly as recorded; it is checked against what a rule can interpret, never assumed.</param>
public sealed record AnomalyChannel(string Id, string Unit, double[] Values, string? Title = null)
{
    public string DisplayTitle => Title ?? HistoryChannels.Find(Id)?.Title ?? Id;
}

/// <summary>
/// What the analyzer reads: sample times and metric columns in time order with unique times, the timeline events, and what is known about the input's completeness.
/// It is built from a <see cref="LoadedHistory"/> or directly from arrays (tests, other sources). Out-of-order input is ordered (stable) and samples with the same time are
/// combined (the first valid value per metric wins), both reported as limitations, so the result does not depend on how the samples happened to arrive.
/// </summary>
public sealed class AnomalyInput
{
    private readonly Dictionary<string, AnomalyChannel> byId;

    private AnomalyInput(string recordingKey, long[] ticks, IReadOnlyList<AnomalyChannel> channels, IReadOnlyList<HistoryEvent> events, TimeSpan gapThreshold,
        IReadOnlyList<string> protocols, IReadOnlyList<string> limitations, bool reducedConfidence)
    {
        RecordingKey = recordingKey; Ticks = ticks; Channels = channels; Events = events; GapThreshold = gapThreshold;
        Protocols = protocols; Limitations = limitations; ReducedConfidence = reducedConfidence;
        byId = channels.ToDictionary(c => c.Id, StringComparer.Ordinal);
    }

    public string RecordingKey { get; }
    internal long[] Ticks { get; }
    public int SampleCount => Ticks.Length;
    public IReadOnlyList<AnomalyChannel> Channels { get; }
    public IReadOnlyList<HistoryEvent> Events { get; }
    /// <summary>Intervals longer than this without a sample are gaps; an observation never continues across one.</summary>
    public TimeSpan GapThreshold { get; }
    /// <summary>Protocols the recording names (for example "Omnissa Horizon Blast"); empty when it names none.</summary>
    public IReadOnlyList<string> Protocols { get; }
    /// <summary>What reduces the input before any rule runs (partial recording, damaged or missing data, reordered samples).</summary>
    public IReadOnlyList<string> Limitations { get; }
    public bool ReducedConfidence { get; }

    public AnomalyChannel? Channel(string id) => byId.GetValueOrDefault(id);

    /// <param name="times">Sample times; they may be unordered or repeated.</param>
    public static AnomalyInput Create(string recordingKey, IReadOnlyList<DateTimeOffset> times, IEnumerable<AnomalyChannel> channels, IEnumerable<HistoryEvent>? events = null,
        IEnumerable<string>? protocols = null, TimeSpan? gapThreshold = null, IEnumerable<string>? limitations = null, bool reducedConfidence = false)
    {
        var ticks = new long[times.Count];
        for (var i = 0; i < ticks.Length; i++) ticks[i] = times[i].UtcTicks;
        return Create(recordingKey, ticks, channels, events, protocols, gapThreshold, limitations, reducedConfidence);
    }

    public static AnomalyInput Create(string recordingKey, long[] ticks, IEnumerable<AnomalyChannel> channels, IEnumerable<HistoryEvent>? events = null,
        IEnumerable<string>? protocols = null, TimeSpan? gapThreshold = null, IEnumerable<string>? limitations = null, bool reducedConfidence = false)
    {
        ArgumentNullException.ThrowIfNull(ticks); ArgumentNullException.ThrowIfNull(channels);
        var list = channels.ToList();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var c in list)
        {
            if (c.Values.Length != ticks.Length) throw new ArgumentException($"Channel {c.Id} has {c.Values.Length} values for {ticks.Length} samples.", nameof(channels));
            if (!seen.Add(c.Id)) throw new ArgumentException($"Channel {c.Id} appears twice.", nameof(channels));
        }

        var notes = (limitations ?? []).ToList();
        var sorted = true; var duplicates = false;
        for (var i = 1; i < ticks.Length; i++) { if (ticks[i] < ticks[i - 1]) { sorted = false; break; } if (ticks[i] == ticks[i - 1]) duplicates = true; }
        if (!sorted)
        {
            var order = new int[ticks.Length];
            for (var i = 0; i < order.Length; i++) order[i] = i;
            // Index as tie-breaker: the order of equal times is the order of arrival, whatever the sort algorithm does.
            Array.Sort(order, (a, b) => ticks[a] != ticks[b] ? ticks[a].CompareTo(ticks[b]) : a.CompareTo(b));
            var t2 = new long[ticks.Length];
            for (var i = 0; i < order.Length; i++) t2[i] = ticks[order[i]];
            list = list.Select(c => { var v = new double[c.Values.Length]; for (var i = 0; i < v.Length; i++) v[i] = c.Values[order[i]]; return c with { Values = v }; }).ToList();
            ticks = t2; duplicates = false;
            for (var i = 1; i < ticks.Length; i++) if (ticks[i] == ticks[i - 1]) { duplicates = true; break; }
            notes.Add("Samples were not in time order in the input; they were ordered by time before analysis.");
            reducedConfidence = true;
        }
        if (duplicates)
        {
            var keep = new List<int>(ticks.Length);
            for (var i = 0; i < ticks.Length; i++) if (i == 0 || ticks[i] != ticks[i - 1]) keep.Add(i);
            var removed = ticks.Length - keep.Count;
            var t2 = new long[keep.Count];
            for (var i = 0; i < keep.Count; i++) t2[i] = ticks[keep[i]];
            list = list.Select(c => CollapseDuplicates(c, ticks, keep, t2.Length)).ToList();
            ticks = t2;
            notes.Add($"{removed} sample(s) shared a timestamp with an earlier sample; per metric the first valid value for that time was used.");
            reducedConfidence = true;
        }
        // The caller's arrays are never modified: an infinite value (a counter error) becomes "no value" in a copy.
        list = list.Select(c => c.Values.Any(double.IsInfinity) ? c with { Values = c.Values.Select(v => double.IsFinite(v) ? v : double.NaN).ToArray() } : c).ToList();

        var ordered = (events ?? []).OrderBy(e => e.At).ThenBy(e => e.Order).ToArray();
        return new(recordingKey, ticks, list, ordered, gapThreshold ?? DefaultGapThreshold(ticks), (protocols ?? []).ToArray(), notes, reducedConfidence);
    }

    private static AnomalyChannel CollapseDuplicates(AnomalyChannel channel, long[] ticks, List<int> keep, int count)
    {
        var values = new double[count];
        var k = 0;
        for (var i = 0; i < ticks.Length; i++)
        {
            var isFirstOfTime = i == 0 || ticks[i] != ticks[i - 1];
            if (isFirstOfTime) { values[k++] = channel.Values[i]; continue; }
            // A later sample with the same time fills only a value that is still missing.
            if (!double.IsFinite(values[k - 1]) && double.IsFinite(channel.Values[i])) values[k - 1] = channel.Values[i];
        }
        return channel with { Values = values };
    }

    /// <summary>The loader's rule: a gap is anything longer than three times the median step, but never less than five seconds.</summary>
    internal static TimeSpan DefaultGapThreshold(long[] ticks)
    {
        if (ticks.Length < 3) return TimeSpan.FromSeconds(5);
        var deltas = new List<long>(Math.Min(ticks.Length - 1, 2000));
        var step = Math.Max(1, (ticks.Length - 1) / 2000);
        for (var i = 1; i < ticks.Length; i += step) if (ticks[i] > ticks[i - 1]) deltas.Add(ticks[i] - ticks[i - 1]);
        if (deltas.Count == 0) return TimeSpan.FromSeconds(5);
        deltas.Sort();
        return TimeSpan.FromTicks(Math.Max(TimeSpan.FromSeconds(5).Ticks, deltas[deltas.Count / 2] * 3));
    }

    /// <summary>The recorded history as analyzer input. Metric values are shared with the history (read-only), not copied.</summary>
    public static AnomalyInput From(LoadedHistory history)
    {
        ArgumentNullException.ThrowIfNull(history);
        var info = history.Info;
        var notes = new List<string>(); var reduced = false;

        if (info.State == RecordingCompleteness.Partial)
        {
            notes.Add($"The recording is only partly analyzed: {(info.LimitReason.Length > 0 ? info.LimitReason : "a reading limit was reached")}. Nothing after {HistoryTime.Format(history.LastSample, TimeZoneInfo.Utc, withDate: true)} UTC was examined.");
            reduced = true;
        }
        else if (info.State == RecordingCompleteness.InProgress) notes.Add("The recording is still in progress; its end is not known.");
        else if (info.State == RecordingCompleteness.Interrupted) notes.Add("The recording was interrupted: no end of recording was written, so the last observations may have continued.");

        var problems = history.Issues.Where(i => i.IsProblem && i.Kind is not HistoryIssueKind.UnreadableAnnotations && i.Kind is not HistoryIssueKind.LimitReached && i.Kind is not HistoryIssueKind.SampleCapReached).ToArray();
        foreach (var issue in problems.Take(5)) notes.Add($"Recording problem ({issue.Kind}): {issue.Message}");
        if (problems.Length > 5) notes.Add($"{problems.Length - 5} more recording problem(s) were found while reading.");
        if (problems.Length > 0) reduced = true;
        if (info.LostSamples > 0) { notes.Add($"{info.LostSamples.ToString("N0", CultureInfo.InvariantCulture)} sample(s) could not be written during the recording (data loss)."); reduced = true; }

        var channels = history.AllChannels.Select(c => new AnomalyChannel(c.Channel.Id, c.Unit, c.Values, c.Channel.Title)).ToArray();
        // The history guarantees time order; duplicates are possible and are combined by Create when present.
        // "Unknown protocol" means the provider found no Blast counters at that moment; it is a detection state, not a second protocol. Next to a detected protocol it is
        // left out, so that a recording of one Horizon session is not treated as a mix of protocols. A recording with nothing but "Unknown" keeps it.
        var unknown = HistoryLoader.ProtocolName(RemoteProtocol.Unknown);
        var protocols = info.Protocols.Count > 1 ? info.Protocols.Where(p => p != unknown).ToArray() : info.Protocols;
        return Create(info.RecordingId?.ToString("N") ?? info.ShortId, history.TickArray, channels, history.Events, protocols, history.GapThreshold, notes, reduced);
    }
}
