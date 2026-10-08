namespace EucLens.Core;

public enum RemoteProtocol { CitrixHdx, OmnissaBlast, MicrosoftRdp }
public enum DemoScenario { Healthy, HighNetworkLatency, LowFps, GpuBottleneck, BandwidthCongestion }
public enum WorkloadContext { Idle, Office, InteractiveGraphics }
public enum MetricStatus { Available, Unavailable }
public enum Severity { Info, Warning, Critical }
public enum HealthStatus { Healthy, Warning, Critical, Unavailable }

/// <summary>A missing value is never represented as a zero measurement.</summary>
public sealed record Metric(double? Value, string Unit, string? UnavailableReason = null)
{
    public MetricStatus Status => Value.HasValue ? MetricStatus.Available : MetricStatus.Unavailable;
    public static Metric Available(double value, string unit)
    {
        if (!double.IsFinite(value)) throw new ArgumentOutOfRangeException(nameof(value));
        return new(value, unit);
    }
    public static Metric Unavailable(string unit, string reason) => new(null, unit, reason);
    public string Display => Value is { } value
        ? $"{value.ToString("0.0", System.Globalization.CultureInfo.CurrentCulture)} {Unit}"
        : "Unavailable";
}

public sealed record SessionInfo(RemoteProtocol Protocol, int SessionId, string Username,
    string ClientHostname, string ServerHostname, string OperatingSystem, string Transport,
    DateTimeOffset ConnectedAt, bool IsDemo);

public sealed record ConnectionMetrics(Metric RoundTripTime, Metric NetworkLatency,
    Metric AvailableBandwidth, Metric BandwidthConsumption, Metric PacketLoss);
public sealed record GraphicsMetrics(Metric FramesPerSecond, string Encoder, string Codec,
    Metric DroppedFramesPercent, string Mode, bool? HardwareEncoding, string Resolution);
public sealed record GpuMetrics(string Model, Metric Utilization, Metric VramUtilization,
    Metric EncoderUtilization, Metric DecoderUtilization, string DriverVersion);
public sealed record SystemMetrics(Metric CpuUtilization, Metric RamUtilization, Metric DiskActivity);
public sealed record SessionSnapshot(DateTimeOffset Timestamp, SessionInfo Session,
    WorkloadContext Workload, ConnectionMetrics Connection, GraphicsMetrics Graphics,
    GpuMetrics Gpu, SystemMetrics System, DemoScenario? DemoScenario = null);

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
    string PossibleCause, string RecommendedAction);
public sealed record DiagnosticReport(HealthStatus Health, IReadOnlyList<DiagnosticResult> Results);
public interface IDiagnosticRule { DiagnosticResult Evaluate(SessionSnapshot snapshot); }
public interface IDiagnosticEngine { DiagnosticReport Evaluate(SessionSnapshot snapshot); }

public interface ISessionRecorder
{
    bool IsRecording { get; }
    int SampleCount { get; }
    TimeSpan Duration { get; }
    IReadOnlyList<SessionSnapshot> Samples { get; }
    void Start();
    void Stop();
    void Add(SessionSnapshot sample);
    Task ExportJsonAsync(string path, CancellationToken cancellationToken = default);
    Task ExportCsvAsync(string path, CancellationToken cancellationToken = default);
}
