using System.Globalization;
using EucLens.Core;

namespace EucLens.Diagnostics;

/// <summary>Thresholds for host rules. A finding needs a full window and a high share of samples over the threshold.</summary>
public sealed record HostDiagnosticThresholds
{
    /// <summary>Samples per window; at the 1 Hz sampling rate 30 samples ≈ 30 seconds.</summary>
    public int WindowSamples { get; init; } = 30;
    /// <summary>Share of available samples that must exceed a threshold, and the minimum share of samples that must be available.</summary>
    public double RequiredFraction { get; init; } = 0.8;
    public double CpuPercent { get; init; } = 90;
    public double MemoryPercent { get; init; } = 90;
    public double GpuPercent { get; init; } = 90;
    public double VramPercent { get; init; } = 90;
    public double EncoderPercent { get; init; } = 85;
    public double LowFps { get; init; } = 20;
}

/// <summary>Bounded history of one metric. Non-available samples are kept as gaps, never as zeros.</summary>
public sealed class SampleWindow(int capacity)
{
    private readonly Queue<double?> values = new(capacity);
    public int Count => values.Count;
    public int Available => values.Count(v => v.HasValue);
    public bool IsFull => values.Count >= capacity;
    public void Add(Metric metric)
    {
        if (values.Count >= capacity) values.Dequeue();
        values.Enqueue(metric.Status == MetricStatus.Available ? metric.Value : null);
    }
    public void Clear() => values.Clear();
    public double ShareAtOrAbove(double threshold) => Available == 0 ? 0 : (double)values.Count(v => v >= threshold) / Available;
    public double ShareBelow(double threshold) => Available == 0 ? 0 : (double)values.Count(v => v < threshold) / Available;
    public double? Average => Available == 0 ? null : values.Where(v => v.HasValue).Average(v => v!.Value);
    public double? Max => Available == 0 ? null : values.Max();
}

public abstract class SustainedHostRule(HostDiagnosticThresholds thresholds) : IDiagnosticRule
{
    protected HostDiagnosticThresholds Thresholds { get; } = thresholds;
    private readonly SampleWindow window = new(thresholds.WindowSamples);
    protected abstract string Id { get; }
    protected abstract string Title { get; }
    protected abstract string MetricName { get; }
    protected abstract double Threshold { get; }
    protected abstract Metric Select(SessionSnapshot snapshot);
    protected abstract (string Cause, string Action) Recommendation { get; }

    public DiagnosticResult Evaluate(SessionSnapshot snapshot)
    {
        var metric = Select(snapshot);
        window.Add(metric);
        var evidence = Evidence(MetricName, metric, window);
        if (metric.Status != MetricStatus.Available)
            return new(Id, Severity.Info, metric.Status, Title, $"{metric.Status}: {MetricName} — {metric.Error ?? metric.UnavailableReason ?? "not supplied"}",
                evidence, "No current measurement.", "Check the Advanced Metrics view for the source and reason.", DiagnosticArea.Host);
        if (!Sufficient(window, Thresholds))
            return new(Id, Severity.Info, MetricStatus.Unavailable, Title,
                $"Collecting evidence: {window.Available}/{Thresholds.WindowSamples} samples. Short spikes are not assessed.",
                evidence, "Insufficient samples.", "Keep monitoring; a finding needs a full window.", DiagnosticArea.Host);
        var share = window.ShareAtOrAbove(Threshold);
        var limit = Threshold.ToString("0.#", CultureInfo.InvariantCulture);
        return share >= Thresholds.RequiredFraction
            ? new(Id, Severity.Warning, MetricStatus.Available, Title,
                $"Sustained: {share:P0} of the last {window.Available} samples are at or above {limit} {metric.Unit}.",
                evidence, Recommendation.Cause, Recommendation.Action, DiagnosticArea.Host)
            : new(Id, Severity.Info, MetricStatus.Available, Title,
                $"Not sustained: {share:P0} of the last {window.Available} samples reach {limit} {metric.Unit} (finding needs {Thresholds.RequiredFraction:P0}).",
                evidence, "No sustained pressure indicated.", "Continue observing trends.", DiagnosticArea.Host);
    }

    public void Reset() => window.Clear();

    internal static bool Sufficient(SampleWindow window, HostDiagnosticThresholds t) =>
        window.IsFull && window.Available >= Math.Ceiling(t.WindowSamples * t.RequiredFraction);

    internal static Dictionary<string, string> Evidence(string name, Metric metric, SampleWindow window) => new()
    {
        [name] = metric.Display,
        ["Window average"] = window.Average is { } avg ? $"{avg.ToString("0.0", CultureInfo.CurrentCulture)} {metric.Unit}" : "N/A",
        ["Window peak"] = window.Max is { } max ? $"{max.ToString("0.0", CultureInfo.CurrentCulture)} {metric.Unit}" : "N/A",
        ["Samples"] = $"{window.Available} available of {window.Count}",
        ["Source"] = metric.Source ?? "N/A",
    };
}

public sealed class SustainedCpuRule(HostDiagnosticThresholds t) : SustainedHostRule(t)
{
    protected override string Id => "host-cpu";
    protected override string Title => "Sustained CPU utilization (machine-wide)";
    protected override string MetricName => "CPU";
    protected override double Threshold => Thresholds.CpuPercent;
    protected override Metric Select(SessionSnapshot s) => s.System.CpuUtilization;
    protected override (string, string) Recommendation => ("CPU saturation slows the desktop and the protocol encoder alike.",
        "Identify the busiest processes (Task Manager › Details) and compare with the vCPU allocation and host CPU ready time.");
}

public sealed class MemoryPressureRule(HostDiagnosticThresholds t) : SustainedHostRule(t)
{
    protected override string Id => "host-memory";
    protected override string Title => "Sustained memory pressure (physical RAM)";
    protected override string MetricName => "RAM";
    protected override double Threshold => Thresholds.MemoryPercent;
    protected override Metric Select(SessionSnapshot s) => s.System.RamUtilization;
    protected override (string, string) Recommendation => ("Little available physical memory leads to paging and stalls.",
        "Check per-process working sets and hard faults/sec; consider more RAM or fewer concurrent applications.");
}

public sealed class SustainedGpuRule(HostDiagnosticThresholds t) : SustainedHostRule(t)
{
    protected override string Id => "host-gpu";
    protected override string Title => "Sustained GPU utilization (busiest engine)";
    protected override string MetricName => "GPU";
    protected override double Threshold => Thresholds.GpuPercent;
    protected override Metric Select(SessionSnapshot s) => s.Gpu.Utilization;
    protected override (string, string) Recommendation => ("The busiest GPU engine is saturated; frames can queue before encoding.",
        "Check which engine is busiest in Advanced Metrics, then correlate with FPS and the vGPU profile/scheduler.");
}

public sealed class GpuMemoryPressureRule(HostDiagnosticThresholds t) : SustainedHostRule(t)
{
    protected override string Id => "host-vram";
    protected override string Title => "Sustained GPU memory pressure (dedicated)";
    protected override string MetricName => "Dedicated GPU memory";
    protected override double Threshold => Thresholds.VramPercent;
    protected override Metric Select(SessionSnapshot s) => s.Gpu.VramUtilization;
    protected override (string, string) Recommendation => ("Dedicated video memory is nearly full; the driver may evict to shared system memory.",
        "Compare shared GPU memory usage in Advanced Metrics; consider a larger framebuffer profile or fewer GPU-heavy applications.");
}

public sealed class EncoderUtilizationRule(HostDiagnosticThresholds t) : SustainedHostRule(t)
{
    protected override string Id => "host-encoder";
    protected override string Title => "Sustained video encoder utilization";
    protected override string MetricName => "Video encoder";
    protected override double Threshold => Thresholds.EncoderPercent;
    protected override Metric Select(SessionSnapshot s) => s.Gpu.EncoderUtilization;
    protected override (string, string) Recommendation => ("The hardware encoder is busy; with several sessions per GPU this can cap frame rate.",
        "Correlate with FPS and the number of sessions sharing the GPU; review resolution, monitor count and frame-rate limits.");
}

/// <summary>Low frame rate is only a finding for interactive graphics, and only together with sustained CPU or GPU pressure.</summary>
public sealed class GraphicsBottleneckRule(HostDiagnosticThresholds thresholds) : IDiagnosticRule
{
    private readonly SampleWindow fps = new(thresholds.WindowSamples), cpu = new(thresholds.WindowSamples), gpu = new(thresholds.WindowSamples);

    public DiagnosticResult Evaluate(SessionSnapshot s)
    {
        fps.Add(s.Graphics.FramesPerSecond); cpu.Add(s.System.CpuUtilization); gpu.Add(s.Gpu.Utilization);
        const string id = "host-graphics-bottleneck", title = "Combined graphics and system bottleneck";
        var evidence = SustainedHostRule.Evidence("FPS", s.Graphics.FramesPerSecond, fps);
        evidence["CPU"] = s.System.CpuUtilization.Display;
        evidence["GPU"] = s.Gpu.Utilization.Display;
        if (s.Workload != WorkloadContext.InteractiveGraphics)
            return new(id, Severity.Info, MetricStatus.Available, title, "Not assessed: low frame rate is expected for idle or office workloads.",
                evidence, "Static content produces few frames.", "Select Interactive graphics when testing graphics workloads.", DiagnosticArea.Host);
        if (!SustainedHostRule.Sufficient(fps, thresholds) || (!SustainedHostRule.Sufficient(cpu, thresholds) && !SustainedHostRule.Sufficient(gpu, thresholds)))
            return new(id, Severity.Info, MetricStatus.Unavailable, title,
                $"Collecting evidence: FPS {fps.Available}, CPU {cpu.Available}, GPU {gpu.Available} of {thresholds.WindowSamples} samples.",
                evidence, "Insufficient samples.", "Keep monitoring; FPS and at least one of CPU/GPU must be measured.", DiagnosticArea.Host);
        var lowFps = fps.ShareBelow(thresholds.LowFps) >= thresholds.RequiredFraction;
        var cpuBound = SustainedHostRule.Sufficient(cpu, thresholds) && cpu.ShareAtOrAbove(thresholds.CpuPercent) >= thresholds.RequiredFraction;
        var gpuBound = SustainedHostRule.Sufficient(gpu, thresholds) && gpu.ShareAtOrAbove(thresholds.GpuPercent) >= thresholds.RequiredFraction;
        if (lowFps && (cpuBound || gpuBound))
            return new(id, Severity.Warning, MetricStatus.Available, title,
                $"Interactive frame rate stays below {thresholds.LowFps} FPS while {(cpuBound && gpuBound ? "CPU and GPU are" : cpuBound ? "CPU is" : "GPU is")} saturated.",
                evidence, cpuBound ? "The VM cannot render or encode frames fast enough (CPU-bound)." : "The GPU cannot render or encode frames fast enough (GPU-bound).",
                "Reduce the load on the saturated resource or size the VM/vGPU profile for the workload, then re-test.", DiagnosticArea.Host);
        return new(id, Severity.Info, MetricStatus.Available, title, "Low frame rate and host saturation do not coincide.",
            evidence, "No combined bottleneck indicated.", "Continue observing trends.", DiagnosticArea.Host);
    }

    public void Reset() { fps.Clear(); cpu.Clear(); gpu.Clear(); }
}
