namespace EucLens.Core;

/// <summary>
/// What a statement of the analyzer means. <see cref="ConfirmedObservation"/> says only that a measured condition is supported by recorded values with enough coverage;
/// it is never a root cause. <see cref="QualifiedObservation"/> is the same measured condition with a stated reduction in confidence (for example many missing samples
/// inside the interval). <see cref="InsufficientData"/> is a rule that could not be evaluated: it produces no observation.
/// </summary>
public enum AnalysisStatus { ConfirmedObservation, QualifiedObservation, InsufficientData }

/// <summary><see cref="Intermittent"/>: repeated exceedances close enough to be grouped, longer than a spike, but too sparse to call sustained.</summary>
public enum ObservationKind { Sustained, Spike, Burst, Intermittent }

/// <summary>
/// How an observation ended. <see cref="Recovered"/> only when later valid samples followed without any gap in between. When monitoring lost coverage
/// (<see cref="CoverageEnded"/>: a gap, missing values, a disconnect, the end of the recording) nothing is known about what happened next. <see cref="ContextEnded"/>:
/// the display activity that made the measurement meaningful stopped, which is not a recovery either.
/// </summary>
public enum ObservationEnd { Recovered, CoverageEnded, ContextEnded }

/// <summary>The kind of criterion that decided a sample: a fixed threshold, a deviation from the session's own baseline, or both together.</summary>
public enum ThresholdBasis { Absolute, BaselineDeviation, AbsoluteAndBaseline }

/// <summary>The measurements of one metric inside an observation interval, in the unit the recording stored. Percentiles use the history's own definition. <see cref="WorstAt"/> is when the most extreme value in the direction that matters for the metric (highest, or lowest for frame rate) was recorded.</summary>
public sealed record MetricEvidence(string ChannelId, string Title, string Unit, int ValidSamples, double Minimum, double P50, double P95, double Maximum, DateTimeOffset WorstAt, string Role);

/// <summary>The session's own normal range for one metric, computed from healthy-side samples only (the worst tail is trimmed first), using the median and the median absolute deviation.</summary>
/// <param name="Sigma">1.4826 × MAD, never below the rule's minimum scale: the robust equivalent of a standard deviation.</param>
public sealed record MetricBaseline(string ChannelId, string Unit, double Median, double Mad, double Sigma, double P05, double P95, int Samples, TimeSpan Span, bool Reliable, string? Reason);

/// <summary>The thresholds that decided an observation, in the recorded unit. A threshold that was not used is null.</summary>
public sealed record ThresholdEvidence(ThresholdBasis Basis, double? Absolute, double? BaselineLimit, MetricBaseline? Baseline, string Comparison);

/// <summary>What was recorded for one metric over the whole recording, so a reader sees what the analysis could and could not use.</summary>
/// <param name="Usable">False when the metric was recorded in a unit the analyzer does not interpret (or never had a valid value).</param>
public sealed record MetricCoverage(string ChannelId, string Title, string Unit, bool Usable, int Samples, int ValidSamples, int OutOfRangeSamples, double Coverage, DateTimeOffset? FirstValid, DateTimeOffset? LastValid, string? Reason);

public enum RuleState { Evaluated, InsufficientData, NotApplicable }

/// <summary>What happened to one rule: it ran (and how many samples it could not use), or why it could not be evaluated. A rule that did not run never reports "healthy".</summary>
public sealed record RuleEvaluation(string RuleId, int RuleVersion, string ChannelId, RuleState State, AnalysisStatus Status, string? Reason, int Observations, int EligibleSamples, int ExcludedSamples);

/// <summary>One measured condition over a bounded interval of the recording, with everything needed to verify it from the recorded samples.</summary>
/// <param name="Id">Stable identity: the same recording and options give the same id.</param>
/// <param name="Start">UTC time of the first sample that met the criterion.</param>
/// <param name="End">UTC time of the last sample that met it. <see cref="Ending"/> says what is known afterwards.</param>
/// <param name="Coverage">Valid samples of the primary metric divided by the samples on the recording's timeline inside the interval.</param>
public sealed record AnomalyObservation(
    string Id, string RuleId, int RuleVersion, ObservationKind Kind, HistoryGroup Domain, Severity Severity, AnalysisStatus Status, string Title,
    DateTimeOffset Start, DateTimeOffset End, ObservationEnd Ending, DateTimeOffset? RecoveredAt,
    IReadOnlyList<MetricEvidence> Metrics, int SampleCount, int MatchingSamples, double Coverage,
    ThresholdEvidence Threshold, IReadOnlyList<HistoryEvent> AssociatedEvents, int AssociatedEventCount,
    string Explanation, IReadOnlyList<string> Limitations)
{
    public TimeSpan Duration => End - Start;
    public MetricEvidence Primary => Metrics[0];
}

public enum CorrelationPattern { RttAndPacketLoss, GraphicsAndConnection, FpsAndGuestCpu, MultipleGuestResources, AvailabilityOverlap }

/// <summary>
/// How the components of a correlation relate in time. <see cref="CommonOverlap"/>: one interval in which every component was present. <see cref="WithinTolerance"/>: two
/// components that do not overlap but lie within the correlation tolerance of each other. <see cref="Chained"/>: three or more components, each linked to another by overlap or
/// tolerance, with no instant at which all of them were present. <see cref="SingleObservation"/>: one observation placed next to missing data.
/// </summary>
public enum CorrelationTiming { CommonOverlap, WithinTolerance, Chained, SingleObservation }

/// <summary>
/// Why two components are linked: the interval in which both were present (null when they do not overlap) and, in that case, the gap between them.
/// </summary>
public sealed record CorrelationLink(string FirstId, string SecondId, DateTimeOffset? OverlapStart, DateTimeOffset? OverlapEnd, TimeSpan Gap);

/// <summary>
/// Observations that are linked in time (they overlap, or lie within a stated tolerance of each other) on one continuous stretch of data. It describes timing only; it does not say
/// what caused what. <see cref="CommonOverlapStart"/>/<see cref="CommonOverlapEnd"/> are set only when every component was present during that interval; chained or tolerance-linked
/// components have none, and <see cref="Links"/> says which pairs are linked and how.
/// </summary>
/// <param name="ComponentIds">The observations it is built from, in a stable order.</param>
/// <param name="Links">The linked pairs that form the correlation, in a stable order.</param>
public sealed record AnomalyCorrelation(
    string Id, CorrelationPattern Pattern, Severity Severity, string Title, DateTimeOffset Start, DateTimeOffset End, CorrelationTiming Timing,
    DateTimeOffset? CommonOverlapStart, DateTimeOffset? CommonOverlapEnd, IReadOnlyList<CorrelationLink> Links,
    IReadOnlyList<string> ComponentIds, IReadOnlyList<string> Channels, string Explanation, IReadOnlyList<string> Limitations)
{
    /// <summary>Length of the interval common to all components; null when there is none.</summary>
    public TimeSpan? CommonOverlap => CommonOverlapStart is { } s && CommonOverlapEnd is { } e ? e - s : null;
}

/// <summary>
/// The complete, deterministic result for one recording. Everything in it is derived from recorded samples and events; limits that reduced the analysis are listed,
/// never dropped silently. A result with <see cref="ReducedConfidence"/> must be presented with its <see cref="Limitations"/>.
/// </summary>
public sealed record AnomalyAnalysis(
    string RecordingKey, int EngineVersion, DateTimeOffset? From, DateTimeOffset? To, int SamplesAnalyzed, bool ReducedConfidence,
    IReadOnlyList<AnomalyObservation> Observations, IReadOnlyList<AnomalyCorrelation> Correlations, IReadOnlyList<MetricCoverage> Coverage,
    IReadOnlyList<RuleEvaluation> Rules, IReadOnlyList<string> Limitations, int OmittedObservations)
{
    public bool IsTruncated => OmittedObservations > 0;
    /// <summary>Rules that could not be evaluated, with the reason: the "Insufficient Data" results.</summary>
    public IEnumerable<RuleEvaluation> InsufficientData => Rules.Where(r => r.State == RuleState.InsufficientData);
}
