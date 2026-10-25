namespace EucLens.Core;

/// <summary>Range statistics over valid values only (a NaN is a sample without a valid value).</summary>
public static class HistoryStatistics
{
    /// <summary>Linear interpolation between closest ranks, the same definition the diagnostic engine uses.</summary>
    public static double Percentile(ReadOnlySpan<double> sorted, double p)
    {
        if (sorted.Length == 0) throw new ArgumentException("No values.", nameof(sorted));
        var rank = p * (sorted.Length - 1);
        var lower = (int)Math.Floor(rank);
        var upper = Math.Min(sorted.Length - 1, lower + 1);
        return sorted[lower] + (sorted[upper] - sorted[lower]) * (rank - lower);
    }

    public static RangeStatistics Compute(ReadOnlySpan<long> ticks, ReadOnlySpan<double> values)
    {
        var n = values.Length;
        if (n == 0) return RangeStatistics.Empty;
        var valid = new double[n];
        var count = 0; double max = double.NegativeInfinity; long maxAt = 0;
        for (var i = 0; i < n; i++)
        {
            var v = values[i];
            if (double.IsNaN(v)) continue;
            valid[count++] = v;
            if (v > max) { max = v; maxAt = ticks[i]; }
        }
        if (count == 0) return new(n, 0, n, null, null, null, null, null);
        var sorted = valid.AsSpan(0, count);
        sorted.Sort();
        return new(n, count, n - count, sorted[0], Percentile(sorted, 0.5), Percentile(sorted, 0.95), max, new DateTimeOffset(new DateTime(maxAt, DateTimeKind.Utc)));
    }
}

/// <summary>
/// Reduces a long series to a bounded number of points without hiding short spikes: every time bucket keeps its minimum and its maximum (with their own
/// timestamps, in time order) instead of an average. Missing-data runs and long intervals without samples end the current bucket and leave a gap point
/// (a null value), so a break is a break and never a line through nothing. Anchor times (incidents) always start a new bucket. The input is unchanged.
/// </summary>
public static class PeakPreservingDownsampler
{
    public static IReadOnlyList<SeriesPoint> Reduce(ReadOnlySpan<long> ticks, ReadOnlySpan<double> values, long from, long to, int maxPoints, long gapThresholdTicks,
        IReadOnlyList<long>? anchors, out bool decimated)
    {
        if (ticks.Length != values.Length) throw new ArgumentException("Times and values differ in length.");
        if (maxPoints < 4) throw new ArgumentOutOfRangeException(nameof(maxPoints), "At least four points are needed to keep a minimum and a maximum.");
        var n = values.Length;
        var valid = 0;
        for (var i = 0; i < n; i++) if (!double.IsNaN(values[i])) valid++;
        var points = new List<SeriesPoint>(Math.Min(valid, maxPoints) + 8);
        long Midpoint(long a, long b) => a + (b - a) / 2;

        if (valid <= maxPoints)
        {
            decimated = false;
            var previous = -1;
            for (var i = 0; i < n; i++)
            {
                if (double.IsNaN(values[i])) continue;
                if (previous >= 0 && (i - previous > 1 || ticks[i] - ticks[previous] > gapThresholdTicks)) points.Add(new(Midpoint(ticks[previous], ticks[i]), null));
                points.Add(new(ticks[i], values[i]));
                previous = i;
            }
            return points;
        }

        decimated = true;
        var width = Math.Max(1, (to - from) / Math.Max(1, maxPoints / 2));
        var sortedAnchors = (anchors ?? []).Where(a => a >= from && a <= to).Distinct().Order().ToArray();
        var anchorIndex = 0;
        long currentBucket = -1; var currentAnchors = -1;
        var has = false; long minAt = 0, maxAt = 0; double min = 0, max = 0;
        var last = -1;

        void Flush()
        {
            if (!has) return;
            if (min == max || minAt == maxAt) points.Add(new(minAt, min));
            else if (minAt < maxAt) { points.Add(new(minAt, min)); points.Add(new(maxAt, max)); }
            else { points.Add(new(maxAt, max)); points.Add(new(minAt, min)); }
            has = false;
        }

        for (var i = 0; i < n; i++)
        {
            var v = values[i];
            if (double.IsNaN(v)) continue;
            var t = ticks[i];
            var bucket = (t - from) / width;
            while (anchorIndex < sortedAnchors.Length && sortedAnchors[anchorIndex] <= t) anchorIndex++;
            // A break is kept when it is wide enough to be seen at this resolution; narrower ones would only add points.
            var gap = last >= 0 && (i - last > 1 || t - ticks[last] > gapThresholdTicks) && t - ticks[last] >= width / 4;
            if (has && (gap || bucket != currentBucket || anchorIndex != currentAnchors)) Flush();
            if (gap) points.Add(new(Midpoint(ticks[last], t), null));
            if (!has)
            {
                has = true; currentBucket = bucket; currentAnchors = anchorIndex;
                min = max = v; minAt = maxAt = t;
            }
            else
            {
                if (v < min) { min = v; minAt = t; }
                if (v > max) { max = v; maxAt = t; }
            }
            last = i;
        }
        Flush();
        return points;
    }
}
