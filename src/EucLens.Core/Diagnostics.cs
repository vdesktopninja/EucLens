using System.Globalization;

namespace EucLens.Core;

public enum DiagnosticDomain { Connection, Graphics, Guest, Gpu, DataQuality }
public enum HealthState { Healthy, Warning, Critical, InsufficientData }
public enum AssessmentCoverage { Full, Partial, None }
public enum FindingTransition { Detected, Escalated, Downgraded, Recovered, Expired }

public sealed record DiagnosticEvidence(string Metric, string Value, string? Detail = null);

/// <summary>One diagnostic conclusion with the measurements and rule that produced it.</summary>
public sealed record DiagnosticFinding(string RuleId, DiagnosticDomain Domain, Severity Severity, string Title, string Description,
    DateTimeOffset DetectedAt, DateTimeOffset LastConfirmedAt, DateTimeOffset? RecoveredAt, string? RecoveryReason,
    double ObservationSeconds, int ValidSamples, int TotalSamples, IReadOnlyList<DiagnosticEvidence> Evidence,
    string DecisionRule, IReadOnlyList<string> PossibleCauses, IReadOnlyList<string> RecommendedActions, string Limitations)
{
    public bool IsActive => RecoveredAt is null;
}

public sealed record DiagnosticEvent(DateTimeOffset Timestamp, FindingTransition Transition, DiagnosticFinding Finding);

/// <summary>Explainable health of one domain: state, how much of the domain could be evaluated, and why.</summary>
public sealed record DomainAssessment(DiagnosticDomain Domain, HealthState State, AssessmentCoverage Coverage,
    string Evidence, string CoverageNote, double WindowSeconds)
{
    public string StateLabel => State == HealthState.InsufficientData ? "Insufficient Data" : State.ToString();
}

/// <summary>An evidence-based explanation. Correlation is never presented as proven causation.</summary>
public sealed record DiagnosticHypothesis(string Id, string Statement, string Confidence,
    IReadOnlyList<string> SupportingRuleIds, IReadOnlyList<string> VerificationSteps);

/// <summary>Per-sample diagnostic state stored with recordings. Unchanged parts are shared between samples.</summary>
public sealed record DiagnosticSnapshot(IReadOnlyList<DomainAssessment> Assessments, IReadOnlyList<string> ActiveFindings,
    IReadOnlyList<DiagnosticEvent> Events);

/// <summary>
/// Tunable diagnostic thresholds. Defaults are conservative starting points for VDI troubleshooting, not vendor or
/// industry standards. Units are part of each property name. Use <see cref="Validate"/> before applying.
/// </summary>
public sealed record DiagnosticThresholds
{
    // Observation windows (seconds of elapsed monotonic time).
    public double ShortWindowSeconds { get; init; } = 30;
    public double StandardWindowSeconds { get; init; } = 60;
    public double LongWindowSeconds { get; init; } = 120;
    /// <summary>Minimum share of a window covered by valid samples before a rule may conclude anything.</summary>
    public double MinimumCoverageFraction { get; init; } = 0.7;
    /// <summary>Minimum valid samples in a window, independent of coverage.</summary>
    public int MinimumValidSamples { get; init; } = 10;
    /// <summary>A sample never represents more time than this (longer gaps are treated as missing data).</summary>
    public double MaximumSampleGapSeconds { get; init; } = 5;
    /// <summary>Share of covered time a condition must hold to count as sustained.</summary>
    public double SustainedFraction { get; init; } = 0.7;
    /// <summary>Recovery requires values below the warning threshold reduced by this percentage (hysteresis).</summary>
    public double RecoveryMarginPercent { get; init; } = 10;
    public double RecoveryWindowSeconds { get; init; } = 30;
    /// <summary>A finding without usable data for this long expires instead of staying active indefinitely.</summary>
    public double StaleFindingSeconds { get; init; } = 120;

    // Connection (Horizon Blast).
    public double RttWarningMs { get; init; } = 150;
    public double RttCriticalMs { get; init; } = 300;
    /// <summary>RTT interquartile range (P75 − P25); unaffected by isolated spikes, unlike a standard deviation.</summary>
    public double RttInstabilitySpreadMs { get; init; } = 50;
    public double PacketLossWarningPercent { get; init; } = 1;
    public double PacketLossCriticalPercent { get; init; } = 5;
    public int ReconnectWarningCount { get; init; } = 1;

    // Graphics.
    public double LowFps { get; init; } = 15;
    public double ActivityDirtyFps { get; init; } = 5;
    public double FpsInstabilityCoefficient { get; init; } = 0.5;
    public double QueueingWarningMicroseconds { get; init; } = 50_000;

    // VDI guest.
    public double CpuWarningPercent { get; init; } = 90;
    public double CpuCriticalPercent { get; init; } = 98;
    public double MemoryWarningPercent { get; init; } = 90;
    public double MemoryCriticalPercent { get; init; } = 95;
    public double AvailableRamWarningGiB { get; init; } = 1.0;
    public double AvailableRamCriticalGiB { get; init; } = 0.5;
    public double DiskActiveWarningPercent { get; init; } = 90;

    // GPU (only evaluated when a hardware GPU and its metrics are available).
    public double GpuWarningPercent { get; init; } = 90;
    public double GpuCriticalPercent { get; init; } = 98;
    public double VramWarningPercent { get; init; } = 90;
    public double VramCriticalPercent { get; init; } = 97;
    public double EncoderWarningPercent { get; init; } = 85;
    public double EncoderCriticalPercent { get; init; } = 95;

    public static DiagnosticThresholds Defaults { get; } = new();

    /// <summary>Range and consistency checks; an empty list means the configuration is usable.</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        void Range(string name, double value, double min, double max)
        {
            if (!double.IsFinite(value) || value < min || value > max)
                errors.Add(string.Create(CultureInfo.InvariantCulture, $"{name} must be between {min} and {max} (was {value})."));
        }
        void Ordered(string warning, double w, string critical, double c)
        {
            if (w >= c) errors.Add($"{warning} must be lower than {critical}.");
        }
        Range(nameof(ShortWindowSeconds), ShortWindowSeconds, 10, 300);
        Range(nameof(StandardWindowSeconds), StandardWindowSeconds, 20, 600);
        Range(nameof(LongWindowSeconds), LongWindowSeconds, 30, 600);
        if (!(ShortWindowSeconds < StandardWindowSeconds && StandardWindowSeconds < LongWindowSeconds))
            errors.Add("Windows must satisfy ShortWindowSeconds < StandardWindowSeconds < LongWindowSeconds.");
        Range(nameof(MinimumCoverageFraction), MinimumCoverageFraction, 0.3, 1);
        Range(nameof(MinimumValidSamples), MinimumValidSamples, 3, 300);
        Range(nameof(MaximumSampleGapSeconds), MaximumSampleGapSeconds, 1, 60);
        Range(nameof(SustainedFraction), SustainedFraction, 0.5, 1);
        Range(nameof(RecoveryMarginPercent), RecoveryMarginPercent, 0, 50);
        Range(nameof(RecoveryWindowSeconds), RecoveryWindowSeconds, 10, 600);
        Range(nameof(StaleFindingSeconds), StaleFindingSeconds, 30, 3600);
        Range(nameof(RttWarningMs), RttWarningMs, 10, 5000);
        Range(nameof(RttCriticalMs), RttCriticalMs, 10, 10000);
        Ordered(nameof(RttWarningMs), RttWarningMs, nameof(RttCriticalMs), RttCriticalMs);
        Range(nameof(RttInstabilitySpreadMs), RttInstabilitySpreadMs, 5, 2000);
        Range(nameof(PacketLossWarningPercent), PacketLossWarningPercent, 0.01, 50);
        Range(nameof(PacketLossCriticalPercent), PacketLossCriticalPercent, 0.01, 100);
        Ordered(nameof(PacketLossWarningPercent), PacketLossWarningPercent, nameof(PacketLossCriticalPercent), PacketLossCriticalPercent);
        Range(nameof(ReconnectWarningCount), ReconnectWarningCount, 1, 100);
        Range(nameof(LowFps), LowFps, 1, 60);
        Range(nameof(ActivityDirtyFps), ActivityDirtyFps, 0.5, 60);
        Range(nameof(FpsInstabilityCoefficient), FpsInstabilityCoefficient, 0.1, 5);
        Range(nameof(QueueingWarningMicroseconds), QueueingWarningMicroseconds, 1_000, 10_000_000);
        foreach (var (w, wv, c, cv) in new[]
        {
            (nameof(CpuWarningPercent), CpuWarningPercent, nameof(CpuCriticalPercent), CpuCriticalPercent),
            (nameof(MemoryWarningPercent), MemoryWarningPercent, nameof(MemoryCriticalPercent), MemoryCriticalPercent),
            (nameof(GpuWarningPercent), GpuWarningPercent, nameof(GpuCriticalPercent), GpuCriticalPercent),
            (nameof(VramWarningPercent), VramWarningPercent, nameof(VramCriticalPercent), VramCriticalPercent),
            (nameof(EncoderWarningPercent), EncoderWarningPercent, nameof(EncoderCriticalPercent), EncoderCriticalPercent),
        })
        {
            Range(w, wv, 50, 100); Range(c, cv, 50, 100); Ordered(w, wv, c, cv);
        }
        Range(nameof(AvailableRamWarningGiB), AvailableRamWarningGiB, 0.05, 64);
        Range(nameof(AvailableRamCriticalGiB), AvailableRamCriticalGiB, 0.01, 64);
        if (AvailableRamCriticalGiB >= AvailableRamWarningGiB) errors.Add($"{nameof(AvailableRamCriticalGiB)} must be lower than {nameof(AvailableRamWarningGiB)}.");
        Range(nameof(DiskActiveWarningPercent), DiskActiveWarningPercent, 50, 100);
        return errors;
    }
}

/// <summary>Engines that accept validated thresholds at runtime.</summary>
public interface IConfigurableDiagnostics
{
    void Configure(DiagnosticThresholds thresholds);
}
