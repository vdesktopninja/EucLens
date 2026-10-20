using EucLens.Core;

namespace EucLens.Diagnostics;

/// <summary>Statistics of one metric over one observation window. Statistics cover valid samples only.</summary>
public sealed record WindowStats(string Key, double WindowSeconds, int TotalSamples, int ValidSamples, int StaleSamples,
    int FailedSamples, double CoveredSeconds, double? Mean, double? Min, double? Max, double? P50, double? P95,
    double? StdDev, double? Latest, double? SlopePerMinute, string? LatestReason, double? P25 = null, double? P75 = null)
{
    /// <summary>Interquartile range: spread of the middle 50 % of samples, unaffected by up to 25 % outliers.</summary>
    public double? Spread => P75 - P25;
    public double Coverage => WindowSeconds <= 0 ? 0 : Math.Min(1, CoveredSeconds / WindowSeconds);
    public bool IsSufficient(DiagnosticThresholds t) => ValidSamples >= t.MinimumValidSamples && Coverage >= t.MinimumCoverageFraction;
    public double? CoefficientOfVariation => Mean is > 0 && StdDev is { } sd ? sd / Mean : null;
}

/// <summary>
/// Bounded history of one metric keyed by monotonic elapsed seconds. Fixed-size arrays: no allocation per sample.
/// Each valid sample represents the time since the previous sample, capped at the maximum gap, so irregular
/// intervals and missing samples reduce coverage instead of being assumed to be one second.
/// </summary>
public sealed class MetricSeries(string key, int capacity = MetricSeries.DefaultCapacity)
{
    public const int DefaultCapacity = 512;
    private readonly double[] times = new double[capacity];
    private readonly double[] values = new double[capacity];
    private readonly MetricStatus[] statuses = new MetricStatus[capacity];
    private readonly string?[] reasons = new string?[capacity];
    private readonly double[] scratch = new double[capacity];
    private int start, count;
    public string Key { get; } = key;
    public int Count => count;

    public void Add(double time, Metric? metric)
    {
        // Out-of-order times would corrupt interval arithmetic; the engine supplies a monotonic clock.
        if (count > 0 && time < times[Index(count - 1)]) return;
        var i = count < times.Length ? Index(count++) : Advance();
        times[i] = time;
        var status = metric?.Status ?? MetricStatus.Unavailable;
        var valid = status == MetricStatus.Available && metric?.Value is { } v && double.IsFinite(v);
        statuses[i] = valid ? MetricStatus.Available : status == MetricStatus.Available ? MetricStatus.Failed : status;
        values[i] = valid ? metric!.Value!.Value : double.NaN;
        reasons[i] = valid ? null : metric?.Error ?? metric?.UnavailableReason ?? "Not supplied.";
    }

    public void Clear() { start = 0; count = 0; }

    private int Index(int offset) => (start + offset) % times.Length;
    private int Advance() { var i = start; start = (start + 1) % times.Length; return i; }

    /// <summary>Interval represented by entry <paramref name="offset"/>, capped at the maximum gap.</summary>
    private double Interval(int offset, double maxGap, double nominal)
    {
        if (offset == 0) return Math.Min(nominal, maxGap);
        return Math.Min(times[Index(offset)] - times[Index(offset - 1)], maxGap);
    }

    private int FirstInWindow(double now, double window)
    {
        var from = now - window;
        var first = count;
        for (var k = count - 1; k >= 0 && times[Index(k)] > from; k--) first = k;
        return first;
    }

    public WindowStats Analyze(double now, double window, double maxGap, double nominal = 1)
    {
        var first = FirstInWindow(now, window);
        int total = 0, valid = 0, stale = 0, failed = 0;
        double covered = 0, sum = 0, min = double.MaxValue, max = double.MinValue;
        double tSum = 0, vSum = 0, tvSum = 0, ttSum = 0;
        double? latest = null;
        string? latestReason = null;
        for (var k = first; k < count; k++)
        {
            var i = Index(k);
            total++;
            switch (statuses[i])
            {
                case MetricStatus.Stale: stale++; break;
                case MetricStatus.Failed: failed++; break;
            }
            if (statuses[i] != MetricStatus.Available) { latest = null; latestReason = reasons[i]; continue; }
            var v = values[i];
            scratch[valid++] = v;
            covered += Interval(k, maxGap, nominal);
            sum += v; min = Math.Min(min, v); max = Math.Max(max, v);
            var t = (times[i] - now) / 60.0;
            tSum += t; vSum += v; tvSum += t * v; ttSum += t * t;
            latest = v; latestReason = null;
        }
        if (valid == 0) return new(Key, window, total, 0, stale, failed, 0, null, null, null, null, null, null, null, null, latestReason);
        var mean = sum / valid;
        double variance = 0;
        for (var j = 0; j < valid; j++) variance += (scratch[j] - mean) * (scratch[j] - mean);
        Array.Sort(scratch, 0, valid);
        var denominator = valid * ttSum - tSum * tSum;
        double? slope = valid >= 3 && denominator > 1e-12 ? (valid * tvSum - tSum * vSum) / denominator : null;
        return new(Key, window, total, valid, stale, failed, Math.Min(covered, window), mean, min, max,
            Percentile(valid, 0.5), Percentile(valid, 0.95), Math.Sqrt(variance / valid), latest, slope, latestReason,
            Percentile(valid, 0.25), Percentile(valid, 0.75));
    }

    /// <summary>Share of valid covered time in the window for which <paramref name="predicate"/> holds.</summary>
    public double ShareOfCoveredTime(double now, double window, double maxGap, Func<double, bool> predicate, double nominal = 1)
    {
        var first = FirstInWindow(now, window);
        double covered = 0, matching = 0;
        for (var k = first; k < count; k++)
        {
            var i = Index(k);
            if (statuses[i] != MetricStatus.Available) continue;
            var dt = Interval(k, maxGap, nominal);
            covered += dt;
            if (predicate(values[i])) matching += dt;
        }
        return covered <= 0 ? 0 : matching / covered;
    }

    /// <summary>Consecutive newest valid samples satisfying the predicate; invalid samples end the run.</summary>
    public int TrailingConsecutive(Func<double, bool> predicate)
    {
        var run = 0;
        for (var k = count - 1; k >= 0; k--)
        {
            var i = Index(k);
            if (statuses[i] != MetricStatus.Available || !predicate(values[i])) break;
            run++;
        }
        return run;
    }

    /// <summary>Last minus first valid value in the window (for cumulative counters).</summary>
    public double? Delta(double now, double window)
    {
        double? first = null, last = null;
        for (var k = FirstInWindow(now, window); k < count; k++)
        {
            var i = Index(k);
            if (statuses[i] != MetricStatus.Available) continue;
            first ??= values[i];
            last = values[i];
        }
        return first is null ? null : last - first;
    }

    private double Percentile(int n, double p)
    {
        // Linear interpolation between closest ranks.
        var rank = p * (n - 1);
        var lower = (int)Math.Floor(rank);
        var upper = Math.Min(n - 1, lower + 1);
        return scratch[lower] + (scratch[upper] - scratch[lower]) * (rank - lower);
    }
}

/// <summary>Metric identifiers used by diagnostic rules. Collected once from the existing snapshot; no new collectors.</summary>
public static class DiagnosticMetrics
{
    public const string Rtt = "connection.rtt", PacketLoss = "connection.packetLossUplink", Reconnects = "connection.reconnects",
        JitterRaw = "connection.jitterRaw", EstimatedBandwidthRaw = "connection.estimatedBandwidthRaw",
        UdpBytes = "connection.udpBytes", TcpBytes = "connection.tcpBytes",
        Fps = "graphics.fps", DirtyFps = "graphics.dirtyFps", ImagingOutMbps = "graphics.imagingOutMbps", QueueingUs = "graphics.queueingUs",
        Cpu = "guest.cpu", Ram = "guest.ram", AvailableRamGiB = "guest.availableRamGiB", DiskActive = "guest.diskActive",
        Gpu = "gpu.utilization", Vram = "gpu.vram", Encoder = "gpu.encoder";
}

/// <summary>Bounded per-metric history fed from each snapshot. Only touched under the engine lock.</summary>
public sealed class MetricsHistory
{
    private readonly Dictionary<string, MetricSeries> series = new(StringComparer.Ordinal);
    public int SampleCount { get; private set; }

    public MetricSeries this[string key] => series.TryGetValue(key, out var s) ? s : series[key] = new MetricSeries(key);
    public bool Has(string key) => series.TryGetValue(key, out var s) && s.Count > 0;

    public void Record(SessionSnapshot s, double now)
    {
        SampleCount++;
        Metric? Blast(string key) => s.BlastMetrics is { } b && b.TryGetValue(key, out var m) ? m : null;
        Metric? Host(string id) => s.HostMetrics?.FirstOrDefault(e => e.Id == id)?.Metric;
        this[DiagnosticMetrics.Rtt].Add(now, s.Connection.RoundTripTime);
        this[DiagnosticMetrics.PacketLoss].Add(now, s.Connection.PacketLoss);
        this[DiagnosticMetrics.Fps].Add(now, s.Graphics.FramesPerSecond);
        this[DiagnosticMetrics.ImagingOutMbps].Add(now, s.Connection.BandwidthConsumption);
        this[DiagnosticMetrics.Cpu].Add(now, s.System.CpuUtilization);
        this[DiagnosticMetrics.Ram].Add(now, s.System.RamUtilization);
        this[DiagnosticMetrics.Gpu].Add(now, s.Gpu.Utilization);
        this[DiagnosticMetrics.Vram].Add(now, s.Gpu.VramUtilization);
        this[DiagnosticMetrics.Encoder].Add(now, s.Gpu.EncoderUtilization);
        // Real-session-only sources; absent in Demo Mode, which keeps these series empty rather than zero.
        if (s.BlastMetrics is not null)
        {
            this[DiagnosticMetrics.Reconnects].Add(now, Blast("reconnects"));
            this[DiagnosticMetrics.JitterRaw].Add(now, Blast("jitter"));
            this[DiagnosticMetrics.EstimatedBandwidthRaw].Add(now, Blast("estimatedBandwidth"));
            this[DiagnosticMetrics.DirtyFps].Add(now, Blast("dirtyFps"));
            this[DiagnosticMetrics.QueueingUs].Add(now, Blast("outQueueingTime"));
            this[DiagnosticMetrics.UdpBytes].Add(now, Sum(Blast("udpReceivedBytes"), Blast("udpTransmittedBytes")));
            this[DiagnosticMetrics.TcpBytes].Add(now, Sum(Blast("tcpReceivedBytes"), Blast("tcpTransmittedBytes")));
        }
        if (s.HostMetrics is not null)
        {
            this[DiagnosticMetrics.AvailableRamGiB].Add(now, Host("memory.availableGiB"));
            this[DiagnosticMetrics.DiskActive].Add(now, Host("disk.activePercent"));
        }
    }

    private static Metric? Sum(Metric? a, Metric? b) =>
        a is { Status: MetricStatus.Available, Value: { } x } && b is { Status: MetricStatus.Available, Value: { } y }
            ? new(x + y, "bytes", Source: a.Source, SampleStatus: MetricStatus.Available)
            : a?.Status != MetricStatus.Available ? a : b;

    public void Clear() { foreach (var s in series.Values) s.Clear(); SampleCount = 0; }
}
