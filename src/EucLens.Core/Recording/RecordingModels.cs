namespace EucLens.Core;

/// <summary>Why a recording ended. Anything other than a user action is reported to the user.</summary>
public enum RecordingEndReason { UserStopped, ModeSwitched, MonitoringStopped, AppExit, SessionEnding, StorageError, QuotaReached, Unknown }

/// <summary>Timeline entries that are not samples. Pauses and measurement gaps are explicit; they are never filled with values.</summary>
public enum RecordingMarkKind { Paused, Resumed, Availability, SessionLocked, SessionUnlocked, RemoteDisconnected, RemoteReconnected, DataLoss, Note }

/// <param name="Gap">Length of the interval without samples, for <see cref="RecordingMarkKind.Resumed"/> and <see cref="RecordingMarkKind.DataLoss"/>.</param>
public sealed record RecordingMark(RecordingMarkKind Kind, DateTimeOffset At, string Detail = "", TimeSpan? Gap = null);

public sealed record RecordingStartInfo(Guid Id, DateTimeOffset StartedAt, bool IsDemo, string AppVersion);

public enum RecordingFaultKind { DiskFull, LowDiskSpace, AccessDenied, Unavailable, Io, QuotaReached, QueueOverflow }

/// <summary>A storage problem. <see cref="IsFatal"/> means the recording was stopped because of it.</summary>
public sealed record RecordingFault(RecordingFaultKind Kind, string Message, DateTimeOffset At, bool IsFatal);

public enum RecordingSinkState { Idle, Active, Faulted }

/// <summary>Counters surfaced on the Recorder page; samples that could not be written are counted, never ignored.</summary>
public sealed record RecordingSinkStatus(RecordingSinkState State, Guid? RecordingId, string? CurrentFile, int Part, long BytesWritten, long SamplesWritten,
    long SamplesLost, RecordingFault? LastFault);

/// <summary>Where a persisted recording goes. See <see cref="RecordingWriter"/>.</summary>
public interface IRecordingSink
{
    RecordingSinkStatus Status { get; }
    /// <summary>Raised on a background thread for every storage problem, fatal or not.</summary>
    event Action<RecordingFault>? Faulted;
    /// <summary>Raised when the sink itself ended the recording (storage failure, quota, low disk space). Not raised for <see cref="EndAsync"/>.</summary>
    event Action<RecordingEndReason>? Ended;
    /// <summary>Opens a new recording. Throws <see cref="IOException"/> or <see cref="UnauthorizedAccessException"/> when the location is unusable.</summary>
    void Begin(RecordingStartInfo info);
    /// <summary>Never blocks. A sample that cannot be queued is counted as lost.</summary>
    void Add(SessionSnapshot sample);
    void Mark(RecordingMark mark);
    /// <summary>Writes everything queued so far to disk.</summary>
    Task FlushAsync(CancellationToken cancellationToken = default);
    Task EndAsync(RecordingEndReason reason, CancellationToken cancellationToken = default);
    /// <summary>Streams the samples of the current or last recording from disk.</summary>
    IEnumerable<SessionSnapshot> ReadSamples();
}
