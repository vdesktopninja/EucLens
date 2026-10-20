using EucLens.Core;

namespace EucLens.Diagnostics;

public sealed class DiagnosticEngine(IEnumerable<IDiagnosticRule> rules) : IDiagnosticEngine
{
    private readonly IDiagnosticRule[] rules = rules.ToArray();
    public DiagnosticReport Evaluate(SessionSnapshot snapshot)
    {
        var results = rules.Select(rule => rule.Evaluate(snapshot)).ToArray();
        var available = results.Where(r => r.Status == MetricStatus.Available).ToArray();
        var health = available.Any(r => r.Severity == Severity.Critical) ? HealthStatus.Critical
            : available.Any(r => r.Severity == Severity.Warning) ? HealthStatus.Warning
            : results.Any(r => r.Status != MetricStatus.Available) || results.Length == 0 ? HealthStatus.Unavailable
            : HealthStatus.Healthy;
        return new(health, results);
    }
}

public abstract class MetricRule : IDiagnosticRule
{
    protected abstract string Id { get; }
    protected abstract string Title { get; }
    protected abstract IReadOnlyDictionary<string, Metric> Metrics(SessionSnapshot snapshot);
    protected abstract (Severity Severity, string Description, string Cause, string Action) Analyze(SessionSnapshot snapshot);
    public DiagnosticResult Evaluate(SessionSnapshot snapshot)
    {
        var metrics = Metrics(snapshot);
        var evidence = metrics.ToDictionary(pair => pair.Key, pair => pair.Value.Display);
        // Stale or failed samples are not evidence; never evaluate thresholds against them.
        var missing = metrics.Where(pair => pair.Value.Status != MetricStatus.Available).ToArray();
        if (missing.Length > 0)
        {
            var status = missing.Any(m => m.Value.Status == MetricStatus.Failed) ? MetricStatus.Failed
                : missing.Any(m => m.Value.Status == MetricStatus.Stale) ? MetricStatus.Stale : MetricStatus.Unavailable;
            return new(Id, Severity.Info, status, Title, $"{status}: " +
                string.Join("; ", missing.Select(m => $"{m.Key}: {m.Value.Error ?? m.Value.UnavailableReason ?? "not supplied"}")),
                evidence, "Insufficient measurements.", "Use a provider that supplies these metrics.");
        }
        var result = Analyze(snapshot);
        return new(Id, result.Severity, MetricStatus.Available, Title, result.Description, evidence, result.Cause, result.Action);
    }
}
public sealed class LatencyRule : MetricRule
{
    protected override string Id => "session-latency";
    protected override string Title => "Session responsiveness";
    protected override IReadOnlyDictionary<string, Metric> Metrics(SessionSnapshot s) => new Dictionary<string, Metric> { ["Session RTT"] = s.Connection.RoundTripTime };
    protected override (Severity, string, string, string) Analyze(SessionSnapshot s) => s.Connection.RoundTripTime.Value switch
    {
        >= 200 => (Severity.Critical, "Session RTT is above the critical rule threshold (200 ms).", "Network delay or transport congestion.", "Compare endpoint/network latency and transport behavior."),
        >= 100 => (Severity.Warning, "Session RTT is above the warning rule threshold (100 ms).", "Network delay or load.", "Check network path and correlate with bandwidth usage."),
        _ => (Severity.Info, "RTT is within the sample rule threshold.", "No latency issue indicated.", "Continue observing trends.")
    };
}
public sealed class FrameRateRule : MetricRule
{
    protected override string Id => "workload-fps";
    protected override string Title => "Workload-aware frame delivery";
    protected override IReadOnlyDictionary<string, Metric> Metrics(SessionSnapshot s) => new Dictionary<string, Metric> { ["FPS"] = s.Graphics.FramesPerSecond, ["Dropped frames"] = s.Graphics.DroppedFramesPercent };
    protected override (Severity, string, string, string) Analyze(SessionSnapshot s)
    {
        if (s.Workload != WorkloadContext.InteractiveGraphics)
            return (Severity.Info, "Low FPS can be expected for idle or office workloads; no FPS alert is raised.", "Content may be static.", "Select the actual workload before interpreting FPS.");
        if (s.Graphics.FramesPerSecond.Value < 20 && s.Graphics.DroppedFramesPercent.Value >= 3)
            return (Severity.Warning, "Interactive graphics has low FPS (<20) and elevated dropped frames (≥3%).", "Encoding, GPU or network pressure.", "Correlate GPU usage and network saturation before changing graphics policy.");
        return (Severity.Info, "Frame delivery does not meet the combined alert criteria.", "No contextual frame-delivery issue indicated.", "Continue observing trends.");
    }
}
public sealed class GpuPressureRule : MetricRule
{
    protected override string Id => "gpu-pressure";
    protected override string Title => "GPU pressure";
    protected override IReadOnlyDictionary<string, Metric> Metrics(SessionSnapshot s) => new Dictionary<string, Metric> { ["GPU"] = s.Gpu.Utilization, ["VRAM"] = s.Gpu.VramUtilization };
    protected override (Severity, string, string, string) Analyze(SessionSnapshot s)
    {
        if (s.Gpu.Utilization.Value >= 95 || s.Gpu.VramUtilization.Value >= 95)
            return (Severity.Critical, "GPU utilization or VRAM is ≥95% in this sample.", "Rendering load or GPU memory pressure.", "Correlate sustained load with frame drops; review workload and GPU sizing.");
        if (s.Gpu.Utilization.Value >= 85 || s.Gpu.VramUtilization.Value >= 85)
            return (Severity.Warning, "GPU utilization or VRAM is ≥85% in this sample.", "Limited GPU headroom.", "Observe sustained usage before resizing resources.");
        return (Severity.Info, "GPU and VRAM have headroom under the sample thresholds.", "No GPU pressure indicated.", "Continue observing trends.");
    }
}
public sealed class BandwidthRule : MetricRule
{
    protected override string Id => "bandwidth-saturation";
    protected override string Title => "Bandwidth headroom";
    protected override IReadOnlyDictionary<string, Metric> Metrics(SessionSnapshot s) => new Dictionary<string, Metric> { ["Available capacity"] = s.Connection.AvailableBandwidth.Value is <= 0 ? Metric.Unavailable("Mbps", "Capacity must be greater than zero to evaluate saturation.") : s.Connection.AvailableBandwidth, ["Consumption"] = s.Connection.BandwidthConsumption };
    protected override (Severity, string, string, string) Analyze(SessionSnapshot s)
    {
        var ratio = s.Connection.BandwidthConsumption.Value / s.Connection.AvailableBandwidth.Value;
        return ratio >= .9
            ? (Severity.Warning, "Consumption is ≥90% of the reported link capacity.", "Bandwidth congestion.", "Correlate latency and investigate competing traffic or graphics demand.")
            : (Severity.Info, "Consumption is below the sample saturation threshold.", "No saturation indicated.", "Continue observing trends.");
    }
}
