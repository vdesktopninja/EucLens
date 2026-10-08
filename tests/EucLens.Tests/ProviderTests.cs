using EucLens.Core;
using EucLens.Providers;
using Xunit;

namespace EucLens.Tests;

public sealed class ProviderTests
{
    [Theory]
    [InlineData(RemoteProtocol.CitrixHdx)]
    [InlineData(RemoteProtocol.OmnissaBlast)]
    [InlineData(RemoteProtocol.MicrosoftRdp)]
    public async Task EveryProtocolIsExplicitlyDemo(RemoteProtocol protocol)
    {
        await using var provider = new MockMetricsProvider();
        Assert.True((await provider.GetAvailabilityAsync()).IsAvailable);
        await provider.InitializeAsync();
        provider.Configure(protocol, DemoScenario.Healthy, WorkloadContext.InteractiveGraphics);
        var sample = await provider.CollectAsync();
        Assert.Equal(protocol, sample.Session.Protocol);
        Assert.True(sample.Session.IsDemo);
        Assert.Contains("DEMO", sample.Session.ClientHostname);
        Assert.Equal(DemoScenario.Healthy, sample.DemoScenario);
        Assert.True(sample.Timestamp >= sample.Session.ConnectedAt);
    }
    [Theory]
    [InlineData(DemoScenario.Healthy)]
    [InlineData(DemoScenario.HighNetworkLatency)]
    [InlineData(DemoScenario.LowFps)]
    [InlineData(DemoScenario.GpuBottleneck)]
    [InlineData(DemoScenario.BandwidthCongestion)]
    public async Task ScenarioHasBoundedTimeSeriesAndExpectedSignal(DemoScenario scenario)
    {
        await using var provider = new MockMetricsProvider();
        await provider.InitializeAsync();
        provider.Configure(RemoteProtocol.CitrixHdx, scenario, WorkloadContext.InteractiveGraphics);
        var values = new List<SessionSnapshot>();
        for (var i = 0; i < 120; i++) values.Add(await provider.CollectAsync());
        Assert.True(values.Select(v => v.Connection.RoundTripTime.Value).Distinct().Count() > 100);
        foreach (var sample in values)
        {
            Assert.InRange(sample.Gpu.Utilization.Value!.Value, 0, 100);
            Assert.InRange(sample.System.CpuUtilization.Value!.Value, 0, 100);
            Assert.InRange(sample.Graphics.FramesPerSecond.Value!.Value, 0, 120);
        }
        var first = values[0];
        switch (scenario)
        {
            case DemoScenario.Healthy: Assert.True(first.Connection.RoundTripTime.Value < 100); Assert.True(first.Graphics.FramesPerSecond.Value > 40); break;
            case DemoScenario.HighNetworkLatency: Assert.True(first.Connection.RoundTripTime.Value >= 200); break;
            case DemoScenario.LowFps: Assert.True(first.Graphics.FramesPerSecond.Value < 20); Assert.True(first.Graphics.DroppedFramesPercent.Value >= 3); break;
            case DemoScenario.GpuBottleneck: Assert.True(first.Gpu.Utilization.Value >= 95); break;
            case DemoScenario.BandwidthCongestion: Assert.True(first.Connection.BandwidthConsumption.Value / first.Connection.AvailableBandwidth.Value >= .9); break;
        }
    }
    [Fact]
    public async Task ProviderEnforcesInitializationCancellationAndDisposal()
    {
        var provider = new MockMetricsProvider();
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.CollectAsync());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.InitializeAsync(new CancellationToken(true)));
        await provider.InitializeAsync();
        await provider.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => provider.CollectAsync());
        Assert.Throws<ObjectDisposedException>(() => provider.Configure(RemoteProtocol.CitrixHdx, DemoScenario.Healthy, WorkloadContext.Idle));
    }
    [Fact]
    public async Task AllVendorAdaptersAreUnavailableAndReturnNoFabricatedMeasurements()
    {
        IMetricsProvider[] providers = [new CitrixHdxProvider(), new OmnissaBlastProvider(), new MicrosoftRdpProvider(), new NvidiaGpuProvider()];
        foreach (var provider in providers)
        {
            await using (provider)
            {
                Assert.False((await provider.GetAvailabilityAsync()).IsAvailable);
                await Assert.ThrowsAsync<NotSupportedException>(() => provider.InitializeAsync());
                await Assert.ThrowsAsync<NotSupportedException>(() => provider.CollectAsync());
            }
        }
    }
    [Fact]
    public void MetricsDistinguishZeroFromUnavailableAndRejectNonFiniteValues()
    {
        Assert.Equal(MetricStatus.Available, Metric.Available(0, "fps").Status);
        Assert.Equal(MetricStatus.Unavailable, Metric.Unavailable("fps", "No source").Status);
        Assert.Equal("Unavailable", Metric.Unavailable("fps", "No source").Display);
        Assert.Throws<ArgumentOutOfRangeException>(() => Metric.Available(double.NaN, "%"));
    }
}
