using System.Text.Json;
using EucLens.App;
using EucLens.App.ViewModels;
using EucLens.Core;
using EucLens.Diagnostics;
using EucLens.Providers;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EucLens.Tests;

/// <summary>Deterministic synthetic time series with a fake monotonic clock; no Horizon Agent, GPU or VDI needed.</summary>
public sealed class DiagnosticIntelligenceTests
{
    // ---------- harness ----------

    private sealed class TestClock : TimeProvider
    {
        private long ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => ticks;
        public override DateTimeOffset GetUtcNow() => new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero).AddTicks(ticks);
        public void Advance(double seconds) => ticks += (long)(seconds * TimeSpan.TicksPerSecond);
    }

    private sealed record Sample(double? Rtt = 30, double? Loss = 0, double? Fps = 30, double? Cpu = 20, double? Ram = 40,
        WorkloadContext Workload = WorkloadContext.Idle, bool? HardwareGpu = false, double? Dirty = null, double? Gpu = null,
        double? AvailableGiB = 4, double? Reconnects = null, double? Jitter = null, double? DiskActive = null, bool Demo = false,
        MetricStatus RttStatus = MetricStatus.Available, MetricStatus CpuStatus = MetricStatus.Available);

    private static Metric M(double? value, string unit, MetricStatus status = MetricStatus.Available) =>
        value is { } v && status == MetricStatus.Available
            ? new(v, unit, Source: "synthetic", SampleStatus: MetricStatus.Available)
            : new(null, unit, status == MetricStatus.Available ? "missing" : $"synthetic {status}", "synthetic", SampleStatus: status == MetricStatus.Available ? MetricStatus.Unavailable : status,
                Error: status == MetricStatus.Failed ? "synthetic failure" : null);

    private static SessionSnapshot Snap(Sample x, DateTimeOffset wall)
    {
        var blast = new Dictionary<string, Metric>();
        if (x.Dirty is not null) blast["dirtyFps"] = M(x.Dirty, "FPS");
        if (x.Reconnects is not null) blast["reconnects"] = M(x.Reconnects, "count");
        if (x.Jitter is not null) { blast["jitter"] = M(x.Jitter, "raw (unit unverified)"); blast["estimatedBandwidth"] = M(5000, "raw (unit unverified)"); }
        var host = new List<MetricEntry>
        {
            new("memory.availableGiB", MetricCategory.WindowsMemory, "Available", M(x.AvailableGiB, "GiB")),
            new("cpu.logicalProcessors", MetricCategory.WindowsCpu, "Logical processors", M(2, "count")),
        };
        if (x.DiskActive is not null) host.Add(new("disk.activePercent", MetricCategory.WindowsDisk, "Disk active", M(x.DiskActive, "%")));
        var gpu = M(x.Gpu, "%");
        return new(wall, new(RemoteProtocol.OmnissaBlast, 3, "user", "client", "vdi", "Windows 11", "N/A", wall, x.Demo), x.Workload,
            new(M(x.Rtt, "ms", x.RttStatus), M(null, "ms"), M(null, "Mbps"), M(1, "Mbps"), M(x.Loss, "%")),
            new(M(x.Fps, "FPS"), "Raw code 1", "N/A", M(null, "%"), "Blast", null, "1920 × 1080"),
            new(x.HardwareGpu == true ? "NVIDIA A16-2Q" : "None (software rendering)", gpu, gpu, gpu, gpu, "1.0", null,
                x.HardwareGpu == false ? "No hardware GPU: Microsoft Basic Render Driver ×2" : "NVIDIA", x.HardwareGpu),
            new(M(x.Cpu, "%", x.CpuStatus), M(x.Ram, "%"), M(1, "MB/s")),
            BlastMetrics: x.Demo ? null : blast, HostMetrics: x.Demo ? null : host);
    }

    private sealed class Harness(DiagnosticThresholds? thresholds = null)
    {
        public TestClock Clock { get; } = new();
        public DiagnosticIntelligenceEngine Engine { get; private set; } = null!;
        public List<DiagnosticEvent> Events { get; } = [];
        public DiagnosticReport Last { get; private set; } = null!;
        public Harness Init() { Engine = new(DiagnosticRuleSet.Default(), Clock, thresholds); return this; }

        public DiagnosticReport Run(int samples, Func<int, Sample> make, double interval = 1)
        {
            for (var i = 0; i < samples; i++)
            {
                Clock.Advance(interval);
                Last = Engine.Evaluate(Snap(make(i), Clock.GetUtcNow()));
                Events.AddRange(Last.Snapshot!.Events);
            }
            return Last;
        }
        public DiagnosticReport Run(int samples, Sample sample, double interval = 1) => Run(samples, _ => sample, interval);
    }

    private static Harness New(DiagnosticThresholds? t = null) => new Harness(t).Init();
    private static DiagnosticFinding? Active(DiagnosticReport r, string id) => r.Findings!.FirstOrDefault(f => f.RuleId == id && f.IsActive);
    private static HealthState State(DiagnosticReport r, DiagnosticDomain d) => r.Assessments!.Single(a => a.Domain == d).State;

    // ---------- connection ----------

    [Theory]
    [InlineData(200, Severity.Warning)]
    [InlineData(350, Severity.Critical)]
    public void SustainedRttElevationRaisesFindingWithEvidence(double rtt, Severity expected)
    {
        var h = New();
        var r = h.Run(60, new Sample(Rtt: rtt));

        var f = Active(r, DiagnosticRuleIds.Rtt)!;
        Assert.Equal(expected, f.Severity);
        Assert.Equal(60, f.ObservationSeconds);
        Assert.Equal(60, f.ValidSamples);
        Assert.Contains(f.Evidence, e => e.Metric == "Window P95");
        Assert.Contains("not one-way", f.Limitations);
        Assert.Contains("150", f.DecisionRule);
        Assert.Equal(expected == Severity.Critical ? HealthState.Critical : HealthState.Warning, State(r, DiagnosticDomain.Connection));
    }

    [Fact]
    public void BriefRttSpikeDoesNotAlert()
    {
        var r = New().Run(60, i => new Sample(Rtt: i is >= 30 and < 35 ? 450 : 30));

        Assert.Null(Active(r, DiagnosticRuleIds.Rtt));
        Assert.Null(Active(r, DiagnosticRuleIds.RttUnstable));
        Assert.Equal(HealthState.Healthy, State(r, DiagnosticDomain.Connection));
    }

    [Theory]
    [InlineData(2, Severity.Warning)]
    [InlineData(6, Severity.Critical)]
    public void PacketLossIsDetectedAndScopedToUplink(double loss, Severity expected)
    {
        var f = Active(New().Run(60, new Sample(Loss: loss)), DiagnosticRuleIds.PacketLoss)!;

        Assert.Equal(expected, f.Severity);
        Assert.Equal("Uplink packet loss", f.Title);
        Assert.Contains("uplink", f.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Downstream (agent to client) loss is not measured", f.Limitations);
    }

    [Fact]
    public void PersistentRttVariationIsUnstable()
    {
        var r = New().Run(60, i => new Sample(Rtt: i % 2 == 0 ? 40 : 140));
        Assert.Equal(Severity.Warning, Active(r, DiagnosticRuleIds.RttUnstable)!.Severity);
        Assert.Null(Active(r, DiagnosticRuleIds.Rtt));
    }

    [Fact]
    public void ReconnectIncreaseWarnsAndRecoversAfterQuietWindow()
    {
        var h = New();
        h.Run(20, new Sample(Reconnects: 0));
        var r = h.Run(5, new Sample(Reconnects: 1));
        Assert.Equal(Severity.Warning, Active(r, DiagnosticRuleIds.Reconnects)!.Severity);

        r = h.Run(130, new Sample(Reconnects: 1));
        Assert.Null(Active(r, DiagnosticRuleIds.Reconnects));
        Assert.Contains(h.Events, e => e.Transition == FindingTransition.Recovered && e.Finding.RuleId == DiagnosticRuleIds.Reconnects);
    }

    [Fact]
    public void UnverifiedUnitMetricsAreEvidenceOnly()
    {
        var r = New().Run(60, new Sample(Jitter: 99_999));

        var connection = r.Assessments!.Single(a => a.Domain == DiagnosticDomain.Connection);
        Assert.Equal(HealthState.Healthy, connection.State);
        Assert.Equal(AssessmentCoverage.Partial, connection.Coverage);
        Assert.Contains("jitter and estimated bandwidth units unverified", connection.CoverageNote);
        var info = Active(r, DiagnosticRuleIds.ExcludedMetrics)!;
        Assert.Equal(Severity.Info, info.Severity);
        Assert.Contains("not which transport was negotiated", info.Description);
        Assert.DoesNotContain(r.Findings!, f => f.Severity > Severity.Info);
    }

    // ---------- guest ----------

    [Fact]
    public void CpuSaturationAccountsForLogicalProcessors()
    {
        var r = New().Run(60, new Sample(Cpu: 95));

        var f = Active(r, DiagnosticRuleIds.Cpu)!;
        Assert.Equal(Severity.Warning, f.Severity);
        Assert.Contains(f.Evidence, e => e.Metric == "Logical processors" && e.Detail!.Contains("50 %"));
        Assert.Contains("not show hypervisor CPU contention", f.Limitations);
        Assert.Equal(HealthState.Warning, State(r, DiagnosticDomain.Guest));
    }

    [Theory]
    [InlineData(96, 4, Severity.Critical)]
    [InlineData(92, 4, Severity.Warning)]
    [InlineData(80, 0.4, Severity.Critical)]
    [InlineData(80, 0.8, Severity.Warning)]
    public void MemoryPressureFromPercentOrAvailableRam(double ram, double available, Severity expected) =>
        Assert.Equal(expected, Active(New().Run(60, new Sample(Ram: ram, AvailableGiB: available)), DiagnosticRuleIds.Memory)!.Severity);

    [Fact]
    public void DiskSaturationUsesActiveTimeOnly()
    {
        Assert.Null(Active(New().Run(60, new Sample(DiskActive: 20)), DiagnosticRuleIds.Disk));
        Assert.Equal(Severity.Warning, Active(New().Run(60, new Sample(DiskActive: 97)), DiagnosticRuleIds.Disk)!.Severity);
        Assert.Contains("Throughput alone is never used", Active(New().Run(60, new Sample(DiskActive: 97)), DiagnosticRuleIds.Disk)!.Limitations);
    }

    // ---------- graphics ----------

    [Fact]
    public void LowFpsOnIdleDesktopIsNotAProblem()
    {
        var r = New().Run(60, new Sample(Fps: 2, Dirty: 0.5));

        Assert.Null(Active(r, DiagnosticRuleIds.LowFps));
        Assert.Equal(HealthState.Healthy, State(r, DiagnosticDomain.Graphics));
        Assert.Contains(r.Hypotheses!, h => h.Statement == "No graphics performance problem can be inferred from low FPS alone.");
    }

    [Fact]
    public void LowFpsWhileTheContentIsChangingWarns()
    {
        // The sample's workload value (Idle, as older recordings could carry) plays no part: the measured screen activity decides.
        var f = Active(New().Run(60, new Sample(Fps: 8, Dirty: 25, Workload: WorkloadContext.Idle)), DiagnosticRuleIds.LowFps)!;

        Assert.Equal(Severity.Warning, f.Severity);
        Assert.Equal("Low frame rate while the content is changing", f.Title);
        Assert.Contains("confirmed by Blast dirty frames", f.Description);
        Assert.Contains(f.Evidence, e => e.Metric == "Dirty FPS mean" && e.Value == "25 FPS");
        Assert.DoesNotContain(f.Evidence, e => e.Metric.Contains("Workload", StringComparison.Ordinal));
        Assert.Contains("Not judged when screen activity cannot be measured", f.DecisionRule);
    }

    [Fact]
    public void LowFpsWithoutActivityEvidenceIsNotJudged()
    {
        // No dirty-frame counter: a slow screen cannot be told from a static one, so nothing is claimed, even for a declared workload.
        var r = New().Run(60, new Sample(Fps: 8, Workload: WorkloadContext.VideoPlayback));
        Assert.Null(Active(r, DiagnosticRuleIds.LowFps));
        Assert.Null(Active(r, DiagnosticRuleIds.CpuBoundGraphics));
        Assert.Null(Active(r, DiagnosticRuleIds.FpsUnstable));
        Assert.Contains(r.Hypotheses!, h => h.Statement == "No graphics performance problem can be inferred from low FPS alone."
            && h.Confidence.StartsWith("Not judged — screen activity cannot be measured", StringComparison.Ordinal));
        Assert.False(r.Inputs!.Single(i => i.Key == DiagnosticMetrics.Fps).ThresholdApplies);
    }

    [Fact]
    public void SlowlyChangingContentIsNotJudgedAgainstTheLowFpsThreshold()
    {
        // Typing or scrolling a document: the screen changes 8 times per second and Blast delivers about that. Without a workload
        // selection this must not read as a frame-rate problem: the content asks for fewer frames than the 15 FPS threshold.
        var r = New().Run(60, new Sample(Fps: 8, Dirty: 8));
        Assert.Null(Active(r, DiagnosticRuleIds.LowFps));
        Assert.Null(Active(r, DiagnosticRuleIds.CpuBoundGraphics));
        Assert.Equal(HealthState.Healthy, State(r, DiagnosticDomain.Graphics));
        Assert.Contains(r.Hypotheses!, h => h.Statement == "No graphics performance problem can be inferred from low FPS alone."
            && h.Confidence.StartsWith("Expected — the content changes fewer times per second than the low-FPS threshold", StringComparison.Ordinal));
        Assert.False(r.Inputs!.Single(i => i.Key == DiagnosticMetrics.Fps).ThresholdApplies);

        // The same delivery while the content changes 25 times per second is a finding; when the content slows down again it recovers.
        var h = New();
        Assert.NotNull(Active(h.Run(60, new Sample(Fps: 8, Dirty: 25)), DiagnosticRuleIds.LowFps));
        Assert.Null(Active(h.Run(70, new Sample(Fps: 8, Dirty: 8)), DiagnosticRuleIds.LowFps));
    }

    [Fact]
    public void StaticScreenDuringDeclaredActiveWorkloadDoesNotWarn() =>
        Assert.Null(Active(New().Run(60, new Sample(Fps: 3, Dirty: 1, Workload: WorkloadContext.InteractiveGraphics)), DiagnosticRuleIds.LowFps));

    [Fact]
    public void AFindingRaisedWhileTheScreenChangedClosesWhenActivityCanNoLongerBeMeasured()
    {
        var h = New();
        Assert.NotNull(Active(h.Run(60, new Sample(Fps: 8, Dirty: 25)), DiagnosticRuleIds.LowFps));
        Assert.Null(Active(h.Run(70, new Sample(Fps: 8)), DiagnosticRuleIds.LowFps));
    }

    [Fact]
    public void UndocumentedQueueingCounterIsInformationOnly()
    {
        var h = New();
        for (var i = 0; i < 60; i++)
        {
            h.Clock.Advance(1);
            var snap = Snap(new Sample(Fps: 30, Dirty: 30), h.Clock.GetUtcNow());
            var blast = new Dictionary<string, Metric>(snap.BlastMetrics!) { ["outQueueingTime"] = M(900_000, "µs") };
            h.Engine.Evaluate(snap with { BlastMetrics = blast });
        }
        var r = h.Engine.Evaluate(Snap(new Sample(Fps: 30, Dirty: 30), h.Clock.GetUtcNow()) with
            { BlastMetrics = new Dictionary<string, Metric> { ["dirtyFps"] = M(30, "FPS"), ["outQueueingTime"] = M(900_000, "µs") } });

        Assert.Equal(Severity.Info, Active(r, DiagnosticRuleIds.Queueing)!.Severity);
        Assert.Equal(HealthState.Healthy, State(r, DiagnosticDomain.Graphics));
    }

    [Fact]
    public void CpuBoundGraphicsIsAPossibleContributorNotACause()
    {
        var r = New().Run(60, new Sample(Fps: 8, Dirty: 25, Cpu: 97, Workload: WorkloadContext.InteractiveGraphics));

        var f = Active(r, DiagnosticRuleIds.CpuBoundGraphics)!;
        Assert.Contains("may be limiting", f.Title);
        Assert.Contains("Correlation, not proof", f.Limitations);
        Assert.Contains(r.Hypotheses!, h => h.Statement == "Guest CPU saturation may be contributing to graphics performance degradation.");
        Assert.Null(Active(r, DiagnosticRuleIds.NetworkGraphics));
    }

    // ---------- hypotheses ----------

    [Fact]
    public void NetworkDegradationWithNormalCpuIsSuspected()
    {
        var r = New().Run(60, new Sample(Rtt: 250, Loss: 3, Cpu: 20));

        var h = r.Hypotheses!.Single(x => x.Id == "network");
        Assert.Equal("Network-related connection degradation is suspected.", h.Statement);
        Assert.Contains("guest CPU is within thresholds", h.Confidence);
        Assert.NotEmpty(h.VerificationSteps);
    }

    [Fact]
    public void AmbiguousEvidenceListsMultipleCauses()
    {
        var r = New().Run(60, new Sample(Rtt: 250, Cpu: 97));

        var h = r.Hypotheses!.Single(x => x.Id == "network-or-cpu");
        Assert.Equal("Multiple plausible causes", h.Confidence);
        Assert.True(h.VerificationSteps.Count >= 2);
    }

    // ---------- GPU ----------

    [Fact]
    public void NoHardwareGpuIsASupportedConfiguration()
    {
        var r = New().Run(60, new Sample(HardwareGpu: false, Dirty: 1));

        var info = Active(r, DiagnosticRuleIds.SoftwareRendering)!;
        Assert.Equal(Severity.Info, info.Severity);
        Assert.DoesNotContain(r.Findings!, f => f.Severity > Severity.Info);
        Assert.Equal(HealthState.Healthy, State(r, DiagnosticDomain.Guest));
        Assert.Contains("software rendering — GPU analysis not applicable", r.Assessments!.Single(a => a.Domain == DiagnosticDomain.Guest).CoverageNote);
        Assert.Contains(r.Hypotheses!, h => h.Statement == "No GPU-related problem detected. Software rendering is in use.");
    }

    [Fact]
    public void MissingGpuMetricsAreNotZeroUtilization()
    {
        var r = New().Run(60, new Sample(HardwareGpu: true, Gpu: null));

        Assert.Null(Active(r, DiagnosticRuleIds.Gpu));
        var guest = r.Assessments!.Single(a => a.Domain == DiagnosticDomain.Guest);
        Assert.Equal(HealthState.Healthy, guest.State);
        Assert.Equal(AssessmentCoverage.Partial, guest.Coverage);
        Assert.Contains("GPU unavailable", guest.CoverageNote);
    }

    [Fact]
    public void HardwareGpuSaturationIsEvaluated() =>
        Assert.Equal(Severity.Critical, Active(New().Run(60, new Sample(HardwareGpu: true, Gpu: 99)), DiagnosticRuleIds.Gpu)!.Severity);

    // ---------- data quality ----------

    [Fact]
    public void StaleMeasurementsAreInsufficientNotHealthy()
    {
        var r = New().Run(60, new Sample(Rtt: 400, RttStatus: MetricStatus.Stale));

        Assert.Equal(HealthState.InsufficientData, State(r, DiagnosticDomain.Connection));
        Assert.Null(Active(r, DiagnosticRuleIds.Rtt));
        Assert.Equal(Severity.Warning, Active(r, DiagnosticRuleIds.MeasurementQuality)!.Severity);
    }

    [Fact]
    public void FailedCounterReadsDoNotProduceFindings()
    {
        var r = New().Run(60, new Sample(Cpu: 99, CpuStatus: MetricStatus.Failed));

        Assert.Null(Active(r, DiagnosticRuleIds.Cpu));
        Assert.Equal(HealthState.InsufficientData, State(r, DiagnosticDomain.Guest));
        Assert.Contains("CPU", Active(r, DiagnosticRuleIds.MeasurementQuality)!.Description);
    }

    [Fact]
    public void IncompleteObservationWindowIsInsufficientData()
    {
        var r = New().Run(20, new Sample(Cpu: 99, Rtt: 400));

        Assert.Null(Active(r, DiagnosticRuleIds.Cpu));
        Assert.Null(Active(r, DiagnosticRuleIds.Rtt));
        Assert.All(r.Assessments!, a => Assert.Equal(HealthState.InsufficientData, a.State));
        Assert.Contains(r.Hypotheses!, h => h.Id == "insufficient-data");
    }

    [Fact]
    public void BlastUnavailableIsReportedAsInformation()
    {
        var f = Active(New().Run(10, new Sample(Rtt: null)), DiagnosticRuleIds.BlastUnavailable)!;
        Assert.Equal(Severity.Info, f.Severity);
    }

    // ---------- thresholds and timing ----------

    [Theory]
    [InlineData(150, true)]
    [InlineData(149.9, false)]
    public void RttThresholdIsInclusive(double rtt, bool raised) =>
        Assert.Equal(raised, Active(New().Run(60, new Sample(Rtt: rtt)), DiagnosticRuleIds.Rtt) is not null);

    [Theory]
    [InlineData(42, true)]   // 42 of 60 s = 70 %
    [InlineData(41, false)]
    public void SustainedFractionBoundary(int highSamples, bool raised) =>
        // High values at the end: the final 60 s window holds exactly highSamples of 60.
        Assert.Equal(raised, Active(New().Run(60, i => new Sample(Rtt: i >= 60 - highSamples ? 200 : 30)), DiagnosticRuleIds.Rtt) is not null);

    [Fact]
    public void IrregularIntervalsUseElapsedTime()
    {
        // 2 s intervals: 30 samples still cover the 60 s window.
        Assert.NotNull(Active(New().Run(31, new Sample(Rtt: 200), interval: 2), DiagnosticRuleIds.Rtt));
        // 10 s gaps: each sample covers at most 5 s, so the window is not sufficiently covered.
        var sparse = New().Run(12, new Sample(Rtt: 200), interval: 10);
        Assert.Null(Active(sparse, DiagnosticRuleIds.Rtt));
        Assert.Equal(HealthState.InsufficientData, State(sparse, DiagnosticDomain.Connection));
    }

    [Fact]
    public void MissingAndNonMonotonicTimestampsDoNotAffectWindows()
    {
        var h = New();
        for (var i = 0; i < 60; i++)
        {
            h.Clock.Advance(1);
            // Wall-clock timestamps jump backwards and metrics carry no timestamps; the monotonic clock drives windows.
            var wall = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddSeconds(i % 2 == 0 ? -i : i);
            h.Engine.Evaluate(Snap(new Sample(Rtt: 200), wall));
        }
        Assert.NotNull(Active(h.Engine.Evaluate(Snap(new Sample(Rtt: 200), DateTimeOffset.MinValue.AddYears(1))), DiagnosticRuleIds.Rtt));
    }

    [Fact]
    public void HysteresisHoldsInBandThenRecovers()
    {
        var h = New();
        h.Run(60, new Sample(Rtt: 200));
        // 140 ms: below the 150 ms warning but above the 135 ms recovery level → stays active.
        var r = h.Run(120, new Sample(Rtt: 140));
        Assert.NotNull(Active(r, DiagnosticRuleIds.Rtt));

        r = h.Run(35, new Sample(Rtt: 100));
        Assert.Null(Active(r, DiagnosticRuleIds.Rtt));
        var recovered = h.Events.Single(e => e.Transition == FindingTransition.Recovered && e.Finding.RuleId == DiagnosticRuleIds.Rtt);
        Assert.NotNull(recovered.Finding.RecoveredAt);
        Assert.Contains("stayed below 135", recovered.Finding.RecoveryReason);
        Assert.Contains(r.Findings!, f => f.RuleId == DiagnosticRuleIds.Rtt && !f.IsActive);
    }

    [Fact]
    public void ValuesOscillatingAroundThresholdDoNotFlap()
    {
        var h = New();
        h.Run(60, new Sample(Rtt: 200));
        // Oscillating across the 150 ms threshold but above the 135 ms recovery level: no detect/recover churn.
        var r = h.Run(300, i => new Sample(Rtt: i % 2 == 0 ? 160 : 145));

        Assert.Equal(FindingTransition.Detected, Assert.Single(h.Events, e => e.Finding.RuleId == DiagnosticRuleIds.Rtt).Transition);
        Assert.NotNull(Active(r, DiagnosticRuleIds.Rtt));
    }

    [Fact]
    public void CriticalDowngradesOnlyAfterRecoveryWindow()
    {
        var h = New();
        h.Run(60, new Sample(Rtt: 350));
        var r = h.Run(40, new Sample(Rtt: 200));
        Assert.Equal(Severity.Critical, Active(r, DiagnosticRuleIds.Rtt)!.Severity);   // critical share still ≥ 70 % early on
        r = h.Run(60, new Sample(Rtt: 200));

        Assert.Equal(Severity.Warning, Active(r, DiagnosticRuleIds.Rtt)!.Severity);
        Assert.Contains(h.Events, e => e.Transition == FindingTransition.Downgraded && e.Finding.RuleId == DiagnosticRuleIds.Rtt);
    }

    [Fact]
    public void FindingExpiresWhenDataStopsArriving()
    {
        var h = New();
        h.Run(60, new Sample(Rtt: 200));
        // The window stays sufficient for ~18 s after data stops; expiry follows 120 s after the last confirmation.
        var r = h.Run(150, new Sample(Rtt: null));

        Assert.Null(Active(r, DiagnosticRuleIds.Rtt));
        Assert.Contains(h.Events, e => e.Transition == FindingTransition.Expired && e.Finding.RuleId == DiagnosticRuleIds.Rtt);
    }

    [Fact]
    public void HealthySessionRaisesNoWarnings()
    {
        var r = New().Run(180, i => new Sample(Rtt: 30 + i % 7, Loss: 0, Fps: 3 + i % 4, Dirty: 1, Cpu: 15 + i % 20, Ram: 55));

        Assert.DoesNotContain(r.Findings!, f => f.Severity > Severity.Info);
        Assert.All(r.Assessments!, a => Assert.Equal(HealthState.Healthy, a.State));
        Assert.Equal(HealthStatus.Healthy, r.Health);
    }

    // ---------- isolation, history, configuration ----------

    [Fact]
    public void ResetIsolatesDemoAndRealHistories()
    {
        var h = New();
        h.Run(60, new Sample(Cpu: 99, Demo: true));
        Assert.NotNull(Active(h.Last, DiagnosticRuleIds.Cpu));

        h.Engine.Reset();
        var r = h.Run(1, new Sample(Cpu: 20));

        Assert.DoesNotContain(r.Findings!, f => f.Severity > Severity.Info);
        Assert.Equal(HealthState.InsufficientData, State(r, DiagnosticDomain.Guest));
    }

    [Fact]
    public void SeriesStatisticsAreCorrectAndBounded()
    {
        var series = new MetricSeries("x", capacity: 128);
        for (var i = 1; i <= 100; i++) series.Add(i, new Metric(i, "u", SampleStatus: MetricStatus.Available));
        var s = series.Analyze(100, 1000, 5);

        Assert.Equal(50.5, s.Mean);
        Assert.Equal(50.5, s.P50!.Value, 6);
        Assert.Equal(95.05, s.P95!.Value, 6);
        Assert.Equal(60, s.SlopePerMinute!.Value, 6);   // +1 per second
        Assert.Equal(28.866, s.StdDev!.Value, 3);

        for (var i = 101; i <= 10_000; i++) series.Add(i, new Metric(1, "u", SampleStatus: MetricStatus.Available));
        Assert.Equal(128, series.Count);
    }

    [Fact]
    public void ThresholdValidationRejectsContradictions()
    {
        Assert.Empty(DiagnosticThresholds.Defaults.Validate());
        Assert.Contains(new DiagnosticThresholds { RttWarningMs = 300, RttCriticalMs = 200 }.Validate(), e => e.Contains("RttWarningMs must be lower"));
        Assert.Contains(new DiagnosticThresholds { ShortWindowSeconds = 90 }.Validate(), e => e.Contains("ShortWindowSeconds < StandardWindowSeconds"));
        Assert.Contains(new DiagnosticThresholds { CpuWarningPercent = 120 }.Validate(), e => e.Contains("CpuWarningPercent must be between"));
        Assert.Contains(new DiagnosticThresholds { AvailableRamWarningGiB = 0.2 }.Validate(), e => e.Contains("AvailableRamCriticalGiB must be lower"));
        Assert.Throws<ArgumentException>(() => New().Engine.Configure(new DiagnosticThresholds { SustainedFraction = 0.2 }));
    }

    [Fact]
    public void CustomThresholdsChangeDecisions()
    {
        var r = New(new DiagnosticThresholds { RttWarningMs = 80, RttCriticalMs = 160 }).Run(60, new Sample(Rtt: 100));
        Assert.Equal(Severity.Warning, Active(r, DiagnosticRuleIds.Rtt)!.Severity);
    }

    [Fact]
    public void RuleIdsAreUniqueAndDeclareRequirements()
    {
        var rules = DiagnosticRuleSet.Default();
        Assert.Equal(rules.Count, rules.Select(r => r.Id).Distinct().Count());
        Assert.All(rules.Where(r => r.Id != DiagnosticRuleIds.SoftwareRendering && r.Id != DiagnosticRuleIds.ExcludedMetrics), r => Assert.NotEmpty(r.RequiredMetrics));
    }

    // ---------- export ----------

    [Fact]
    public async Task FindingsAreExportedToCsvAndJson()
    {
        var h = New();
        var recorder = new SessionRecorder();
        recorder.Start();
        for (var i = 0; i < 100; i++)
        {
            h.Clock.Advance(1);
            var s = Snap(new Sample(Rtt: i < 60 ? 200 : 40), h.Clock.GetUtcNow());
            var report = h.Engine.Evaluate(s);
            recorder.Add(s with { Diagnostics = report.Snapshot });
        }
        recorder.Stop();
        var dir = Directory.CreateTempSubdirectory("euclens-diag-");
        try
        {
            await recorder.ExportCsvAsync(Path.Combine(dir.FullName, "d.csv"));
            await recorder.ExportJsonAsync(Path.Combine(dir.FullName, "d.json"));
            var csv = await File.ReadAllLinesAsync(Path.Combine(dir.FullName, "d.csv"));
            Assert.EndsWith(",diag_connection,diag_graphics,diag_guest,diag_coverage,diag_active_findings,diag_events", csv[0]);
            Assert.Contains(csv, l => l.Contains($"\"Detected {DiagnosticRuleIds.Rtt} Warning\"", StringComparison.Ordinal));
            Assert.Contains(csv, l => l.Contains($"\"Recovered {DiagnosticRuleIds.Rtt} Warning\"", StringComparison.Ordinal));
            Assert.Contains(csv, l => l.Contains($"{DiagnosticRuleIds.Rtt}:Warning", StringComparison.Ordinal) && l.Contains("\"Warning\"", StringComparison.Ordinal));

            using var json = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(dir.FullName, "d.json")));
            var events = json.RootElement.EnumerateArray().SelectMany(e => e.GetProperty("Diagnostics").GetProperty("Events").EnumerateArray()).ToArray();
            var detected = events.Single(e => e.GetProperty("Transition").GetString() == "Detected" && e.GetProperty("Finding").GetProperty("RuleId").GetString() == DiagnosticRuleIds.Rtt);
            Assert.True(detected.GetProperty("Finding").GetProperty("Evidence").GetArrayLength() > 3);
            Assert.Equal(60, detected.GetProperty("Finding").GetProperty("ObservationSeconds").GetDouble());
            var recovered = events.Single(e => e.GetProperty("Transition").GetString() == "Recovered");
            Assert.Equal(JsonValueKind.String, recovered.GetProperty("Finding").GetProperty("RecoveredAt").ValueKind);
        }
        finally { dir.Delete(true); }
    }

    // ---------- view model ----------

    [Fact]
    public async Task ViewModelShowsIndependentDomainsAndThresholdErrors()
    {
        await using var demo = new MockMetricsProvider();
        var store = new FakeSettings(new ThresholdLoadResult(DiagnosticThresholds.Defaults, true, ["RttWarningMs must be lower than RttCriticalMs."], "test.json"));
        var engine = new DiagnosticIntelligenceEngine(DiagnosticRuleSet.Default());
        var vm = new MainViewModel(demo, engine, new SessionRecorder(), new FakeDesktop(), NullLogger<MainViewModel>.Instance, store).WithSynthetic(demo);
        await vm.InitializeAsync(default);
        Assert.Contains("Invalid settings", vm.ThresholdStatus);
        Assert.NotEmpty(vm.ThresholdCards);

        using (var run = new CancellationTokenSource(TimeSpan.FromMilliseconds(300))) await vm.RunAsync(run.Token);
        Assert.Equal(3, vm.HealthTiles.Count);
        Assert.Equal("Insufficient Data", vm.ConnectionHealth);
        Assert.Equal("Insufficient Data", vm.GuestHealth);
        // Diagnostics follow the measurements: there is no workload (or any other) selection that could change a rule.
        Assert.DoesNotContain(typeof(MainViewModel).GetProperties(), p => p.Name.Contains("Workload", StringComparison.Ordinal));

        await vm.RestoreThresholdDefaultsCommand.ExecuteAsync();
        Assert.True(store.Restored);
        Assert.StartsWith("Default thresholds", vm.ThresholdStatus);
    }

    [Fact]
    public void FindingFilterAndStableItems()
    {
        var info = new DiagnosticFinding("a", DiagnosticDomain.Gpu, Severity.Info, "Info", "", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, null, null, 0, 0, 0, [], "", [], [], "");
        var card = FindingCard.From(info);
        var item = new FindingItem(card) { IsExpanded = true };
        item.Card = FindingCard.From(info with { Description = "updated" });

        Assert.True(item.IsExpanded);
        Assert.Equal("updated", item.Description);
        Assert.Equal("INFO", item.Severity);
        Assert.Equal("RECOVERED", FindingCard.From(info with { RecoveredAt = DateTimeOffset.UnixEpoch, RecoveryReason = "x" }).Severity);
    }

    private sealed class FakeSettings(ThresholdLoadResult load) : IDiagnosticSettingsStore
    {
        public bool Restored { get; private set; }
        public string Path => "test.json";
        public Task<ThresholdLoadResult> LoadAsync() => Task.FromResult(load);
        public Task<ThresholdLoadResult> RestoreDefaultsAsync() { Restored = true; return Task.FromResult(new ThresholdLoadResult(DiagnosticThresholds.Defaults, true, [], Path)); }
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
