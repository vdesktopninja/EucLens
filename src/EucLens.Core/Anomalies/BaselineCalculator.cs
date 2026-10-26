using System.Buffers;

namespace EucLens.Core;

/// <summary>
/// Builds the session's own normal range for one metric: the worst tail is removed first (so anomalies cannot pull their own reference), then the median and the
/// median absolute deviation of what is left. Needs enough samples over enough time; otherwise the baseline is reported as unreliable and rules that depend on it
/// say "insufficient data" instead of falling back to a number nobody chose.
/// </summary>
internal static class BaselineCalculator
{
    /// <summary>1 / Φ⁻¹(3/4): scales a MAD to the standard deviation of a normal distribution.</summary>
    private const double MadToSigma = 1.4826;

    public static MetricBaseline Compute(string channelId, string unit, double factor, ReadOnlySpan<double> canonical, ReadOnlySpan<byte> eligible, ReadOnlySpan<long> ticks,
        AnomalyRule rule, AnomalyOptions options, out double canonicalMedian, out double canonicalSigma)
    {
        canonicalMedian = canonicalSigma = double.NaN;
        var criterion = rule.Baseline!;
        var pool = ArrayPool<double>.Shared;
        var values = pool.Rent(Math.Max(1, canonical.Length));
        try
        {
            var m = 0; long first = long.MaxValue, last = long.MinValue;
            for (var i = 0; i < canonical.Length; i++)
            {
                if (eligible[i] == 0) continue;
                values[m++] = canonical[i];
                if (ticks[i] < first) first = ticks[i];
                if (ticks[i] > last) last = ticks[i];
            }
            var span = m > 1 ? TimeSpan.FromTicks(last - first) : TimeSpan.Zero;
            string? reason = null;
            if (m < options.MinimumBaselineSamples || span < options.MinimumBaselineSpan)
                reason = $"Only {AnomalyNarrative.Count(m)} valid sample(s) over {AnomalyNarrative.Duration(span)}; a baseline needs at least {AnomalyNarrative.Count(options.MinimumBaselineSamples)} over {AnomalyNarrative.Duration(options.MinimumBaselineSpan)}.";
            if (m == 0) return new(channelId, unit, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, 0, TimeSpan.Zero, false, reason);

            var all = values.AsSpan(0, m);
            all.Sort();
            var p05 = HistoryStatistics.Percentile(all, 0.05); var p95 = HistoryStatistics.Percentile(all, 0.95);
            var keep = Math.Max(1, (int)Math.Ceiling(m * (1 - options.BaselineTrimFraction)));
            var kept = rule.HigherIsWorse ? all[..keep] : all[(m - keep)..];
            var median = HistoryStatistics.Percentile(kept, 0.5);

            var deviations = pool.Rent(kept.Length);
            try
            {
                var dev = deviations.AsSpan(0, kept.Length);
                for (var i = 0; i < kept.Length; i++) dev[i] = Math.Abs(kept[i] - median);
                dev.Sort();
                var mad = HistoryStatistics.Percentile(dev, 0.5);
                var sigma = Math.Max(MadToSigma * mad, Math.Max(criterion.MinimumScale, criterion.RelativeScaleFloor * Math.Abs(median)));
                canonicalMedian = median; canonicalSigma = sigma;
                return new(channelId, unit, Report(median, factor), Report(mad, factor), Report(sigma, factor), Report(p05, factor), Report(p95, factor), m, span, reason is null, reason);
            }
            finally { pool.Return(deviations); }
        }
        finally { pool.Return(values); }
    }

    /// <summary>Canonical → recorded unit, without the noise a multiplication by 0.001 and back would add.</summary>
    public static double Report(double canonical, double factor) => Clean(canonical / factor);

    public static double Clean(double value) => double.IsFinite(value) ? Math.Round(value, 9) : value;
}
