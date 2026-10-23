namespace EucLens.Core;

public enum RemoteProtocol { CitrixHdx, OmnissaBlast, MicrosoftRdp, Unknown }
public enum DemoScenario { Healthy, HighNetworkLatency, LowFps, GpuBottleneck, BandwidthCongestion }
public enum WorkloadContext { Idle, Office, InteractiveGraphics, VideoPlayback }
public enum MetricStatus { Available, Unavailable, Stale, Failed }
public enum MonitoringMode { RealSession, DemoMode }
public enum Severity { Info, Warning, Critical }
public enum HealthStatus { Healthy, Warning, Critical, Unavailable }
/// <summary>What a measurement describes: the whole machine, this Windows session, or one process.</summary>
public enum MetricScope { Machine, Session, Process }
public enum MetricCategory { BlastSession, BlastImaging, WindowsCpu, WindowsMemory, WindowsDisk, Gpu, Display }
public enum DiagnosticArea { Connection, Host }

/// <summary>A missing value is never represented as a zero measurement.</summary>
public sealed record Metric(double? Value, string Unit, string? UnavailableReason = null,
    string? Source = null, DateTimeOffset? Timestamp = null, long? RawValue = null,
    MetricStatus? SampleStatus = null, string? Error = null, MetricScope? Scope = null)
{
    public MetricStatus Status => SampleStatus ?? (Value.HasValue ? MetricStatus.Available : MetricStatus.Unavailable);
    public static Metric Available(double value, string unit)
    {
        if (!double.IsFinite(value)) throw new ArgumentOutOfRangeException(nameof(value));
        return new(value, unit);
    }
    public static Metric Unavailable(string unit, string reason) => new(null, unit, reason);
    public string Display => Value is { } value
        ? $"{value.ToString("0.0", System.Globalization.CultureInfo.CurrentCulture)} {Unit}"
        : Source is null ? "Unavailable" : "N/A";
}

public sealed record SessionInfo(RemoteProtocol Protocol, int SessionId, string Username,
    string ClientHostname, string ServerHostname, string OperatingSystem, string Transport,
    DateTimeOffset ConnectedAt, bool IsDemo, string? ProtocolDetection = null);

public sealed record ConnectionMetrics(Metric RoundTripTime, Metric NetworkLatency,
    Metric AvailableBandwidth, Metric BandwidthConsumption, Metric PacketLoss);
public sealed record GraphicsMetrics(Metric FramesPerSecond, string Encoder, string Codec,
    Metric DroppedFramesPercent, string Mode, bool? HardwareEncoding, string Resolution);
public sealed record GpuMetrics(string Model, Metric Utilization, Metric VramUtilization,
    Metric EncoderUtilization, Metric DecoderUtilization, string DriverVersion,
    string? VramUsedTotal = null, string? AdapterKind = null, bool? HardwareAcceleration = null);
public sealed record SystemMetrics(Metric CpuUtilization, Metric RamUtilization, Metric DiskActivity);
public sealed record SessionSnapshot(DateTimeOffset Timestamp, SessionInfo Session,
    WorkloadContext Workload, ConnectionMetrics Connection, GraphicsMetrics Graphics,
    GpuMetrics Gpu, SystemMetrics System, DemoScenario? DemoScenario = null,
    IReadOnlyDictionary<string, Metric>? BlastMetrics = null, string? CollectionStatus = null,
    IReadOnlyList<MetricEntry>? HostMetrics = null, DiagnosticSnapshot? Diagnostics = null);

/// <summary>One protocol-independent host measurement. Text carries non-numeric facts such as a GPU model.</summary>
public sealed record MetricEntry(string Id, MetricCategory Category, string Name, Metric Metric, string? Text = null)
{
    public string Display => Text ?? Metric.Display;
}

public sealed record ProviderAvailability(bool IsAvailable, string Description);
public interface IMetricsProvider : IAsyncDisposable
{
    string Name { get; }
    Task<ProviderAvailability> GetAvailabilityAsync(CancellationToken cancellationToken = default);
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task<SessionSnapshot> CollectAsync(CancellationToken cancellationToken = default);
}
public interface IDemoMetricsProvider : IMetricsProvider
{
    void Configure(RemoteProtocol protocol, DemoScenario scenario, WorkloadContext workload);
}

public sealed record DiagnosticResult(string RuleId, Severity Severity, MetricStatus Status,
    string Title, string Description, IReadOnlyDictionary<string, string> SupportingMetrics,
    string PossibleCause, string RecommendedAction, DiagnosticArea Area = DiagnosticArea.Connection);
/// <summary>Health is also reported per area so missing host data never hides connection findings, or vice versa.</summary>
public sealed record DiagnosticReport(HealthStatus Health, IReadOnlyList<DiagnosticResult> Results,
    HealthStatus ConnectionHealth = HealthStatus.Unavailable, HealthStatus HostHealth = HealthStatus.Unavailable,
    IReadOnlyList<DomainAssessment>? Assessments = null, IReadOnlyList<DiagnosticFinding>? Findings = null,
    IReadOnlyList<DiagnosticHypothesis>? Hypotheses = null, DiagnosticSnapshot? Snapshot = null,
    IReadOnlyList<AssessmentInput>? Inputs = null);
public interface IDiagnosticRule
{
    DiagnosticResult Evaluate(SessionSnapshot snapshot);
    bool AppliesTo(SessionSnapshot snapshot) => true;
    /// <summary>Clears sample history kept by sustained rules, e.g. after a mode switch.</summary>
    void Reset() { }
}
public interface IDiagnosticEngine
{
    DiagnosticReport Evaluate(SessionSnapshot snapshot);
    void Reset() { }
}

public interface ISessionRecorder
{
    bool IsRecording { get; }
    int SampleCount { get; }
    TimeSpan Duration { get; }
    IReadOnlyList<SessionSnapshot> Samples { get; }
    void Start();
    void Stop();
    /// <summary>Starts a recording that is labelled as demo or real data. Recorders that do not persist ignore the label.</summary>
    void Start(bool isDemo) => Start();
    void Stop(RecordingEndReason reason) => Stop();
    /// <summary>Stops and waits until a persisted recording is finalized on disk.</summary>
    Task StopAsync(RecordingEndReason reason = RecordingEndReason.UserStopped) { Stop(reason); return Task.CompletedTask; }
    /// <summary>Records a pause, resume, availability change or similar in a persisted recording.</summary>
    void Mark(RecordingMark mark) { }
    /// <summary>The recording ended by itself, for example because of a storage failure. May be raised on a background thread.</summary>
    event Action<RecordingEndReason>? Ended { add { } remove { } }
    RecordingSinkStatus? SinkStatus => null;
    /// <summary>True when samples are streamed to disk, so they are saved without an export.</summary>
    bool IsPersistent => false;
    void Add(SessionSnapshot sample);
    Task ExportJsonAsync(string path, CancellationToken cancellationToken = default);
    Task ExportCsvAsync(string path, CancellationToken cancellationToken = default);
}
