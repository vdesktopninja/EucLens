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
            Raise(nameof(IsDemo)); Raise(nameof(ModeLabel)); Raise(nameof(ModeBanner)); Raise(nameof(ModeDescription));
            if (stopped) Status = $"Recording stopped because monitoring switched to {value.Label}. Export to keep it.";
        }
    }
    public bool IsDemo => SelectedMode.Value == MonitoringMode.DemoMode;
    public string ModeLabel => SelectedMode.Label;
    public string ModeBanner => IsDemo ? "DEMO DATA · Synthetic measurements" : "REAL SESSION · Local Horizon Blast counters";
    public string ModeDescription => IsDemo ? "Synthetic session metrics." : "Current Windows session only. Missing measurements display N/A.";
    private readonly IDiagnosticEngine diagnostics;
    private readonly ISessionRecorder recorder;
    private readonly IDesktopServices desktop;
    private readonly ILogger<MainViewModel> logger;
    private readonly ObservableCollection<DateTimePoint> rttPoints = [], fpsPoints = [], recordedPoints = [];
    private SessionSnapshot? current;
    private bool dark;
    private string status = "Initializing monitoring…";
    private HealthStatus health = HealthStatus.Unavailable;
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
    public IReadOnlyList<Choice<WorkloadContext>> Workloads { get; } = [new(WorkloadContext.Idle, "Idle / static content"), new(WorkloadContext.Office, "Office productivity"), new(WorkloadContext.InteractiveGraphics, "Interactive graphics")];
    public IReadOnlyList<PageViewModel> Pages { get; }
    public PageViewModel SelectedPage { get => selectedPage; set => Set(ref selectedPage, value); }
    public Choice<RemoteProtocol> SelectedProtocol { get => selectedProtocol; set { if (Set(ref selectedProtocol, value)) Configure(); } }
    public Choice<DemoScenario> SelectedScenario { get => selectedScenario; set { if (Set(ref selectedScenario, value)) Configure(); } }
    public Choice<WorkloadContext> SelectedWorkload { get => selectedWorkload; set { if (Set(ref selectedWorkload, value)) Configure(); } }
    public bool IsDark { get => dark; set { if (Set(ref dark, value)) desktop.ApplyTheme(value); } }
    public string Status { get => status; private set => Set(ref status, value); }
    public string SessionIdentity => current is null ? "Awaiting session sample" : $"{Protocols.Single(p => p.Value == current.Session.Protocol).Label} | {current.Session.Transport} | Session {current.Session.SessionId}";
    public string NetworkReadout => current?.Connection.RoundTripTime.Display ?? "Unavailable";
    public string GraphicsReadout => current?.Graphics.FramesPerSecond.Display ?? "Unavailable";
    public string Health => health.ToString();
    public string ConnectionQuality => health switch { HealthStatus.Healthy => "Good (rule assessment)", HealthStatus.Warning => "Degraded (rule assessment)", HealthStatus.Critical => "Poor (rule assessment)", _ => "Unavailable" };
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
    public ObservableCollection<DiagnosticCard> DiagnosticCards { get; } = [];
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
        IDesktopServices desktop, ILogger<MainViewModel> logger, IMetricsProvider? realProvider = null)
    {
        this.realProvider = realProvider; selectedMode = Modes[realProvider is null ? 1 : 0];
        this.provider = provider; this.diagnostics = diagnostics; this.recorder = recorder; this.desktop = desktop; this.logger = logger;
        selectedProtocol = Protocols[0]; selectedScenario = Scenarios[0]; selectedWorkload = Workloads[realProvider is null ? 2 : 0];
        Pages = [new DashboardPage(this), new DiagnosticsPage(this), new RecorderPage(this), new SystemPage(this), new SettingsPage(this)];
        selectedPage = Pages[0];
        RttSeries = [Series(rttPoints, "RTT", "ms")];
        FpsSeries = [Series(fpsPoints, "FPS", "FPS")];
        RecordingSeries = [Series(recordedPoints, "Recorded RTT", "ms")];
        StartRecordingCommand = new(StartRecording, () => ready && !recorder.IsRecording);
        StopRecordingCommand = new(() => { recorder.Stop(); Status = "Recording stopped. Export to save the collected metrics."; RefreshRecorder(); }, () => recorder.IsRecording);
        ExportJsonCommand = new(() => ExportAsync(true), () => recorder.SampleCount > 0);
        ExportCsvCommand = new(() => ExportAsync(false), () => recorder.SampleCount > 0);
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
                Raise(nameof(SelectedMode)); Raise(nameof(IsDemo)); Raise(nameof(ModeLabel)); Raise(nameof(ModeBanner)); Raise(nameof(ModeDescription));
            }
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            logger.LogWarning(ex, "Preferences could not be loaded; using defaults.");
            Status = "Preferences could not be loaded; using defaults.";
        }
        await provider.InitializeAsync(token);
        if (realProvider is not null) await realProvider.InitializeAsync(token);
        ready = true; Configure(); RefreshRecorder();
    }
    private void Configure()
    {
        if (!ready) return;
        configurationVersion++;
        provider.Configure(selectedProtocol.Value, selectedScenario.Value, selectedWorkload.Value);
        // Old samples remain in recordings with their own protocol/workload; live charts start fresh.
        ClearLive();
        Status = IsDemo ? $"DEMO DATA · {SelectedProtocol.Label} · {SelectedScenario.Label}" : "REAL SESSION · Discovering current-session Blast counters…";
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
                var sample = await Task.Run(() => active.CollectAsync(token), token);
                if (!sample.Session.IsDemo) sample = sample with { Workload = workload };
                if (version == configurationVersion)
                {
                    Apply(sample);
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
                health = HealthStatus.Unavailable; Raise(nameof(Health)); Raise(nameof(ConnectionQuality));
            }
            try { if (!await timer.WaitForNextTickAsync(token)) break; }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
        }
    }
    private void Apply(SessionSnapshot s)
    {
        current = s;
        var report = diagnostics.Evaluate(s);
        health = report.Health;
        Replace(DiagnosticCards, report.Results.OrderByDescending(r => r.Severity).Select(r => new DiagnosticCard(
            r.Status != MetricStatus.Available ? r.Status.ToString().ToUpperInvariant() : r.Severity.ToString().ToUpperInvariant(), r.Title,
            r.Description, string.Join(" · ", r.SupportingMetrics.Select(p => $"{p.Key}: {p.Value}")), r.PossibleCause, r.RecommendedAction)));
        MetricCard C(string name, Metric metric) => new(name, metric.Display, s.Session.IsDemo ? metric.UnavailableReason ?? "Simulated measurement" : $"{metric.Status} · {metric.Source ?? "Not collected"} · {metric.Error ?? metric.UnavailableReason}");
        MetricCard T(string name, string? value) => new(name, value ?? "N/A", s.Session.IsDemo ? "Simulated session metadata" : "Local session metadata");
        Replace(OverviewCards, [C("RTT", s.Connection.RoundTripTime), C("FPS", s.Graphics.FramesPerSecond), C("BANDWIDTH", s.Connection.BandwidthConsumption), C("GPU", s.Gpu.Utilization), C("CPU", s.System.CpuUtilization)]);
        Replace(ConnectionCards, [T("Transport", s.Session.Transport), C("Session RTT", s.Connection.RoundTripTime), C("Network latency", s.Connection.NetworkLatency), C("Available bandwidth", s.Connection.AvailableBandwidth), C("Current bandwidth", s.Connection.BandwidthConsumption), C("Packet loss", s.Connection.PacketLoss), T("Connection quality", ConnectionQuality)]);
        Replace(GraphicsCards, [C("Frames per second", s.Graphics.FramesPerSecond), C("Dropped frames", s.Graphics.DroppedFramesPercent), T("Encoder", s.Graphics.Encoder), T("Codec", s.Graphics.Codec), T("Graphics mode", s.Graphics.Mode), T("Hardware encoding", s.Graphics.HardwareEncoding is { } hw ? hw ? "Enabled (demo)" : "Disabled (demo)" : null), T("Display resolution", s.Graphics.Resolution)]);
        Replace(GpuCards, [T("GPU model", s.Gpu.Model), C("GPU utilization", s.Gpu.Utilization), C("VRAM utilization", s.Gpu.VramUtilization), C("Video encoder", s.Gpu.EncoderUtilization), C("Video decoder", s.Gpu.DecoderUtilization), T("Driver version", s.Gpu.DriverVersion)]);
        Replace(SystemCards, [C("CPU utilization", s.System.CpuUtilization), C("RAM utilization", s.System.RamUtilization), C("Disk activity", s.System.DiskActivity)]);
        Replace(SessionCards, [T("Active protocol", Protocols.Single(p => p.Value == s.Session.Protocol).Label), T("Session ID", s.Session.SessionId.ToString()), T("Username", s.Session.Username), T("Client hostname", s.Session.ClientHostname), T("Server hostname", s.Session.ServerHostname), T("Operating system", s.Session.OperatingSystem), T("Connection transport", s.Session.Transport), T(s.Session.IsDemo ? "Session duration" : "Monitoring duration", SessionDuration)]);
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
        var recording = recorder.IsRecording;
        recorder.Add(s);
        if (recording) Append(recordedPoints, s.Timestamp, ChartValue(s.Connection.RoundTripTime), 600);
        if (recording && !recorder.IsRecording) Status = "Recording reached the 86,400 sample limit and stopped. Export to keep it.";
        Raise(nameof(Health)); Raise(nameof(ConnectionQuality)); Raise(nameof(SessionDuration)); Raise(nameof(UpdatedAt));
        Raise(nameof(SessionIdentity)); Raise(nameof(NetworkReadout)); Raise(nameof(GraphicsReadout));
        RefreshRecorder();
    }
    // Already shown through the shared Connection/Graphics model; listing them twice would add noise.
    private static readonly HashSet<string> MappedBlastMetrics = ["rtt", "packetLoss", "imagingOutboundMbps", "fps", "encoderType"];
    private static double? ChartValue(Metric metric) => metric.Status == MetricStatus.Available ? metric.Value : null;
    private void ClearLive()
    {
        current = null; health = HealthStatus.Unavailable;
        rttPoints.Clear(); fpsPoints.Clear();
        OverviewCards.Clear(); ConnectionCards.Clear(); GraphicsCards.Clear(); GpuCards.Clear(); SystemCards.Clear(); SessionCards.Clear(); DiagnosticCards.Clear();
        foreach (var name in new[] { nameof(Health), nameof(ConnectionQuality), nameof(SessionDuration), nameof(UpdatedAt), nameof(SessionIdentity), nameof(NetworkReadout), nameof(GraphicsReadout) }) Raise(name);
    }
    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> values)
    {
        var items = values.ToArray();
        for (var i = 0; i < items.Length; i++) { if (i < target.Count) target[i] = items[i]; else target.Add(items[i]); }
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
