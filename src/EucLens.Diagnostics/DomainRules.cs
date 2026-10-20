using System.Globalization;
using EucLens.Core;

namespace EucLens.Diagnostics;

public static class DiagnosticRuleIds
{
    public const string Rtt = "connection.rtt.sustained", RttUnstable = "connection.rtt.unstable", PacketLoss = "connection.packetloss.uplink",
        Reconnects = "connection.reconnects", LowFps = "graphics.fps.low", FpsUnstable = "graphics.fps.unstable", Queueing = "graphics.queueing",
        CpuBoundGraphics = "graphics.bottleneck.cpu", GpuBoundGraphics = "graphics.bottleneck.gpu", NetworkGraphics = "graphics.bottleneck.network",
        Cpu = "guest.cpu.saturation", Memory = "guest.memory.pressure", Disk = "guest.disk.saturation",
        Gpu = "gpu.utilization", Vram = "gpu.memory.pressure", Encoder = "gpu.encoder", SoftwareRendering = "gpu.software-rendering",
        MeasurementQuality = "dataquality.measurement-failures", BlastUnavailable = "dataquality.blast-unavailable", ExcludedMetrics = "dataquality.excluded-metrics";
}

public static class DiagnosticRuleSet
{
    /// <summary>The default rule set. Add a rule by appending an <see cref="IDomainRule"/>; no other module changes.</summary>
    public static IReadOnlyList<IDomainRule> Default() =>
    [
        new RttRule(), new RttInstabilityRule(), new PacketLossRule(), new ReconnectRule(),
        new LowFpsRule(), new FrameDeliveryInstabilityRule(), new QueueingRule(), new CpuBoundGraphicsRule(), new GpuBoundGraphicsRule(), new NetworkGraphicsRule(),
        new CpuSaturationRule(), new GuestMemoryPressureRule(), new DiskSaturationRule(),
        new GpuUtilizationRule(), new VramPressureRule(), new EncoderRule(), new SoftwareRenderingRule(),
        new MeasurementQualityRule(), new BlastUnavailableRule(), new ExcludedMetricsRule(),
    ];
}

internal static class Fmt
{
    public static string N(double? v, string format = "0.##") => v is { } x ? x.ToString(format, CultureInfo.InvariantCulture) : "N/A";
    public static string S(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);
    public static string Pct(double share) => S($"{share * 100:0}%");

    public static List<DiagnosticEvidence> Window(WindowStats s, string unit) =>
    [
        new("Window mean", $"{N(s.Mean)} {unit}"), new("Window P95", $"{N(s.P95)} {unit}"),
        new("Window min / max", $"{N(s.Min)} / {N(s.Max)} {unit}"), new("Latest", s.Latest is null ? $"N/A ({s.LatestReason})" : $"{N(s.Latest)} {unit}"),
        new("Observation", S($"{s.WindowSeconds:0} s window · {s.ValidSamples} valid of {s.TotalSamples} samples · {s.Coverage:P0} covered"),
            s.StaleSamples + s.FailedSamples > 0 ? S($"{s.StaleSamples} stale, {s.FailedSamples} failed samples excluded") : null),
    ];

    public static string Insufficient(WindowStats s, DiagnosticThresholds t) =>
        S($"{s.ValidSamples} valid samples covering {s.Coverage:P0} of {s.WindowSeconds:0} s (needs ≥ {t.MinimumValidSamples} and ≥ {t.MinimumCoverageFraction:P0}){(s.LatestReason is null ? "" : $"; latest: {s.LatestReason}")}");
}

/// <summary>"Higher is worse" metric with sustained warning/critical thresholds and hysteresis-based recovery.</summary>
public abstract class SustainedThresholdRule : IDomainRule
{
    public abstract string Id { get; }
    public abstract DiagnosticDomain Domain { get; }
    protected abstract string MetricKey { get; }
    protected abstract string MetricName { get; }
    protected abstract string Unit { get; }
    protected abstract string Title { get; }
    protected abstract double Warning(DiagnosticThresholds t);
    protected virtual double? Critical(DiagnosticThresholds t) => null;
    protected virtual double Window(DiagnosticThresholds t) => t.StandardWindowSeconds;
    /// <summary>Null when the rule applies; otherwise the reason it does not.</summary>
    protected virtual string? NotApplicable(DiagnosticContext c) => null;
    protected abstract IReadOnlyList<string> Causes(DiagnosticContext c);
    protected abstract IReadOnlyList<string> Actions(DiagnosticContext c);
    protected abstract string Limitations { get; }
    protected virtual IEnumerable<DiagnosticEvidence> ExtraEvidence(DiagnosticContext c) => [];
    /// <summary>Caps severity for indicators whose counter semantics are not documented.</summary>
    protected virtual Severity MaximumSeverity => Severity.Critical;
    public IReadOnlyList<string> RequiredMetrics => [MetricKey];

    public RuleOutcome Evaluate(DiagnosticContext c)
    {
        if (NotApplicable(c) is { } reason) return RuleOutcome.NotApplicable(reason);
        var t = c.Thresholds;
        var window = Window(t);
        var s = c.Stats(MetricKey, window);
        if (!s.IsSufficient(t)) return RuleOutcome.Insufficient(Fmt.Insufficient(s, t));
        var warning = Warning(t);
        var critical = Critical(t);
        var warnShare = c.Share(MetricKey, window, v => v >= warning);
        var critShare = critical is { } cv ? c.Share(MetricKey, window, v => v >= cv) : 0;
        Severity? severity = critical is not null && critShare >= t.SustainedFraction ? Severity.Critical
            : warnShare >= t.SustainedFraction ? Severity.Warning : null;
        if (severity is { } raised)
        {
            var sev = raised > MaximumSeverity ? MaximumSeverity : raised;
            var share = raised == Severity.Critical ? critShare : warnShare;
            var limit = raised == Severity.Critical ? critical!.Value : warning;
            var evidence = Fmt.Window(s, Unit);
            evidence.Insert(0, new($"Time at or above {Fmt.N(limit)} {Unit}", Fmt.S($"{share * s.CoveredSeconds:0} of {s.CoveredSeconds:0} s ({Fmt.Pct(share)})")));
            evidence.AddRange(ExtraEvidence(c));
            if (c.Snapshot is var snap && Current(snap) is { Source: { } source }) evidence.Add(new("Source", source));
            return RuleOutcome.Raise(sev, new(Title,
                Fmt.S($"{MetricName} was at or above {Fmt.N(limit)} {Unit} for {Fmt.Pct(share)} of the covered {s.CoveredSeconds:0} s (mean {Fmt.N(s.Mean)} {Unit}, P95 {Fmt.N(s.P95)} {Unit})."),
                window, s.ValidSamples, s.TotalSamples, evidence, DecisionRule(t), Causes(c), Actions(c), Limitations));
        }
        var recovery = c.Stats(MetricKey, t.RecoveryWindowSeconds);
        var recoveryLimit = warning * (1 - t.RecoveryMarginPercent / 100);
        return recovery.IsSufficient(t) && recovery.P95 < recoveryLimit
            ? RuleOutcome.Recovered(Fmt.S($"{MetricName} P95 {Fmt.N(recovery.P95)} {Unit} stayed below {Fmt.N(recoveryLimit)} {Unit} for {t.RecoveryWindowSeconds:0} s."))
            : RuleOutcome.Normal;
    }

    protected virtual Metric? Current(SessionSnapshot s) => null;

    public string DecisionRule(DiagnosticThresholds t) => Fmt.S(
        $"Warning when {MetricName} ≥ {Fmt.N(Warning(t))} {Unit}{(Critical(t) is { } c ? $" (Critical ≥ {Fmt.N(c)} {Unit})" : "")} for ≥ {t.SustainedFraction:P0} of valid covered time in a {Window(t):0} s window with ≥ {t.MinimumCoverageFraction:P0} coverage and ≥ {t.MinimumValidSamples} valid samples. Recovers when P95 < {Fmt.N(Warning(t) * (1 - t.RecoveryMarginPercent / 100))} {Unit} over {t.RecoveryWindowSeconds:0} s.");
}

// ---------------- Connection ----------------

public sealed class RttRule : SustainedThresholdRule
{
    public override string Id => DiagnosticRuleIds.Rtt;
    public override DiagnosticDomain Domain => DiagnosticDomain.Connection;
    protected override string MetricKey => DiagnosticMetrics.Rtt;
    protected override string MetricName => "Blast RTT";
    protected override string Unit => "ms";
    protected override string Title => "Elevated Blast round-trip time";
    protected override double Warning(DiagnosticThresholds t) => t.RttWarningMs;
    protected override double? Critical(DiagnosticThresholds t) => t.RttCriticalMs;
    protected override Metric? Current(SessionSnapshot s) => s.Connection.RoundTripTime;
    protected override IReadOnlyList<string> Causes(DiagnosticContext c) =>
        ["Latency on the client → gateway (e.g. Unified Access Gateway) → agent path", "Network congestion or queuing on a WAN, VPN or Wi-Fi segment",
         "Delayed protocol processing when the guest CPU is saturated (check VDI Guest Health)"];
    protected override IReadOnlyList<string> Actions(DiagnosticContext c) =>
        ["Compare with earlier recordings from the same client location.", "Test the client → gateway and gateway → agent paths separately (ping/pathping, gateway statistics).",
         "Check packet loss and congestion at the same time.", "Verify whether the condition persists before changing configuration."];
    protected override string Limitations =>
        "RTT is the round-trip time reported by the Blast session counters. It is not one-way network latency and can include protocol processing time.";
}

public sealed class RttInstabilityRule : IDomainRule
{
    public string Id => DiagnosticRuleIds.RttUnstable;
    public DiagnosticDomain Domain => DiagnosticDomain.Connection;
    public IReadOnlyList<string> RequiredMetrics => [DiagnosticMetrics.Rtt];
    public RuleOutcome Evaluate(DiagnosticContext c)
    {
        var t = c.Thresholds;
        var s = c.Stats(DiagnosticMetrics.Rtt, t.StandardWindowSeconds);
        if (!s.IsSufficient(t)) return RuleOutcome.Insufficient(Fmt.Insufficient(s, t));
        // Interquartile range: persistent variation raises it; spikes in up to 25 % of samples do not.
        if (s.Spread >= t.RttInstabilitySpreadMs)
        {
            var evidence = Fmt.Window(s, "ms");
            evidence.Insert(0, new("RTT P25 → P75 (interquartile range)", $"{Fmt.N(s.P25)} → {Fmt.N(s.P75)} ms ({Fmt.N(s.Spread)} ms)"));
            evidence.Insert(1, new("RTT standard deviation", $"{Fmt.N(s.StdDev)} ms"));
            return RuleOutcome.Raise(Severity.Warning, new("Unstable Blast round-trip time",
                Fmt.S($"The middle 50 % of RTT samples spread over {Fmt.N(s.Spread)} ms ({Fmt.N(s.P25)}–{Fmt.N(s.P75)} ms) during {s.WindowSeconds:0} s. Persistently variable delay can make input feel inconsistent even when the average is acceptable."),
                s.WindowSeconds, s.ValidSamples, s.TotalSamples, evidence,
                Fmt.S($"Warning when the RTT interquartile range (P75 − P25) ≥ {Fmt.N(t.RttInstabilitySpreadMs)} ms over {t.StandardWindowSeconds:0} s with sufficient coverage (spikes in up to 25 % of samples do not move it). Recovers when it stays below {Fmt.N(t.RttInstabilitySpreadMs * (1 - t.RecoveryMarginPercent / 100))} ms over {t.RecoveryWindowSeconds:0} s."),
                ["Bursty congestion or bufferbloat on a shared link", "Wi-Fi interference or roaming on the client side", "Route changes or VPN tunnel instability"],
                ["Check whether packet loss rises at the same time.", "Repeat from a wired client connection to isolate Wi-Fi.", "Record a longer session and compare periods of instability with network events."],
                "Based on Blast RTT only; the jitter counter is not used because its unit is not documented."));
        }
        var r = c.Stats(DiagnosticMetrics.Rtt, t.RecoveryWindowSeconds);
        return r.IsSufficient(t) && r.Spread < t.RttInstabilitySpreadMs * (1 - t.RecoveryMarginPercent / 100)
            ? RuleOutcome.Recovered(Fmt.S($"RTT interquartile range {Fmt.N(r.Spread)} ms over {t.RecoveryWindowSeconds:0} s.")) : RuleOutcome.Normal;
    }
}

public sealed class PacketLossRule : IDomainRule
{
    public string Id => DiagnosticRuleIds.PacketLoss;
    public DiagnosticDomain Domain => DiagnosticDomain.Connection;
    public IReadOnlyList<string> RequiredMetrics => [DiagnosticMetrics.PacketLoss];
    public RuleOutcome Evaluate(DiagnosticContext c)
    {
        var t = c.Thresholds;
        var s = c.Stats(DiagnosticMetrics.PacketLoss, t.StandardWindowSeconds);
        if (!s.IsSufficient(t)) return RuleOutcome.Insufficient(Fmt.Insufficient(s, t));
        // Mean rather than share: loss is bursty, and a sustained average above the threshold is the meaningful signal.
        Severity? severity = s.Mean >= t.PacketLossCriticalPercent ? Severity.Critical : s.Mean >= t.PacketLossWarningPercent ? Severity.Warning : null;
        if (severity is { } sev)
        {
            var withLoss = c.Share(DiagnosticMetrics.PacketLoss, t.StandardWindowSeconds, v => v > 0);
            var evidence = Fmt.Window(s, "%");
            evidence.Insert(0, new("Time with non-zero uplink loss", Fmt.Pct(withLoss)));
            return RuleOutcome.Raise(sev, new("Uplink packet loss",
                Fmt.S($"Blast reported an average uplink packet loss of {Fmt.N(s.Mean)} % over {s.WindowSeconds:0} s (P95 {Fmt.N(s.P95)} %). Loss in the client-to-agent direction can delay input and trigger retransmissions."),
                s.WindowSeconds, s.ValidSamples, s.TotalSamples, evidence,
                Fmt.S($"Warning when mean uplink loss ≥ {Fmt.N(t.PacketLossWarningPercent)} % (Critical ≥ {Fmt.N(t.PacketLossCriticalPercent)} %) over {t.StandardWindowSeconds:0} s with sufficient coverage. Recovers when the mean stays below {Fmt.N(t.PacketLossWarningPercent * (1 - t.RecoveryMarginPercent / 100))} % over {t.RecoveryWindowSeconds:0} s."),
                ["Congested or lossy access network (Wi-Fi, mobile, home broadband)", "Firewall, VPN or gateway dropping UDP under load", "Duplex or interface errors on a network segment"],
                ["Check the client's local network (wired vs Wi-Fi).", "Review interface error counters and gateway statistics on the path.", "Correlate with RTT instability and reconnect events."],
                "The counter is uplink-only (client to agent). Downstream (agent to client) loss is not measured and is not inferred."));
        }
        var r = c.Stats(DiagnosticMetrics.PacketLoss, t.RecoveryWindowSeconds);
        return r.IsSufficient(t) && r.Mean < t.PacketLossWarningPercent * (1 - t.RecoveryMarginPercent / 100)
            ? RuleOutcome.Recovered(Fmt.S($"Mean uplink loss {Fmt.N(r.Mean)} % over {t.RecoveryWindowSeconds:0} s.")) : RuleOutcome.Normal;
    }
}

public sealed class ReconnectRule : IDomainRule
{
    public string Id => DiagnosticRuleIds.Reconnects;
    public DiagnosticDomain Domain => DiagnosticDomain.Connection;
    public IReadOnlyList<string> RequiredMetrics => [DiagnosticMetrics.Reconnects];
    public RuleOutcome Evaluate(DiagnosticContext c)
    {
        var t = c.Thresholds;
        if (!c.History.Has(DiagnosticMetrics.Reconnects)) return RuleOutcome.NotApplicable("Reconnect counter is only available in a real Blast session.");
        var s = c.Stats(DiagnosticMetrics.Reconnects, t.LongWindowSeconds);
        if (s.ValidSamples < 2) return RuleOutcome.Insufficient(Fmt.Insufficient(s, t));
        var delta = c.History[DiagnosticMetrics.Reconnects].Delta(c.Now, t.LongWindowSeconds) ?? 0;
        if (delta >= t.ReconnectWarningCount)
            return RuleOutcome.Raise(Severity.Warning, new("Blast automatic reconnects",
                Fmt.S($"The Blast automatic reconnect count increased by {delta:0} within {t.LongWindowSeconds:0} s (now {Fmt.N(s.Latest)})."),
                t.LongWindowSeconds, s.ValidSamples, s.TotalSamples,
                [new("Increase in window", Fmt.N(delta)), new("Current count", Fmt.N(s.Latest)),
                 new("Observation", Fmt.S($"{t.LongWindowSeconds:0} s window · {s.ValidSamples} valid samples"))],
                Fmt.S($"Warning when the cumulative reconnect counter increases by ≥ {t.ReconnectWarningCount} within {t.LongWindowSeconds:0} s. Recovers when it does not increase for {t.LongWindowSeconds:0} s."),
                ["Short network interruptions (Wi-Fi roaming, VPN renegotiation, ISP outages)", "Gateway or load balancer timeouts", "Client sleep/resume"],
                ["Check the client and gateway logs for disconnect reasons at these times.", "Correlate with packet loss and RTT spikes.", "Check idle and session timeout settings on gateways/load balancers."],
                "Counts reconnects of the current Blast session only."));
        return s.IsSufficient(t) ? RuleOutcome.Recovered(Fmt.S($"No reconnects for {t.LongWindowSeconds:0} s.")) : RuleOutcome.Normal;
    }
}

// ---------------- Graphics ----------------

internal static class Graphics
{
    /// <summary>Low frame rate sustained during a declared active workload. Null when not evaluable.</summary>
    public static (bool? Low, WindowStats Stats, string? Reason) LowFps(DiagnosticContext c)
    {
        var t = c.Thresholds;
        var s = c.Stats(DiagnosticMetrics.Fps, t.StandardWindowSeconds);
        if (!c.IsActiveWorkload) return (null, s, $"Workload is {c.Workload}; low FPS is expected for static content.");
        if (!s.IsSufficient(t)) return (null, s, Fmt.Insufficient(s, t));
        if (c.ScreenActivity == false) return (false, s, Fmt.S($"Dirty FPS below {Fmt.N(t.ActivityDirtyFps)}: the screen is mostly static, so low FPS is expected."));
        return (c.Share(DiagnosticMetrics.Fps, t.StandardWindowSeconds, v => v < t.LowFps) >= t.SustainedFraction, s, null);
    }

    public static string ActivityNote(DiagnosticContext c) => c.ScreenActivity switch
    {
        true => "Screen activity confirmed by Blast dirty frames.",
        false => "No screen activity in Blast dirty frames.",
        _ => "Screen activity could not be verified (no dirty-frame counter); the workload is user-selected, so this conclusion is uncertain.",
    };

    public static bool Sustained(DiagnosticContext c, string key, double threshold)
    {
        var t = c.Thresholds;
        return c.Stats(key, t.StandardWindowSeconds).IsSufficient(t) && c.Share(key, t.StandardWindowSeconds, v => v >= threshold) >= t.SustainedFraction;
    }

    public static bool Recovered(DiagnosticContext c)
    {
        var t = c.Thresholds;
        var r = c.Stats(DiagnosticMetrics.Fps, t.RecoveryWindowSeconds);
        return r.IsSufficient(t) && r.P50 >= t.LowFps * (1 + t.RecoveryMarginPercent / 100);
    }
}

public sealed class LowFpsRule : IDomainRule
{
    public string Id => DiagnosticRuleIds.LowFps;
    public DiagnosticDomain Domain => DiagnosticDomain.Graphics;
    public IReadOnlyList<string> RequiredMetrics => [DiagnosticMetrics.Fps];
    public RuleOutcome Evaluate(DiagnosticContext c)
    {
        var t = c.Thresholds;
        var (low, s, reason) = Graphics.LowFps(c);
        if (!c.IsActiveWorkload) return RuleOutcome.NotApplicable(reason!);
        if (low is null) return RuleOutcome.Insufficient(reason!);
        if (low == false) return Graphics.Recovered(c) || c.ScreenActivity == false ? RuleOutcome.Recovered(reason ?? "Frame rate recovered.") : RuleOutcome.Normal;
        var uncertain = c.ScreenActivity is null;
        var evidence = Fmt.Window(s, "FPS");
        evidence.Insert(0, new($"Time below {Fmt.N(t.LowFps)} FPS", Fmt.Pct(c.Share(DiagnosticMetrics.Fps, t.StandardWindowSeconds, v => v < t.LowFps))));
        evidence.Add(new("Workload (user-selected)", c.Workload.ToString()));
        var dirty = c.Stats(DiagnosticMetrics.DirtyFps, t.StandardWindowSeconds);
        evidence.Add(new("Dirty FPS mean", dirty.Mean is null ? "N/A (counter unavailable)" : $"{Fmt.N(dirty.Mean)} FPS"));
        return RuleOutcome.Raise(Severity.Warning, new(uncertain ? "Low frame rate during declared active workload (activity unverified)" : "Low frame rate during active graphics workload",
            Fmt.S($"Delivered FPS stayed below {Fmt.N(t.LowFps)} for most of {s.WindowSeconds:0} s (P50 {Fmt.N(s.P50)} FPS). {Graphics.ActivityNote(c)}"),
            s.WindowSeconds, s.ValidSamples, s.TotalSamples, evidence,
            Fmt.S($"Warning when the workload is Interactive graphics or Video playback, the screen is changing (dirty FPS ≥ {Fmt.N(t.ActivityDirtyFps)}, or not measurable), and FPS < {Fmt.N(t.LowFps)} for ≥ {t.SustainedFraction:P0} of a {t.StandardWindowSeconds:0} s window. Recovers when the FPS P50 ≥ {Fmt.N(t.LowFps * (1 + t.RecoveryMarginPercent / 100))} over {t.RecoveryWindowSeconds:0} s or the screen becomes static."),
            ["Guest CPU or GPU saturation limiting encoding", "Network constraints causing the encoder to reduce frame rate", "Blast frame-rate policy limits", "Content that changes slowly despite the selected workload"],
            ["Check the graphics bottleneck findings and VDI Guest Health.", "Compare RTT and packet loss in the same window.", "Review Blast policy settings for maximum frame rate.", "Confirm the user was actively working with graphics during this period."],
            "Low FPS alone does not prove a problem; Blast sends fewer frames when content does not change. " + Graphics.ActivityNote(c)));
    }
}

public sealed class FrameDeliveryInstabilityRule : IDomainRule
{
    public string Id => DiagnosticRuleIds.FpsUnstable;
    public DiagnosticDomain Domain => DiagnosticDomain.Graphics;
    public IReadOnlyList<string> RequiredMetrics => [DiagnosticMetrics.Fps, DiagnosticMetrics.DirtyFps];
    public RuleOutcome Evaluate(DiagnosticContext c)
    {
        var t = c.Thresholds;
        if (!c.IsActiveWorkload) return RuleOutcome.NotApplicable("Not an active graphics workload.");
        if (c.ScreenActivity is not true) return c.ScreenActivity is null ? RuleOutcome.Insufficient("Dirty-frame counter unavailable.") : RuleOutcome.Recovered("Screen is static.");
        var fps = c.Stats(DiagnosticMetrics.Fps, t.StandardWindowSeconds);
        var dirty = c.Stats(DiagnosticMetrics.DirtyFps, t.StandardWindowSeconds);
        if (!fps.IsSufficient(t)) return RuleOutcome.Insufficient(Fmt.Insufficient(fps, t));
        // Only unstable when content changes steadily but delivered frames fluctuate strongly.
        if (fps.CoefficientOfVariation >= t.FpsInstabilityCoefficient && dirty.CoefficientOfVariation < 0.25)
            return RuleOutcome.Raise(Severity.Warning, new("Unstable frame delivery",
                Fmt.S($"Delivered FPS fluctuated strongly (coefficient of variation {Fmt.N(fps.CoefficientOfVariation)}) while screen changes stayed steady (dirty FPS variation {Fmt.N(dirty.CoefficientOfVariation)})."),
                fps.WindowSeconds, fps.ValidSamples, fps.TotalSamples,
                [new("FPS mean / std. dev.", $"{Fmt.N(fps.Mean)} / {Fmt.N(fps.StdDev)} FPS"), new("Dirty FPS mean / std. dev.", $"{Fmt.N(dirty.Mean)} / {Fmt.N(dirty.StdDev)} FPS"),
                 new("Observation", Fmt.S($"{fps.WindowSeconds:0} s window · {fps.ValidSamples} valid samples"))],
                Fmt.S($"Warning when FPS coefficient of variation ≥ {Fmt.N(t.FpsInstabilityCoefficient)} while dirty-FPS variation < 0.25 and dirty FPS ≥ {Fmt.N(t.ActivityDirtyFps)}, over {t.StandardWindowSeconds:0} s."),
                ["Intermittent CPU or GPU saturation", "Bursty network conditions forcing frame-rate adaptation"],
                ["Look for CPU/GPU peaks and RTT changes at the same times.", "Record the session and compare FPS with CPU and RTT over time."],
                "Interpretation of dirty frames as screen changes is based on the counter name."));
        var r = c.Stats(DiagnosticMetrics.Fps, t.RecoveryWindowSeconds);
        return r.IsSufficient(t) && r.CoefficientOfVariation < t.FpsInstabilityCoefficient * (1 - t.RecoveryMarginPercent / 100)
            ? RuleOutcome.Recovered("Frame delivery stabilised.") : RuleOutcome.Normal;
    }
}

public sealed class QueueingRule : SustainedThresholdRule
{
    public override string Id => DiagnosticRuleIds.Queueing;
    public override DiagnosticDomain Domain => DiagnosticDomain.Graphics;
    protected override string MetricKey => DiagnosticMetrics.QueueingUs;
    protected override string MetricName => "Imaging out queueing time";
    protected override string Unit => "µs";
    protected override string Title => "High imaging out queueing time (indicator)";
    // Information only until the counter's behaviour is verified on real agents: it never changes Graphics Health.
    protected override Severity MaximumSeverity => Severity.Info;
    protected override double Warning(DiagnosticThresholds t) => t.QueueingWarningMicroseconds;
    protected override string? NotApplicable(DiagnosticContext c) => c.History.Has(MetricKey) ? null : "Counter only available in a real Blast session.";
    protected override Metric? Current(SessionSnapshot s) => s.BlastMetrics?.GetValueOrDefault("outQueueingTime");
    protected override IReadOnlyList<string> Causes(DiagnosticContext c) => ["Outbound imaging data waiting for network capacity", "Encoder output exceeding what the session can send"];
    protected override IReadOnlyList<string> Actions(DiagnosticContext c) => ["Compare with imaging bandwidth, RTT and packet loss in the same window.", "Check for bandwidth limits in Blast policy or on the network path."];
    protected override string Limitations => "Unit (µs) is stated in the counter name; Omnissa does not document whether it is a per-interval or cumulative value, so this is reported as information only and never changes Graphics Health.";
}

public abstract class FrameRateBottleneckRule : IDomainRule
{
    public abstract string Id { get; }
    public DiagnosticDomain Domain => DiagnosticDomain.Graphics;
    public abstract IReadOnlyList<string> RequiredMetrics { get; }
    protected abstract string? NotApplicable(DiagnosticContext c);
    protected abstract bool Pressure(DiagnosticContext c);
    protected abstract bool PressureRecovered(DiagnosticContext c);
    protected abstract FindingContent Content(DiagnosticContext c, WindowStats fps);

    public RuleOutcome Evaluate(DiagnosticContext c)
    {
        if (NotApplicable(c) is { } na) return RuleOutcome.NotApplicable(na);
        var (low, fps, reason) = Graphics.LowFps(c);
        if (!c.IsActiveWorkload) return RuleOutcome.NotApplicable(reason!);
        if (low is null) return RuleOutcome.Insufficient(reason!);
        if (low == true && Pressure(c)) return RuleOutcome.Raise(Severity.Warning, Content(c, fps));
        return Graphics.Recovered(c) || PressureRecovered(c) || low == false && c.ScreenActivity == false
            ? RuleOutcome.Recovered("Low frame rate and resource pressure no longer coincide.") : RuleOutcome.Normal;
    }

    protected static List<DiagnosticEvidence> Evidence(DiagnosticContext c, WindowStats fps, params (string Key, string Name, string Unit)[] metrics)
    {
        var t = c.Thresholds;
        var list = new List<DiagnosticEvidence> { new("FPS P50", $"{Fmt.N(fps.P50)} FPS"), new("Workload (user-selected)", c.Workload.ToString()) };
        foreach (var (key, name, unit) in metrics)
        {
            var s = c.Stats(key, t.StandardWindowSeconds);
            list.Add(new($"{name} mean / P95", $"{Fmt.N(s.Mean)} / {Fmt.N(s.P95)} {unit}"));
        }
        list.Add(new("Observation", Fmt.S($"{t.StandardWindowSeconds:0} s window")));
        return list;
    }
}

public sealed class CpuBoundGraphicsRule : FrameRateBottleneckRule
{
    public override string Id => DiagnosticRuleIds.CpuBoundGraphics;
    public override IReadOnlyList<string> RequiredMetrics => [DiagnosticMetrics.Fps, DiagnosticMetrics.Cpu];
    protected override string? NotApplicable(DiagnosticContext c) => null;
    protected override bool Pressure(DiagnosticContext c) => Graphics.Sustained(c, DiagnosticMetrics.Cpu, c.Thresholds.CpuWarningPercent);
    protected override bool PressureRecovered(DiagnosticContext c)
    {
        var r = c.Stats(DiagnosticMetrics.Cpu, c.Thresholds.RecoveryWindowSeconds);
        return r.IsSufficient(c.Thresholds) && r.P95 < c.Thresholds.CpuWarningPercent * (1 - c.Thresholds.RecoveryMarginPercent / 100);
    }
    protected override FindingContent Content(DiagnosticContext c, WindowStats fps)
    {
        var t = c.Thresholds;
        return new("Guest CPU saturation may be limiting frame rate",
            Fmt.S($"Low FPS during an active workload coincides with sustained guest CPU ≥ {Fmt.N(t.CpuWarningPercent)} %. Blast software encoding uses guest CPU, so CPU saturation may be contributing. {Graphics.ActivityNote(c)}"),
            t.StandardWindowSeconds, fps.ValidSamples, fps.TotalSamples,
            Evidence(c, fps, (DiagnosticMetrics.Cpu, "Guest CPU", "%"), (DiagnosticMetrics.Rtt, "Blast RTT", "ms")),
            Fmt.S($"Low-FPS condition (see graphics.fps.low) and CPU ≥ {Fmt.N(t.CpuWarningPercent)} % for ≥ {t.SustainedFraction:P0} of {t.StandardWindowSeconds:0} s."),
            ["Applications in the guest consuming CPU", "Software (CPU) encoding of a demanding workload", "Too few vCPUs for the workload"],
            ["Identify the busiest processes (Task Manager › Details).", "Compare with a test using fewer applications or a lower resolution.", "Review the vCPU allocation; check hypervisor CPU ready time separately."],
            "Correlation, not proof: guest CPU and FPS are measured independently. Hypervisor contention is not visible from inside the guest.");
    }
}

public sealed class GpuBoundGraphicsRule : FrameRateBottleneckRule
{
    public override string Id => DiagnosticRuleIds.GpuBoundGraphics;
    public override IReadOnlyList<string> RequiredMetrics => [DiagnosticMetrics.Fps, DiagnosticMetrics.Gpu];
    protected override string? NotApplicable(DiagnosticContext c) => c.HardwareGpu == true ? null : "No hardware GPU: GPU bottleneck analysis does not apply.";
    protected override bool Pressure(DiagnosticContext c) => Graphics.Sustained(c, DiagnosticMetrics.Gpu, c.Thresholds.GpuWarningPercent);
    protected override bool PressureRecovered(DiagnosticContext c)
    {
        var r = c.Stats(DiagnosticMetrics.Gpu, c.Thresholds.RecoveryWindowSeconds);
        return r.IsSufficient(c.Thresholds) && r.P95 < c.Thresholds.GpuWarningPercent * (1 - c.Thresholds.RecoveryMarginPercent / 100);
    }
    protected override FindingContent Content(DiagnosticContext c, WindowStats fps)
    {
        var t = c.Thresholds;
        return new("GPU saturation may be limiting frame rate",
            Fmt.S($"Low FPS during an active workload coincides with sustained GPU utilization ≥ {Fmt.N(t.GpuWarningPercent)} % (busiest engine). {Graphics.ActivityNote(c)}"),
            t.StandardWindowSeconds, fps.ValidSamples, fps.TotalSamples,
            Evidence(c, fps, (DiagnosticMetrics.Gpu, "GPU", "%"), (DiagnosticMetrics.Encoder, "Video encoder", "%")),
            Fmt.S($"Low-FPS condition and GPU ≥ {Fmt.N(t.GpuWarningPercent)} % for ≥ {t.SustainedFraction:P0} of {t.StandardWindowSeconds:0} s."),
            ["Rendering load exceeding the GPU or vGPU profile", "vGPU scheduler time-slicing with other VMs"],
            ["Check which engine is busiest in Advanced Metrics.", "Compare with the vGPU profile and scheduler policy.", "Test with reduced resolution or monitor count."],
            "Correlation, not proof. Adapter utilization is machine-wide.");
    }
}

public sealed class NetworkGraphicsRule : FrameRateBottleneckRule
{
    public override string Id => DiagnosticRuleIds.NetworkGraphics;
    public override IReadOnlyList<string> RequiredMetrics => [DiagnosticMetrics.Fps, DiagnosticMetrics.Rtt];
    protected override string? NotApplicable(DiagnosticContext c) => null;
    protected override bool Pressure(DiagnosticContext c)
    {
        var t = c.Thresholds;
        var loss = c.Stats(DiagnosticMetrics.PacketLoss, t.StandardWindowSeconds);
        return Graphics.Sustained(c, DiagnosticMetrics.Rtt, t.RttWarningMs) || loss.IsSufficient(t) && loss.Mean >= t.PacketLossWarningPercent;
    }
    protected override bool PressureRecovered(DiagnosticContext c)
    {
        var t = c.Thresholds;
        var r = c.Stats(DiagnosticMetrics.Rtt, t.RecoveryWindowSeconds);
        var l = c.Stats(DiagnosticMetrics.PacketLoss, t.RecoveryWindowSeconds);
        return r.IsSufficient(t) && r.P95 < t.RttWarningMs * (1 - t.RecoveryMarginPercent / 100) && (!l.IsSufficient(t) || l.Mean < t.PacketLossWarningPercent);
    }
    protected override FindingContent Content(DiagnosticContext c, WindowStats fps)
    {
        var t = c.Thresholds;
        return new("Network conditions may be limiting frame rate",
            Fmt.S($"Low FPS during an active workload coincides with elevated RTT or uplink packet loss. Blast adapts frame rate to network conditions, so the network may be contributing. {Graphics.ActivityNote(c)}"),
            t.StandardWindowSeconds, fps.ValidSamples, fps.TotalSamples,
            Evidence(c, fps, (DiagnosticMetrics.Rtt, "Blast RTT", "ms"), (DiagnosticMetrics.PacketLoss, "Uplink packet loss", "%"), (DiagnosticMetrics.ImagingOutMbps, "Imaging outbound", "Mbps")),
            Fmt.S($"Low-FPS condition and (RTT ≥ {Fmt.N(t.RttWarningMs)} ms sustained or mean uplink loss ≥ {Fmt.N(t.PacketLossWarningPercent)} %) over {t.StandardWindowSeconds:0} s."),
            ["Congested or lossy network path", "Bandwidth limits on the path or in Blast policy"],
            ["Investigate the connection findings first.", "Compare imaging bandwidth with known link capacity.", "Repeat the test from a different network."],
            "Correlation, not proof. Bandwidth saturation cannot be confirmed without a verified capacity metric.");
    }
}

// ---------------- VDI guest ----------------

public sealed class CpuSaturationRule : SustainedThresholdRule
{
    public override string Id => DiagnosticRuleIds.Cpu;
    public override DiagnosticDomain Domain => DiagnosticDomain.Guest;
    protected override string MetricKey => DiagnosticMetrics.Cpu;
    protected override string MetricName => "Guest CPU utilization";
    protected override string Unit => "%";
    protected override string Title => "Sustained guest CPU saturation";
    protected override double Warning(DiagnosticThresholds t) => t.CpuWarningPercent;
    protected override double? Critical(DiagnosticThresholds t) => t.CpuCriticalPercent;
    protected override Metric? Current(SessionSnapshot s) => s.System.CpuUtilization;
    protected override IEnumerable<DiagnosticEvidence> ExtraEvidence(DiagnosticContext c)
    {
        if (c.LogicalProcessors is { } n)
            yield return new("Logical processors", n.ToString(CultureInfo.InvariantCulture),
                Fmt.S($"One fully busy thread is about {100.0 / n:0} % of total CPU on this VM."));
    }
    protected override IReadOnlyList<string> Causes(DiagnosticContext c) =>
        [c.LogicalProcessors is <= 2 ? "Few vCPUs: background tasks and software encoding compete for the same processors" : "Applications or background tasks consuming CPU",
         "Software rendering and Blast software encoding of a demanding workload", "Antivirus, indexing or update activity"];
    protected override IReadOnlyList<string> Actions(DiagnosticContext c) =>
        ["Identify the busiest processes (Task Manager › Details, sorted by CPU).", "Check scheduled scans, updates and indexing during the period.",
         "Review the vCPU allocation for this workload.", "Check hypervisor CPU ready/contention separately; it is not visible from inside the guest."];
    protected override string Limitations => "Machine-wide guest CPU from GetSystemTimes. It does not show hypervisor CPU contention or which process is responsible.";
}

public sealed class GuestMemoryPressureRule : IDomainRule
{
    public string Id => DiagnosticRuleIds.Memory;
    public DiagnosticDomain Domain => DiagnosticDomain.Guest;
    public IReadOnlyList<string> RequiredMetrics => [DiagnosticMetrics.Ram];
    public RuleOutcome Evaluate(DiagnosticContext c)
    {
        var t = c.Thresholds;
        var w = t.StandardWindowSeconds;
        var ram = c.Stats(DiagnosticMetrics.Ram, w);
        if (!ram.IsSufficient(t)) return RuleOutcome.Insufficient(Fmt.Insufficient(ram, t));
        var avail = c.Stats(DiagnosticMetrics.AvailableRamGiB, w);
        double Share(string key, Func<double, bool> p) => c.Share(key, w, p);
        Severity? byPercent = Share(DiagnosticMetrics.Ram, v => v >= t.MemoryCriticalPercent) >= t.SustainedFraction ? Severity.Critical
            : Share(DiagnosticMetrics.Ram, v => v >= t.MemoryWarningPercent) >= t.SustainedFraction ? Severity.Warning : null;
        Severity? byAvailable = !avail.IsSufficient(t) ? null
            : Share(DiagnosticMetrics.AvailableRamGiB, v => v <= t.AvailableRamCriticalGiB) >= t.SustainedFraction ? Severity.Critical
            : Share(DiagnosticMetrics.AvailableRamGiB, v => v <= t.AvailableRamWarningGiB) >= t.SustainedFraction ? Severity.Warning : null;
        var severity = byPercent > byAvailable || byAvailable is null ? byPercent : byAvailable;
        if (severity is { } sev)
        {
            var evidence = Fmt.Window(ram, "%");
            if (avail.Mean is not null) evidence.Insert(0, new("Available RAM mean / min", $"{Fmt.N(avail.Mean)} / {Fmt.N(avail.Min)} GiB"));
            return RuleOutcome.Raise(sev, new("Sustained guest memory pressure",
                Fmt.S($"Physical RAM use averaged {Fmt.N(ram.Mean)} % over {w:0} s{(avail.Mean is null ? "" : $" with {Fmt.N(avail.Mean)} GiB available on average")}. Low available memory leads to paging and stalls."),
                w, ram.ValidSamples, ram.TotalSamples, evidence,
                Fmt.S($"Warning when RAM ≥ {Fmt.N(t.MemoryWarningPercent)} % or available ≤ {Fmt.N(t.AvailableRamWarningGiB)} GiB; Critical at ≥ {Fmt.N(t.MemoryCriticalPercent)} % or ≤ {Fmt.N(t.AvailableRamCriticalGiB)} GiB; each for ≥ {t.SustainedFraction:P0} of {w:0} s. Recovers when RAM P95 < {Fmt.N(t.MemoryWarningPercent * (1 - t.RecoveryMarginPercent / 100))} % over {t.RecoveryWindowSeconds:0} s and available RAM is above the warning level."),
                ["Applications with large working sets (browsers, Teams, Office)", "Too little RAM assigned for the workload", "A memory leak in a long-running process"],
                ["Check per-process memory (Task Manager › Details, Memory column).", "Check hard faults/sec in Resource Monitor for paging.", "Compare RAM assignment with the workload; review memory reservations on the hypervisor separately."],
                "Physical memory from GlobalMemoryStatusEx; commit charge and hypervisor ballooning are not measured."));
        }
        var r = c.Stats(DiagnosticMetrics.Ram, t.RecoveryWindowSeconds);
        var ra = c.Stats(DiagnosticMetrics.AvailableRamGiB, t.RecoveryWindowSeconds);
        return r.IsSufficient(t) && r.P95 < t.MemoryWarningPercent * (1 - t.RecoveryMarginPercent / 100) && (!ra.IsSufficient(t) || ra.Min > t.AvailableRamWarningGiB)
            ? RuleOutcome.Recovered("Memory use back below the recovery level.") : RuleOutcome.Normal;
    }
}

public sealed class DiskSaturationRule : SustainedThresholdRule
{
    public override string Id => DiagnosticRuleIds.Disk;
    public override DiagnosticDomain Domain => DiagnosticDomain.Guest;
    protected override string MetricKey => DiagnosticMetrics.DiskActive;
    protected override string MetricName => "Disk active time";
    protected override string Unit => "%";
    protected override string Title => "Sustained disk saturation";
    protected override double Warning(DiagnosticThresholds t) => t.DiskActiveWarningPercent;
    protected override string? NotApplicable(DiagnosticContext c) => c.History.Has(MetricKey) ? null : "Disk active time is only measured in Real Session mode.";
    protected override IReadOnlyList<string> Causes(DiagnosticContext c) => ["Heavy I/O from applications, updates, antivirus scans or paging", "Storage latency on the underlying datastore"];
    protected override IReadOnlyList<string> Actions(DiagnosticContext c) => ["Check per-process disk activity in Resource Monitor.", "Check whether memory pressure is causing paging.", "Review datastore latency on the hypervisor separately."];
    protected override string Limitations => "Uses 100 − % Idle Time averaged across physical disks. Throughput alone is never used to diagnose a disk problem.";
}

// ---------------- GPU ----------------

public abstract class HardwareGpuRule : SustainedThresholdRule
{
    public override DiagnosticDomain Domain => DiagnosticDomain.Gpu;
    protected override string? NotApplicable(DiagnosticContext c) => c.HardwareGpu switch
    {
        false => "Software rendering: no hardware GPU to evaluate.",
        null => "GPU inventory unknown.",
        _ => null
    };
    protected override string Limitations => "Adapter-level, machine-wide values from Windows GPU counters (or NVML when Windows provides none). Missing counters are never treated as 0 %.";
}

public sealed class GpuUtilizationRule : HardwareGpuRule
{
    public override string Id => DiagnosticRuleIds.Gpu;
    protected override string MetricKey => DiagnosticMetrics.Gpu;
    protected override string MetricName => "GPU utilization (busiest engine)";
    protected override string Unit => "%";
    protected override string Title => "Sustained GPU utilization near capacity";
    protected override double Warning(DiagnosticThresholds t) => t.GpuWarningPercent;
    protected override double? Critical(DiagnosticThresholds t) => t.GpuCriticalPercent;
    protected override Metric? Current(SessionSnapshot s) => s.Gpu.Utilization;
    protected override IReadOnlyList<string> Causes(DiagnosticContext c) => ["Rendering load above the GPU or vGPU profile capacity", "vGPU time-slicing with other VMs"];
    protected override IReadOnlyList<string> Actions(DiagnosticContext c) => ["Check the busiest engine in Advanced Metrics.", "Compare with the vGPU profile and scheduler policy.", "Correlate with FPS during active workloads."];
}

public sealed class VramPressureRule : HardwareGpuRule
{
    public override string Id => DiagnosticRuleIds.Vram;
    protected override string MetricKey => DiagnosticMetrics.Vram;
    protected override string MetricName => "Dedicated GPU memory use";
    protected override string Unit => "%";
    protected override string Title => "GPU memory pressure";
    protected override double Warning(DiagnosticThresholds t) => t.VramWarningPercent;
    protected override double? Critical(DiagnosticThresholds t) => t.VramCriticalPercent;
    protected override Metric? Current(SessionSnapshot s) => s.Gpu.VramUtilization;
    protected override IReadOnlyList<string> Causes(DiagnosticContext c) => ["Applications using more video memory than the profile provides", "Many or high-resolution monitors"];
    protected override IReadOnlyList<string> Actions(DiagnosticContext c) => ["Compare shared GPU memory use in Advanced Metrics (eviction).", "Consider a larger framebuffer profile or fewer GPU-heavy applications."];
}

public sealed class EncoderRule : HardwareGpuRule
{
    public override string Id => DiagnosticRuleIds.Encoder;
    protected override string MetricKey => DiagnosticMetrics.Encoder;
    protected override string MetricName => "Video encoder utilization";
    protected override string Unit => "%";
    protected override string Title => "Hardware video encoder near capacity";
    protected override double Warning(DiagnosticThresholds t) => t.EncoderWarningPercent;
    protected override double? Critical(DiagnosticThresholds t) => t.EncoderCriticalPercent;
    protected override Metric? Current(SessionSnapshot s) => s.Gpu.EncoderUtilization;
    protected override IReadOnlyList<string> Causes(DiagnosticContext c) => ["Several sessions sharing the hardware encoder", "High resolution, many monitors or high frame rates"];
    protected override IReadOnlyList<string> Actions(DiagnosticContext c) => ["Correlate with FPS and the number of sessions per GPU.", "Review resolution, monitor count and frame-rate limits."];
}

public sealed class SoftwareRenderingRule : IDomainRule
{
    public string Id => DiagnosticRuleIds.SoftwareRendering;
    public DiagnosticDomain Domain => DiagnosticDomain.Gpu;
    public IReadOnlyList<string> RequiredMetrics => [];
    public RuleOutcome Evaluate(DiagnosticContext c) => c.HardwareGpu switch
    {
        false => RuleOutcome.Raise(Severity.Info, new("Software rendering in use (no hardware GPU)",
            "No hardware GPU was detected. Rendering and Blast encoding run on the guest CPU. This is a supported configuration and is not a problem by itself; GPU performance analysis is not applicable.",
            0, 0, 0, [new("Graphics acceleration", "Software rendering"), new("Hardware GPU", "Not detected"), new("Adapter", c.Snapshot.Gpu.AdapterKind ?? "N/A")],
            "Information when DXGI enumeration succeeds and finds no hardware GPU.",
            ["VM configured without a GPU or vGPU (intentional for many desktops)"],
            ["No action needed unless graphics workloads are slow; then review guest CPU findings, which carry the rendering load."],
            "Based on DXGI adapter enumeration inside the guest.")),
        true => RuleOutcome.Recovered("Hardware GPU detected."),
        _ => RuleOutcome.NotApplicable("GPU inventory unknown."),
    };
}

// ---------------- Data quality ----------------

public sealed class MeasurementQualityRule : IDomainRule
{
    private static readonly (string Key, string Name)[] Core =
        [(DiagnosticMetrics.Rtt, "Blast RTT"), (DiagnosticMetrics.Fps, "FPS"), (DiagnosticMetrics.Cpu, "CPU"), (DiagnosticMetrics.Ram, "RAM")];
    public string Id => DiagnosticRuleIds.MeasurementQuality;
    public DiagnosticDomain Domain => DiagnosticDomain.DataQuality;
    public IReadOnlyList<string> RequiredMetrics => Core.Select(m => m.Key).ToArray();
    public RuleOutcome Evaluate(DiagnosticContext c)
    {
        var t = c.Thresholds;
        var stats = Core.Select(m => (m.Name, S: c.Stats(m.Key, t.ShortWindowSeconds))).ToArray();
        if (stats.All(x => x.S.TotalSamples < 5)) return RuleOutcome.Insufficient("Fewer than 5 samples.");
        var bad = stats.Where(x => x.S.TotalSamples >= 5 && x.S.StaleSamples + x.S.FailedSamples >= x.S.TotalSamples / 2.0).ToArray();
        if (bad.Length > 0)
            return RuleOutcome.Raise(Severity.Warning, new("Measurements failing or stale",
                $"{string.Join(", ", bad.Select(b => b.Name))}: at least half of the recent samples were stale or failed, so dependent assessments are limited.",
                t.ShortWindowSeconds, 0, bad.Sum(b => b.S.TotalSamples),
                bad.Select(b => new DiagnosticEvidence(b.Name, Fmt.S($"{b.S.StaleSamples} stale, {b.S.FailedSamples} failed of {b.S.TotalSamples}"), b.S.LatestReason)).ToArray(),
                Fmt.S($"Warning when ≥ 50 % of a core metric's samples in {t.ShortWindowSeconds:0} s are stale or failed."),
                ["Counter provider stopped updating", "Access denied to performance counters", "Session disconnected or reconnecting"],
                ["Open Advanced Metrics to see the source and error of each metric.", "Check that the user can read performance counters (Performance Monitor Users)."],
                "Stale and failed samples are excluded from all thresholds."));
        return RuleOutcome.Recovered("Recent samples are valid.");
    }
}

public sealed class BlastUnavailableRule : IDomainRule
{
    public string Id => DiagnosticRuleIds.BlastUnavailable;
    public DiagnosticDomain Domain => DiagnosticDomain.DataQuality;
    public IReadOnlyList<string> RequiredMetrics => [DiagnosticMetrics.Rtt];
    public RuleOutcome Evaluate(DiagnosticContext c)
    {
        if (c.Snapshot.Session.IsDemo) return RuleOutcome.NotApplicable("Demo Mode.");
        var s = c.Stats(DiagnosticMetrics.Rtt, c.Thresholds.ShortWindowSeconds);
        if (s.TotalSamples < 3) return RuleOutcome.Insufficient("Fewer than 3 samples.");
        if (s.ValidSamples == 0)
            return RuleOutcome.Raise(Severity.Info, new("Horizon Blast metrics unavailable",
                $"No Blast counters were available for this session, so Connection Health cannot be assessed. {c.Snapshot.Session.ProtocolDetection}",
                c.Thresholds.ShortWindowSeconds, 0, s.TotalSamples, [new("Latest reason", s.LatestReason ?? "N/A")],
                "Information when no valid Blast RTT sample exists in the short window.",
                ["Session uses another protocol (PCoIP, RDP, HDX)", "Horizon Agent not installed or counters not registered"],
                ["Check the Session card for the detected protocol.", "See docs/HORIZON_BLAST.md troubleshooting."],
                "Guest and graphics assessments continue with the metrics that are available."));
        return RuleOutcome.Recovered("Blast metrics available.");
    }
}

public sealed class ExcludedMetricsRule : IDomainRule
{
    public string Id => DiagnosticRuleIds.ExcludedMetrics;
    public DiagnosticDomain Domain => DiagnosticDomain.DataQuality;
    public IReadOnlyList<string> RequiredMetrics => [];
    public RuleOutcome Evaluate(DiagnosticContext c)
    {
        var t = c.Thresholds;
        var jitter = c.Stats(DiagnosticMetrics.JitterRaw, t.StandardWindowSeconds);
        var bandwidth = c.Stats(DiagnosticMetrics.EstimatedBandwidthRaw, t.StandardWindowSeconds);
        if (jitter.ValidSamples == 0 && bandwidth.ValidSamples == 0) return RuleOutcome.NotApplicable("No Blast jitter or bandwidth samples.");
        var udp = c.History.Has(DiagnosticMetrics.UdpBytes) ? c.History[DiagnosticMetrics.UdpBytes].Delta(c.Now, t.StandardWindowSeconds) : null;
        var tcp = c.History.Has(DiagnosticMetrics.TcpBytes) ? c.History[DiagnosticMetrics.TcpBytes].Delta(c.Now, t.StandardWindowSeconds) : null;
        return RuleOutcome.Raise(Severity.Info, new("Some connection metrics are shown but not evaluated",
            "Jitter (Uplink) and Estimated Bandwidth (Uplink) have no documented units, so they are shown as raw evidence only. Bandwidth saturation is not assessed because no verified link-capacity metric exists. UDP/TCP byte counts show where traffic flowed, not which transport was negotiated.",
            t.StandardWindowSeconds, jitter.ValidSamples, jitter.TotalSamples,
            [new("Jitter (raw) mean", Fmt.N(jitter.Mean), "unit unverified"), new("Estimated bandwidth (raw) mean", Fmt.N(bandwidth.Mean), "unit unverified"),
             new("UDP bytes in window", Fmt.N(udp, "#,0")), new("TCP bytes in window", Fmt.N(tcp, "#,0"))],
            "Information while unverified-unit metrics are present.", ["Units are not documented by Omnissa for these counters"],
            ["Confirm units with PerfMon 'Show description' on your agent build before tuning any rule on them."],
            "Excluded from all unit-dependent thresholds."));
    }
}
