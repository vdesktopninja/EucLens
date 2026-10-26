namespace EucLens.Core;

/// <summary>
/// The sample times of one analysis with what the rules need to know about them: continuous stretches ("segments"), stretches where the remote session was
/// disconnected, the typical sampling step and the coverage. Nothing here looks at metric values.
/// </summary>
internal sealed class AnomalyTimeline
{
    private static readonly HashSet<HistoryEventKind> Boundaries =
    [
        HistoryEventKind.RemoteDisconnected, HistoryEventKind.RemoteReconnected, HistoryEventKind.SessionChanged,
        HistoryEventKind.MonitoringPaused, HistoryEventKind.MonitoringResumed, HistoryEventKind.DataLoss
    ];

    private AnomalyTimeline(long[] ticks, int count, int[] segment, bool[] disconnected, long stepTicks, long gapTicks, List<(long Start, long End)> gaps, double coverage)
    {
        Ticks = ticks; Count = count; Segment = segment; Disconnected = disconnected; StepTicks = stepTicks; GapTicks = gapTicks; Gaps = gaps; Coverage = coverage;
    }

    public long[] Ticks { get; }
    public int Count { get; }
    /// <summary>Index of the continuous stretch each sample belongs to. An observation never spans two stretches.</summary>
    public int[] Segment { get; }
    /// <summary>True between a recorded remote disconnect and the next reconnect (or session change): connection and graphics measurements are not used there.</summary>
    public bool[] Disconnected { get; }
    public long StepTicks { get; }
    public long GapTicks { get; }
    public IReadOnlyList<(long Start, long End)> Gaps { get; }
    /// <summary>Share of the recording's time covered by samples (a sample covers at most one gap threshold).</summary>
    public double Coverage { get; }

    public static AnomalyTimeline Build(AnomalyInput input, int count, CancellationToken cancellationToken)
    {
        var ticks = input.Ticks;
        var gapTicks = input.GapThreshold.Ticks;
        var segment = new int[count]; var disconnected = new bool[count];
        var gaps = new List<(long, long)>();
        var events = input.Events;
        var boundary = new List<long>();
        var disconnects = new List<(long At, bool Disconnect)>();
        foreach (var e in events)
        {
            if (Boundaries.Contains(e.Kind)) boundary.Add(e.At.UtcTicks);
            if (e.Kind == HistoryEventKind.RemoteDisconnected) disconnects.Add((e.At.UtcTicks, true));
            else if (e.Kind is HistoryEventKind.RemoteReconnected or HistoryEventKind.SessionChanged) disconnects.Add((e.At.UtcTicks, false));
        }
        // Events are in time order already; both lists are walked once alongside the samples.
        int b = 0, d = 0, current = 0; var isDisconnected = false;
        long covered = 0;
        var deltas = new List<long>(Math.Min(Math.Max(count - 1, 0), 2000));
        var stride = Math.Max(1, (count - 1) / 2000);
        for (var i = 0; i < count; i++)
        {
            if ((i & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
            if (i > 0)
            {
                var dt = ticks[i] - ticks[i - 1];
                if (dt > gapTicks) { current++; gaps.Add((ticks[i - 1], ticks[i])); covered += gapTicks; }
                else covered += dt;
                if (i % stride == 0 && dt > 0) deltas.Add(dt);
                var crossed = false;
                while (b < boundary.Count && boundary[b] <= ticks[i]) { if (boundary[b] > ticks[i - 1]) crossed = true; b++; }
                if (crossed && (i == 0 || segment[i - 1] == current)) current++;
            }
            else { while (b < boundary.Count && boundary[b] <= ticks[0]) b++; }
            while (d < disconnects.Count && disconnects[d].At <= ticks[i]) { isDisconnected = disconnects[d].Disconnect; d++; }
            segment[i] = current; disconnected[i] = isDisconnected;
        }

        long step;
        if (deltas.Count == 0) step = TimeSpan.TicksPerSecond;
        else { deltas.Sort(); step = Math.Min(deltas[deltas.Count / 2], gapTicks); }
        var span = count > 1 ? ticks[count - 1] - ticks[0] : 0;
        var coverage = count < 2 ? (count == 1 ? 1 : 0) : span <= 0 ? 1 : Math.Clamp(covered / (double)span, 0, 1);
        return new(ticks, count, segment, disconnected, step, gapTicks, gaps, coverage);
    }

    /// <summary>First sample index at or after a time.</summary>
    public int LowerBound(long tick)
    {
        int lo = 0, hi = Count;
        while (lo < hi) { var mid = lo + (hi - lo) / 2; if (Ticks[mid] < tick) lo = mid + 1; else hi = mid; }
        return lo;
    }

    /// <summary>Segment of the sample at (or, between samples, just before) a time; -1 before the first sample.</summary>
    public int SegmentAt(long tick)
    {
        var i = LowerBound(tick);
        if (i < Count && Ticks[i] == tick) return Segment[i];
        return i == 0 ? -1 : Segment[i - 1];
    }

    /// <summary>Number of samples with time in [from, to].</summary>
    public int SamplesBetween(long from, long to) => to < from ? 0 : LowerBound(to + 1) - LowerBound(from);
}
