namespace EucLens.Core;

/// <summary>Windows session events that matter for the timeline of a recording.</summary>
public enum SessionEventKind { Locked, Unlocked, RemoteDisconnected, RemoteReconnected }

/// <summary>
/// Ties the monitoring state machine to the recorder, the notification policy and the tray state. It holds no window, page or chart:
/// its only inputs are the coordinator, the recorder and the samples the sampling loop produces, which is what makes background
/// operation independent of the UI. <see cref="StatusChanged"/> is raised on whichever thread caused the change; the UI marshals it.
/// </summary>
public sealed class MonitoringService
{
    private readonly object gate = new();
    private readonly ISessionRecorder recorder;
    private readonly NotificationPolicy notifications;
    private readonly TimeProvider clock;
    private MonitoringStatus status;
    private int problems, critical;
    private string? storageProblem;
    private DateTimeOffset? pausedAt;
    private bool? blastAvailable, hostAvailable;
    private RecordingFault? reportedFault;
    private DateTimeOffset? lastSampleAt;

    public MonitoringService(MonitoringCoordinator coordinator, ISessionRecorder recorder, NotificationPolicy? notifications = null, TimeProvider? clock = null)
    {
        Coordinator = coordinator;
        this.recorder = recorder;
        this.clock = clock ?? TimeProvider.System;
        this.notifications = notifications ?? new NotificationPolicy(this.clock);
        status = new(coordinator.State, recorder.IsRecording);
        coordinator.Transitioned += OnTransition;
        recorder.Ended += OnRecorderEnded;
    }

    public MonitoringCoordinator Coordinator { get; }
    public NotificationPolicy Notifications => notifications;
    public MonitoringStatus Status { get { lock (gate) return status; } }
    public DateTimeOffset? LastSampleAt { get { lock (gate) return lastSampleAt; } }
    public event Action<MonitoringStatus>? StatusChanged;
    /// <summary>Raised on whichever thread finished the write, for every outcome including failures.</summary>
    public event Action<IncidentResult>? IncidentCompleted;
    public event Action<NotificationRequest>? NotificationRaised;

    private IncidentMarker? lastIncident;
    /// <summary>The latest incident that was really written to the recording, or null.</summary>
    public IncidentMarker? LastIncident { get { lock (gate) return lastIncident; } }

    /// <summary>True while a recording saved to disk is open: the only state in which an incident marker can be kept.</summary>
    public bool CanMarkIncident => recorder is { IsRecording: true, IsPersistent: true };

    /// <summary>Saves a performance issue marked by the user into the open recording. Monitoring and recording are not interrupted.</summary>
    public async Task<IncidentResult> MarkIncidentAsync(CancellationToken cancellationToken = default)
    {
        var result = await recorder.MarkIncidentAsync(cancellationToken).ConfigureAwait(false);
        if (result is { Saved: true, Marker: { } marker }) lock (gate) lastIncident = marker;
        try { IncidentCompleted?.Invoke(result); } catch (Exception) { /* a subscriber must not turn a saved marker into an error */ }
        return result;
    }

    public bool StartMonitoring() => Coordinator.Start();
    public bool PauseMonitoring() => Coordinator.Pause();
    public bool ResumeMonitoring() => Coordinator.Resume();

    /// <summary>Stops monitoring; an open recording is finalized first.</summary>
    public bool StopMonitoring() => Coordinator.Stop();

    private void OnTransition(MonitoringTransition t)
    {
        switch (t.To)
        {
            case MonitoringState.Paused:
                lock (gate) pausedAt = t.At;
                recorder.Mark(new(RecordingMarkKind.Paused, t.At, "Monitoring paused by the user: no samples are collected until it is resumed."));
                break;
            case MonitoringState.Running when t.From == MonitoringState.Paused:
                TimeSpan? gap;
                lock (gate) { gap = pausedAt is { } at ? t.At - at : null; pausedAt = null; }
                recorder.Mark(new(RecordingMarkKind.Resumed, t.At, "Monitoring resumed.", gap));
                break;
            case MonitoringState.Stopped:
                lock (gate) pausedAt = null;
                if (recorder.IsRecording) recorder.Stop(RecordingEndReason.MonitoringStopped);
                break;
        }
        Refresh();
    }

    private void OnRecorderEnded(RecordingEndReason reason)
    {
        if (reason is RecordingEndReason.StorageError or RecordingEndReason.QuotaReached)
            lock (gate) storageProblem = reason == RecordingEndReason.QuotaReached ? "recording stopped: quota reached" : "recording stopped: storage error";
        if (notifications.OnRecordingStopped(reason) is { } request) NotificationRaised?.Invoke(request);
        Refresh();
    }

    /// <summary>Recomputes the status from the real coordinator and recorder state and raises <see cref="StatusChanged"/> when it changed.</summary>
    public void Refresh()
    {
        MonitoringStatus next;
        lock (gate)
        {
            var sink = recorder.SinkStatus;
            var live = sink is { State: RecordingSinkState.Faulted, LastFault: { } fault } ? $"storage {fault.Kind}" : null;
            if (recorder.IsRecording && live is null && storageProblem is not null && sink?.State == RecordingSinkState.Active) storageProblem = null;
            next = new(Coordinator.State, recorder.IsRecording, problems, critical, live ?? storageProblem);
            if (next == status) return;
            status = next;
        }
        StatusChanged?.Invoke(next);
    }

    /// <summary>Called by the sampling loop for every sample, whether or not a window exists.</summary>
    public void OnSample(SessionSnapshot sample, DiagnosticReport? report)
    {
        lock (gate) lastSampleAt = sample.Timestamp;
        if (report is not null)
        {
            var active = (report.Findings ?? []).Where(f => f is { IsActive: true, Severity: >= Severity.Warning } && f.Domain != DiagnosticDomain.DataQuality).ToArray();
            lock (gate) { problems = active.Length; critical = active.Count(f => f.Severity == Severity.Critical); }
            if (notifications.OnDiagnosticEvents(report.Snapshot?.Events ?? []) is { } request) NotificationRaised?.Invoke(request);
        }
        TrackAvailability(sample);
        if (recorder.SinkStatus is { LastFault: { IsFatal: false } fault } && !ReferenceEquals(fault, reportedFault))
        {
            reportedFault = fault;
            if (notifications.OnStorageProblem(fault.Kind) is { } request) NotificationRaised?.Invoke(request);
        }
        Refresh();
    }

    private void TrackAvailability(SessionSnapshot s)
    {
        if (s.Session.IsDemo) return;
        var now = s.Timestamp;
        if (s.BlastMetrics is { Count: > 0 } blast)
        {
            var available = blast.Values.Any(m => m.Status == MetricStatus.Available);
            if (blastAvailable is { } before && before != available)
            {
                var reason = blast.TryGetValue("rtt", out var rtt) ? rtt.UnavailableReason : null;
                recorder.Mark(new(RecordingMarkKind.Availability, now, available
                    ? "Horizon Blast counters are available again."
                    : $"Horizon Blast counters are unavailable ({Trim(reason ?? s.CollectionStatus)}). This alone is not evidence of a network fault."));
            }
            blastAvailable = available;
        }
        var host = s.System.CpuUtilization.Status == MetricStatus.Available;
        if (hostAvailable is { } hostBefore && hostBefore != host)
            recorder.Mark(new(RecordingMarkKind.Availability, now, host ? "Windows host counters are available again." : "Windows host counters are unavailable."));
        hostAvailable = host;
    }

    private static string Trim(string? text) => string.IsNullOrWhiteSpace(text) ? "no reason given" : text.Length <= 160 ? text : text[..160] + "…";

    /// <summary>Lock, unlock and remote disconnect or reconnect of the Windows session. Collection continues through all of them.</summary>
    public void OnSessionEvent(SessionEventKind kind)
    {
        var now = clock.GetUtcNow();
        switch (kind)
        {
            case SessionEventKind.Locked:
                notifications.SessionLocked = true;
                recorder.Mark(new(RecordingMarkKind.SessionLocked, now, "The Windows session was locked. Monitoring continues."));
                break;
            case SessionEventKind.Unlocked:
                notifications.SessionLocked = false;
                recorder.Mark(new(RecordingMarkKind.SessionUnlocked, now, "The Windows session was unlocked."));
                break;
            case SessionEventKind.RemoteDisconnected:
                recorder.Mark(new(RecordingMarkKind.RemoteDisconnected, now, "The remote client disconnected. The session keeps running; Blast counters may become unavailable."));
                break;
            case SessionEventKind.RemoteReconnected:
                recorder.Mark(new(RecordingMarkKind.RemoteReconnected, now, "The remote client reconnected; counter discovery resumes automatically."));
                break;
        }
    }

    /// <summary>Stops monitoring, finalizes any recording and waits for it.</summary>
    public async Task ShutdownAsync(RecordingEndReason reason)
    {
        await recorder.StopAsync(reason).ConfigureAwait(false);
        Coordinator.Stop();
    }
}
