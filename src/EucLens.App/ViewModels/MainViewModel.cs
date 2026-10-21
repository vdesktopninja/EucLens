using System.Collections.ObjectModel;
using EucLens.Core;
using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.SkiaSharpView;
using Microsoft.Extensions.Logging;

namespace EucLens.App.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private readonly IDemoMetricsProvider provider;
    private readonly IMetricsProvider? realProvider;
    private Choice<MonitoringMode> selectedMode;
    private bool recordingIsDemo;
    private IMetricsProvider ActiveProvider => IsDemo ? provider : realProvider ?? throw new InvalidOperationException("Real provider is not configured.");
    public IReadOnlyList<Choice<MonitoringMode>> Modes { get; } = [new(MonitoringMode.RealSession, "Real Session"), new(MonitoringMode.DemoMode, "Demo Mode")];
    public Choice<MonitoringMode> SelectedMode
    {
        get => selectedMode;
        set
        {
            if (!Set(ref selectedMode, value)) return;
            // A recording never mixes demo and real samples.
            var stopped = recorder.IsRecording;
            recorder.Stop(); Configure(); RefreshRecorder();
            Raise(nameof(IsDemo)); Raise(nameof(IsReal)); Raise(nameof(ModeLabel)); Raise(nameof(ModeBanner)); Raise(nameof(ModeDescription));
            Raise(nameof(ProtocolDisplay)); Raise(nameof(ProtocolDetail));
            if (stopped) Status = $"Recording stopped because monitoring switched to {value.Label}. Export to keep it.";
        }
    }
    public bool IsDemo => SelectedMode.Value == MonitoringMode.DemoMode;
    public bool IsReal => !IsDemo;
    public string ModeLabel => SelectedMode.Label;
    public string ModeBanner => IsDemo ? "DEMO DATA · Synthetic measurements" : "REAL SESSION · Horizon Blast counters + Windows host metrics";
    public string ModeDescription => IsDemo ? "Synthetic session metrics." : "Blast: this Windows session. CPU, RAM, disk, GPU: whole machine. Missing measurements display N/A.";
    /// <summary>Real Session shows only the detected protocol; the demo selector never stands in for it.</summary>
    public string ProtocolDisplay => IsDemo ? $"{SelectedProtocol.Label} (demo selection)"
        : current is null ? "Detecting…" : ProtocolLabel(current.Session.Protocol);
    public string ProtocolDetail => IsDemo ? "Simulated protocol chosen in Demo Mode." : current?.Session.ProtocolDetection ?? "Waiting for the first sample.";
    private readonly IDiagnosticEngine diagnostics;
    private readonly IDiagnosticSettingsStore? diagnosticSettings;
    /// <summary>Serializes background evaluation with Reset/Configure so mode switches never mix histories.</summary>
    private readonly object diagnosticsGate = new();
    private const string InsufficientData = "Insufficient Data";
    private string connectionState = InsufficientData, graphicsState = InsufficientData, guestState = InsufficientData;
    private string thresholdStatus = "Default thresholds (initial tunable values).";
    private readonly ISessionRecorder recorder;
    private readonly IDesktopServices desktop;
    private readonly ILogger<MainViewModel> logger;
    private readonly ObservableCollection<DateTimePoint> rttPoints = [], fpsPoints = [], recordedPoints = [];
    private SessionSnapshot? current;
    private bool dark;
    private string status = "Initializing monitoring…";
    private HealthStatus health = HealthStatus.Unavailable;
    private readonly ObservableCollection<DateTimePoint> cpuPoints = [];
    private Choice<MetricCategory?> selectedAdvancedCategory;
    private string advancedSearch = "";
    private string advancedStatus = "Waiting for the first sample.";
    private PageViewModel selectedPage;
    private Choice<RemoteProtocol> selectedProtocol;
    private Choice<DemoScenario> selectedScenario;
    private Choice<WorkloadContext> selectedWorkload;
    private bool ready;
    private bool collectionFailed;
    private int exportedSamples;
    private long recordingGeneration;
    private int configurationVersion;
    public bool HasUnexportedSamples => recorder.SampleCount > exportedSamples;
    public IReadOnlyList<Choice<RemoteProtocol>> Protocols { get; } = [new(RemoteProtocol.CitrixHdx, "Citrix HDX"), new(RemoteProtocol.OmnissaBlast, "Omnissa Blast"), new(RemoteProtocol.MicrosoftRdp, "Microsoft RDP")];
    public IReadOnlyList<Choice<DemoScenario>> Scenarios { get; } = [new(DemoScenario.Healthy, "Healthy session"), new(DemoScenario.HighNetworkLatency, "High network latency"), new(DemoScenario.LowFps, "Low FPS"), new(DemoScenario.GpuBottleneck, "GPU bottleneck"), new(DemoScenario.BandwidthCongestion, "Bandwidth congestion")];
    public IReadOnlyList<Choice<WorkloadContext>> Workloads { get; } = [new(WorkloadContext.Idle, "Idle / static content"), new(WorkloadContext.Office, "Office productivity"), new(WorkloadContext.InteractiveGraphics, "Interactive graphics"), new(WorkloadContext.VideoPlayback, "Video playback")];
    public IReadOnlyList<PageViewModel> Pages { get; }
    public PageViewModel SelectedPage { get => selectedPage; set { if (Set(ref selectedPage, value) && value is AdvancedPage) RebuildAdvanced(); } }
    public Choice<RemoteProtocol> SelectedProtocol { get => selectedProtocol; set { if (Set(ref selectedProtocol, value)) Configure(); } }
    public Choice<DemoScenario> SelectedScenario { get => selectedScenario; set { if (Set(ref selectedScenario, value)) Configure(); } }
    public Choice<WorkloadContext> SelectedWorkload { get => selectedWorkload; set { if (Set(ref selectedWorkload, value)) Configure(); } }
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
    public string RecordingState => $"{(recorder.IsRecording ? "RECORDING" : recorder.SampleCount > 0 ? "STOPPED" : "READY")} · {((recorder.SampleCount > 0 || recorder.IsRecording ? recordingIsDemo : IsDemo) ? "DEMO DATA" : "REAL SESSION")}";
    public string RecordingDuration => FormatDuration(recorder.Duration);
    public int SampleCount => recorder.SampleCount;
    public string RecordLimit => "Up to 86,400 samples (24 hours at 1 Hz). New recordings replace in-memory history after confirmation.";
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
    public ISeries[] CpuSeries { get; }
    public Axis[] CpuTimeAxes { get; } = CreateTimeAxes();
    public Axis[] CpuAxes { get; } = [new() { Name = "CPU · %", MinLimit = 0, MaxLimit = 100, TextSize = 11 }];
    public ISeries[] RttSeries { get; }
    public ISeries[] FpsSeries { get; }
    public ISeries[] RecordingSeries { get; }
    public Axis[] TimeAxes { get; } = [new() { Labeler = value => value > 0 && value <= DateTime.MaxValue.Ticks ? new DateTime((long)value).ToString("HH:mm:ss") : "", UnitWidth = TimeSpan.TicksPerSecond, MinStep = TimeSpan.TicksPerSecond * 20, TextSize = 11, SeparatorsPaint = null }];
    public Axis[] FpsTimeAxes { get; } = CreateTimeAxes();
    public Axis[] RecordingTimeAxes { get; } = CreateTimeAxes();
    public Axis[] RecordingAxes { get; } = CreateRttAxes();
    private static Axis[] CreateTimeAxes() => [new() { Labeler = value => value > 0 && value <= DateTime.MaxValue.Ticks ? new DateTime((long)value).ToString("HH:mm:ss") : "", UnitWidth = TimeSpan.TicksPerSecond, MinStep = TimeSpan.TicksPerSecond * 20, TextSize = 11, SeparatorsPaint = null }];
    private static Axis[] CreateRttAxes() => [new() { Name = "RTT · ms", MinLimit = 0, TextSize = 11 }];
    public Axis[] RttAxes { get; } = [new() { Name = "RTT · ms", MinLimit = 0, TextSize = 11 }];
    public Axis[] FpsAxes { get; } = [new() { Name = "Frames / second", MinLimit = 0, TextSize = 11 }];
    public TimeSpan ChartAnimation => TimeSpan.Zero;
    public RelayCommand StartRecordingCommand { get; }
    public RelayCommand StopRecordingCommand { get; }
    public AsyncCommand ExportJsonCommand { get; }
    public AsyncCommand ExportCsvCommand { get; }
    public AsyncCommand SavePreferencesCommand { get; }
    public MainViewModel(IDemoMetricsProvider provider, IDiagnosticEngine diagnostics, ISessionRecorder recorder,
        IDesktopServices desktop, ILogger<MainViewModel> logger, IMetricsProvider? realProvider = null, IDiagnosticSettingsStore? diagnosticSettings = null)
    {
        this.diagnosticSettings = diagnosticSettings;
        Diagnostics = new(desktop);
        this.realProvider = realProvider; selectedMode = Modes[realProvider is null ? 1 : 0];
        this.provider = provider; this.diagnostics = diagnostics; this.recorder = recorder; this.desktop = desktop; this.logger = logger;
        selectedProtocol = Protocols[0]; selectedScenario = Scenarios[0]; selectedWorkload = Workloads[realProvider is null ? 2 : 0];
        selectedAdvancedCategory = AdvancedCategories[0];
        Pages = [new DashboardPage(this), new DiagnosticsPage(this), new RecorderPage(this), new SystemPage(this), new AdvancedPage(this), new SettingsPage(this)];
        selectedPage = Pages[0];
        RttSeries = [Series(rttPoints, "RTT", "ms")];
        FpsSeries = [Series(fpsPoints, "FPS", "FPS")];
        RecordingSeries = [Series(recordedPoints, "Recorded RTT", "ms")];
        CpuSeries = [Series(cpuPoints, "CPU", "%")];
        StartRecordingCommand = new(StartRecording, () => ready && !recorder.IsRecording);
        StopRecordingCommand = new(() => { recorder.Stop(); Status = "Recording stopped. Export to save the collected metrics."; RefreshRecorder(); }, () => recorder.IsRecording);
        ExportJsonCommand = new(() => ExportAsync(true), () => recorder.SampleCount > 0);
        ExportCsvCommand = new(() => ExportAsync(false), () => recorder.SampleCount > 0);
        RestoreThresholdDefaultsCommand = new(() => ApplyThresholdsAsync(restore: true), () => diagnosticSettings is not null);
        ReloadThresholdsCommand = new(() => ApplyThresholdsAsync(restore: false), () => diagnosticSettings is not null);
        SavePreferencesCommand = new(SavePreferencesAsync);
    }
    private static LineSeries<DateTimePoint> Series(ObservableCollection<DateTimePoint> values, string name, string unit) => new()
    {
        Values = values, Name = name, GeometrySize = 0, LineSmoothness = .2,
        Stroke = null, Fill = null,
        XToolTipLabelFormatter = point => point.Model?.DateTime.ToString("HH:mm:ss") ?? "",
        YToolTipLabelFormatter = point => $"{point.Coordinate.PrimaryValue:0.##} {unit}"
    };
    public async Task InitializeAsync(CancellationToken token)
    {
        try
        {
            var preferences = await desktop.LoadPreferencesAsync();
            if (preferences is not null)
            {
                IsDark = preferences.Dark;
                selectedProtocol = Protocols.Single(p => p.Value == preferences.Protocol);
                selectedScenario = Scenarios.Single(p => p.Value == preferences.Scenario);
                selectedWorkload = Workloads.Single(p => p.Value == preferences.Workload);
                selectedMode = Modes.Single(m => m.Value == (realProvider is null ? MonitoringMode.DemoMode : preferences.Mode));
                Raise(nameof(SelectedProtocol)); Raise(nameof(SelectedScenario)); Raise(nameof(SelectedWorkload));
                Raise(nameof(SelectedMode)); Raise(nameof(IsDemo)); Raise(nameof(IsReal)); Raise(nameof(ProtocolDisplay)); Raise(nameof(ProtocolDetail)); Raise(nameof(ModeLabel)); Raise(nameof(ModeBanner)); Raise(nameof(ModeDescription));
            }
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            logger.LogWarning(ex, "Preferences could not be loaded; using defaults.");
            Status = "Preferences could not be loaded; using defaults.";
        }
        await provider.InitializeAsync(token);
        if (realProvider is not null) await realProvider.InitializeAsync(token);
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
        yield return new("Low FPS (active workloads)", $"< {N(t.LowFps)} FPS", $"Screen activity: dirty FPS ≥ {N(t.ActivityDirtyFps)}.");
        yield return new("Guest CPU warning / critical", $"{N(t.CpuWarningPercent)} / {N(t.CpuCriticalPercent)} %", "Machine-wide guest CPU.");
        yield return new("Guest RAM warning / critical", $"{N(t.MemoryWarningPercent)} / {N(t.MemoryCriticalPercent)} %", $"Or available ≤ {N(t.AvailableRamWarningGiB)} / {N(t.AvailableRamCriticalGiB)} GiB.");
        yield return new("GPU / VRAM / encoder warning", $"{N(t.GpuWarningPercent)} / {N(t.VramWarningPercent)} / {N(t.EncoderWarningPercent)} %", "Hardware GPU only.");
    }
    private void Configure()
    {
        if (!ready) return;
        provider.Configure(selectedProtocol.Value, selectedScenario.Value, selectedWorkload.Value);
        // Sustained rules must not carry evidence across modes or scenarios; an in-flight evaluation is discarded.
        lock (diagnosticsGate) { configurationVersion++; diagnostics.Reset(); }
        Raise(nameof(ProtocolDisplay)); Raise(nameof(ProtocolDetail));
        // Old samples remain in recordings with their own protocol/workload; live charts start fresh.
        ClearLive();
        Status = IsDemo ? $"DEMO DATA · {SelectedProtocol.Label} · {SelectedScenario.Label}" : "REAL SESSION · Discovering Blast counters and host collectors…";
    }
    public async Task RunAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (!token.IsCancellationRequested)
        {
            try
            {
                var version = configurationVersion;
                var active = ActiveProvider;
                var workload = SelectedWorkload.Value;
                // Collection and diagnostic evaluation both run off the UI thread.
                var (sample, report) = await Task.Run(async () =>
                {
                    var s = await active.CollectAsync(token);
                    if (!s.Session.IsDemo) s = s with { Workload = workload };
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
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                collectionFailed = true;
                logger.LogError(ex, "Metrics collection failed.");
                ClearLive();
                Status = $"Collection failed: {ex.Message}. Retrying next second.";
                health = HealthStatus.Unavailable; connectionState = graphicsState = guestState = InsufficientData;
                RaiseHealth();
            }
            try { if (!await timer.WaitForNextTickAsync(token)) break; }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
        }
    }
    private void Apply(SessionSnapshot s, DiagnosticReport report)
    {
        // Diagnostic state travels with the sample so recordings preserve findings and their transitions.
        if (report.Snapshot is not null) s = s with { Diagnostics = report.Snapshot };
        current = s;
        ApplyDiagnostics(report, s);
        MetricCard C(string name, Metric metric) => new(name, metric.Display, s.Session.IsDemo ? metric.UnavailableReason ?? "Simulated measurement"
            : $"{metric.Status} · {(metric.Scope is { } scope ? ScopeLabel(scope) + " · " : "")}{metric.Source ?? "Not collected"} · {metric.Error ?? metric.UnavailableReason}");
        MetricCard T(string name, string? value) => new(name, value ?? "N/A", s.Session.IsDemo ? "Simulated session metadata" : "Local session metadata");
        var host = s.HostMetrics?.ToDictionary(e => e.Id, StringComparer.Ordinal);
        MetricEntry? E(string id) => host is not null && host.TryGetValue(id, out var entry) ? entry : null;
        MetricCard H(string name, string id) => E(id) is { } e ? e.Text is null ? C(name, e.Metric) : new(name, e.Text, $"{e.Metric.Status} · {e.Metric.Source}") : T(name, null);
        Replace(OverviewCards, [C("RTT", s.Connection.RoundTripTime), C("FPS", s.Graphics.FramesPerSecond), C("BANDWIDTH", s.Connection.BandwidthConsumption), C("GPU", s.Gpu.Utilization), C("CPU", s.System.CpuUtilization)]);
        Replace(ConnectionCards, [T("Transport", s.Session.Transport), C("Session RTT", s.Connection.RoundTripTime), C("Network latency", s.Connection.NetworkLatency), C("Available bandwidth", s.Connection.AvailableBandwidth), C("Current bandwidth", s.Connection.BandwidthConsumption), C("Packet loss", s.Connection.PacketLoss), T("Connection quality", ConnectionQuality)]);
        var graphics = new List<MetricCard> { C("Frames per second", s.Graphics.FramesPerSecond), C("Dropped frames", s.Graphics.DroppedFramesPercent), T("Encoder", s.Graphics.Encoder), T("Codec", s.Graphics.Codec), T("Graphics mode", s.Graphics.Mode), T("Hardware encoding", s.Graphics.HardwareEncoding is { } hw ? hw ? "Enabled (demo)" : "Disabled (demo)" : null), T("Display resolution", s.Graphics.Resolution) };
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
        Append(rttPoints, s.Timestamp, ChartValue(s.Connection.RoundTripTime), 120);
        Append(fpsPoints, s.Timestamp, ChartValue(s.Graphics.FramesPerSecond), 120);
        Append(cpuPoints, s.Timestamp, ChartValue(s.System.CpuUtilization), 120);
        var recording = recorder.IsRecording;
        recorder.Add(s);
        if (recording) Append(recordedPoints, s.Timestamp, ChartValue(s.Connection.RoundTripTime), 600);
        if (recording && !recorder.IsRecording) Status = "Recording reached the 86,400 sample limit and stopped. Export to keep it.";
        RaiseHealth(); Raise(nameof(SessionDuration)); Raise(nameof(UpdatedAt));
        Raise(nameof(SessionIdentity)); Raise(nameof(NetworkReadout)); Raise(nameof(GraphicsReadout)); Raise(nameof(ProtocolDisplay)); Raise(nameof(ProtocolDetail));
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
        : Protocols.FirstOrDefault(p => p.Value == protocol)?.Label ?? protocol.ToString();
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
            AdvancedStatus = IsDemo ? "Advanced metrics list real collector output; Demo Mode has none." : "Waiting for the first sample.";
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
        current = null; health = HealthStatus.Unavailable; connectionState = graphicsState = guestState = InsufficientData;
        Diagnostics.Clear();
        rttPoints.Clear(); fpsPoints.Clear(); cpuPoints.Clear(); AdvancedRows.Clear();
        AdvancedStatus = IsDemo ? "Advanced metrics list real collector output; Demo Mode has none." : "Waiting for the first sample.";
        OverviewCards.Clear(); ConnectionCards.Clear(); GraphicsCards.Clear(); GpuCards.Clear(); SystemCards.Clear(); SessionCards.Clear();
        RaiseHealth();
        foreach (var name in new[] { nameof(ProtocolDisplay), nameof(ProtocolDetail), nameof(ConnectionQuality), nameof(SessionDuration), nameof(UpdatedAt), nameof(SessionIdentity), nameof(NetworkReadout), nameof(GraphicsReadout) }) Raise(name);
    }
    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> values)
    {
        var items = values.ToArray();
        // Records compare by value; unchanged rows are left alone so WPF does not re-render them.
        for (var i = 0; i < items.Length; i++) { if (i >= target.Count) target.Add(items[i]); else if (!Equals(target[i], items[i])) target[i] = items[i]; }
        while (target.Count > items.Length) target.RemoveAt(target.Count - 1);
    }
    private static void Append(ObservableCollection<DateTimePoint> target, DateTimeOffset timestamp, double? value, int limit)
    {
        target.Add(new(timestamp.LocalDateTime, value));
        if (target.Count > limit) target.RemoveAt(0);
    }
    private void StartRecording()
    {
        if (recorder.SampleCount > 0 && !desktop.ConfirmNewRecording()) return;
        recordingIsDemo = IsDemo; recorder.Start(); recordingGeneration++; exportedSamples = 0; recordedPoints.Clear(); Status = "Recording metrics. Export locally to save them."; RefreshRecorder();
    }
    private void RefreshRecorder()
    {
        Raise(nameof(RecordingState)); Raise(nameof(RecordingDuration)); Raise(nameof(SampleCount));
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
            await desktop.SavePreferencesAsync(new(IsDark, SelectedProtocol.Value, SelectedScenario.Value, SelectedWorkload.Value, SelectedMode.Value));
            Status = "Theme and monitoring preferences saved locally.";
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        { logger.LogWarning(ex, "Preferences save failed."); Status = $"Could not save preferences: {ex.Message}"; }
    }
    private static string FormatDuration(TimeSpan value) => $"{(int)value.TotalHours:00}:{value.Minutes:00}:{value.Seconds:00}";
}
