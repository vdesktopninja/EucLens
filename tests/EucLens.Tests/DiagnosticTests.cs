using EucLens.Core;
using EucLens.Diagnostics;
using EucLens.Providers;
using Xunit;

namespace EucLens.Tests;

public sealed class DiagnosticTests
{
    internal static async Task<SessionSnapshot> Sample(DemoScenario scenario = DemoScenario.Healthy, WorkloadContext workload = WorkloadContext.InteractiveGraphics)
    {
        await using var provider = new MockMetricsProvider();
        await provider.InitializeAsync();
        provider.Configure(RemoteProtocol.CitrixHdx, scenario, workload);
        return await provider.CollectAsync();
    }
    private static DiagnosticEngine Engine() => new([new LatencyRule(), new FrameRateRule(), new GpuPressureRule(), new BandwidthRule()]);
    [Theory]
    [InlineData(DemoScenario.Healthy, HealthStatus.Healthy)]
    [InlineData(DemoScenario.HighNetworkLatency, HealthStatus.Critical)]
    [InlineData(DemoScenario.LowFps, HealthStatus.Warning)]
    [InlineData(DemoScenario.GpuBottleneck, HealthStatus.Critical)]
    [InlineData(DemoScenario.BandwidthCongestion, HealthStatus.Warning)]
    public async Task HealthReflectsDemoScenario(DemoScenario scenario, HealthStatus expected)
    {
        var report = Engine().Evaluate(await Sample(scenario));
        Assert.Equal(expected, report.Health);
        Assert.Equal(4, report.Results.Count);
        Assert.All(report.Results, r => { Assert.NotEmpty(r.SupportingMetrics); Assert.NotEmpty(r.PossibleCause); Assert.NotEmpty(r.RecommendedAction); });
    }
    [Theory]
    [InlineData(WorkloadContext.Idle)]
    [InlineData(WorkloadContext.Office)]
    public async Task LowFpsAloneDoesNotAlertForStaticWorkload(WorkloadContext workload)
    {
        var sample = await Sample(DemoScenario.LowFps, workload);
        sample = sample with { Graphics = sample.Graphics with { FramesPerSecond = Metric.Available(1, "fps"), DroppedFramesPercent = Metric.Available(10, "%") } };
        Assert.Equal(Severity.Info, new FrameRateRule().Evaluate(sample).Severity);
    }
    [Fact]
    public async Task InteractiveLowFpsNeedsCorrelatedDrops()
    {
        var s = await Sample();
        s = s with { Graphics = s.Graphics with { FramesPerSecond = Metric.Available(1, "fps"), DroppedFramesPercent = Metric.Available(0, "%") } };
        Assert.Equal(Severity.Info, new FrameRateRule().Evaluate(s).Severity);
        s = s with { Graphics = s.Graphics with { DroppedFramesPercent = Metric.Available(3, "%") } };
        Assert.Equal(Severity.Warning, new FrameRateRule().Evaluate(s).Severity);
    }
    [Theory]
    [InlineData(99.9, Severity.Info)]
    [InlineData(100, Severity.Warning)]
    [InlineData(199.9, Severity.Warning)]
    [InlineData(200, Severity.Critical)]
    public async Task LatencyThresholdBoundaries(double rtt, Severity severity)
    {
        var s = await Sample();
        s = s with { Connection = s.Connection with { RoundTripTime = Metric.Available(rtt, "ms") } };
        Assert.Equal(severity, new LatencyRule().Evaluate(s).Severity);
    }
    [Fact]
    public async Task MissingMetricsProduceUnavailableInsteadOfHealthy()
    {
        var s = await Sample();
        s = s with { Connection = s.Connection with { RoundTripTime = Metric.Unavailable("ms", "Protocol does not expose RTT") } };
        var report = Engine().Evaluate(s);
        Assert.Equal(HealthStatus.Unavailable, report.Health);
        var missing = report.Results.Single(r => r.RuleId == "session-latency");
        Assert.Equal(MetricStatus.Unavailable, missing.Status);
        Assert.Equal("Unavailable", missing.SupportingMetrics["Session RTT"]);
        Assert.Contains("does not expose", missing.Description);
    }
    [Fact]
    public async Task MissingMetricDoesNotHideAnAvailableCriticalFinding()
    {
        var s = await Sample(DemoScenario.HighNetworkLatency);
        s = s with { Gpu = s.Gpu with { VramUtilization = Metric.Unavailable("%", "Unavailable") } };
        Assert.Equal(HealthStatus.Critical, Engine().Evaluate(s).Health);
    }
    [Fact]
    public async Task EmptyEngineIsUnavailable()
    {
        Assert.Equal(HealthStatus.Unavailable, new DiagnosticEngine([]).Evaluate(await Sample()).Health);
    }
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task NonPositiveCapacityIsExplicitlyUnavailable(double capacity)
    {
        var s = await Sample();
        s = s with { Connection = s.Connection with { AvailableBandwidth = Metric.Available(capacity, "Mbps") } };
        var result = new BandwidthRule().Evaluate(s);
        Assert.Equal(MetricStatus.Unavailable, result.Status);
        Assert.Contains("greater than zero", result.Description);
        Assert.Equal(HealthStatus.Unavailable, Engine().Evaluate(s).Health);
    }
    [Theory]
    [InlineData(84.9, Severity.Info)]
    [InlineData(85, Severity.Warning)]
    [InlineData(94.9, Severity.Warning)]
    [InlineData(95, Severity.Critical)]
    public async Task GpuPressureThresholdsIncludeMemoryPressure(double utilization, Severity expected)
    {
        var s = await Sample();
        s = s with { Gpu = s.Gpu with { Utilization = Metric.Available(0, "%"), VramUtilization = Metric.Available(utilization, "%") } };
        Assert.Equal(expected, new GpuPressureRule().Evaluate(s).Severity);
    }
    [Theory]
    [InlineData(89.9, Severity.Info)]
    [InlineData(90, Severity.Warning)]
    public async Task BandwidthRatioThreshold(double consumed, Severity expected)
    {
        var s = await Sample();
        s = s with { Connection = s.Connection with { AvailableBandwidth = Metric.Available(100, "Mbps"), BandwidthConsumption = Metric.Available(consumed, "Mbps") } };
        Assert.Equal(expected, new BandwidthRule().Evaluate(s).Severity);
    }
    [Fact]
    public async Task EveryRuleReportsUnavailableForItsMissingRequiredInput()
    {
        var s = await Sample();
        var unavailable = Metric.Unavailable("%", "Not exposed");
        s = s with
        {
            Connection = s.Connection with { RoundTripTime = unavailable, BandwidthConsumption = unavailable },
            Graphics = s.Graphics with { FramesPerSecond = unavailable },
            Gpu = s.Gpu with { Utilization = unavailable }
        };
        var result = Engine().Evaluate(s);
        Assert.Equal(HealthStatus.Unavailable, result.Health);
        Assert.All(result.Results, r => Assert.Equal(MetricStatus.Unavailable, r.Status));
    }

}
