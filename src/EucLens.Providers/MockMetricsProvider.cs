using EucLens.Core;

namespace EucLens.Providers;

/// <summary>
/// Synthetic data for automated tests and the --smoke-test harness only. All identifiers and measurements are synthetic, including OS/GPU
/// information, and every sample is marked <see cref="SessionInfo.IsDemo"/>. The application never registers it: monitoring rejects such samples.
/// </summary>
public sealed class MockMetricsProvider(int seed = 42) : IMetricsProvider
{
    private readonly Random random = new(seed);
    private readonly object gate = new();
    private RemoteProtocol protocol;
    private DemoScenario scenario;
    private WorkloadContext workload = WorkloadContext.InteractiveGraphics;
    private DateTimeOffset connectedAt;
    private int tick;
    private bool initialized;
    private bool disposed;
    public string Name => "EUC Lens demo simulator";
    public Task<ProviderAvailability> GetAvailabilityAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate) { ObjectDisposedException.ThrowIf(disposed, this); }
        return Task.FromResult(new ProviderAvailability(true, "Synthetic DEMO DATA; no system measurements."));
    }
    public Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!initialized) connectedAt = DateTimeOffset.UtcNow;
            initialized = true;
        }
        return Task.CompletedTask;
    }
    public void Configure(RemoteProtocol protocol, DemoScenario scenario, WorkloadContext workload)
    {
        if (!Enum.IsDefined(protocol) || !Enum.IsDefined(scenario) || !Enum.IsDefined(workload))
            throw new ArgumentOutOfRangeException(nameof(protocol));
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (this.protocol != protocol) connectedAt = DateTimeOffset.UtcNow;
            this.protocol = protocol; this.scenario = scenario; this.workload = workload;
            tick = 0;
        }
    }
    public Task<SessionSnapshot> CollectAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!initialized) throw new InvalidOperationException("Initialize the provider before collecting.");
            tick++;
            var wave = Math.Sin(tick / 7.0);
            double Around(double baseline, double spread, double max = 100) =>
                Math.Clamp(baseline + wave * spread * .65 + (random.NextDouble() - .5) * spread, 0, max);
            Metric M(double baseline, double spread, string unit, double max = 100) => Metric.Available(Around(baseline, spread, max), unit);
            var congested = scenario == DemoScenario.BandwidthCongestion;
            var gpuBound = scenario == DemoScenario.GpuBottleneck;
            var latency = scenario == DemoScenario.HighNetworkLatency;
            var lowFps = scenario == DemoScenario.LowFps || gpuBound || congested;
            var fps = workload == WorkloadContext.Idle ? 2 : workload == WorkloadContext.Office ? 24 : lowFps ? 14 : 58;
            var info = new SessionInfo(protocol, 7, "DEMO\\alex", "DEMO-CLIENT", "DEMO-VDI-01",
                "Windows 11 Enterprise (simulated)", protocol == RemoteProtocol.MicrosoftRdp ? "UDP / TCP (demo)" : "UDP (demo)", connectedAt, true);
            var snapshot = new SessionSnapshot(DateTimeOffset.UtcNow, info, workload,
                new(M(latency ? 225 : congested ? 115 : 28, latency ? 35 : 8, "ms", 1000),
                    M(latency ? 105 : 12, latency ? 20 : 4, "ms", 1000),
                    M(congested ? 20 : 100, congested ? 1 : 5, "Mbps", 1000),
                    M(congested ? 19 : 8, congested ? .5 : 3, "Mbps", 1000),
                    M(latency ? 2 : .1, latency ? .6 : .1, "%")),
                new(M(fps, workload == WorkloadContext.Idle ? 1 : 3, "fps", 120),
                    "Demo hardware encoder", "H.264 (demo)", M(lowFps ? 6 : .2, lowFps ? 1 : .1, "%"),
                    "Adaptive graphics (demo)", true, "1920 × 1080 (demo)"),
                new("NVIDIA virtual GPU (simulated)", M(gpuBound ? 97 : 35, gpuBound ? 2 : 10, "%"),
                    M(gpuBound ? 93 : 42, 3, "%"), M(gpuBound ? 95 : 28, 4, "%"), M(5, 2, "%"), "Demo driver — not measured", HardwareAcceleration: true),
                new(M(gpuBound ? 70 : 24, 8, "%"), M(48, 4, "%"), M(2.5, 1.5, "MB/s", 1000)), scenario);
            return Task.FromResult(snapshot);
        }
    }
    public ValueTask DisposeAsync()
    {
        lock (gate) { disposed = true; initialized = false; }
        return ValueTask.CompletedTask;
    }
}
