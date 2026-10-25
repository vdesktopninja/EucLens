using System.IO;
using System.Collections.ObjectModel;
using System.Globalization;
using EucLens.Core;
using Microsoft.Extensions.Logging;

namespace EucLens.App.ViewModels;

/// <summary>
/// Background monitoring from the user's point of view: monitoring state and controls, the Background Monitoring settings, the
/// recordings folder and the list of saved recordings. Holds no window, page or chart.
/// </summary>
public sealed class BackgroundViewModel : ObservableObject
{
    private readonly MonitoringService monitoring;
    private readonly ISessionRecorder recorder;
    private readonly IDesktopServices desktop;
    private readonly BackgroundSettingsStore? store;
    private readonly IRecordingStorage storage;
    private readonly ILogger? logger;
    private readonly SynchronizationContext? ui = SynchronizationContext.Current;
    private BackgroundSettings current = BackgroundSettings.Defaults;
    private bool minimize, close, notify, monitorOnStart = true, recordOnStart;
    private string directory = "", quota = "1024", days = "14", settingsMessage = "", recordingsMessage = "Open this page to list saved recordings.", storageStatus = "";
    private bool settingsInvalid;

    public BackgroundViewModel(MonitoringService monitoring, ISessionRecorder recorder, IDesktopServices desktop, BackgroundSettingsStore? store = null,
        IRecordingStorage? storage = null, ILogger? logger = null)
    {
        this.monitoring = monitoring; this.recorder = recorder; this.desktop = desktop; this.store = store;
        this.storage = storage ?? new PhysicalRecordingStorage(); this.logger = logger;
        StartMonitoringCommand = new(() => { monitoring.StartMonitoring(); }, () => monitoring.Coordinator.State == MonitoringState.Stopped);
        PauseMonitoringCommand = new(() => { monitoring.PauseMonitoring(); }, () => monitoring.Coordinator.State == MonitoringState.Running);
        ResumeMonitoringCommand = new(() => { monitoring.ResumeMonitoring(); }, () => monitoring.Coordinator.State == MonitoringState.Paused);
        SaveCommand = new(SaveAsync);
        BrowseFolderCommand = new(() => { if (desktop.SelectFolder(EffectiveDirectoryText) is { } folder) RecordingDirectory = folder; });
        UseDefaultFolderCommand = new(() => RecordingDirectory = "");
        OpenRecordingsFolderCommand = new(OpenRecordingsFolder);
        RefreshRecordingsCommand = new(RefreshRecordingsAsync);
        MarkIncidentCommand = new(() => MarkIncidentAsync(), () => monitoring.CanMarkIncident);
        monitoring.StatusChanged += _ => Post(RefreshMonitoring);
        Apply(BackgroundSettings.Defaults);
    }

    private void Post(Action action)
    {
        if (ui is null || SynchronizationContext.Current == ui) action(); else ui.Post(_ => action(), null);
    }

    // ---------- monitoring state ----------

    public MonitoringState State => monitoring.Coordinator.State;
    public bool IsStopped => State == MonitoringState.Stopped;
    public bool IsRunning => State == MonitoringState.Running;
    public bool IsPaused => State == MonitoringState.Paused;
    public string MonitoringText => State switch
    {
        MonitoringState.Running => "Monitoring is running.",
        MonitoringState.Paused => "Monitoring is paused. No measurements are collected until you resume it.",
        _ => "Monitoring is stopped. No measurements are collected."
    };
    public string TrayDescription => TrayStateResolver.Resolve(monitoring.Status).Tooltip;
    public RelayCommand StartMonitoringCommand { get; }
    public RelayCommand PauseMonitoringCommand { get; }
    public RelayCommand ResumeMonitoringCommand { get; }

    // ---------- incident marker ----------

    private string lastIncidentText = "";
    private bool lastIncidentFailed;
    public AsyncCommand MarkIncidentCommand { get; }
    /// <summary>Raised on the UI context after every attempt, so the status bar and the tray can confirm or explain.</summary>
    public event Action<IncidentResult>? IncidentCompleted;
    public bool CanMarkIncident => monitoring.CanMarkIncident;
    /// <summary>Time and outcome of the latest attempt; empty before the first one.</summary>
    public string LastIncidentText { get => lastIncidentText; private set => Set(ref lastIncidentText, value); }
    public bool LastIncidentFailed { get => lastIncidentFailed; private set => Set(ref lastIncidentFailed, value); }
    /// <summary>Why the action is unavailable, shown next to the disabled button; empty while it can be used.</summary>
    public string IncidentHint => monitoring.CanMarkIncident ? "" : recorder.IsRecording
        ? "Only a recording saved to disk can hold incident markers."
        : "Start a recording to mark performance issues. The marker is saved into the recording at the moment you click.";

    /// <summary>Marks a performance issue now. Never reports success unless the marker is on disk.</summary>
    public async Task<IncidentResult> MarkIncidentAsync()
    {
        var result = await monitoring.MarkIncidentAsync().ConfigureAwait(false);
        Post(() =>
        {
            LastIncidentFailed = !result.Saved;
            LastIncidentText = result is { Saved: true, Marker: { } marker }
                ? $"Last incident: no. {marker.Sequence} at {marker.At.ToLocalTime():HH:mm:ss} (saved to the recording)."
                : $"Incident not saved: {result.Message}";
            Raise(nameof(IncidentHint)); Raise(nameof(CanMarkIncident));
            IncidentCompleted?.Invoke(result);
        });
        return result;
    }

    private void RefreshMonitoring()
    {
        MarkIncidentCommand.Refresh(); Raise(nameof(CanMarkIncident)); Raise(nameof(IncidentHint));
        foreach (var name in new[] { nameof(State), nameof(IsStopped), nameof(IsRunning), nameof(IsPaused), nameof(MonitoringText), nameof(TrayDescription) }) Raise(name);
        StartMonitoringCommand.Refresh(); PauseMonitoringCommand.Refresh(); ResumeMonitoringCommand.Refresh();
        RefreshStorage();
    }

    // ---------- settings ----------

    /// <summary>The settings in effect. Edits below take effect when saved.</summary>
    public BackgroundSettings Current => current;
    public event Action<BackgroundSettings>? SettingsChanged;
    public bool MinimizeToTray { get => minimize; set => Set(ref minimize, value); }
    public bool CloseToTray { get => close; set => Set(ref close, value); }
    public bool Notifications { get => notify; set => Set(ref notify, value); }
    public bool MonitorOnStart { get => monitorOnStart; set => Set(ref monitorOnStart, value); }
    public bool RecordOnStart { get => recordOnStart; set => Set(ref recordOnStart, value); }
    /// <summary>Empty selects the default folder.</summary>
    public string RecordingDirectory { get => directory; set { if (Set(ref directory, value ?? "")) { Raise(nameof(EffectiveDirectoryText)); Raise(nameof(DurabilityNote)); } } }
    public string RetentionQuotaMiB { get => quota; set => Set(ref quota, value ?? ""); }
    public string RetentionDays { get => days; set => Set(ref days, value ?? ""); }
    public string EffectiveDirectoryText => RecordingLocation.Resolve(string.IsNullOrWhiteSpace(directory) ? null : directory.Trim());
    public string DurabilityNote => RecordingLocation.DurabilityNote(string.IsNullOrWhiteSpace(directory) ? null : directory.Trim()) ?? "";
    public string SettingsMessage { get => settingsMessage; private set => Set(ref settingsMessage, value); }
    public bool SettingsInvalid { get => settingsInvalid; private set => Set(ref settingsInvalid, value); }
    public string TrayExplanation => "When a tray option is on, hiding the window does not stop anything: EUC Lens keeps monitoring, evaluating diagnostics and recording until you choose Exit from the tray icon menu (right-click the icon). The tray icon always shows whether it is monitoring, recording or paused. Nothing starts with Windows.";
    public AsyncCommand SaveCommand { get; }
    public RelayCommand BrowseFolderCommand { get; }
    public RelayCommand UseDefaultFolderCommand { get; }

    private void Apply(BackgroundSettings s)
    {
        current = s;
        MinimizeToTray = s.MinimizeToTray; CloseToTray = s.CloseToTray; Notifications = s.Notifications; MonitorOnStart = s.MonitorOnStart; RecordOnStart = s.RecordOnStart;
        RecordingDirectory = s.RecordingDirectory ?? ""; RetentionQuotaMiB = s.RetentionQuotaMiB.ToString(CultureInfo.InvariantCulture); RetentionDays = s.RetentionDays.ToString(CultureInfo.InvariantCulture);
        monitoring.Notifications.Enabled = s.Notifications;
        Raise(nameof(Current));
    }

    /// <summary>Loads the saved settings at startup. A damaged file leaves the defaults in place and says why.</summary>
    public async Task LoadAsync()
    {
        if (store is null) return;
        var result = await store.LoadAsync();
        Apply(result.Settings);
        if (result.Errors.Count > 0)
        {
            SettingsInvalid = true;
            SettingsMessage = $"The background settings file could not be used; defaults are in effect: {string.Join(" ", result.Errors)}";
        }
        SettingsChanged?.Invoke(current);
    }

    private async Task SaveAsync()
    {
        if (!int.TryParse(quota.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var quotaMiB)) { Fail("The storage quota must be a whole number of MiB."); return; }
        if (!int.TryParse(days.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var retentionDays)) { Fail("Retention must be a whole number of days (0 keeps recordings until the quota requires removal)."); return; }
        var candidate = current with
        {
            MinimizeToTray = minimize, CloseToTray = close, Notifications = notify, MonitorOnStart = monitorOnStart, RecordOnStart = recordOnStart,
            RecordingDirectory = string.IsNullOrWhiteSpace(directory) ? null : directory.Trim(), RetentionQuotaMiB = quotaMiB, RetentionDays = retentionDays
        };
        if (candidate.Validate() is { Count: > 0 } errors) { Fail(string.Join(" ", errors)); return; }
        if (candidate.RecordingDirectory is not null && candidate.RecordingDirectory != current.RecordingDirectory)
        {
            var problem = await Task.Run(() => ProbeFolder(candidate.EffectiveDirectory));
            if (problem is not null) { Fail($"The recordings folder cannot be used: {problem}"); return; }
        }
        try
        {
            if (store is not null) await store.SaveAsync(candidate);
            Apply(candidate);
            SettingsInvalid = false;
            SettingsMessage = "Background settings saved." + (candidate.MinimizeToTray || candidate.CloseToTray ? " EUC Lens will keep running in the background when its window is hidden; use the tray icon menu to exit." : "");
            SettingsChanged?.Invoke(current);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger?.LogWarning(ex, "Background settings could not be saved.");
            Fail($"Could not save the background settings: {ex.Message}");
        }
    }

    private void Fail(string message) { SettingsInvalid = true; SettingsMessage = message; }

    /// <summary>Creates the folder and writes and removes a probe file; returns the problem or null.</summary>
    private static string? ProbeFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            var probe = Path.Combine(path, ".euclens-write-test-" + Guid.NewGuid().ToString("N"));
            File.WriteAllText(probe, "x");
            File.Delete(probe);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return ex.Message; }
    }

    /// <summary>Remembers that the one-time background-operation explanation was shown.</summary>
    public async Task MarkTrayHintShownAsync()
    {
        if (current.TrayHintShown) return;
        var next = current with { TrayHintShown = true };
        current = next;
        try { if (store is not null) await store.SaveAsync(next); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { logger?.LogWarning(ex, "The tray hint flag could not be saved."); }
    }

    // ---------- recordings ----------

    public ObservableCollection<RecordingItem> Recordings { get; } = [];
    public string RecordingsMessage { get => recordingsMessage; private set => Set(ref recordingsMessage, value); }
    public string StorageStatus { get => storageStatus; private set => Set(ref storageStatus, value); }
    public string RecordingFolder => current.EffectiveDirectory;
    public string RecordingFolderNote => RecordingLocation.DurabilityNote(current.RecordingDirectory) ?? "";
    public AsyncCommand RefreshRecordingsCommand { get; }
    public RelayCommand OpenRecordingsFolderCommand { get; }

    /// <summary>Outcome text of the storage of the current or last recording, refreshed with every sample while the window is shown.</summary>
    public void RefreshStorage()
    {
        if (recorder.SinkStatus is not { } s || (s.State == RecordingSinkState.Idle && s.SamplesWritten == 0 && s.LastFault is null)) { StorageStatus = ""; return; }
        var lost = s.SamplesLost > 0 ? $" · {s.SamplesLost:N0} samples lost (counted, see the recording)" : "";
        StorageStatus = s.State switch
        {
            RecordingSinkState.Active => $"Writing {Path.GetFileName(s.CurrentFile)} · part {s.Part} · {FormatBytes(s.BytesWritten)} · {s.SamplesWritten:N0} samples written{lost}",
            RecordingSinkState.Faulted => $"Storage problem: {s.LastFault?.Message} · {s.SamplesLost:N0} samples lost so far. EUC Lens keeps retrying.",
            _ => $"Last recording: {FormatBytes(s.BytesWritten)} · {s.SamplesWritten:N0} samples written{lost}" + (s.LastFault is { IsFatal: true } f ? $" · stopped: {f.Message}" : "")
        };
    }

    public static string FormatBytes(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.0} GiB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.0} MiB",
        >= 1L << 10 => $"{bytes / 1024d:0} KiB",
        _ => $"{bytes} B"
    };

    private void OpenRecordingsFolder()
    {
        var folder = current.EffectiveDirectory;
        try { Directory.CreateDirectory(folder); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { RecordingsMessage = $"The recordings folder cannot be opened: {ex.Message}"; return; }
        if (!desktop.OpenPath(folder)) RecordingsMessage = $"Windows Explorer could not open {folder}.";
    }

    public async Task RefreshRecordingsAsync()
    {
        var folder = current.EffectiveDirectory;
        try
        {
            var found = await Task.Run(() => RecordingCatalog.Scan(storage, folder));
            Recordings.Clear();
            foreach (var summary in found) Recordings.Add(new(summary, this));
            RecordingsMessage = found.Count == 0 ? $"No recordings in {folder}." : $"{found.Count} recording(s) in {folder}.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger?.LogWarning(ex, "Recordings could not be listed.");
            RecordingsMessage = $"The recordings folder cannot be read: {ex.Message}";
        }
    }

    internal async Task ExportAsync(RecordingSummary summary, bool json)
    {
        try
        {
            var path = desktop.SelectExportPath(json);
            if (path is null) return;
            IEnumerable<SessionSnapshot> Read() => RecordingCatalog.ReadSamples(storage, summary);
            if (json) await RecordingExport.ExportJsonAsync(path, Read); else await RecordingExport.ExportCsvAsync(path, Read);
            RecordingsMessage = $"Exported {summary.Samples:N0} samples to {path}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or InvalidDataException)
        { logger?.LogError(ex, "Recording export failed."); RecordingsMessage = $"Export failed: {ex.Message}"; }
    }

    internal void Open(RecordingSummary summary, bool folderOnly)
    {
        var target = summary.Parts.Count > 0 && !folderOnly ? summary.Parts[0] : summary.Directory;
        if (!desktop.OpenPath(target, selectFile: !folderOnly)) RecordingsMessage = "Windows Explorer could not open the location.";
    }
}

/// <summary>One saved recording in the Session Recorder list.</summary>
public sealed class RecordingItem(RecordingSummary summary, BackgroundViewModel owner) : ObservableObject
{
    public RecordingSummary Summary { get; } = summary;
    public string Started => Summary.StartedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
    public string Ended => Summary.EndedUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) ?? "—";
    public string Duration => $"{(int)Summary.Duration.TotalHours:00}:{Summary.Duration.Minutes:00}:{Summary.Duration.Seconds:00}";
    public string Size => BackgroundViewModel.FormatBytes(Summary.Bytes);
    public string Samples => Summary.Samples.ToString("N0", CultureInfo.CurrentCulture);
    public string Events => $"{Summary.Warnings} warning · {Summary.Criticals} critical";
    public string Status => Summary.IsComplete
        ? Summary.EndReason + (Summary.LostSamples > 0 ? $" · {Summary.LostSamples:N0} samples lost" : "")
        : "Interrupted · readable up to the last complete line" + (Summary.LostSamples > 0 ? $" · {Summary.LostSamples:N0} samples lost" : "");
    public string Source => Summary.Source == "Demo" ? "Demo data" : "Real session";
    public string AutomationName => $"Recording from {Started}, {Duration}, {Samples} samples, {Events}, {Status}";
    public RelayCommand OpenFileCommand { get; } = new(() => owner.Open(summary, folderOnly: false));
    public RelayCommand OpenFolderCommand { get; } = new(() => owner.Open(summary, folderOnly: true));
    public AsyncCommand ExportJsonCommand { get; } = new(() => owner.ExportAsync(summary, json: true));
    public AsyncCommand ExportCsvCommand { get; } = new(() => owner.ExportAsync(summary, json: false));
}
