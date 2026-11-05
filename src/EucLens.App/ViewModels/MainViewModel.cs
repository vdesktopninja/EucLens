using System.IO;
using System.Collections.ObjectModel;
using EucLens.Core;
using Microsoft.Extensions.Logging;

namespace EucLens.App.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    /// <summary>The real session: Horizon Blast counters where present and protocol-independent Windows metrics. The only source in the application.</summary>
    private readonly IMetricsProvider provider;
    /// <summary>Synthetic test data installed by automated validation through <see cref="UseSyntheticSource"/>; always null in normal use.</summary>
    private IMetricsProvider? syntheticSource;
    private bool recordingIsSynthetic;
    private IMetricsProvider ActiveProvider => syntheticSource ?? provider;
    private bool IsSynthetic => syntheticSource is not null;
    /// <summary>For the Session Recorder's presentation: whether the harness's synthetic source is in use.</summary>
    internal bool UsesSyntheticSource => IsSynthetic;
    public string ModeLabel => IsSynthetic ? "Synthetic test data" : "Real Session";
    /// <summary>What the current data is and which collectors provide it; never claims Blast counters the session does not have.</summary>
    public string ModeBanner => IsSynthetic ? "SYNTHETIC TEST DATA · Automated validation only" : "REAL SESSION · " + DataAvailability;
    public string DataAvailability => IsSynthetic ? "Synthetic measurements"
        : current is null ? "Detecting the session…"
        : current.Session.Protocol == RemoteProtocol.OmnissaBlast ? "Horizon Blast counters + Windows metrics"
        : "Windows metrics only · No Horizon Blast counters for this session";
    public string ModeDescription => IsSynthetic ? "Synthetic measurements for automated validation." : "Blast: this Windows session. CPU, RAM, disk, GPU: whole machine. Missing measurements display N/A.";
    /// <summary>Only the protocol detected in the session's own data is shown; nothing is selected or assumed.</summary>
    public string ProtocolDisplay => current is null ? "Detecting…" : ProtocolLabel(current.Session.Protocol) + (current.Session.IsDemo ? " (synthetic)" : "");
    public string ProtocolDetail => IsSynthetic ? "Synthetic protocol of the automated-validation data." : current?.Session.ProtocolDetection ?? "Waiting for the first sample.";
    /// <summary>
    /// Automated validation only (the --smoke-test harness and unit tests): replaces the measurement source with synthetic test data, or
    /// restores real monitoring with null. No page, preference or command-line option of normal use reaches it. The caller initializes the
    /// source. A different source stops a recording, so synthetic and real samples are never mixed; any call clears the live data and the
    /// sustained diagnostics.
    /// </summary>
    internal void UseSyntheticSource(IMetricsProvider? source)
    {
        var changed = !ReferenceEquals(syntheticSource, source);
        var stopped = changed && recorder.IsRecording;
        syntheticSource = source;
        if (changed) recorder.Stop(RecordingEndReason.ModeSwitched);
        Configure(); RefreshRecorder();
        Raise(nameof(ModeLabel)); Raise(nameof(ModeBanner)); Raise(nameof(DataAvailability)); Raise(nameof(ModeDescription)); Raise(nameof(ProtocolDisplay)); Raise(nameof(ProtocolDetail));
        if (stopped) Status = "Recording stopped because the data source changed. Export to keep it.";
    }
    private readonly IDiagnosticEngine diagnostics;
    private readonly IDiagnosticSettingsStore? diagnosticSettings;
    /// <summary>Serializes background evaluation with Reset/Configure so mode switches never mix histories.</summary>
    private readonly object diagnosticsGate = new();
    private const string InsufficientData = "Insufficient Data";
    private string connectionState = InsufficientData, graphicsState = InsufficientData, guestState = InsufficientData;
    private string thresholdStatus = "Default thresholds (initial tunable values).";
    private readonly ISessionRecorder recorder;
    private readonly MonitoringService monitoring;
    private readonly SynchronizationContext? ui = SynchronizationContext.Current;
    private readonly IDesktopServices desktop;
    private readonly ILogger<MainViewModel> logger;
    private SessionSnapshot? current;
    private bool dark;
    private string status = "Initializing monitoring…";
    private HealthStatus health = HealthStatus.Unavailable;
    private Choice<MetricCategory?> selectedAdvancedCategory;
    private string advancedSearch = "";
    private string advancedStatus = "Waiting for the first sample.";
    private PageViewModel selectedPage;
    private bool ready;
    private bool presenting = true;
    private bool gapPending;
    private (SessionSnapshot Sample, DiagnosticReport Report)? deferred;
    private bool collectionFailed;
    private int exportedSamples;
    private long recordingGeneration;
    private int configurationVersion;
    /// <summary>Persisted recordings are already on disk; only an in-memory recording can be lost by exiting.</summary>
    public bool HasUnexportedSamples => !recorder.IsPersistent && recorder.SampleCount > exportedSamples;
    public BackgroundViewModel Background { get; }
    /// <summary>The Session Recorder page's presentation: one status, the tiles, the chart's empty state and the storage details.</summary>
    public RecorderWorkspace Recorder { get; }
    /// <summary>Session History: saved recordings, incident timeline and historical charts. Independent of the live view and of monitoring.</summary>
    public SessionHistoryViewModel History { get; }
    /// <summary>The About page: identity, license, release notes and the update check the user starts. It makes no network request until asked.</summary>
    public AboutViewModel About { get; }
    public MonitoringService Monitoring => monitoring;
    private static readonly IReadOnlyDictionary<RemoteProtocol, string> ProtocolLabels = new Dictionary<RemoteProtocol, string>
    {
        [RemoteProtocol.CitrixHdx] = "Citrix HDX", [RemoteProtocol.OmnissaBlast] = "Omnissa Blast", [RemoteProtocol.MicrosoftRdp] = "Microsoft RDP",
    };
    public IReadOnlyList<PageViewModel> Pages { get; }
    public PageViewModel SelectedPage
    {
        get => selectedPage;
        set
        {
            var previous = selectedPage;
            if (!Set(ref selectedPage, value)) return;
            // Leaving Session History cancels its loading and releases the loaded recording; coming back opens it again.
            if (previous is SessionHistoryPage) History.OnPageHidden();
            if (value is SessionHistoryPage) History.OnPageShown();
            if (value is AdvancedPage) RebuildAdvanced();
            else if (value is RecorderPage) _ = Background.RefreshRecordingsAsync();
        }
    }
    public bool IsDark { get => dark; set { if (Set(ref dark, value)) desktop.ApplyTheme(value); } }
    public string Status { get => status; private set => Set(ref status, value); }
    public string SessionIdentity => current is null ? "Awaiting session sample" : $"{ProtocolLabel(current.Session.Protocol)} | {current.Session.Transport} | Session {current.Session.SessionId}";
    public string NetworkReadout => current?.Connection.RoundTripTime.Display ?? "Unavailable";
    public string GraphicsReadout => current?.Graphics.FramesPerSecond.Display ?? "Unavailable";
    public string Health => health.ToString();
    /// <summary>Independent domain states: Healthy, Warning, Critical or Insufficient Data. Never one combined score.</summary>
    public string ConnectionHealth => connectionState;
    public string GraphicsHealth => graphicsState;
    public string GuestHealth => guestState;
    public string ConnectionQuality => $"{connectionState} (rule assessment)";
    /// <summary>Diagnostics page presentation: health overview, evidence coverage, findings, evidence and trends.</summary>
    public DiagnosticsViewModel Diagnostics { get; }
    public ObservableCollection<DomainHealthItem> HealthTiles => Diagnostics.Domains;
    public string ThresholdStatus { get => thresholdStatus; private set => Set(ref thresholdStatus, value); }
    public ObservableCollection<MetricCard> ThresholdCards { get; } = [];
    public string ThresholdPath => diagnosticSettings?.Path ?? "Not configured";
    public AsyncCommand RestoreThresholdDefaultsCommand { get; }
    public AsyncCommand ReloadThresholdsCommand { get; }
    public string SessionDuration => current is null ? "Unavailable" : FormatDuration(current.Timestamp - current.Session.ConnectedAt);
    public string UpdatedAt => current is null ? "Awaiting first sample" : $"Last sample {current.Timestamp.ToLocalTime():HH:mm:ss} · 1 second interval";
    public string RecordingState => $"{(recorder.IsRecording ? "RECORDING" : recorder.SampleCount > 0 ? "STOPPED" : "READY")} · {((recorder.SampleCount > 0 || recorder.IsRecording ? recordingIsSynthetic : IsSynthetic) ? "SYNTHETIC TEST DATA" : "REAL SESSION")}";
    public string RecordingDuration => FormatDuration(recorder.Duration);
    public int SampleCount => recorder.SampleCount;
    public string RecordLimit => recorder.IsPersistent
        ? "Recordings stream to disk as they are collected and are limited by the storage quota in Settings, not by memory. Every recording is a new file."
        : "Up to 86,400 samples (24 hours at 1 Hz). New recordings replace in-memory history after confirmation.";
    public ObservableCollection<MetricCard> OverviewCards { get; } = [];
    public ObservableCollection<MetricCard> ConnectionCards { get; } = [];
    public ObservableCollection<MetricCard> GraphicsCards { get; } = [];
    public ObservableCollection<MetricCard> GpuCards { get; } = [];
    public ObservableCollection<MetricCard> SystemCards { get; } = [];
    public ObservableCollection<MetricCard> SessionCards { get; } = [];
    public ObservableCollection<AdvancedMetricRow> AdvancedRows { get; } = [];
    public IReadOnlyList<Choice<MetricCategory?>> AdvancedCategories { get; } =
    [
        new(null, "All categories"), new(MetricCategory.BlastSession, "Horizon Blast Session"), new(MetricCategory.BlastImaging, "Horizon Blast Imaging"),
        new(MetricCategory.WindowsCpu, "Windows CPU"), new(MetricCategory.WindowsMemory, "Windows Memory"), new(MetricCategory.WindowsDisk, "Windows Disk"),
        new(MetricCategory.Gpu, "GPU"), new(MetricCategory.Display, "Display"),
    ];
    public Choice<MetricCategory?> SelectedAdvancedCategory { get => selectedAdvancedCategory; set { if (Set(ref selectedAdvancedCategory, value)) RebuildAdvanced(); } }
    public string AdvancedSearch { get => advancedSearch; set { if (Set(ref advancedSearch, value ?? "")) RebuildAdvanced(); } }
    public string AdvancedStatus { get => advancedStatus; private set => Set(ref advancedStatus, value); }
    /// <summary>Chart content only; each chart view builds its own LiveCharts objects from it.</summary>
    public ChartData RttChart { get; } = new("RTT", "ms", 120, "RTT · ms", smoothness: .2);
    public ChartData FpsChart { get; } = new("FPS", "FPS", 120, "Frames / second", smoothness: .2);
    public ChartData CpuChart { get; } = new("CPU", "%", 120, "CPU · %", yMax: 100, smoothness: .2);
    public ChartData RecordingChart { get; } = new("Recorded RTT", "ms", 600, "RTT · ms", smoothness: .2);
    public TimeSpan ChartAnimation => TimeSpan.Zero;
    public RelayCommand StartRecordingCommand { get; }
    public RelayCommand StopRecordingCommand { get; }
    public AsyncCommand ExportJsonCommand { get; }
    public AsyncCommand ExportCsvCommand { get; }
    public AsyncCommand SavePreferencesCommand { get; }
    public MainViewModel(IMetricsProvider provider, IDiagnosticEngine diagnostics, ISessionRecorder recorder,
        IDesktopServices desktop, ILogger<MainViewModel> logger, IDiagnosticSettingsStore? diagnosticSettings = null,
        MonitoringService? monitoring = null, BackgroundViewModel? background = null, IUpdateChecker? updateChecker = null)
    {
        this.diagnosticSettings = diagnosticSettings;
        Diagnostics = new(desktop);
        this.provider = provider; this.diagnostics = diagnostics; this.recorder = recorder; this.desktop = desktop; this.logger = logger;
        this.monitoring = monitoring ?? new MonitoringService(new MonitoringCoordinator(), recorder);
        Background = background ?? new BackgroundViewModel(this.monitoring, recorder, desktop, logger: logger);
        History = new SessionHistoryViewModel(Background, desktop, recorder, Background.Storage, logger: logger);
        About = new AboutViewModel(desktop, updateChecker);
        Recorder = new RecorderWorkspace(this, Background, recorder);
        recorder.Ended += reason => Post(() => OnRecordingEnded(reason));
        this.monitoring.StatusChanged += _ => Post(RefreshRecorder);
        Background.IncidentCompleted += result => Status = result.Message;
        selectedAdvancedCategory = AdvancedCategories[0];
        Pages = [new DashboardPage(this), new DiagnosticsPage(this), new RecorderPage(this), new SessionHistoryPage(this), new SystemPage(this), new AdvancedPage(this), new SettingsPage(this), new AboutPage(this)];
        selectedPage = Pages[0];
        Background.AnalyzeRequested += summary =>
        {
            // Opening first marks the history as loading, so showing the page does not start a second load.
            var opening = History.OpenAsync(summary);
            SelectedPage = Pages.Single(p => p is SessionHistoryPage);
            _ = opening;
        };
        StartRecordingCommand = new(StartRecording, () => ready && !recorder.IsRecording && this.monitoring.Coordinator.State == MonitoringState.Running);
        StopRecordingCommand = new(() => { recorder.Stop(); Status = "Recording stopped. Export to save the collected metrics."; RefreshRecorder(); }, () => recorder.IsRecording);
        ExportJsonCommand = new(() => ExportAsync(true), () => recorder.SampleCount > 0);
        ExportCsvCommand = new(() => ExportAsync(false), () => recorder.SampleCount > 0);
        RestoreThresholdDefaultsCommand = new(() => ApplyThresholdsAsync(restore: true), () => diagnosticSettings is not null);
        ReloadThresholdsCommand = new(() => ApplyThresholdsAsync(restore: false), () => diagnosticSettings is not null);
        SavePreferencesCommand = new(SavePreferencesAsync);
    }
    public async Task InitializeAsync(CancellationToken token)
    {
        try
        {
            var preferences = await desktop.LoadPreferencesAsync();
            // Only the theme is a preference. Files from 0.5.0 and earlier may also hold a data source, demo protocol, scenario and workload:
            // those values are ignored, so nothing saved can start synthetic data or change a diagnostic rule.
            if (preferences is not null) IsDark = preferences.Dark;
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            logger.LogWarning(ex, "Preferences could not be loaded; using defaults.");
            Status = "Preferences could not be loaded; using defaults.";
        }
        await provider.InitializeAsync(token);
        await ApplyThresholdsAsync(restore: false);
        ready = true; Configure(); RefreshRecorder();
    }
    /// <summary>Loads (or restores) validated thresholds; invalid files keep the defaults and say why.</summary>
    private async Task ApplyThresholdsAsync(bool restore)
    {
        var configurable = diagnostics as IConfigurableDiagnostics;
        try
        {
            ThresholdLoadResult? result = diagnosticSettings is null ? null
                : restore ? await diagnosticSettings.RestoreDefaultsAsync() : await diagnosticSettings.LoadAsync();
            var thresholds = result?.Thresholds ?? DiagnosticThresholds.Defaults;
            lock (diagnosticsGate) { configurationVersion++; configurable?.Configure(thresholds); }
            Diagnostics.Configure(thresholds);
            ThresholdStatus = result is null ? "Default thresholds (initial tunable values)."
                : result.Errors.Count > 0 ? $"Invalid settings in {result.Path}; defaults in use: {string.Join(" ", result.Errors)}"
                : thresholds == DiagnosticThresholds.Defaults ? $"Default thresholds (initial tunable values). Edit {result.Path} to tune them."
                : $"Custom thresholds loaded from {result.Path}.";
            Replace(ThresholdCards, ThresholdRows(thresholds));
            if (ready) { ClearLive(); if (restore) Status = "Diagnostic thresholds restored to defaults."; }
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Diagnostic thresholds could not be saved or loaded.");
            ThresholdStatus = $"Could not access diagnostic settings: {ex.Message}. Defaults in use.";
        }
    }
    private static IEnumerable<MetricCard> ThresholdRows(DiagnosticThresholds t)
    {
        string N(double v) => v.ToString("0.##", System.Globalization.CultureInfo.CurrentCulture);
        yield return new("Observation windows", $"{N(t.ShortWindowSeconds)} / {N(t.StandardWindowSeconds)} / {N(t.LongWindowSeconds)} s", "Short / standard / long. Elapsed monotonic time.");
        yield return new("Sustained condition", $"≥ {N(t.SustainedFraction * 100)} % of covered time", $"Needs ≥ {N(t.MinimumCoverageFraction * 100)} % window coverage and ≥ {t.MinimumValidSamples} valid samples.");
        yield return new("Recovery (hysteresis)", $"{N(t.RecoveryMarginPercent)} % below warning for {N(t.RecoveryWindowSeconds)} s", $"Findings without data expire after {N(t.StaleFindingSeconds)} s.");
        yield return new("Blast RTT warning / critical", $"{N(t.RttWarningMs)} / {N(t.RttCriticalMs)} ms", "Round-trip, not one-way latency.");
        yield return new("Uplink packet loss warning / critical", $"{N(t.PacketLossWarningPercent)} / {N(t.PacketLossCriticalPercent)} %", "Mean over the standard window.");
        yield return new("Low FPS (while the content is changing)", $"< {N(t.LowFps)} FPS", $"Judged only when Blast dirty FPS ≥ {N(t.LowFps)}; not judged without that counter.");
        yield return new("Guest CPU warning / critical", $"{N(t.CpuWarningPercent)} / {N(t.CpuCriticalPercent)} %", "Machine-wide guest CPU.");
        yield return new("Guest RAM warning / critical", $"{N(t.MemoryWarningPercent)} / {N(t.MemoryCriticalPercent)} %", $"Or available ≤ {N(t.AvailableRamWarningGiB)} / {N(t.AvailableRamCriticalGiB)} GiB.");
        yield return new("GPU / VRAM / encoder warning", $"{N(t.GpuWarningPercent)} / {N(t.VramWarningPercent)} / {N(t.EncoderWarningPercent)} %", "Hardware GPU only.");
    }
    private void Configure()
    {
        if (!ready) return;
        // Sustained rules must not carry evidence across data sources; an in-flight evaluation is discarded.
        lock (diagnosticsGate) { configurationVersion++; diagnostics.Reset(); }
        Raise(nameof(ProtocolDisplay)); Raise(nameof(ProtocolDetail));
        // Old samples remain in recordings with their own protocol; live charts start fresh.
        ClearLive();
        Status = IsSynthetic ? "SYNTHETIC TEST DATA · Automated validation" : "REAL SESSION · Discovering Blast counters and host collectors…";
    }
    /// <summary>
    /// The monitoring loop belongs to the <see cref="MonitoringCoordinator"/>: one loop per process, independent of any window. This
    /// method only supplies what one tick does. Tests and the application both start it here.
    /// </summary>
    public Task RunAsync(CancellationToken token) => monitoring.Coordinator.RunAsync(TickAsync, token);

    private async Task TickAsync(CancellationToken token)
    {
        try
        {
            var version = configurationVersion;
            var active = ActiveProvider;
            var synthetic = IsSynthetic;
            // Collection and diagnostic evaluation both run off the UI thread.
            var (sample, report) = await Task.Run(async () =>
            {
                var s = await active.CollectAsync(token);
                // Real monitoring never accepts a synthetic sample, whatever produced it: it is neither shown, evaluated nor recorded.
                if (s.Session.IsDemo && !synthetic) throw new InvalidDataException("A synthetic sample reached real monitoring and was discarded.");
                DiagnosticReport? r = null;
                lock (diagnosticsGate) { if (version == configurationVersion) r = diagnostics.Evaluate(s); }
                return (s, r);
            }, token);
            if (version == configurationVersion && report is not null)
            {
                Apply(sample, report);
                if (collectionFailed) { Status = sample.CollectionStatus ?? "Collection recovered."; collectionFailed = false; }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            collectionFailed = true;
            logger.LogError(ex, "Metrics collection failed.");
            ClearLive();
            Status = $"Collection failed: {ex.Message}. Retrying next second.";
            health = HealthStatus.Unavailable; connectionState = graphicsState = guestState = InsufficientData;
            RaiseHealth();
        }
    }

    /// <summary>
    /// The window is shown (true) or hidden/minimized (false). While it is not shown, samples are still collected, evaluated by the
    /// diagnostic engine and recorded, but no card, chart or list is updated; showing the window presents the latest state at once.
    /// </summary>
    public bool IsPresenting => presenting;

    public void SetPresenting(bool value)
    {
        if (presenting == value) return;
        presenting = value;
        if (!value) { gapPending = true; Raise(nameof(IsPresenting)); return; }
        Raise(nameof(IsPresenting));
        if (deferred is { } latest) { deferred = null; Present(latest.Sample, latest.Report); }
        RefreshRecorder();
    }

    private void Post(Action action)
    {
        if (ui is null || SynchronizationContext.Current == ui) action(); else ui.Post(_ => action(), null);
    }

    private void OnRecordingEnded(RecordingEndReason reason)
    {
        Recorder.OnRecordingEnded(reason);
        Status = reason switch
        {
            RecordingEndReason.StorageError => "The recording stopped because of a storage problem. What was written is saved; see the Session Recorder page.",
            RecordingEndReason.QuotaReached => "The recording stopped because the storage quota was reached. What was written is saved.",
            _ => "The recording stopped."
        };
        RefreshRecorder();
        _ = Background.RefreshRecordingsAsync();
    }

    private void Apply(SessionSnapshot s, DiagnosticReport report)
    {
        // Diagnostic state travels with the sample so recordings preserve findings and their transitions.
        if (report.Snapshot is not null) s = s with { Diagnostics = report.Snapshot };
        // Recording, tray state and notifications never depend on the window: this part runs for every sample.
        var recording = recorder.IsRecording;
        recorder.Add(s);
        if (recording && !recorder.IsRecording && !recorder.IsPersistent) Status = "Recording reached the 86,400 sample limit and stopped. Export to keep it.";
        monitoring.OnSample(s, report);
        if (!presenting) { deferred = (s, report); return; }
        Present(s, report);
    }

    /// <summary>Updates every card, chart and list from one sample. Only runs while the window is shown.</summary>
    private void Present(SessionSnapshot s, DiagnosticReport report)
    {
        current = s;
        var recording = recorder.IsRecording;
        ApplyDiagnostics(report, s);
        MetricCard C(string name, Metric metric) => new(name, metric.Display, s.Session.IsDemo ? metric.UnavailableReason ?? "Simulated measurement"
            : $"{metric.Status} · {(metric.Scope is { } scope ? ScopeLabel(scope) + " · " : "")}{metric.Source ?? "Not collected"} · {metric.Error ?? metric.UnavailableReason}");
        MetricCard T(string name, string? value) => new(name, value ?? "N/A", s.Session.IsDemo ? "Simulated session metadata" : "Local session metadata");
        var host = s.HostMetrics?.ToDictionary(e => e.Id, StringComparer.Ordinal);
        MetricEntry? E(string id) => host is not null && host.TryGetValue(id, out var entry) ? entry : null;
        MetricCard H(string name, string id) => E(id) is { } e ? e.Text is null ? C(name, e.Metric) : new(name, e.Text, $"{e.Metric.Status} · {e.Metric.Source}") : T(name, null);
        Replace(OverviewCards, [C("RTT", s.Connection.RoundTripTime), C("FPS", s.Graphics.FramesPerSecond), C("BANDWIDTH", s.Connection.BandwidthConsumption), C("GPU", s.Gpu.Utilization), C("CPU", s.System.CpuUtilization)]);
        Replace(ConnectionCards, [T("Transport", s.Session.Transport), C("Session RTT", s.Connection.RoundTripTime), C("Network latency", s.Connection.NetworkLatency), C("Available bandwidth", s.Connection.AvailableBandwidth), C("Current bandwidth", s.Connection.BandwidthConsumption), C("Packet loss", s.Connection.PacketLoss), T("Connection quality", ConnectionQuality)]);
        var graphics = new List<MetricCard> { C("Frames per second", s.Graphics.FramesPerSecond), C("Dropped frames", s.Graphics.DroppedFramesPercent), T("Encoder", s.Graphics.Encoder), T("Codec", s.Graphics.Codec), T("Graphics mode", s.Graphics.Mode), T("Hardware encoding", s.Graphics.HardwareEncoding is { } hw ? (hw ? "Enabled" : "Disabled") + (s.Session.IsDemo ? " (synthetic)" : "") : null), T("Display resolution", s.Graphics.Resolution) };
        if (host is not null) graphics.AddRange([H("Active monitors", "display.monitorCount"), H("Primary display scaling", "display.primaryScalePercent")]);
        Replace(GraphicsCards, graphics);
        var gpu = new List<MetricCard>();
        if (s.Gpu.HardwareAcceleration == false)
        {
            // Software rendering is a supported configuration: a concise statement instead of rows of N/A.
            gpu.AddRange([new("Graphics acceleration", "Software rendering", "No hardware GPU; rendering and Blast encoding use the guest CPU."),
                new("Hardware GPU", "Not detected", s.Gpu.AdapterKind is { } kind ? GpuText.Adapters(kind) : "DXGI enumeration found no hardware GPU."),
                new("GPU performance analysis", "Not applicable", "GPU rules apply only when a hardware GPU and its counters are available.")]);
            OverviewCards[3] = new("GPU", "Software", "Software rendering; no hardware GPU detected.");
        }
        else
        {
            gpu.Add(T("GPU model", s.Gpu.Model));
            if (s.Gpu.AdapterKind is not null) gpu.Add(T("Adapter type", s.Gpu.AdapterKind));
            gpu.AddRange([C("GPU utilization", s.Gpu.Utilization), C("GPU memory utilization", s.Gpu.VramUtilization)]);
            if (s.Gpu.VramUsedTotal is not null) gpu.Add(T("VRAM used / total", s.Gpu.VramUsedTotal));
            gpu.AddRange([C("Video encoder", s.Gpu.EncoderUtilization), C("Video decoder", s.Gpu.DecoderUtilization), T("Driver version", s.Gpu.DriverVersion)]);
        }
        Replace(GpuCards, gpu);
        var system = new List<MetricCard> { C("CPU utilization", s.System.CpuUtilization), C("RAM utilization", s.System.RamUtilization) };
        if (host is not null) system.AddRange([H("Available RAM", "memory.availableGiB"), H("Physical RAM", "memory.totalGiB"), H("Disk read", "disk.readMBps"), H("Disk write", "disk.writeMBps")]);
        system.Add(C(host is null ? "Disk activity" : "Disk throughput (read + write)", s.System.DiskActivity));
        if (host is not null) system.AddRange([H("Disk active time", "disk.activePercent"), H("CPU model", "cpu.model"), H("Logical processors", "cpu.logicalProcessors"), H("Windows version", "os.version")]);
        Replace(SystemCards, system);
        Replace(SessionCards, [T("Active protocol", ProtocolDisplay), T("Protocol detection", ProtocolDetail), T("Session ID", s.Session.SessionId.ToString()), T("Username", s.Session.Username), T("Client hostname", s.Session.ClientHostname), T("Server hostname", s.Session.ServerHostname), T("Operating system", s.Session.OperatingSystem), T("Connection transport", s.Session.Transport), T(s.Session.IsDemo ? "Session duration" : "Monitoring duration", SessionDuration)]);
        if (s.BlastMetrics is not null)
        {
            foreach (var (key, metric) in s.BlastMetrics)
            {
                if (MappedBlastMetrics.Contains(key)) continue;
                var cards = metric.Source?.Contains("Imaging Counters", StringComparison.Ordinal) == true ? GraphicsCards : ConnectionCards;
                var name = metric.Source?.Split('\\').LastOrDefault() ?? key;
                cards.Add(C(name, metric));
            }
            Status = s.CollectionStatus ?? "REAL SESSION";
        }
        if (gapPending)
        {
            // The window was hidden: the trend lines show the interval as a gap instead of a line across it.
            var gapAt = s.Timestamp.LocalDateTime.AddMilliseconds(-1);
            RttChart.Add(gapAt, null); FpsChart.Add(gapAt, null); CpuChart.Add(gapAt, null);
            if (recording) RecordingChart.Add(gapAt, null);
            gapPending = false;
        }
        RttChart.Add(s.Timestamp.LocalDateTime, ChartValue(s.Connection.RoundTripTime));
        FpsChart.Add(s.Timestamp.LocalDateTime, ChartValue(s.Graphics.FramesPerSecond));
        CpuChart.Add(s.Timestamp.LocalDateTime, ChartValue(s.System.CpuUtilization));
        if (recording) RecordingChart.Add(s.Timestamp.LocalDateTime, ChartValue(s.Connection.RoundTripTime));
        RaiseHealth(); Raise(nameof(SessionDuration)); Raise(nameof(UpdatedAt));
        Raise(nameof(SessionIdentity)); Raise(nameof(NetworkReadout)); Raise(nameof(GraphicsReadout)); Raise(nameof(ProtocolDisplay)); Raise(nameof(ProtocolDetail)); Raise(nameof(ModeBanner)); Raise(nameof(DataAvailability));
        if (SelectedPage is AdvancedPage) RebuildAdvanced();
        RefreshRecorder();
    }
    private void RaiseHealth()
    {
        Raise(nameof(Health)); Raise(nameof(ConnectionHealth)); Raise(nameof(GraphicsHealth)); Raise(nameof(GuestHealth)); Raise(nameof(ConnectionQuality));
    }
    private void ApplyDiagnostics(DiagnosticReport report, SessionSnapshot s)
    {
        health = report.Health;
        Diagnostics.Apply(report, s);
        string State(DiagnosticDomain d) => Diagnostics.Domains.First(x => x.DomainKey == d).State;
        connectionState = State(DiagnosticDomain.Connection); graphicsState = State(DiagnosticDomain.Graphics); guestState = State(DiagnosticDomain.Guest);
    }
    private string ProtocolLabel(RemoteProtocol protocol) => protocol == RemoteProtocol.Unknown ? "Not detected"
        : ProtocolLabels.GetValueOrDefault(protocol, protocol.ToString());
    private static string ScopeLabel(MetricScope scope) => scope switch
    {
        MetricScope.Machine => "Machine-wide", MetricScope.Session => "This session", _ => "Process"
    };
    private static readonly Dictionary<MetricCategory, string> CategoryLabels = new()
    {
        [MetricCategory.BlastSession] = "Horizon Blast Session", [MetricCategory.BlastImaging] = "Horizon Blast Imaging",
        [MetricCategory.WindowsCpu] = "Windows CPU", [MetricCategory.WindowsMemory] = "Windows Memory",
        [MetricCategory.WindowsDisk] = "Windows Disk", [MetricCategory.Gpu] = "GPU", [MetricCategory.Display] = "Display",
    };
    /// <summary>Only runs while the Advanced Metrics page is visible, so hidden pages cost nothing per sample.</summary>
    private void RebuildAdvanced()
    {
        var s = current;
        if (s is null || s.Session.IsDemo)
        {
            AdvancedRows.Clear();
            AdvancedStatus = IsSynthetic ? "Advanced metrics list real collector output; synthetic test data has none." : "Waiting for the first sample.";
            return;
        }
        static string Value(Metric m) => m.Value is { } v ? v.ToString(Math.Abs(v) >= 1000 ? "#,0.#" : "0.###", System.Globalization.CultureInfo.CurrentCulture) : "N/A";
        AdvancedMetricRow Row(MetricCategory category, string id, string name, Metric m, string? text) => new(CategoryLabels[category], name,
            text ?? Value(m), m.Unit, m.Scope is { } scope ? ScopeLabel(scope) : "", m.Status.ToString(),
            m.Status == MetricStatus.Available ? m.Source ?? "" : $"{m.Source} — {m.Error ?? m.UnavailableReason}",
            m.Timestamp?.ToLocalTime().ToString("HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture) ?? "", id);
        var all = new List<AdvancedMetricRow>();
        foreach (var (key, metric) in s.BlastMetrics ?? new Dictionary<string, Metric>())
            all.Add(Row(metric.Source?.Contains("Imaging Counters", StringComparison.Ordinal) == true ? MetricCategory.BlastImaging : MetricCategory.BlastSession,
                "blast." + key, metric.Source?.Split('\\').LastOrDefault() ?? key, metric, null));
        foreach (var entry in s.HostMetrics ?? []) all.Add(Row(entry.Category, entry.Id, entry.Name, entry.Metric, entry.Text));
        var category = SelectedAdvancedCategory.Value is { } c ? CategoryLabels[c] : null;
        var search = AdvancedSearch.Trim();
        var rows = all.Where(r => (category is null || r.Category == category) && (search.Length == 0 ||
            r.Name.Contains(search, StringComparison.OrdinalIgnoreCase) || r.Id.Contains(search, StringComparison.OrdinalIgnoreCase) ||
            r.Source.Contains(search, StringComparison.OrdinalIgnoreCase) || r.Value.Contains(search, StringComparison.OrdinalIgnoreCase))).ToArray();
        Replace(AdvancedRows, rows);
        AdvancedStatus = $"Updated {s.Timestamp.ToLocalTime():HH:mm:ss} · showing {rows.Length} of {all.Count} metrics · {all.Count(r => r.Status == nameof(MetricStatus.Available))} available, {all.Count(r => r.Status != nameof(MetricStatus.Available))} N/A";
    }
    // Already shown through the shared Connection/Graphics model; listing them twice would add noise.
    private static readonly HashSet<string> MappedBlastMetrics = ["rtt", "packetLoss", "imagingOutboundMbps", "fps", "encoderType"];
    private static double? ChartValue(Metric metric) => metric.Status == MetricStatus.Available ? metric.Value : null;
    private void ClearLive()
    {
        current = null; deferred = null; // a sample deferred while hidden belongs to the mode being left
 health = HealthStatus.Unavailable; connectionState = graphicsState = guestState = InsufficientData;
        Diagnostics.Clear();
        RttChart.Clear(); FpsChart.Clear(); CpuChart.Clear(); AdvancedRows.Clear();
        AdvancedStatus = IsSynthetic ? "Advanced metrics list real collector output; synthetic test data has none." : "Waiting for the first sample.";
        OverviewCards.Clear(); ConnectionCards.Clear(); GraphicsCards.Clear(); GpuCards.Clear(); SystemCards.Clear(); SessionCards.Clear();
        RaiseHealth();
        foreach (var name in new[] { nameof(ProtocolDisplay), nameof(ProtocolDetail), nameof(ModeBanner), nameof(DataAvailability), nameof(ConnectionQuality), nameof(SessionDuration), nameof(UpdatedAt), nameof(SessionIdentity), nameof(NetworkReadout), nameof(GraphicsReadout) }) Raise(name);
    }
    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> values)
    {
        var items = values.ToArray();
        // Records compare by value; unchanged rows are left alone so WPF does not re-render them.
        for (var i = 0; i < items.Length; i++) { if (i >= target.Count) target.Add(items[i]); else if (!Equals(target[i], items[i])) target[i] = items[i]; }
        while (target.Count > items.Length) target.RemoveAt(target.Count - 1);
    }
    private void StartRecording()
    {
        // A second request while a recording is running (a double click, a repeated key press) must not start another one: the recorder would refuse it with an exception.
        if (recorder.IsRecording) return;
        // A persisted recording never replaces an earlier one (it is a new file), so only in-memory history needs the confirmation.
        if (!recorder.IsPersistent && recorder.SampleCount > 0 && !desktop.ConfirmNewRecording()) return;
        if (monitoring.Coordinator.State != MonitoringState.Running) { Status = "Recording needs monitoring: start or resume monitoring first."; return; }
        try { recordingIsSynthetic = IsSynthetic; recorder.Start(IsSynthetic); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            logger.LogWarning(ex, "Recording could not be started.");
            Status = $"The recording could not be started: {ex.Message} Check the recordings folder in Settings.";
            RefreshRecorder();
            return;
        }
        recordingGeneration++; exportedSamples = 0; RecordingChart.Clear();
        Status = recorder.IsPersistent ? $"Recording to {Background.RecordingFolder}. Samples are saved as they are collected." : "Recording metrics. Export locally to save them.";
        RefreshRecorder();
    }
    private void RefreshRecorder()
    {
        // Starting or stopping a recording changes what the tray shows; the status must not wait for the next sample. Refresh only
        // raises StatusChanged when the status really changed, so the re-entrant call this triggers ends after one more pass.
        monitoring.Refresh();
        Raise(nameof(RecordingState)); Raise(nameof(RecordingDuration)); Raise(nameof(SampleCount)); Raise(nameof(RecordLimit));
        Background.RefreshStorage();
        Recorder?.Refresh();
        StartRecordingCommand.Refresh(); StopRecordingCommand.Refresh(); ExportJsonCommand.Refresh(); ExportCsvCommand.Refresh();
    }
    private async Task ExportAsync(bool json)
    {
        try
        {
            var path = desktop.SelectExportPath(json);
            if (path is null) return;
            var count = recorder.SampleCount;
            var generation = recordingGeneration;
            if (json) await recorder.ExportJsonAsync(path); else await recorder.ExportCsvAsync(path);
            if (generation == recordingGeneration) exportedSamples = Math.Max(exportedSamples, count);
            Status = generation == recordingGeneration
                ? $"Exported recording to {path}"
                : $"Exported a previous recording to {path}. Current recording still needs export.";
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or System.Security.SecurityException)
        { logger.LogError(ex, "Recording export failed."); Status = $"Export failed: {ex.Message}"; }
    }
    private async Task SavePreferencesAsync()
    {
        try
        {
            await desktop.SavePreferencesAsync(new(IsDark));
            Status = "Theme preference saved locally.";
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        { logger.LogWarning(ex, "Preferences save failed."); Status = $"Could not save preferences: {ex.Message}"; }
    }
    private static string FormatDuration(TimeSpan value) => $"{(int)value.TotalHours:00}:{value.Minutes:00}:{value.Seconds:00}";
}
