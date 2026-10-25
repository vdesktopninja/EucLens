namespace EucLens.Core;

/// <summary>A recording that cannot be read at all (unsupported schema, no header, nothing readable). The message is meant for the user.</summary>
public sealed class RecordingFormatException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Everything the timeline can show. Each kind has its own icon and label; colour is never the only carrier.</summary>
public enum HistoryEventKind
{
    RecordingStarted, RecordingEnded, UserIncident,
    FindingDetected, FindingEscalated, FindingDowngraded, FindingRecovered, FindingExpired,
    MonitoringPaused, MonitoringResumed, SessionLocked, SessionUnlocked, RemoteDisconnected, RemoteReconnected,
    AvailabilityChanged, DataLoss, Note, SessionChanged, Other
}

/// <summary>One entry of the incident timeline, with the time it was recorded (UTC) and its original type.</summary>
/// <param name="Order">Position in the recording; breaks ties between entries with the same time.</param>
/// <param name="Severity">For findings, the severity recorded with the transition.</param>
public sealed record HistoryEvent(HistoryEventKind Kind, DateTimeOffset At, string Title, string Detail = "", Severity? Severity = null, string? RuleId = null,
    Guid? IncidentId = null, int? Sequence = null, TimeSpan? Gap = null, int Order = 0)
{
    public bool IsFinding => Kind is HistoryEventKind.FindingDetected or HistoryEventKind.FindingEscalated or HistoryEventKind.FindingDowngraded or HistoryEventKind.FindingRecovered or HistoryEventKind.FindingExpired;
}

public enum HistoryIssueKind
{
    /// <summary>The last line of an interrupted part was cut off: the supported recovery, reported for information.</summary>
    TruncatedTail,
    /// <summary>A line in the middle of a file could not be read and was skipped.</summary>
    DamagedLine,
    DamagedCompressedData,
    MissingPart,
    PartUnavailable,
    SampleCapReached,
    /// <summary>A part has no readable header: it was skipped, the other parts are used.</summary>
    DamagedHeader,
    /// <summary>A reading limit (bytes, events or incidents) was reached: only the beginning of the recording was analyzed.</summary>
    LimitReached,
    /// <summary>The system clock moved backwards while recording; samples are shown in time order.</summary>
    ClockChange,
    UnreadableAnnotations
}

public sealed record HistoryIssue(HistoryIssueKind Kind, string Message, int? Part = null)
{
    /// <summary>False only for the expected truncated tail of an interrupted recording.</summary>
    public bool IsProblem => Kind != HistoryIssueKind.TruncatedTail;
}

/// <summary><see cref="Partial"/>: the recording is longer than the sample limit, so only its beginning was analyzed; it is never presented as the whole session.</summary>
public enum RecordingCompleteness { Complete, Interrupted, InProgress, Partial }

/// <summary>A stretch without samples, from the last sample before it to the first after it.</summary>
public sealed record HistoryGap(DateTimeOffset Start, DateTimeOffset End, string Reason)
{
    public TimeSpan Length => End - Start;
}

/// <summary>What the file said about the recording itself.</summary>
public sealed record HistoryInfo(string ShortId, Guid? RecordingId, DateTimeOffset StartedUtc, DateTimeOffset EndedUtc, bool HasEnded, string Source,
    string AppVersion, IReadOnlyList<string> Protocols, long Samples, int Warnings, int Criticals, int Incidents, long LostSamples, int Parts,
    RecordingCompleteness State, string EndReason, double DataCoverage, int Gaps, TimeSpan GapTime, string Directory, int SampleLimit = 0, string LimitReason = "")
{
    /// <summary>True when loading stopped at the sample limit: everything shown covers only the beginning of the recording.</summary>
    public bool IsPartial => State == RecordingCompleteness.Partial;
    public TimeSpan Duration => EndedUtc - StartedUtc;
    public bool IsDemo => Source == "Demo";
}

/// <summary>A user-marked incident with the diagnostic state that was recorded around it.</summary>
/// <param name="Before">The last sample at or before the marker, and the first at or after it, when the recording has them.</param>
public sealed record HistoryIncident(Guid Id, int Sequence, DateTimeOffset At, SessionSnapshot? Before, SessionSnapshot? After)
{
    /// <summary>The diagnostic state as it was recorded just before the marker.</summary>
    public DiagnosticSnapshot? RecordedState => Before?.Diagnostics ?? After?.Diagnostics;
}

/// <summary>A value on a chart; a null value is a break in the line (a gap), never a zero.</summary>
public readonly record struct SeriesPoint(long Ticks, double? Value)
{
    public DateTimeOffset At => new(new DateTime(Ticks, DateTimeKind.Utc));
}

/// <summary>One metric over a time range, reduced for drawing, plus its range statistics.</summary>
public sealed record SeriesWindow(HistoryChannel Channel, string Unit, bool UnitVerified, IReadOnlyList<SeriesPoint> Points, bool Decimated, RangeStatistics Statistics);

/// <summary>Statistics over valid values only. Missing and unavailable samples are counted separately and never averaged.</summary>
public sealed record RangeStatistics(int Samples, int Valid, int Missing, double? Minimum, double? P50, double? P95, double? Maximum, DateTimeOffset? MaximumAt)
{
    public static RangeStatistics Empty { get; } = new(0, 0, 0, null, null, null, null, null);
}

public enum HistoryGroup { Connection, Graphics, Guest }

/// <param name="HigherIsWorse">Direction used by the observed-change comparison: FPS is the only metric where lower is worse.</param>
public sealed record HistoryChannel(string Id, string Title, HistoryGroup Group, bool HigherIsWorse, Func<SessionSnapshot, Metric?> Select, string Description = "");

/// <summary>One observation that stands out against the same recording; not a diagnostic finding and never a cause.</summary>
public sealed record ObservedChange(string ChannelId, string Title, string Unit, bool UnitVerified, double Extreme, DateTimeOffset ExtremeAt, double SessionMedian, double SessionReference, string Reference, string Statement);

/// <summary>Plain-language, deterministic description of the recording around one instant.</summary>
public sealed record IncidentAnalysis(DateTimeOffset At, DateTimeOffset From, DateTimeOffset To, IReadOnlyList<ObservedChange> Changes, IReadOnlyList<HistoryEvent> RecordedFindings,
    IReadOnlyList<string> Missing, IReadOnlyList<string> Stable, IReadOnlyList<HistoryGap> Gaps, string Summary);

/// <summary>
/// What loading a recording may consume, whatever the file says. A recording that needs more is analyzed up to the limit and reported as partial; it is
/// never presented as complete. The sample limit is a separate argument of the loader (<see cref="HistoryLoader.MaximumSamples"/>).
/// </summary>
/// <param name="MaximumLineBytes">One line of a part. A real line (a sample with every counter) is a few kilobytes; longer lines are skipped and reported.</param>
/// <param name="MaximumPartBytes">Decompressed bytes read from one part. This also bounds what a compressed file may expand to.</param>
/// <param name="MaximumTotalBytes">Decompressed bytes read from all parts of one recording.</param>
/// <param name="MaximumEvents">Timeline entries kept (the 5,005 of the validation fixture are far below it).</param>
/// <param name="MaximumIncidents">User-marked incidents kept.</param>
/// <param name="MaximumListedIssuesPerKind">Problems listed per kind; more are counted in one summary line.</param>
public sealed record HistoryLimits(int MaximumLineBytes = 1 << 20, long MaximumPartBytes = 1L << 30, long MaximumTotalBytes = 4L << 30,
    int MaximumEvents = 100_000, int MaximumIncidents = 5_000, int MaximumListedIssuesPerKind = 50);
