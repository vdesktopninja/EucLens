using EucLens.Core;

namespace EucLens.Diagnostics;

/// <summary>
/// Combines active findings into cautious explanations. Wording reflects evidence strength: "suspected",
/// "may be contributing" or "cannot be distinguished". Correlation is never stated as cause.
/// </summary>
public static class HypothesisBuilder
{
    public static IReadOnlyList<DiagnosticHypothesis> Build(DiagnosticContext c, IReadOnlyList<DiagnosticFinding> active, IReadOnlyList<DomainAssessment> assessments)
    {
        var t = c.Thresholds;
        bool Has(string id) => active.Any(f => f.RuleId == id && f.Severity > Severity.Info);
        HealthState State(DiagnosticDomain d) => assessments.FirstOrDefault(a => a.Domain == d)?.State ?? HealthState.InsufficientData;
        var network = Has(DiagnosticRuleIds.Rtt) || Has(DiagnosticRuleIds.PacketLoss) || Has(DiagnosticRuleIds.RttUnstable);
        var cpu = Has(DiagnosticRuleIds.Cpu);
        var memory = Has(DiagnosticRuleIds.Memory);
        var lowFps = Has(DiagnosticRuleIds.LowFps);
        var cpuStats = c.Stats(DiagnosticMetrics.Cpu, t.StandardWindowSeconds);
        var cpuNormal = cpuStats.IsSufficient(t) && cpuStats.P95 < t.CpuWarningPercent;
        var rttStats = c.Stats(DiagnosticMetrics.Rtt, t.StandardWindowSeconds);
        var rttNormal = rttStats.IsSufficient(t) && rttStats.P95 < t.RttWarningMs;
        var list = new List<DiagnosticHypothesis>();

        if (assessments.All(a => a.State == HealthState.InsufficientData))
        {
            list.Add(new("insufficient-data", "Not enough valid measurements yet to assess the session.", "No conclusion",
                [], [$"Keep EUC Lens running for at least {t.StandardWindowSeconds:0} s.", "Check Advanced Metrics for unavailable or failing sources."]));
            return list;
        }
        if (network && cpu)
            list.Add(new("network-or-cpu", "Connection degradation and guest CPU saturation occur together; the available evidence cannot distinguish which is primary.",
                "Multiple plausible causes",
                active.Where(f => f.RuleId is DiagnosticRuleIds.Rtt or DiagnosticRuleIds.PacketLoss or DiagnosticRuleIds.RttUnstable or DiagnosticRuleIds.Cpu).Select(f => f.RuleId).ToArray(),
                ["Reduce guest CPU load briefly and check whether RTT improves (CPU-bound protocol processing).",
                 "Test the network path independently (ping/pathping from the client to the gateway).", "Compare with a recording from a time when the guest was idle."]));
        else if (network)
            list.Add(new("network", "Network-related connection degradation is suspected.",
                cpuNormal ? "Suspected — guest CPU is within thresholds" : "Suspected — guest CPU could not be fully assessed",
                active.Where(f => f.Domain == DiagnosticDomain.Connection && f.Severity > Severity.Info).Select(f => f.RuleId).ToArray(),
                ["Investigate the client → gateway and gateway → agent paths.", "Check whether other users on the same network are affected.", "Compare with earlier recordings from the same location."]));

        if (lowFps && cpu && !network)
            list.Add(new("cpu-graphics", "Guest CPU saturation may be contributing to graphics performance degradation.",
                rttNormal ? "Possible — RTT remains within thresholds" : "Possible",
                [DiagnosticRuleIds.LowFps, DiagnosticRuleIds.Cpu, .. active.Where(f => f.RuleId == DiagnosticRuleIds.CpuBoundGraphics).Select(f => f.RuleId)],
                ["Identify the busiest processes during low-FPS periods.", "Repeat the workload with background applications closed.", "Review the vCPU allocation for this workload."]));
        if (cpu && memory)
            list.Add(new("guest-contention", "Guest CPU and memory pressure coincide; the VM may be undersized for its current workload.", "Possible",
                [DiagnosticRuleIds.Cpu, DiagnosticRuleIds.Memory],
                ["Check whether paging (hard faults/sec) drives CPU use.", "Compare the workload with the VM's vCPU and RAM allocation."]));

        var fps = c.Stats(DiagnosticMetrics.Fps, t.StandardWindowSeconds);
        if (!c.IsActiveWorkload && fps.IsSufficient(t) && fps.P50 < t.LowFps && cpuNormal && (rttNormal || !rttStats.IsSufficient(t)))
            list.Add(new("idle-low-fps", "No graphics performance problem can be inferred from low FPS alone.",
                $"Expected — workload is {c.Workload} and CPU/RTT are normal", [],
                ["Select Interactive graphics or Video playback when testing a graphics workload."]));

        if (c.HardwareGpu == false && State(DiagnosticDomain.Guest) == HealthState.Healthy && State(DiagnosticDomain.Connection) is HealthState.Healthy or HealthState.InsufficientData)
            list.Add(new("software-rendering", "No GPU-related problem detected. Software rendering is in use.", "Supported configuration",
                [DiagnosticRuleIds.SoftwareRendering], ["No action needed; if graphics workloads are slow, watch guest CPU, which carries the rendering load."]));

        if (list.Count == 0 && !active.Any(f => f.Severity > Severity.Info))
            list.Add(new("no-problem", "No measurable performance problem detected in the evaluated metrics.",
                assessments.Any(a => a.Coverage != AssessmentCoverage.Full) ? "Limited coverage — see domain coverage notes" : "Full coverage",
                [], ["Keep monitoring or record a session while reproducing the reported problem."]));
        return list;
    }
}
