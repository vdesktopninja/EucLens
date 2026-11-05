using System.Globalization;
using EucLens.Core;

namespace EucLens.App.ViewModels;

/// <summary>What the Session Recorder page says about the recording: the engine's own conditions, named for a person. No state here exists in the recording engine only by name.</summary>
public enum RecorderStatusKind { Ready, Recording, Finalizing, Completed, Error }

/// <summary>
/// The presentation of the Session Recorder page: one status for monitoring and recording, the three overview tiles, the live-latency empty state and the storage details.
/// <para>
/// It reads the recorder, the monitoring state and the chart data that already exist and is refreshed by the events that already exist (every sample, every monitoring change, the end of a recording);
/// it starts no timer, no polling and no service, and it changes nothing about how samples are collected, recorded, stored or exported. Monitoring (collecting) and recording (saving what is collected) are shown as two things.
/// </para>
/// </summary>
public sealed class RecorderWorkspace : ObservableObject
{
    private readonly MainViewModel main;
    private readonly BackgroundViewModel background;
    private readonly ISessionRecorder recorder;
    private RecordingEndReason? lastEnd;
    private bool wasRecording, wasFinalizing;
    private RecorderStatusKind status = RecorderStatusKind.Ready;
    private string statusLabel = "Ready", statusDetail = "";

    public RecorderWorkspace(MainViewModel main, BackgroundViewModel background, ISessionRecorder recorder)
    {
        this.main = main; this.background = background; this.recorder = recorder;
        StatusTile = new("Recording", "Ready", "", "", "Whether a recording is running. Monitoring is shown separately, below.");
        DurationTile = new("Duration", "00:00:00", "", "", "Elapsed time of the current or last recording (hours:minutes:seconds).");
        SamplesTile = new("Samples", "0", "", "", "Samples collected by the current or last recording.");
        Tiles = [StatusTile, DurationTile, SamplesTile];
        // Folder, quota and retention are edited in Settings; the details below follow them. Same lifetime as the settings object.
        background.SettingsChanged += _ => RaiseStorage();
        Refresh();
    }

    public HistoryTile StatusTile { get; }
    public HistoryTile DurationTile { get; }
    public HistoryTile SamplesTile { get; }
    public IReadOnlyList<HistoryTile> Tiles { get; }

    public RecorderStatusKind Status { get => status; private set => Set(ref status, value); }
    public string StatusLabel { get => statusLabel; private set => Set(ref statusLabel, value); }
    /// <summary>One sentence under the tiles: what is happening and what can be done next (the live region of the page).</summary>
    public string StatusDetail { get => statusDetail; private set => Set(ref statusDetail, value); }

    // ---------- monitoring ----------

    public bool IsMonitoring => background.IsRunning;
    /// <summary>"Monitoring active · Real Session · Horizon Blast": the state of collecting, the kind of data and what the session provides, from the detection that already exists.</summary>
    public string MonitoringSummary => string.Join(" · ", MonitoringWord, main.UsesSyntheticSource ? "Synthetic test data" : "Real Session", DataSource);
    private string MonitoringWord => background.State switch { MonitoringState.Running => "Monitoring active", MonitoringState.Paused => "Monitoring paused", _ => "Monitoring stopped" };
    private string DataSource
    {
        get
        {
            var availability = main.DataAvailability;
            return availability.StartsWith("Horizon Blast counters", StringComparison.Ordinal) ? "Horizon Blast"
                 : availability.StartsWith("Windows metrics only", StringComparison.Ordinal) ? "Windows metrics only"
                 : availability.StartsWith("Synthetic", StringComparison.Ordinal) ? "synthetic measurements"
                 : "detecting the session…";
        }
    }
    /// <summary>The honest explanation behind the summary, for its tooltip.</summary>
    public string MonitoringDetail => main.UsesSyntheticSource ? "Synthetic measurements for automated validation." : main.DataAvailability;

    public string MonitoringActionLabel => background.State switch { MonitoringState.Running => "Pause monitoring", MonitoringState.Paused => "Resume monitoring", _ => "Start monitoring" };
    /// <summary>Starts, pauses or resumes monitoring: the existing commands of the monitoring service, with their own conditions.</summary>
    public RelayCommand MonitoringActionCommand => background.State switch { MonitoringState.Running => background.PauseMonitoringCommand, MonitoringState.Paused => background.ResumeMonitoringCommand, _ => background.StartMonitoringCommand };

    // ---------- recording ----------

    public bool IsRecording => recorder.IsRecording;
    /// <summary>The recording is stopped but its file is still being finalized (a new recording waits for it).</summary>
    public bool IsFinalizing => !recorder.IsRecording && recorder.SinkStatus is { State: RecordingSinkState.Active };

    /// <summary>Why recording cannot start right now, shown next to the actions; empty when it can.</summary>
    public string StartHint => recorder.IsRecording ? ""
        : background.State switch { MonitoringState.Stopped => "Start monitoring first: a recording saves what monitoring collects.", MonitoringState.Paused => "Resume monitoring first: a recording saves what monitoring collects.", _ => "" };

    /// <summary>Called when the recorder reports that a recording ended by itself (storage problem, quota). The reason is kept until the next recording starts.</summary>
    internal void OnRecordingEnded(RecordingEndReason reason) { lastEnd = reason; Refresh(); }

    /// <summary>Recomputes everything from the recorder, the monitoring state and the chart. Called by the events that already exist; cheap and free of I/O.</summary>
    public void Refresh()
    {
        var recording = recorder.IsRecording;
        var sink = recorder.SinkStatus;
        var finalizing = IsFinalizing;
        var samples = recorder.SampleCount;
        if (recording && !wasRecording) lastEnd = null;
        var faulted = sink is { State: RecordingSinkState.Faulted };

        string shortDetail = "";
        if (faulted)
        {
            Apply(RecorderStatusKind.Error, "Storage problem", background.StorageStatus.Length > 0 ? background.StorageStatus : "The recording could not be written. EUC Lens keeps retrying.", recording ? "Not all samples are saved" : "");
        }
        else if (recording)
        {
            var paused = background.IsPaused;
            shortDetail = paused ? "Monitoring paused" : recorder.IsPersistent ? "Saved as collected" : "Kept in memory";
            Apply(RecorderStatusKind.Recording, "Recording",
                paused ? "Monitoring is paused: no samples are collected, and the recording notes the pause and its exact length as a gap. Resume monitoring to continue collecting."
                       : recorder.IsPersistent ? "Recording to disk. Samples are saved as they are collected; nothing is lost if you leave this page or hide the window."
                       : "Recording in memory. Export to save the collected measurements.", shortDetail);
        }
        else if (finalizing) Apply(RecorderStatusKind.Finalizing, "Finalizing", "The recording was stopped; its end is being written to disk. A new recording waits for that to finish.", "Writing the end");
        else if (lastEnd is RecordingEndReason.StorageError) Apply(RecorderStatusKind.Error, "Stopped", "The recording stopped because of a storage problem. What was written is saved. Check the recordings folder, then start a new recording.", "Storage problem");
        else if (lastEnd is RecordingEndReason.QuotaReached) Apply(RecorderStatusKind.Error, "Stopped", "The recording stopped because the storage quota was reached. What was written is saved. Free space or raise the quota in Settings, then start a new recording.", "Quota reached");
        else if (samples > 0) Apply(RecorderStatusKind.Completed, "Completed", recorder.IsPersistent ? "The recording is saved. It is listed under Saved recordings (refresh if it is not there yet); analyze or export it from there." : "The recording is kept in memory. Export it to save the measurements.", "");
        else Apply(RecorderStatusKind.Ready, "Ready", background.State switch
        {
            MonitoringState.Running => "Ready. Start recording to save what is being measured.",
            MonitoringState.Paused => "Monitoring is paused. Resume monitoring, then start recording.",
            _ => "Monitoring is stopped. Start monitoring, then start recording.",
        }, "");

        var lost = sink?.SamplesLost ?? 0;
        DurationTile.Set(main.RecordingDuration, "", "");
        SamplesTile.Set(samples.ToString("N0", CultureInfo.CurrentCulture), lost > 0 ? $"{lost:N0} lost" : "", lost > 0 ? "Warning" : "");

        // A recording that finished its finalization is new information for the list: refresh it once, when that happens (never per sample).
        if (wasFinalizing && !finalizing && !recording) _ = background.RefreshRecordingsAsync();
        wasRecording = recording; wasFinalizing = finalizing;

        foreach (var name in new[] { nameof(IsMonitoring), nameof(MonitoringSummary), nameof(MonitoringDetail), nameof(MonitoringActionLabel), nameof(MonitoringActionCommand), nameof(IsRecording), nameof(IsFinalizing), nameof(StartHint),
            nameof(ChartHasValues), nameof(ChartShowEmpty), nameof(ChartEmptyText), nameof(RecordLimit) }) Raise(name);
    }

    private void Apply(RecorderStatusKind kind, string label, string detail, string tileDetail)
    {
        Status = kind; StatusLabel = label; StatusDetail = detail;
        StatusTile.Set(label, tileDetail, kind switch { RecorderStatusKind.Error => "Critical", RecorderStatusKind.Recording when background.IsPaused => "Warning", _ => "" });
    }

    // ---------- live connection latency ----------

    /// <summary>True when at least one RTT value was recorded; a recording of only missing values is not a chart.</summary>
    public bool ChartHasValues => main.RecordingChart.Points.Any(p => p.Value is not null);
    public bool ChartShowEmpty => !ChartHasValues;
    public string ChartSubtitle => "Round-trip time (RTT) · latest 600 samples";
    private bool WindowsOnly => !main.UsesSyntheticSource && main.DataAvailability.StartsWith("Windows metrics only", StringComparison.Ordinal);
    /// <summary>Says why there is nothing to draw: nothing recorded yet, or RTT is not provided by this session (a recording of Windows metrics is not an empty recording). Nothing is drawn in its place.</summary>
    public string ChartEmptyText
    {
        get
        {
            var anyPoint = main.RecordingChart.Points.Count > 0;
            if (anyPoint) return "RTT is not available in this session, so there is nothing to chart. The recording still holds every other measurement." + (WindowsOnly ? " Round-trip time needs Horizon Blast counters." : "");
            if (WindowsOnly) return "No RTT samples available yet. This session has no Horizon Blast counters, so RTT will stay unavailable; Windows metrics are still recorded.";
            return recorder.IsRecording ? "No RTT samples available yet. The first value appears here within a second or two." : "No RTT samples available yet. Start a recording to chart round-trip time.";
        }
    }

    // ---------- storage and recording details (a collapsed section) ----------

    public string RecordLimit => main.RecordLimit;
    public string FolderText => background.RecordingFolder;
    public string FolderNote => background.RecordingFolderNote;
    public string QuotaText
    {
        get
        {
            var s = background.Current;
            var age = s.RetentionDays > 0 ? $"older than {s.RetentionDays} day{(s.RetentionDays == 1 ? "" : "s")}" : "no age limit";
            return $"Storage quota {s.RetentionQuotaMiB:N0} MiB, {age}. Older EUC Lens recordings are removed first when the quota or the age limit is exceeded; only files EUC Lens wrote in this folder are ever deleted, and never the recording in progress. Change both under Settings, Background monitoring.";
        }
    }
    public const string FormatText = "Each recording is a series of readable JSON Lines files in the recordings folder (schema euclens.recording/1). Unavailable measurements have no value and are never written as zero; pauses, disconnects and lost samples are explicit entries. Exports keep the existing JSON and CSV formats.";
    public const string PauseText = "Pausing monitoring collects no samples. An open recording records the pause and its exact length as a gap, never invented values. A recording needs monitoring: it saves what monitoring collects.";
    public const string VdiText = "On a non-persistent VDI desktop the recordings folder may be erased at logoff: choose a folder on persistent storage, or export a recording before you leave the session.";
    public string TrayText => background.TrayDescription;
    public string StorageStatus => background.StorageStatus;

    private void RaiseStorage() { foreach (var name in new[] { nameof(FolderText), nameof(FolderNote), nameof(QuotaText) }) Raise(name); }
}
