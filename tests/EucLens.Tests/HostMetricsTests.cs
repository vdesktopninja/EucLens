using EucLens.App;
using EucLens.App.ViewModels;
using EucLens.Core;
using EucLens.Diagnostics;
using EucLens.Providers;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EucLens.Tests;

/// <summary>System, GPU, NVML and display collection with mocked native interfaces; no GPU or Horizon Agent needed.</summary>
public sealed class HostMetricsTests
{
    private const double GiB = 1024d * 1024 * 1024;

    // ---------- CPU / memory / disk ----------

    [Fact]
    public void CpuNeedsTwoSamplesThenUsesSystemTimeDeltas()
    {
        var native = new FakeSystemNative { Times = [new(0, 0, 0), new(300, 600, 400)] };
        using var provider = new WindowsSystemMetricsProvider(native, new FakeCounterReader());

        var first = provider.Collect(default).Cpu;
        var second = provider.Collect(default).Cpu;

        Assert.Equal(MetricStatus.Unavailable, first.Status);
        Assert.Contains("Warming up", first.UnavailableReason);
        // Kernel (600) includes idle (300): busy = (600 + 400) − 300 = 700 of 1000.
        Assert.Equal(70, second.Value!.Value, 6);
        Assert.Equal(MetricScope.Machine, second.Scope);
    }

    [Fact]
    public void CpuFailureIsNeverReportedAsZero()
    {
        var native = new FakeSystemNative { Times = [null] };
        using var provider = new WindowsSystemMetricsProvider(native, new FakeCounterReader());

        var cpu = provider.Collect(default).Cpu;

        Assert.Equal(MetricStatus.Failed, cpu.Status);
        Assert.Null(cpu.Value);
        Assert.Equal("N/A", cpu.Display);
    }

    [Theory]
    [InlineData(100, 200, 100)] // no time elapsed
    [InlineData(50, 100, 50)]   // counters moved backwards
    public void CpuWithoutElapsedOrBackwardsTimeIsStale(ulong idle, ulong kernel, ulong user)
    {
        var native = new FakeSystemNative { Times = [new(100, 200, 100), new(idle, kernel, user)] };
        using var provider = new WindowsSystemMetricsProvider(native, new FakeCounterReader());
        provider.Collect(default);

        Assert.Equal(MetricStatus.Stale, provider.Collect(default).Cpu.Status);
    }

    [Fact]
    public void MemoryUsesPhysicalRamOnly()
    {
        var native = new FakeSystemNative { Memory = new((ulong)(16 * GiB), (ulong)(4 * GiB)) };
        using var provider = new WindowsSystemMetricsProvider(native, new FakeCounterReader());

        var sample = provider.Collect(default);
        Metric Entry(string id) => sample.Entries.Single(e => e.Id == id).Metric;

        Assert.Equal(75, sample.RamUtilization.Value!.Value, 6);
        Assert.Equal(16, Entry("memory.totalGiB").Value!.Value, 6);
        Assert.Equal(4, Entry("memory.availableGiB").Value!.Value, 6);
        Assert.Equal(12, Entry("memory.usedGiB").Value!.Value, 6);
        Assert.Equal((long)(4 * GiB), Entry("memory.availableGiB").RawValue);
        Assert.Equal("GiB", Entry("memory.usedGiB").Unit);
    }

    [Fact]
    public void InvalidMemoryStatusFails()
    {
        var native = new FakeSystemNative { Memory = new(100, 200) };
        using var provider = new WindowsSystemMetricsProvider(native, new FakeCounterReader());

        Assert.Equal(MetricStatus.Failed, provider.Collect(default).RamUtilization.Status);
    }

    [Fact]
    public void DiskConvertsBytesPerSecondAndIdleTime()
    {
        var counters = new FakeCounterReader();
        counters.Set("Disk Read Bytes/sec", 5_000_000);
        counters.Set("Disk Write Bytes/sec", 2_500_000);
        counters.Set("% Idle Time", 30);
        using var provider = new WindowsSystemMetricsProvider(new FakeSystemNative(), counters);

        var sample = provider.Collect(default);
        Metric Entry(string id) => sample.Entries.Single(e => e.Id == id).Metric;

        Assert.Equal(5, Entry("disk.readMBps").Value);
        Assert.Equal(2.5, Entry("disk.writeMBps").Value);
        Assert.Equal(70, Entry("disk.activePercent").Value);
        Assert.Equal(7.5, sample.DiskThroughput.Value);
        Assert.Equal(@"\PhysicalDisk(_Total)\Disk Read Bytes/sec", Entry("disk.readMBps").Source);
    }

    [Fact]
    public void DiskWarmUpAndFailuresPropagateWithoutZero()
    {
        var counters = new FakeCounterReader();
        counters.Set("Disk Read Bytes/sec", 0, MetricStatus.Unavailable, "Warming up: this counter requires two samples.");
        counters.Throw("Disk Write Bytes/sec", new InvalidOperationException("Category does not exist."));
        using var provider = new WindowsSystemMetricsProvider(new FakeSystemNative(), counters);

        var sample = provider.Collect(default);

        Assert.Equal(MetricStatus.Unavailable, sample.Entries.Single(e => e.Id == "disk.readMBps").Metric.Status);
        Assert.Equal(MetricStatus.Failed, sample.Entries.Single(e => e.Id == "disk.writeMBps").Metric.Status);
        Assert.Null(sample.DiskThroughput.Value);
        Assert.NotEqual(MetricStatus.Available, sample.DiskThroughput.Status);
    }

    [Fact]
    public void StaticInventoryIsQueriedOnceAndReused()
    {
        var native = new FakeSystemNative();
        using var provider = new WindowsSystemMetricsProvider(native, new FakeCounterReader());

        var a = provider.Collect(default).Entries.Single(e => e.Id == "cpu.model");
        var b = provider.Collect(default).Entries.Single(e => e.Id == "cpu.model");

        Assert.Same(a, b);
        Assert.Equal(1, native.InventoryCalls);
        Assert.Equal("Test CPU", a.Text);
    }

    // ---------- GPU classification and selection ----------

    [Theory]
    [InlineData(0x10DE, "NVIDIA A16-2Q", false, false, 2048, GpuAdapterKind.NvidiaVirtualGpu)]
    [InlineData(0x10DE, "GRID T4-4B", false, false, 4096, GpuAdapterKind.NvidiaVirtualGpu)]
    [InlineData(0x10DE, "NVIDIA RTX A4000", false, false, 16384, GpuAdapterKind.NvidiaGpu)]
    [InlineData(0x1002, "AMD Radeon Pro V620", false, false, 8192, GpuAdapterKind.AmdGpu)]
    [InlineData(0x8086, "Intel(R) UHD Graphics 770", false, false, 128, GpuAdapterKind.IntelIntegratedGpu)]
    [InlineData(0x1414, "Microsoft Basic Render Driver", true, false, 0, GpuAdapterKind.MicrosoftBasicRender)]
    [InlineData(0x1414, "Microsoft Basic Display Adapter", false, false, 0, GpuAdapterKind.MicrosoftBasicDisplay)]
    [InlineData(0x15AD, "VMware SVGA 3D", false, false, 0, GpuAdapterKind.VmwareSvga)]
    [InlineData(0x1414, "Microsoft Remote Display Adapter", false, true, 0, GpuAdapterKind.RemoteOrIndirect)]
    [InlineData(0x0000, "Citrix Indirect Display Adapter", false, false, 0, GpuAdapterKind.RemoteOrIndirect)]
    public void ClassifiesAdapters(int vendor, string name, bool software, bool remote, int dedicatedMiB, GpuAdapterKind expected) =>
        Assert.Equal(expected, Adapter(0, name, (uint)vendor, dedicatedMiB, software: software, remote: remote).Kind);

    [Fact]
    public void SelectsHardwareGpuRatherThanFirstEnumeratedAdapter()
    {
        GpuAdapterInfo[] adapters = [Adapter(0, "VMware SVGA 3D", 0x15AD, 0), Adapter(1, "Microsoft Basic Render Driver", 0x1414, 0, software: true),
            Adapter(2, "Intel(R) UHD Graphics", 0x8086, 128), Adapter(3, "NVIDIA A16-2Q", 0x10DE, 2048)];

        var (selected, reason) = GpuClassifier.SelectPrimary(adapters);

        Assert.Equal(3, selected!.Index);
        Assert.Contains("discrete before integrated", reason);
    }

    [Fact]
    public void NoHardwareGpuExplainsEveryAdapter()
    {
        var (selected, reason) = GpuClassifier.SelectPrimary([Adapter(0, "VMware SVGA 3D", 0x15AD, 0), Adapter(1, "Microsoft Basic Render Driver", 0x1414, 0, software: true)]);

        Assert.Null(selected);
        Assert.Contains("VMware SVGA 3D", reason);
        Assert.Contains("Basic Render", reason);
    }

    // ---------- GPU engine accounting ----------

    [Fact]
    public void AdapterUtilizationIsBusiestEngineNotSumOfEngines()
    {
        const string luid = "luid_0x00000000_0x0000C3F2";
        var result = GpuEngineMath.Aggregate([
            new($"pid_100_{luid}_phys_0_eng_0_engtype_3D", 30),
            new($"pid_200_{luid}_phys_0_eng_0_engtype_3D", 25),
            new($"pid_100_{luid}_phys_0_eng_1_engtype_Copy", 10),
            new($"pid_300_{luid}_phys_0_eng_2_engtype_VideoEncode", 40),
        ], pid => pid == 100)["0x00000000_0x0000C3F2"];

        Assert.Equal(55, result.Utilization);           // 3D: 30 + 25 (same engine, time-shared)
        Assert.Equal(30, result.SessionUtilization);    // only pid 100 is in this session
        Assert.Equal(40, result.Encode);
        Assert.Null(result.Decode);
        Assert.Equal("3D", result.BusiestEngine);
        Assert.Equal(["3D", "Copy", "VideoEncode"], result.EngineTypes);
    }

    [Fact]
    public void EngineSumsAreClampedAndAdaptersKeptSeparate()
    {
        var result = GpuEngineMath.Aggregate([
            new("pid_1_luid_0x00000000_0x00000001_phys_0_eng_0_engtype_3D", 70),
            new("pid_2_luid_0x00000000_0x00000001_phys_0_eng_0_engtype_3D", 45),
            new("pid_1_luid_0x00000000_0x00000002_phys_0_eng_0_engtype_3D", 5),
            new("garbage", 99),
            new("pid_3_luid_0x00000000_0x00000002_phys_0_eng_0_engtype_3D", double.NaN),
        ], _ => true);

        Assert.Equal(100, result["0x00000000_0x00000001"].Utilization);
        Assert.Equal(5, result["0x00000000_0x00000002"].Utilization);
    }

    [Fact]
    public void LuidFormatMatchesCounterInstances()
    {
        Assert.Equal("0x00000000_0x0000C3F2", GpuAdapterInfo.FormatLuid(0, 0xC3F2));
        Assert.Equal("0x00000000_0x0000C3F2", GpuEngineMath.MemoryInstanceLuid("luid_0x00000000_0x0000c3f2_phys_0"));
    }

    // ---------- GPU provider ----------

    [Fact]
    public void MissingGpuCountersAreNotApplicableWithReason()
    {
        var counters = new FakeGpuCounters { EngineStatus = MetricStatus.Unavailable, EngineReason = "\"GPU Engine\" counters are not present." };
        using var provider = Gpu([Adapter(0, "NVIDIA A16-2Q", 0x10DE, 2048)], counters);

        var gpu = provider.Collect(1, default).Dashboard;

        Assert.Equal("NVIDIA A16-2Q", gpu.Model);
        Assert.Equal(MetricStatus.Unavailable, gpu.Utilization.Status);
        Assert.Contains("not present", gpu.Utilization.UnavailableReason);
        Assert.Equal("N/A", gpu.EncoderUtilization.Display);
    }

    [Fact]
    public void DedicatedAndSharedGpuMemoryAreSeparate()
    {
        var counters = new FakeGpuCounters();
        counters.Engines.Add(new("pid_1_luid_0x00000000_0x0000AAAA_phys_0_eng_0_engtype_3D", 12));
        counters.Memory.Add(new("luid_0x00000000_0x0000AAAA_phys_0", 1 * GiB, 3 * GiB));
        using var provider = Gpu([Adapter(0, "NVIDIA RTX A4000", 0x10DE, 4096, luidLow: 0xAAAA)], counters);

        var sample = provider.Collect(1, default);
        Metric Entry(string id) => sample.Entries.Single(e => e.Id == id).Metric;

        Assert.Equal(25, sample.Dashboard.VramUtilization.Value!.Value, 6);   // 1 GiB of 4 GiB dedicated
        Assert.Equal(1, Entry("gpu.0.dedicatedUsedGiB").Value!.Value, 6);
        Assert.Equal(3, Entry("gpu.0.sharedUsedGiB").Value!.Value, 6);
        Assert.Contains("1.00 / 4.00 GiB", sample.Dashboard.VramUsedTotal!.Replace(',', '.'));
        Assert.Equal(12, sample.Dashboard.Utilization.Value);
        Assert.Equal(MetricScope.Machine, sample.Dashboard.Utilization.Scope);
        Assert.Equal(MetricScope.Session, Entry("gpu.0.sessionUtilization").Scope);
    }

    [Fact]
    public void EncoderIsNotApplicableWhenAdapterHasNoEncodeEngine()
    {
        var counters = new FakeGpuCounters();
        counters.Engines.Add(new("pid_1_luid_0x00000000_0x0000AAAA_phys_0_eng_0_engtype_3D", 12));
        using var provider = Gpu([Adapter(0, "NVIDIA RTX A4000", 0x10DE, 4096, luidLow: 0xAAAA)], counters);

        var gpu = provider.Collect(1, default).Dashboard;

        Assert.Equal(MetricStatus.Unavailable, gpu.EncoderUtilization.Status);
        Assert.Contains("no video encode engine", gpu.EncoderUtilization.UnavailableReason);
    }

    [Fact]
    public void MultipleAdaptersAreReportedIndividually()
    {
        var counters = new FakeGpuCounters();
        counters.Engines.Add(new("pid_1_luid_0x00000000_0x00000001_phys_0_eng_0_engtype_3D", 80));
        counters.Engines.Add(new("pid_1_luid_0x00000000_0x00000002_phys_0_eng_0_engtype_3D", 5));
        using var provider = Gpu([Adapter(0, "Intel(R) UHD Graphics", 0x8086, 128, luidLow: 1), Adapter(1, "NVIDIA RTX A4000", 0x10DE, 16384, luidLow: 2)], counters);

        var sample = provider.Collect(1, default);

        Assert.Equal(80, sample.Entries.Single(e => e.Id == "gpu.0.utilization").Metric.Value);
        Assert.Equal(5, sample.Entries.Single(e => e.Id == "gpu.1.utilization").Metric.Value);
        Assert.Equal("NVIDIA RTX A4000", sample.Dashboard.Model);
        Assert.Equal(5, sample.Dashboard.Utilization.Value);
    }

    [Fact]
    public void UnavailableNvmlFallsBackToWindowsCounters()
    {
        var counters = new FakeGpuCounters();
        counters.Engines.Add(new("pid_1_luid_0x00000000_0x0000AAAA_phys_0_eng_0_engtype_3D", 33));
        using var provider = Gpu([Adapter(0, "NVIDIA RTX A4000", 0x10DE, 4096, luidLow: 0xAAAA)], counters, new FakeNvml(available: false));

        var sample = provider.Collect(1, default);

        Assert.Equal(33, sample.Dashboard.Utilization.Value);
        Assert.StartsWith(@"\GPU Engine", sample.Dashboard.Utilization.Source);
        var status = sample.Entries.Single(e => e.Id == "nvml.status");
        Assert.Equal(MetricStatus.Unavailable, status.Metric.Status);
        Assert.Contains("not found", status.Text);
    }

    [Fact]
    public void NvmlFillsOnlyFieldsWindowsCannotAndKeepsItsSource()
    {
        var counters = new FakeGpuCounters { EngineStatus = MetricStatus.Unavailable, EngineReason = "No engine counters." };
        var nvml = new FakeNvml(available: true) { Devices = [Device(0, gpu: 47, encoder: new(null, 3, "Not supported (NVML 3)"))] };
        using var provider = Gpu([Adapter(0, "NVIDIA A16-2Q", 0x10DE, 2048)], counters, nvml);

        var sample = provider.Collect(1, default);

        Assert.Equal(47, sample.Dashboard.Utilization.Value);
        Assert.StartsWith("NVML", sample.Dashboard.Utilization.Source);
        Assert.Equal(MetricStatus.Unavailable, sample.Dashboard.EncoderUtilization.Status);
        var nvenc = sample.Entries.Single(e => e.Id == "nvml.0.encoder").Metric;
        Assert.Equal(MetricStatus.Unavailable, nvenc.Status);              // NOT_SUPPORTED is unavailable, not a failure
        Assert.Contains("Not supported", nvenc.UnavailableReason);
        Assert.Contains("not VRAM", sample.Entries.Single(e => e.Id == "nvml.0.memoryControllerUtilization").Name);
        Assert.Contains("vGPU guest", sample.Dashboard.AdapterKind);
    }

    [Fact]
    public void AmbiguousNvmlDevicesAreNotLinked()
    {
        var counters = new FakeGpuCounters { EngineStatus = MetricStatus.Unavailable, EngineReason = "No engine counters." };
        var nvml = new FakeNvml(available: true) { Devices = [Device(0, gpu: 47), Device(1, gpu: 3)] };
        using var provider = Gpu([Adapter(0, "NVIDIA A16-2Q", 0x10DE, 2048), Adapter(1, "NVIDIA A16-2Q", 0x10DE, 2048, luidLow: 2)], counters, nvml);

        var gpu = provider.Collect(1, default).Dashboard;

        Assert.Equal(MetricStatus.Unavailable, gpu.Utilization.Status);
    }

    [Fact]
    public void AdapterInventoryIsNotEnumeratedEverySample()
    {
        var clock = new ManualClock();
        var source = new FakeAdapterSource([Adapter(0, "NVIDIA RTX A4000", 0x10DE, 4096)]);
        using var provider = new WindowsGpuMetricsProvider(source, new FakeGpuCounters(), null, clock);

        for (var i = 0; i < 10; i++) { provider.Collect(1, default); clock.Advance(TimeSpan.FromSeconds(1)); }
        Assert.Equal(1, source.Calls);
        clock.Advance(TimeSpan.FromSeconds(60));
        provider.Collect(1, default);
        Assert.Equal(2, source.Calls);
    }

    [Fact]
    public void GpuCollectorFailuresBecomeFailedMetrics()
    {
        var counters = new FakeGpuCounters { Failure = new UnauthorizedAccessException("Access denied") };
        using var provider = Gpu([Adapter(0, "NVIDIA RTX A4000", 0x10DE, 4096)], counters);

        var gpu = provider.Collect(1, default).Dashboard;

        Assert.Equal(MetricStatus.Failed, gpu.Utilization.Status);
        Assert.Contains("Access denied", gpu.Utilization.UnavailableReason);
    }

    [Fact]
    public void GpuProviderDisposesCountersAndNvml()
    {
        var counters = new FakeGpuCounters();
        var nvml = new FakeNvml(available: true);
        var provider = Gpu([], counters, nvml);
        provider.Collect(1, default);

        provider.Dispose();

        Assert.True(counters.Disposed);
        Assert.True(nvml.Disposed);
    }

    [Fact]
    public void NvmlClientWithoutLibraryIsUnavailable()
    {
        using var client = new NvmlClient([Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "nvml.dll")]);

        Assert.False(client.IsAvailable);
        Assert.Empty(client.Sample());
        Assert.False(string.IsNullOrWhiteSpace(client.Status));
    }

    // ---------- Display ----------

    [Fact]
    public void DisplayOrdersPrimaryFirstAndLabelsTheSessionDesktop()
    {
        var source = new FakeDisplaySource(new([new("\\\\.\\DISPLAY2", false, 1920, 0, 2560, 1440, 60, 144), new("\\\\.\\DISPLAY1", true, 0, 0, 1920, 1080, 60, 96)], true));
        var provider = new DisplayInformationProvider(source, new ManualClock());

        var sample = provider.Collect(default);

        Assert.Equal("1920 × 1080", sample.PrimaryResolution);
        Assert.Equal(2, sample.Entries.Single(e => e.Id == "display.monitorCount").Metric.Value);
        Assert.Equal(100, sample.Entries.Single(e => e.Id == "display.primaryScalePercent").Metric.Value);
        Assert.Contains("150 %", sample.Entries.Single(e => e.Id == "display.monitor2").Text);
        Assert.Contains("not the client", sample.Entries.Single(e => e.Id == "display.remoteSession").Text);
        Assert.All(sample.Entries, e => Assert.Equal(MetricScope.Session, e.Metric.Scope));
    }

    [Fact]
    public void DisplayIsCachedAndFailuresAreReported()
    {
        var clock = new ManualClock();
        var source = new FakeDisplaySource(new([new("\\\\.\\DISPLAY1", true, 0, 0, 1920, 1080, 60, 96)], false));
        var provider = new DisplayInformationProvider(source, clock);
        provider.Collect(default); provider.Collect(default);
        Assert.Equal(1, source.Calls);

        source.Failure = new System.ComponentModel.Win32Exception(5, "Access denied");
        clock.Advance(TimeSpan.FromSeconds(6));
        var failed = provider.Collect(default);

        Assert.Equal("N/A", failed.PrimaryResolution);
        Assert.Equal(MetricStatus.Failed, failed.Entries.Single().Metric.Status);
    }

    // ---------- Composite provider, protocol detection, lifecycle ----------

    [Fact]
    public async Task RealSessionMergesHostMetricsAndReportsUndetectedProtocol()
    {
        var host = new FakeHost();
        await using var provider = new RealSessionMetricsProvider(new OmnissaBlastProvider(new EmptyBlastReader(), new NoAgentContext()), host, new FakeProbe());

        var s = await provider.CollectAsync();

        Assert.Equal(RemoteProtocol.Unknown, s.Session.Protocol);
        Assert.Contains("Not detected", s.Session.ProtocolDetection);
        Assert.Contains("protocol type 2", s.Session.ProtocolDetection);
        Assert.Equal(42, s.System.CpuUtilization.Value);
        Assert.Equal("Test GPU", s.Gpu.Model);
        Assert.Equal("2560 × 1440 (session desktop)", s.Graphics.Resolution);
        Assert.Equal("Windows 11 Pro (test)", s.Session.OperatingSystem);
        Assert.Equal(MetricStatus.Unavailable, s.Connection.RoundTripTime.Status);
        Assert.NotNull(s.HostMetrics);
        await provider.DisposeAsync();
        Assert.True(host.Disposed);
    }

    [Fact]
    public async Task BlastInstanceMakesTheProtocolOmnissaBlast()
    {
        await using var provider = new RealSessionMetricsProvider(new OmnissaBlastProvider(new OneSessionBlastReader(), new ActiveContext()), new FakeHost(), new FakeProbe());

        var s = await provider.CollectAsync();

        Assert.Equal(RemoteProtocol.OmnissaBlast, s.Session.Protocol);
        Assert.DoesNotContain("protocol type", s.Session.ProtocolDetection);
        Assert.Equal(34, s.Connection.RoundTripTime.Value);
    }

    [Fact]
    public async Task HostFailureDoesNotHideBlastMetrics()
    {
        var system = new WindowsSystemMetricsProvider(new FakeSystemNative { Failure = new System.ComponentModel.Win32Exception(5, "denied") }, new FakeCounterReader());
        using var host = new HostMetricsCollector(system, Gpu([], new FakeGpuCounters()), new DisplayInformationProvider(new FakeDisplaySource(new([], false))));
        await using var provider = new RealSessionMetricsProvider(new OmnissaBlastProvider(new OneSessionBlastReader(), new ActiveContext()), host, new FakeProbe());

        var s = await provider.CollectAsync();

        Assert.Equal(34, s.Connection.RoundTripTime.Value);
        Assert.Equal(MetricStatus.Failed, s.System.CpuUtilization.Status);
        Assert.Equal("N/A", s.Graphics.Resolution);
    }

    [Fact]
    public void RealCollectorsRunOnWindowsWithoutNvidiaHardware()
    {
        // Executes the native collectors only on Windows (CI); elsewhere they must degrade to N/A.
        using var host = new HostMetricsCollector();
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        host.Collect(process.SessionId, default);
        Thread.Sleep(1100);
        var sample = host.Collect(process.SessionId, default);
        var nvml = sample.Entries.Single(e => e.Id == "nvml.status");
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(MetricStatus.Available, sample.System.CpuUtilization.Status);
            Assert.InRange(sample.System.CpuUtilization.Value!.Value, 0, 100);
            Assert.InRange(sample.System.RamUtilization.Value!.Value, 0.1, 100);
            Assert.True(sample.Entries.Single(e => e.Id == "cpu.logicalProcessors").Metric.Value >= 1);
            Assert.NotEqual("N/A", sample.WindowsVersion);
        }
        else
        {
            Assert.NotEqual(MetricStatus.Available, sample.System.CpuUtilization.Status);
            Assert.NotEqual(MetricStatus.Available, nvml.Metric.Status);
        }
    }

    // ---------- Diagnostics ----------

    private static readonly HostDiagnosticThresholds Thresholds = new() { WindowSamples = 10, RequiredFraction = 0.8, CpuPercent = 90 };

    [Fact]
    public async Task SustainedCpuNeedsAFullWindow()
    {
        var rule = new SustainedCpuRule(Thresholds);
        var s = await Real(cpu: 99);
        for (var i = 0; i < 9; i++) Assert.Equal(MetricStatus.Unavailable, rule.Evaluate(s).Status);

        var result = rule.Evaluate(s);

        Assert.Equal(Severity.Warning, result.Severity);
        Assert.Equal(DiagnosticArea.Host, result.Area);
        rule.Reset();
        Assert.Equal(MetricStatus.Unavailable, rule.Evaluate(s).Status);
    }

    [Fact]
    public async Task ShortCpuSpikesDoNotWarn()
    {
        var rule = new SustainedCpuRule(Thresholds);
        var high = await Real(cpu: 99);
        var normal = await Real(cpu: 20);
        DiagnosticResult result = null!;
        for (var i = 0; i < 10; i++) result = rule.Evaluate(i % 3 == 0 ? high : normal);

        Assert.Equal(Severity.Info, result.Severity);
        Assert.Equal(MetricStatus.Available, result.Status);
    }

    [Fact]
    public async Task StaleHostSampleIsNeverHealthy()
    {
        var engine = new DiagnosticEngine([new SustainedCpuRule(Thresholds)]);
        var s = await Real(cpu: 10);
        for (var i = 0; i < 10; i++) engine.Evaluate(s);
        Assert.Equal(HealthStatus.Healthy, engine.Evaluate(s).HostHealth);

        var stale = s with { System = s.System with { CpuUtilization = new(null, "%", "not advanced", "test", SampleStatus: MetricStatus.Stale) } };
        var report = engine.Evaluate(stale);

        Assert.Equal(HealthStatus.Unavailable, report.HostHealth);
        Assert.Equal(MetricStatus.Stale, report.Results.Single().Status);
    }

    [Fact]
    public async Task ConnectionAndHostHealthAreSeparate()
    {
        var engine = new DiagnosticEngine([new LatencyRule(), new SustainedCpuRule(Thresholds)]);
        var s = await Real(cpu: 99, rtt: 250);
        DiagnosticReport report = null!;
        for (var i = 0; i < 10; i++) report = engine.Evaluate(s);

        Assert.Equal(HealthStatus.Critical, report.ConnectionHealth);
        Assert.Equal(HealthStatus.Warning, report.HostHealth);

        var noBlast = s with { Connection = s.Connection with { RoundTripTime = new(null, "ms", "No Blast session", "test", SampleStatus: MetricStatus.Unavailable) } };
        report = engine.Evaluate(noBlast);
        Assert.Equal(HealthStatus.Unavailable, report.ConnectionHealth);
        Assert.Equal(HealthStatus.Warning, report.HostHealth);
    }

    [Fact]
    public async Task SingleSampleGpuRuleOnlyAppliesToDemo()
    {
        var engine = new DiagnosticEngine([new GpuPressureRule()]);
        var real = await Real(cpu: 10);
        real = real with { Gpu = real.Gpu with { Utilization = Metric.Available(99, "%"), VramUtilization = Metric.Available(99, "%") } };

        Assert.Empty(engine.Evaluate(real).Results);
        Assert.Single(engine.Evaluate(await DiagnosticTests.Sample(DemoScenario.GpuBottleneck)).Results);
    }

    [Fact]
    public async Task GraphicsBottleneckNeedsInteractiveLowFpsAndSaturation()
    {
        var rule = new GraphicsBottleneckRule(Thresholds);
        var s = await Real(cpu: 98, fps: 8);
        DiagnosticResult result = null!;
        for (var i = 0; i < 10; i++) result = rule.Evaluate(s with { Workload = WorkloadContext.InteractiveGraphics });
        Assert.Equal(Severity.Warning, result.Severity);
        Assert.Contains("CPU-bound", result.PossibleCause);

        Assert.Equal(Severity.Info, rule.Evaluate(s with { Workload = WorkloadContext.Idle }).Severity);
    }

    // ---------- View model: protocol display, Advanced Metrics, mode switching ----------

    [Fact]
    public async Task ProtocolDisplayShowsOnlyTheDetectedProtocol()
    {
        await using var demo = new MockMetricsProvider();
        await using var real = new RealSessionMetricsProvider(new OmnissaBlastProvider(new OneSessionBlastReader(), new ActiveContext()), new FakeHost(), new FakeProbe());
        var vm = Vm(demo, real);
        await vm.InitializeAsync(default);
        Assert.Equal("Real Session", vm.ModeLabel);
        await demo.InitializeAsync();
        demo.Configure(RemoteProtocol.CitrixHdx, DemoScenario.Healthy, WorkloadContext.Idle);
        vm.UseSyntheticSource(demo);
        await RunOnce(vm);
        Assert.Equal("Citrix HDX (synthetic)", vm.ProtocolDisplay);
        Assert.StartsWith("SYNTHETIC TEST DATA", vm.ModeBanner);

        vm.UseSyntheticSource(null);
        Assert.Equal("Detecting…", vm.ProtocolDisplay);
        Assert.Equal("REAL SESSION · Detecting the session…", vm.ModeBanner);
        await RunOnce(vm);

        Assert.Equal("Omnissa Blast", vm.ProtocolDisplay);
        Assert.Equal("REAL SESSION · Horizon Blast counters + Windows metrics", vm.ModeBanner);
        Assert.Equal("Horizon Blast counters + Windows metrics", vm.DataAvailability);
        Assert.Contains(vm.SessionCards, c => c.Label == "Active protocol" && c.Value == "Omnissa Blast");
        Assert.StartsWith("Omnissa Blast |", vm.SessionIdentity);
        Assert.Equal("42.0 %".Replace(".", System.Globalization.CultureInfo.CurrentCulture.NumberFormat.NumberDecimalSeparator), vm.OverviewCards.Single(c => c.Label == "CPU").Value);
        Assert.Contains(vm.GpuCards, c => c.Label == "VRAM used / total");
        Assert.Contains(vm.SystemCards, c => c.Label == "Available RAM");
    }

    [Fact]
    public async Task UnknownProtocolIsShownAsNotDetected()
    {
        await using var demo = new MockMetricsProvider();
        await using var real = new RealSessionMetricsProvider(new OmnissaBlastProvider(new EmptyBlastReader(), new NoAgentContext()), new FakeHost(), new FakeProbe());
        var vm = Vm(demo, real);
        await vm.InitializeAsync(default);
        await RunOnce(vm);

        Assert.Equal("Not detected", vm.ProtocolDisplay);
        Assert.Contains("protocol type 2", vm.ProtocolDetail);
        // A session without Blast counters is monitored with Windows metrics, and the banner says so instead of claiming Blast counters.
        Assert.Equal("REAL SESSION · Windows metrics only · No Horizon Blast counters for this session", vm.ModeBanner);
        Assert.NotEqual("N/A", vm.OverviewCards.Single(c => c.Label == "CPU").Value);
        Assert.StartsWith("Not detected |", vm.SessionIdentity);
    }

    [Fact]
    public async Task AdvancedMetricsFilterAndSearch()
    {
        await using var demo = new MockMetricsProvider();
        await using var real = new RealSessionMetricsProvider(new OmnissaBlastProvider(new OneSessionBlastReader(), new ActiveContext()), new FakeHost(), new FakeProbe());
        var vm = Vm(demo, real);
        await vm.InitializeAsync(default);
        await RunOnce(vm);
        Assert.Empty(vm.AdvancedRows);                 // only built while the page is visible

        vm.SelectedPage = vm.Pages.Single(p => p is AdvancedPage);
        Assert.Contains(vm.AdvancedRows, r => r.Category == "Horizon Blast Session" && r.Name == "RTT" && r.Value == "34");
        Assert.Contains(vm.AdvancedRows, r => r.Id == "cpu.utilization" && r.Scope == "Machine-wide" && r.Status == "Available");

        vm.SelectedAdvancedCategory = vm.AdvancedCategories.Single(c => c.Value == MetricCategory.Gpu);
        Assert.All(vm.AdvancedRows, r => Assert.Equal("GPU", r.Category));
        vm.SelectedAdvancedCategory = vm.AdvancedCategories[0];
        vm.AdvancedSearch = "rtt";
        Assert.All(vm.AdvancedRows, r => Assert.Contains("rtt", r.Name + r.Id + r.Source, StringComparison.OrdinalIgnoreCase));
        Assert.Contains("showing", vm.AdvancedStatus);

        await demo.InitializeAsync();
        vm.UseSyntheticSource(demo);
        Assert.Empty(vm.AdvancedRows);
        Assert.Contains("synthetic test data has none", vm.AdvancedStatus);
    }

    [Fact]
    public async Task SourceChangeResetsSustainedDiagnostics()
    {
        await using var demo = new MockMetricsProvider();
        await using var real = new RealSessionMetricsProvider(new OmnissaBlastProvider(new OneSessionBlastReader(), new ActiveContext()), new FakeHost(), new FakeProbe());
        var rule = new CountingRule();
        var vm = new MainViewModel(real, new DiagnosticEngine([rule]), new SessionRecorder(), new FakeDesktop(), NullLogger<MainViewModel>.Instance);
        await vm.InitializeAsync(default);
        var before = rule.Resets;

        await demo.InitializeAsync();
        vm.UseSyntheticSource(demo);

        Assert.Equal(before + 1, rule.Resets);
    }

    // ---------- Recording / export ----------

    [Fact]
    public async Task CsvAppendsHostColumnsWithoutMovingExistingOnes()
    {
        await using var real = new RealSessionMetricsProvider(new OmnissaBlastProvider(new OneSessionBlastReader(), new ActiveContext()), new FakeHost(), new FakeProbe());
        var recorder = new SessionRecorder();
        recorder.Start();
        recorder.Add(await real.CollectAsync());
        recorder.Stop();
        var path = Path.Combine(Path.GetTempPath(), $"euclens-host-{Guid.NewGuid():N}.csv");
        try
        {
            await recorder.ExportCsvAsync(path);
            var lines = await File.ReadAllLinesAsync(path);
            var header = lines[0].Split(',');

            Assert.StartsWith("timestamp,data_source,is_demo,protocol,session_id,workload,demo_scenario,rtt_ms,", lines[0]);
            Assert.True(Array.IndexOf(header, "blast_rtt") < Array.IndexOf(header, "protocol_detection"));
            Assert.True(Array.IndexOf(header, "protocol_detection") < Array.IndexOf(header, "host_cpu.utilization"));
            Assert.Contains("host_cpu.utilization_scope", header);
            Assert.Contains("OmnissaBlast", lines[1]);
            Assert.Contains(",42,\"%\",Available,,Machine,", lines[1]);
            Assert.Contains("\"Test GPU, \"\"quoted\"\"\"", lines[1]);
        }
        finally { File.Delete(path); }
    }

    // ---------- helpers ----------

    private static async Task<SessionSnapshot> Real(double cpu, double rtt = 30, double fps = 30)
    {
        var s = await DiagnosticTests.Sample();
        return s with
        {
            Session = s.Session with { IsDemo = false },
            Connection = s.Connection with { RoundTripTime = Metric.Available(rtt, "ms") },
            Graphics = s.Graphics with { FramesPerSecond = Metric.Available(fps, "FPS") },
            System = s.System with { CpuUtilization = Metric.Available(cpu, "%") },
            Gpu = s.Gpu with { Utilization = Metric.Available(10, "%") },
        };
    }

    private static async Task RunOnce(MainViewModel vm)
    {
        using var run = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        await vm.RunAsync(run.Token);
    }

    private static MainViewModel Vm(MockMetricsProvider demo, IMetricsProvider real) =>
        new(real, new DiagnosticEngine([new LatencyRule(), new SustainedCpuRule(new())]), new SessionRecorder(), new FakeDesktop(), NullLogger<MainViewModel>.Instance);

    private static GpuAdapterInfo Adapter(int index, string name, uint vendor, int dedicatedMiB, bool software = false, bool remote = false, uint luidLow = 0) =>
        new(index, name, vendor, 0x25B6, 0x14A910DE, (ulong)dedicatedMiB * 1024 * 1024, 0, 8UL << 30,
            GpuAdapterInfo.FormatLuid(0, luidLow), software, remote, "32.0.15.6094", "1-1-2025");

    private static NvmlDeviceSample Device(int index, double gpu, NvmlValue? encoder = null) =>
        new(index, "NVIDIA A16-2Q", 0x10DE, 0x25B6, 0x14A910DE, "00000000:02:00.0", "vGPU guest", NvmlValue.Ok(gpu), NvmlValue.Ok(5),
            NvmlValue.Ok(GiB), NvmlValue.Ok(2 * GiB), encoder ?? NvmlValue.Ok(12), NvmlValue.Ok(0));

    private static WindowsGpuMetricsProvider Gpu(IReadOnlyList<GpuAdapterInfo> adapters, FakeGpuCounters counters, FakeNvml? nvml = null) =>
        new(new FakeAdapterSource(adapters), counters, () => nvml ?? new FakeNvml(available: false), new ManualClock());

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset now = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan by) => now += by;
    }

    private sealed class FakeSystemNative : ISystemNative
    {
        public List<CpuTimes?> Times { get; init; } = [new(0, 0, 0)];
        public PhysicalMemory? Memory { get; init; } = new((ulong)(8 * GiB), (ulong)(2 * GiB));
        public Exception? Failure { get; init; }
        public int InventoryCalls { get; private set; }
        private int index;
        public CpuTimes? GetSystemTimes() => Failure is not null ? throw Failure : Times[Math.Min(index++, Times.Count - 1)];
        public PhysicalMemory? GetPhysicalMemory() => Failure is not null ? throw Failure : Memory;
        public CpuInventory GetCpuInventory() { InventoryCalls++; return new("Test CPU", 8, 4); }
        public string GetWindowsVersion() => "Windows 11 Pro (test)";
    }

    private sealed class FakeCounterReader : IPerformanceCounterReader
    {
        private readonly Dictionary<string, Func<CounterReading>> values = [];
        public void Set(string counter, double value, MetricStatus status = MetricStatus.Available, string? error = null) =>
            values[counter] = () => status == MetricStatus.Available ? new(value, (long)value, DateTimeOffset.UtcNow) : new(null, null, DateTimeOffset.UtcNow, status, error);
        public void Throw(string counter, Exception ex) => values[counter] = () => throw ex;
        public IReadOnlyList<string> GetInstances(string category) => ["_Total"];
        public CounterReading Read(string category, string instance, string counter) =>
            values.TryGetValue(counter, out var read) ? read() : new(null, null, DateTimeOffset.UtcNow, MetricStatus.Unavailable, "Not installed.");
        public void Reset() { }
        public void Dispose() { }
    }

    private sealed class FakeAdapterSource(IReadOnlyList<GpuAdapterInfo> adapters) : IGpuAdapterSource
    {
        public int Calls { get; private set; }
        public IReadOnlyList<GpuAdapterInfo> Enumerate() { Calls++; return adapters; }
    }

    private sealed class FakeGpuCounters : IGpuCounterSource
    {
        public List<GpuEngineReading> Engines { get; } = [];
        public List<GpuMemoryReading> Memory { get; } = [];
        public MetricStatus EngineStatus { get; init; } = MetricStatus.Available;
        public string? EngineReason { get; init; }
        public Exception? Failure { get; init; }
        public bool Disposed { get; private set; }
        public GpuCounterRead<GpuEngineReading> ReadEngines() => Failure is not null ? throw Failure : new(Engines, EngineStatus, EngineReason, DateTimeOffset.UtcNow);
        public GpuCounterRead<GpuMemoryReading> ReadAdapterMemory() => Failure is not null ? throw Failure : new(Memory, MetricStatus.Available, null, DateTimeOffset.UtcNow);
        public int? SessionOf(int processId) => 1;
        public void Dispose() => Disposed = true;
    }

    private sealed class FakeNvml(bool available) : INvmlClient
    {
        public IReadOnlyList<NvmlDeviceSample> Devices { get; init; } = [];
        public bool Disposed { get; private set; }
        public bool IsAvailable => available;
        public string Status => available ? "NVML loaded (fake)." : "nvml.dll not found (fake).";
        public string? DriverVersion => available ? "570.00" : null;
        public IReadOnlyList<NvmlDeviceSample> Sample() => available ? Devices : [];
        public void Dispose() => Disposed = true;
    }

    private sealed class FakeDisplaySource(DisplayTopology topology) : IDisplayInfoSource
    {
        public int Calls { get; private set; }
        public Exception? Failure { get; set; }
        public DisplayTopology Read() { Calls++; return Failure is not null ? throw Failure : topology; }
    }

    private sealed class FakeHost : IHostMetricsCollector
    {
        public bool Disposed { get; private set; }
        public HostSample Collect(int sessionId, CancellationToken token)
        {
            var cpu = new Metric(42, "%", Source: "GetSystemTimes (kernel32)", Timestamp: DateTimeOffset.UtcNow, SampleStatus: MetricStatus.Available, Scope: MetricScope.Machine);
            var ram = new Metric(50, "%", Source: "GlobalMemoryStatusEx", SampleStatus: MetricStatus.Available, Scope: MetricScope.Machine);
            var na = new Metric(null, "%", "No engine counters.", @"\GPU Engine(*)", SampleStatus: MetricStatus.Unavailable, Scope: MetricScope.Machine);
            return new(new(cpu, ram, new(1, "MB/s", Source: "disk", SampleStatus: MetricStatus.Available)),
                new("Test GPU", na, na, na, na, "1.0", "N/A / 2.00 GiB (dedicated)", "NVIDIA GPU"), "2560 × 1440", "1 · primary scale 100 %", "Windows 11 Pro (test)",
                [new("cpu.utilization", MetricCategory.WindowsCpu, "CPU utilization", cpu),
                 new("memory.availableGiB", MetricCategory.WindowsMemory, "Physical RAM available", new(4, "GiB", Source: "GlobalMemoryStatusEx", SampleStatus: MetricStatus.Available, Scope: MetricScope.Machine)),
                 new("gpu.0.utilization", MetricCategory.Gpu, "GPU 0 · utilization", na),
                 new("gpu.0.description", MetricCategory.Gpu, "GPU 0 · adapter", new(null, "", Source: "DXGI", SampleStatus: MetricStatus.Available), "Test GPU, \"quoted\""),
                 new("display.monitorCount", MetricCategory.Display, "Active monitors", new(1, "count", Source: "EnumDisplayMonitors", SampleStatus: MetricStatus.Available, Scope: MetricScope.Session))]);
        }
        public void Dispose() => Disposed = true;
    }

    private sealed class FakeProbe : ISessionProtocolProbe
    {
        public string Describe(int sessionId) => "Windows reports protocol type 2 (RDP or an RDP-registered protocol stack).";
    }

    private sealed class NoAgentContext : IBlastSessionContext
    {
        public BlastSessionContext GetCurrent() => new(3, false, false, "Horizon Agent installation was not detected (test).");
    }

    private sealed class ActiveContext : IBlastSessionContext
    {
        public BlastSessionContext GetCurrent() => new(3, true, true, "active");
    }

    private class EmptyBlastReader : IBlastCounterReader
    {
        public virtual IReadOnlyList<string> GetInstances(string category) => [];
        public virtual CounterReading Read(string category, string instance, string counter) =>
            new(null, null, DateTimeOffset.UtcNow, MetricStatus.Unavailable, "Not installed.");
        public void Reset() { }
        public void Dispose() { }
    }

    private sealed class OneSessionBlastReader : EmptyBlastReader
    {
        public override IReadOnlyList<string> GetInstances(string category) =>
            [category == BlastCounterCatalog.SessionCategory ? "Session ID: 3; (main)" : "Session ID: 3; Channel: Imaging; (main)"];
        public override CounterReading Read(string category, string instance, string counter) =>
            counter == "RTT" ? new(34, 34, DateTimeOffset.UtcNow) : base.Read(category, instance, counter);
    }

    private sealed class CountingRule : IDiagnosticRule
    {
        public int Resets { get; private set; }
        public DiagnosticResult Evaluate(SessionSnapshot snapshot) =>
            new("count", Severity.Info, MetricStatus.Available, "Count", "", new Dictionary<string, string>(), "", "", DiagnosticArea.Host);
        public void Reset() => Resets++;
    }

    private sealed class FakeDesktop : IDesktopServices
    {
        public string? SelectExportPath(bool json) => null;
        public bool ConfirmNewRecording() => true;
        public void ApplyTheme(bool dark) { }
        public Task SavePreferencesAsync(Preferences preferences) => Task.CompletedTask;
        public Task<Preferences?> LoadPreferencesAsync() => Task.FromResult<Preferences?>(null);
    }
}
