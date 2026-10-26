using System.Globalization;

namespace EucLens.Core;

/// <summary>
/// The units a rule interprets and how each converts to the rule's canonical unit. A recorded unit that is not listed (or a raw counter flagged as unverified) is never
/// guessed: the rule reports "insufficient data" for it. Thresholds are written in the canonical unit; evidence is reported in the unit the recording stored.
/// </summary>
public sealed class UnitTable
{
    private readonly Dictionary<string, double> factors;

    public UnitTable(string canonical, params (string Unit, double Factor)[] units)
    {
        Canonical = canonical;
        factors = units.ToDictionary(u => u.Unit, u => u.Factor, StringComparer.OrdinalIgnoreCase);
    }

    public string Canonical { get; }

    /// <summary>Canonical value = recorded value × factor.</summary>
    public bool TryGetFactor(string unit, out double factor)
    {
        factor = 1;
        return !HistoryChannels.IsUnverified(unit) && factors.TryGetValue(unit.Trim(), out factor);
    }

    public static UnitTable Milliseconds { get; } = new("ms", ("ms", 1), ("s", 1000), ("µs", 0.001), ("us", 0.001));
    public static UnitTable Percent { get; } = new("%", ("%", 1));
    public static UnitTable FramesPerSecond { get; } = new("FPS", ("FPS", 1));
    public static UnitTable Megabits { get; } = new("Mbps", ("Mbps", 1), ("Kbps", 0.001), ("Gbps", 1000), ("bps", 1e-6));
    public static UnitTable Megabytes { get; } = new("MB/s", ("MB/s", 1), ("KB/s", 1.0 / 1024), ("GB/s", 1024), ("B/s", 1.0 / (1024 * 1024)));
}

/// <summary>How the thresholds of a rule combine: a fixed value, a deviation from the session's own baseline, both at once, or either of them.</summary>
public enum RuleCriterion { Absolute, Baseline, Both, Either }

/// <summary>
/// A sample deviates from the baseline when it is more than <see cref="Deviations"/> robust standard deviations on the worse side of the median. The scale never drops below
/// <see cref="MinimumScale"/> (canonical unit) or <see cref="RelativeScaleFloor"/> × the median, so a perfectly flat session does not turn noise into anomalies.
/// </summary>
public sealed record BaselineCriterion(double Deviations, double MinimumScale, double RelativeScaleFloor = 0.1);

/// <summary>
/// One explicit, versioned detection rule. Every number a rule uses is here (and in <see cref="AnomalyOptions"/>), none is hidden in the algorithms. A change of any value of a rule
/// is a new <see cref="Version"/>, which is part of every observation it produces.
/// </summary>
public sealed record AnomalyRule
{
    public required string Id { get; init; }
    public int Version { get; init; } = 1;
    public required string ChannelId { get; init; }
    public required HistoryGroup Domain { get; init; }
    public required string Title { get; init; }
    public required UnitTable Units { get; init; }
    /// <summary>Plausible range in the canonical unit. A value outside it is a counter error: it is excluded and counted, never used as evidence.</summary>
    public double MinimumValue { get; init; }
    public double MaximumValue { get; init; } = double.MaxValue;
    public bool HigherIsWorse { get; init; } = true;
    public RuleCriterion Criterion { get; init; } = RuleCriterion.Absolute;
    /// <summary>At or above (below, for lower-is-worse metrics) this value a sample meets the absolute criterion.</summary>
    public double? AbsoluteThreshold { get; init; }
    /// <summary>A sustained observation is critical when at least half of its matching samples reach this value (a short one is a warning when its peak does).</summary>
    public double? CriticalThreshold { get; init; }
    public BaselineCriterion? Baseline { get; init; }
    /// <summary>Time a condition must last (first to last matching sample, plus one sampling step) to be sustained.</summary>
    public TimeSpan SustainedAfter { get; init; } = TimeSpan.FromSeconds(30);
    public int MinimumSamples { get; init; } = 5;
    /// <summary>Matching samples divided by valid samples between the first and last match: guards against calling sparse repeated exceedances sustained.</summary>
    public double MinimumMatchingFraction { get; init; } = 0.5;
    /// <summary>Matching samples at most this far apart (at least two sampling steps) are one observation.</summary>
    public TimeSpan MergeTolerance { get; init; } = TimeSpan.FromSeconds(10);
    /// <summary>When set, conditions shorter than <see cref="SustainedAfter"/> are reported with this kind; when null only sustained conditions are reported.</summary>
    public ObservationKind? ShortKind { get; init; }
    public int ShortMinimumSamples { get; init; } = 1;
    /// <summary>The metric means something only while the screen demonstrably changes (recorded Dirty FPS); without that evidence the rule reports insufficient data.</summary>
    public bool RequiresDisplayActivity { get; init; }
    /// <summary>The metric is a Horizon Blast counter: the rule applies only when the recording shows Horizon Blast evidence.</summary>
    public bool RequiresBlast { get; init; }
    /// <summary>A rule whose observations are activity or context, not a malfunction: never above Info.</summary>
    public bool AlwaysInfo { get; init; }
    /// <summary>Observations that overlap an observation of one of these rules (same metric family) are part of it and are not reported twice.</summary>
    public IReadOnlyList<string> SuppressedBy { get; init; } = [];
    /// <summary>When set, the value is <see cref="ChannelId"/> divided by this metric (both converted with <see cref="Units"/>); the threshold is then a ratio.</summary>
    public string? RatioOfChannelId { get; init; }
    /// <summary>For metrics whose absence is the usual case: what is missing, for the reason text.</summary>
    public string? MissingMetricNote { get; init; }

    public string CanonicalUnit => RatioOfChannelId is null ? Units.Canonical : "ratio";
    public bool UsesBaseline => Baseline is not null && Criterion != RuleCriterion.Absolute;
}

/// <summary>The default rules. Rationale and units are in docs/M1_ANOMALY_ENGINE.md; thresholds for RTT, loss, FPS, CPU, memory and queueing follow the live diagnostic engine's defaults.</summary>
public static class AnomalyRules
{
    private static readonly TimeSpan Thirty = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan Minute = TimeSpan.FromMinutes(1);

    public static IReadOnlyList<AnomalyRule> Default { get; } =
    [
        // ----- Connection -----
        new() { Id = "conn.rtt.high", ChannelId = "rtt", Domain = HistoryGroup.Connection, Title = "Sustained high round-trip time", Units = UnitTable.Milliseconds, MaximumValue = 3_600_000,
            AbsoluteThreshold = 150, CriticalThreshold = 300, SustainedAfter = Thirty, MinimumSamples = 5 },
        new() { Id = "conn.rtt.deviation", ChannelId = "rtt", Domain = HistoryGroup.Connection, Title = "Round-trip time well above this session's normal", Units = UnitTable.Milliseconds, MaximumValue = 3_600_000,
            Criterion = RuleCriterion.Both, AbsoluteThreshold = 100, CriticalThreshold = 300, Baseline = new(6, 5), SustainedAfter = Thirty, MinimumSamples = 5, ShortKind = ObservationKind.Spike,
            MergeTolerance = TimeSpan.FromSeconds(5), SuppressedBy = ["conn.rtt.high"] },
        new() { Id = "conn.loss.sustained", ChannelId = "packetLoss", Domain = HistoryGroup.Connection, Title = "Sustained packet loss", Units = UnitTable.Percent, MaximumValue = 100.5,
            AbsoluteThreshold = 1, CriticalThreshold = 5, SustainedAfter = Thirty, MinimumSamples = 5 },
        new() { Id = "conn.loss.burst", ChannelId = "packetLoss", Domain = HistoryGroup.Connection, Title = "Packet loss burst", Units = UnitTable.Percent, MaximumValue = 100.5,
            AbsoluteThreshold = 3, CriticalThreshold = 10, SustainedAfter = Thirty, MinimumSamples = 5, ShortKind = ObservationKind.Burst, ShortMinimumSamples = 2,
            MergeTolerance = TimeSpan.FromSeconds(5), SuppressedBy = ["conn.loss.sustained"] },
        new() { Id = "conn.queueing.increase", ChannelId = "outQueueing", Domain = HistoryGroup.Connection, Title = "Increased image-delivery queueing", Units = UnitTable.Milliseconds, MaximumValue = 3_600_000,
            Criterion = RuleCriterion.Both, AbsoluteThreshold = 50, CriticalThreshold = 250, Baseline = new(6, 5), SustainedAfter = TimeSpan.FromSeconds(15), MinimumSamples = 5, RequiresBlast = true },
        new() { Id = "conn.bandwidth.pressure", ChannelId = "bandwidthConsumption", RatioOfChannelId = "availableBandwidth", Domain = HistoryGroup.Connection, Title = "Bandwidth consumption close to the available bandwidth",
            Units = UnitTable.Megabits, AbsoluteThreshold = 0.85, CriticalThreshold = 1.0, SustainedAfter = Thirty, MinimumSamples = 5,
            MissingMetricNote = "available bandwidth in a verified unit" },

        // ----- Graphics -----
        new() { Id = "gfx.fps.low", ChannelId = "fps", Domain = HistoryGroup.Graphics, Title = "Low frame rate during display activity", Units = UnitTable.FramesPerSecond, MaximumValue = 1000, HigherIsWorse = false,
            Criterion = RuleCriterion.Either, AbsoluteThreshold = 15, CriticalThreshold = 5, Baseline = new(4, 2), SustainedAfter = Thirty, MinimumSamples = 5, RequiresDisplayActivity = true },
        new() { Id = "gfx.dropped.high", ChannelId = "droppedFrames", Domain = HistoryGroup.Graphics, Title = "Increased dropped frames during display activity", Units = UnitTable.Percent, MaximumValue = 100.5,
            AbsoluteThreshold = 5, CriticalThreshold = 15, SustainedAfter = Thirty, MinimumSamples = 5, RequiresDisplayActivity = true },

        // ----- VDI guest -----
        new() { Id = "guest.cpu.saturation", ChannelId = "cpu", Domain = HistoryGroup.Guest, Title = "Sustained high guest CPU utilization", Units = UnitTable.Percent, MaximumValue = 100.5,
            AbsoluteThreshold = 90, CriticalThreshold = 98, SustainedAfter = Minute, MinimumSamples = 10 },
        new() { Id = "guest.cpu.spike", ChannelId = "cpu", Domain = HistoryGroup.Guest, Title = "Guest CPU utilization well above this session's normal", Units = UnitTable.Percent, MaximumValue = 100.5,
            Criterion = RuleCriterion.Both, AbsoluteThreshold = 90, Baseline = new(6, 5), SustainedAfter = Minute, MinimumSamples = 10, ShortKind = ObservationKind.Spike, ShortMinimumSamples = 1,
            MergeTolerance = TimeSpan.FromSeconds(5), AlwaysInfo = true, SuppressedBy = ["guest.cpu.saturation"] },
        new() { Id = "guest.ram.high", ChannelId = "ram", Domain = HistoryGroup.Guest, Title = "Sustained high guest memory utilization", Units = UnitTable.Percent, MaximumValue = 100.5,
            AbsoluteThreshold = 90, CriticalThreshold = 95, SustainedAfter = TimeSpan.FromMinutes(2), MinimumSamples = 10 },
        new() { Id = "guest.gpu.saturation", ChannelId = "gpu", Domain = HistoryGroup.Guest, Title = "Sustained high guest GPU utilization", Units = UnitTable.Percent, MaximumValue = 100.5,
            AbsoluteThreshold = 90, CriticalThreshold = 98, SustainedAfter = Minute, MinimumSamples = 10 },
        new() { Id = "guest.disk.throughput-spike", ChannelId = "disk", Domain = HistoryGroup.Guest, Title = "Disk throughput well above this session's normal (activity, not a disk problem)", Units = UnitTable.Megabytes, MaximumValue = 1_000_000,
            Criterion = RuleCriterion.Both, AbsoluteThreshold = 100, Baseline = new(8, 5), SustainedAfter = Minute, MinimumSamples = 5, ShortKind = ObservationKind.Spike, ShortMinimumSamples = 1,
            MergeTolerance = TimeSpan.FromSeconds(5), AlwaysInfo = true },
        new() { Id = "guest.disk.stall", ChannelId = "diskLatency", Domain = HistoryGroup.Guest, Title = "Sustained high disk latency", Units = UnitTable.Milliseconds, MaximumValue = 3_600_000,
            AbsoluteThreshold = 100, CriticalThreshold = 500, SustainedAfter = TimeSpan.FromSeconds(15), MinimumSamples = 3,
            MissingMetricNote = "disk latency or queue metric" },
    ];

    public static string Describe(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);
}

/// <summary>
/// Every default of the analyzer that is not part of one rule. See docs/M1_ANOMALY_ENGINE.md for the rationale of each value.
/// </summary>
public sealed record AnomalyOptions
{
    public static AnomalyOptions Default { get; } = new();

    public IReadOnlyList<AnomalyRule> Rules { get; init; } = AnomalyRules.Default;

    // ----- Baseline -----
    /// <summary>A baseline needs at least this many valid samples ...</summary>
    public int MinimumBaselineSamples { get; init; } = 120;
    /// <summary>... spread over at least this much time (a burst of samples in a few seconds is not a session baseline).</summary>
    public TimeSpan MinimumBaselineSpan { get; init; } = TimeSpan.FromMinutes(2);
    /// <summary>The share of the worst values removed before the baseline median and MAD are computed, so anomalies cannot pull their own reference.</summary>
    public double BaselineTrimFraction { get; init; } = 0.10;

    // ----- Display activity -----
    /// <summary>Dirty FPS at or above this (smoothed over <see cref="ActivityWindow"/>) shows the screen is changing; matches the live engine's ActivityDirtyFps.</summary>
    public double ActivityDirtyFps { get; init; } = 5;
    public TimeSpan ActivityWindow { get; init; } = TimeSpan.FromSeconds(10);
    public int MinimumActivitySamples { get; init; } = 3;

    // ----- Coverage -----
    /// <summary>Observations whose valid samples cover less than this share of their interval are reported as qualified.</summary>
    public double QualifiedCoverage { get; init; } = 0.8;
    /// <summary>A recording with less sampled time than this is reported with reduced confidence.</summary>
    public double MinimumRecordingCoverage { get; init; } = 0.8;

    // ----- Correlation -----
    /// <summary>Intervals within this distance of each other count as overlapping (different counters are aggregated differently).</summary>
    public TimeSpan CorrelationTolerance { get; init; } = TimeSpan.FromSeconds(5);
    public int MinimumOverlapSamples { get; init; } = 2;

    // ----- Events -----
    /// <summary>Recorded events within this distance of an observation are attached to it.</summary>
    public TimeSpan EventContext { get; init; } = TimeSpan.FromSeconds(5);
    public int MaximumAssociatedEvents { get; init; } = 10;

    // ----- Limits -----
    /// <summary>The analyzer reads no more samples than the history loader loads.</summary>
    public int MaximumSamples { get; init; } = HistoryLoader.MaximumSamples;
    public int MaximumObservationsPerRule { get; init; } = 100;
    public int MaximumObservations { get; init; } = 500;

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (MinimumBaselineSamples < 10) errors.Add("MinimumBaselineSamples must be at least 10.");
        if (MinimumBaselineSpan < TimeSpan.FromSeconds(10)) errors.Add("MinimumBaselineSpan must be at least 10 seconds.");
        if (!(BaselineTrimFraction is >= 0 and <= 0.5)) errors.Add("BaselineTrimFraction must be between 0 and 0.5.");
        if (!(ActivityDirtyFps > 0)) errors.Add("ActivityDirtyFps must be positive.");
        if (ActivityWindow < TimeSpan.FromSeconds(2)) errors.Add("ActivityWindow must be at least 2 seconds.");
        if (MinimumActivitySamples < 1) errors.Add("MinimumActivitySamples must be at least 1.");
        if (!(QualifiedCoverage is > 0 and <= 1)) errors.Add("QualifiedCoverage must be between 0 and 1.");
        if (!(MinimumRecordingCoverage is > 0 and <= 1)) errors.Add("MinimumRecordingCoverage must be between 0 and 1.");
        if (CorrelationTolerance < TimeSpan.Zero) errors.Add("CorrelationTolerance cannot be negative.");
        if (MinimumOverlapSamples < 1) errors.Add("MinimumOverlapSamples must be at least 1.");
        if (MaximumSamples < 1) errors.Add("MaximumSamples must be positive.");
        if (MaximumObservationsPerRule < 1 || MaximumObservations < 1) errors.Add("The observation limits must be positive.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rule in Rules)
        {
            if (!ids.Add(rule.Id)) errors.Add($"Rule {rule.Id} appears twice.");
            if (rule.Criterion != RuleCriterion.Baseline && rule.AbsoluteThreshold is null) errors.Add($"Rule {rule.Id} needs an absolute threshold.");
            if (rule.Criterion != RuleCriterion.Absolute && rule.Baseline is null) errors.Add($"Rule {rule.Id} needs a baseline criterion.");
            if (rule.SustainedAfter <= TimeSpan.Zero) errors.Add($"Rule {rule.Id}: SustainedAfter must be positive.");
            if (!(rule.MinimumMatchingFraction is > 0 and <= 1)) errors.Add($"Rule {rule.Id}: MinimumMatchingFraction must be between 0 and 1.");
        }
        return errors;
    }
}
