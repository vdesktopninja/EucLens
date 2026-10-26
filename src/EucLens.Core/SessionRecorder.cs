using System.Diagnostics;

namespace EucLens.Core;

/// <summary>
/// The session recorder. Without a sink it keeps every sample in memory (at most <see cref="MaximumSamples"/>), as before.
/// With an <see cref="IRecordingSink"/> every sample is streamed to disk instead and only the latest
/// <see cref="PersistedTailSamples"/> stay in memory, so a recording of many hours needs no more RAM than one of a few minutes.
/// UI owns mutation; exports take a stable copy (or stream from disk) before asynchronous I/O.
/// </summary>
public sealed class SessionRecorder : ISessionRecorder
{
    public const int MaximumSamples = 86_400;
    public const int PersistedTailSamples = 600;
    private readonly List<SessionSnapshot> samples = [];
    private readonly Stopwatch elapsed = new();
    private readonly IRecordingSink? sink;
    private readonly TimeProvider clock;
    private readonly object gate = new();
    private long total;
    private Task ending = Task.CompletedTask;
    private volatile bool recording;
    private readonly object incidentGate = new();
    private int incidentSequence;

    public SessionRecorder(IRecordingSink? sink = null, TimeProvider? clock = null)
    {
        this.sink = sink;
        this.clock = clock ?? TimeProvider.System;
        if (sink is not null) sink.Ended += OnSinkEnded;
    }

    public bool IsRecording => recording;
    /// <summary>Samples recorded in total, also those that only exist on disk.</summary>
    public int SampleCount => (int)Math.Min(Interlocked.Read(ref total), int.MaxValue);
    public TimeSpan Duration { get { lock (gate) return elapsed.Elapsed; } }
    /// <summary>All samples without a sink; the latest <see cref="PersistedTailSamples"/> with one.</summary>
    public IReadOnlyList<SessionSnapshot> Samples => samples.AsReadOnly();
    public bool IsPersistent => sink is not null;
    public RecordingSinkStatus? SinkStatus => sink?.Status;
    /// <summary>The recording ended by itself (storage failure, quota). Raised on a background thread.</summary>
    public event Action<RecordingEndReason>? Ended;

    public void Start() => Start(false);

    public void Start(bool isDemo)
    {
        if (recording) throw new InvalidOperationException("A recording is already active.");
        samples.Clear();
        Interlocked.Exchange(ref total, 0);
        lock (incidentGate) incidentSequence = 0;
        // Begin throws when the location is unusable; the recorder then stays idle.
        sink?.Begin(new(Guid.NewGuid(), clock.GetUtcNow(), isDemo, AppVersion.Current));
        lock (gate) elapsed.Restart();
        recording = true;
    }

    public void Stop() => Stop(RecordingEndReason.UserStopped);

    public void Stop(RecordingEndReason reason)
    {
        if (!recording) return;
        recording = false;
        lock (gate) elapsed.Stop();
        if (sink is not null) ending = sink.EndAsync(reason);
    }

    /// <summary>Stops (if needed) and waits until the recording file is finalized.</summary>
    public async Task StopAsync(RecordingEndReason reason = RecordingEndReason.UserStopped)
    {
        Stop(reason);
        await ending.ConfigureAwait(false);
    }

    public void Add(SessionSnapshot sample)
    {
        if (!recording) return;
        if (sink is null)
        {
            if (samples.Count >= MaximumSamples) { Stop(); return; }
            samples.Add(sample);
            Interlocked.Increment(ref total);
            if (samples.Count == MaximumSamples) Stop();
            return;
        }
        Interlocked.Increment(ref total);
        samples.Add(sample);
        if (samples.Count > PersistedTailSamples) samples.RemoveRange(0, samples.Count - PersistedTailSamples);
        sink.Add(sample);
    }

    /// <summary>Records a pause, resume, availability change or similar. Only meaningful for persisted recordings.</summary>
    public void Mark(RecordingMark mark) { if (recording) sink?.Mark(mark); }

    /// <summary>
    /// Saves a user-marked performance issue. The time comes from the injected clock (UTC), the id is new, and the marker is queued behind the samples
    /// already collected; the result is Saved only after the line is on disk.
    /// </summary>
    public async Task<IncidentResult> MarkIncidentAsync(CancellationToken cancellationToken = default)
    {
        Task<MarkWriteResult> written; IncidentMarker marker;
        lock (incidentGate)
        {
            if (!recording) return new(IncidentOutcome.NoRecording, null, "No recording is running. Start a recording first, then mark the issue.");
            if (sink is null) return new(IncidentOutcome.NotPersistent, null, "Only a recording saved to disk can hold incident markers.");
            marker = new(Guid.NewGuid(), clock.GetUtcNow(), incidentSequence + 1);
            written = sink.MarkDurableAsync(new RecordingMark(RecordingMarkKind.UserIncident, marker.At, "Performance issue marked by the user.", null, marker.Id, marker.Sequence), cancellationToken);
            incidentSequence = marker.Sequence;
        }
        MarkWriteResult result;
        try { result = await written.ConfigureAwait(false); }
        catch (OperationCanceledException) { return new(IncidentOutcome.StorageFailed, null, "The incident marker was cancelled before it was confirmed on disk."); }
        if (result.Saved) return new(IncidentOutcome.Saved, marker, $"Incident {marker.Sequence} saved at {marker.At.ToLocalTime():HH:mm:ss}.");
        // Not confirmed, but the writer had started on it: it may still be in the file. Said as such, with its identity, and never retried.
        if (result.OutcomeUnknown)
            return new(IncidentOutcome.Unknown, marker, $"The incident marker may or may not have been saved: {result.Failure} Check Session History before marking again.");
        return new(IncidentOutcome.StorageFailed, null, $"The incident marker could not be saved: {result.Failure} The recording continues.");
    }

    private void OnSinkEnded(RecordingEndReason reason)
    {
        recording = false;
        lock (gate) elapsed.Stop();
        try { Ended?.Invoke(reason); } catch (Exception) { /* a subscriber must not break the writer thread */ }
    }

    public async Task ExportJsonAsync(string path, CancellationToken cancellationToken = default)
    {
        var source = await SnapshotAsync(cancellationToken).ConfigureAwait(false);
        await RecordingExport.ExportJsonAsync(path, source, cancellationToken).ConfigureAwait(false);
    }

    public async Task ExportCsvAsync(string path, CancellationToken cancellationToken = default)
    {
        var source = await SnapshotAsync(cancellationToken).ConfigureAwait(false);
        await RecordingExport.ExportCsvAsync(path, source, cancellationToken).ConfigureAwait(false);
    }

    private async Task<Func<IEnumerable<SessionSnapshot>>> SnapshotAsync(CancellationToken cancellationToken)
    {
        if (sink is null) { var copy = samples.ToArray(); return () => copy; }
        await sink.FlushAsync(cancellationToken).ConfigureAwait(false);
        return sink.ReadSamples;
    }
}
