using EucLens.Core;
using Xunit;

namespace EucLens.Tests;

/// <summary>One positive and one negative case for every rule of the anomaly engine, on synthetic one-second recordings.</summary>
public sealed class AnomalyRuleTests
{
    private static AnomalyObservation Only(AnomalyAnalysis analysis, string ruleId) => Assert.Single(analysis.Observations, o => o.RuleId == ruleId);
    private static RuleEvaluation Rule(AnomalyAnalysis analysis, string ruleId) => Assert.Single(analysis.Rules, r => r.RuleId == ruleId);

    // ---------- healthy ----------

    [Fact]
    public void AHealthySessionProducesNoObservationsAndNoCorrelations()
    {
        var analysis = new Synthetic(1200).Analyze();
        Assert.Empty(analysis.Observations); Assert.Empty(analysis.Correlations);
        Assert.False(analysis.ReducedConfidence); Assert.Empty(analysis.Limitations);
        Assert.Equal(1200, analysis.SamplesAnalyzed);
        Assert.Equal(Synthetic.T0, analysis.From); Assert.Equal(Synthetic.T0.AddSeconds(1199), analysis.To);
        foreach (var id in new[] { "conn.rtt.high", "conn.rtt.deviation", "conn.loss.sustained", "conn.loss.burst", "conn.queueing.increase", "gfx.fps.low", "gfx.dropped.high", "guest.cpu.saturation", "guest.cpu.spike", "guest.ram.high", "guest.disk.throughput-spike" })
            Assert.Equal(RuleState.Evaluated, Rule(analysis, id).State);
    }

    // ---------- connection: round-trip time ----------

    [Fact]
    public void SustainedHighRttIsOneBoundedObservationWithItsEvidence()
    {
        var a = new Synthetic(1200).Set("rtt", 300, 360, 200).Analyze();
        var o = Only(a, "conn.rtt.high");
        Assert.Single(a.Observations);                                   // the deviation rule folds into the absolute one
        Assert.Equal(ObservationKind.Sustained, o.Kind); Assert.Equal(Severity.Warning, o.Severity); Assert.Equal(HistoryGroup.Connection, o.Domain);
        Assert.Equal(AnalysisStatus.ConfirmedObservation, o.Status);
        Assert.Equal(Synthetic.T0.AddSeconds(300), o.Start); Assert.Equal(Synthetic.T0.AddSeconds(359), o.End);
        Assert.Equal(TimeSpan.FromSeconds(59), o.Duration);
        Assert.Equal(60, o.SampleCount); Assert.Equal(60, o.MatchingSamples); Assert.Equal(1.0, o.Coverage);
        Assert.Equal("ms", o.Primary.Unit); Assert.Equal(200, o.Primary.Maximum); Assert.Equal(200, o.Primary.Minimum); Assert.Equal(200, o.Primary.P50);
        Assert.Equal(Synthetic.T0.AddSeconds(300), o.Primary.WorstAt);
        Assert.Equal(ThresholdBasis.Absolute, o.Threshold.Basis); Assert.Equal(150, o.Threshold.Absolute); Assert.Null(o.Threshold.BaselineLimit);
        Assert.Equal(ObservationEnd.Recovered, o.Ending); Assert.Equal(Synthetic.T0.AddSeconds(360), o.RecoveredAt);
        Assert.Contains("200 ms", o.Explanation); Assert.Contains("14:05:00 and 14:05:59 UTC", o.Explanation); Assert.Contains("does not establish the underlying cause", o.Explanation);
        Assert.Equal(1, Rule(a, "conn.rtt.high").Observations);
    }

    [Fact]
    public void ARttAboveTheCriticalThresholdForMostOfTheEpisodeIsCritical()
    {
        var a = new Synthetic(1200).Set("rtt", 300, 360, 350).Analyze();
        Assert.Equal(Severity.Critical, Only(a, "conn.rtt.high").Severity);
    }

    [Fact]
    public void ARttBelowTheThresholdIsNotReportedAbsoluteAndDeviationBothStayQuiet()
    {
        // 80 ms is far above this session's normal but below the practical floor of 100 ms and the 150 ms threshold.
        var a = new Synthetic(1200).Set("rtt", 300, 360, 80).Analyze();
        Assert.Empty(a.Observations);
    }

    [Fact]
    public void AShortRttSpikeIsPreservedAsItsOwnObservation()
    {
        var a = new Synthetic(1200).Set("rtt", 500, 501, 420).Analyze();
        var o = Assert.Single(a.Observations);
        Assert.Equal("conn.rtt.deviation", o.RuleId); Assert.Equal(ObservationKind.Spike, o.Kind);
        Assert.Equal(1, o.MatchingSamples); Assert.Equal(420, o.Primary.Maximum);
        Assert.Equal(Severity.Warning, o.Severity);                      // the peak reached the critical threshold
        Assert.Equal(ThresholdBasis.AbsoluteAndBaseline, o.Threshold.Basis);
        Assert.NotNull(o.Threshold.Baseline); Assert.True(o.Threshold.Baseline!.Reliable);
        Assert.Equal(Synthetic.T0.AddSeconds(500), o.Start); Assert.Equal(o.Start, o.End);
    }

    [Fact]
    public void ARttEpisodeShorterThanThirtySecondsIsASpikeNotASustainedCondition()
    {
        var a = new Synthetic(1200).Set("rtt", 500, 520, 200).Analyze();
        var o = Assert.Single(a.Observations);
        Assert.Equal("conn.rtt.deviation", o.RuleId); Assert.Equal(ObservationKind.Spike, o.Kind); Assert.Equal(Severity.Info, o.Severity);
        Assert.DoesNotContain(a.Observations, x => x.RuleId == "conn.rtt.high");
    }

    [Fact]
    public void ARttFarAboveTheSessionNormalButBelowTheAbsoluteThresholdIsFoundByTheBaselineRule()
    {
        var a = new Synthetic(1200).Set("rtt", 300, 360, 120).Analyze();
        var o = Assert.Single(a.Observations);
        Assert.Equal("conn.rtt.deviation", o.RuleId); Assert.Equal(ObservationKind.Sustained, o.Kind);
        Assert.Equal(Severity.Warning, o.Severity);
        Assert.InRange(o.Threshold.Baseline!.Median, 28, 32);
    }

    // ---------- connection: packet loss ----------

    [Fact]
    public void SustainedPacketLossIsDetectedAndStatesItsDirection()
    {
        var a = new Synthetic(1200).Set("packetLoss", 400, 480, 2).Analyze();
        var o = Only(a, "conn.loss.sustained");
        Assert.Equal(Severity.Warning, o.Severity); Assert.Equal(80, o.SampleCount); Assert.Equal("%", o.Primary.Unit);
        Assert.Contains(o.Limitations, l => l.Contains("uplink") && l.Contains("other direction was not measured"));
        Assert.Contains("uplink", o.Explanation);
        Assert.Single(a.Observations);                                   // the burst rule folds into the sustained one
    }

    [Fact]
    public void HighSustainedPacketLossIsCriticalAndLowLossIsIgnored()
    {
        Assert.Equal(Severity.Critical, Only(new Synthetic(1200).Set("packetLoss", 400, 480, 6).Analyze(), "conn.loss.sustained").Severity);
        Assert.Empty(new Synthetic(1200).Set("packetLoss", 400, 480, 0.5).Analyze().Observations);
    }

    [Fact]
    public void APacketLossBurstIsDetectedAsABurst()
    {
        var a = new Synthetic(1200).Set("packetLoss", 600, 604, 8).Analyze();
        var o = Assert.Single(a.Observations);
        Assert.Equal("conn.loss.burst", o.RuleId); Assert.Equal(ObservationKind.Burst, o.Kind); Assert.Equal(4, o.MatchingSamples); Assert.Equal(Severity.Info, o.Severity);
        Assert.Equal(Severity.Warning, Assert.Single(new Synthetic(1200).Set("packetLoss", 600, 604, 14).Analyze().Observations).Severity);
    }

    [Fact]
    public void ASingleLossSampleIsNotABurstAndMildShortLossIsIgnored()
    {
        Assert.Empty(new Synthetic(1200).Set("packetLoss", 600, 601, 20).Analyze().Observations);   // one sample: not enough to call a burst
        Assert.Empty(new Synthetic(1200).Set("packetLoss", 600, 610, 2).Analyze().Observations);    // below the burst threshold and shorter than sustained
    }

    // ---------- connection: queueing and bandwidth ----------

    [Fact]
    public void IncreasedQueueingKeepsItsRecordedUnit()
    {
        var a = new Synthetic(1200).Set("outQueueing", 300, 330, 120_000).Analyze();   // 120 ms, recorded in µs
        var o = Only(a, "conn.queueing.increase");
        Assert.Equal("µs", o.Primary.Unit); Assert.Equal(120_000, o.Primary.Maximum); Assert.Equal(50_000, o.Threshold.Absolute);
        Assert.Contains("120000 µs", o.Explanation);
    }

    [Fact]
    public void QueueingBelowTheThresholdOrOnANonBlastSessionIsNotReported()
    {
        Assert.Empty(new Synthetic(1200).Set("outQueueing", 300, 330, 30_000).Analyze().Observations);
        var rdp = new Synthetic(1200); rdp.Protocols.Clear(); rdp.Protocols.Add("Microsoft RDP"); rdp.Set("outQueueing", 300, 330, 120_000);
        var a = rdp.Analyze();
        Assert.Empty(a.Observations); Assert.Equal(RuleState.NotApplicable, Rule(a, "conn.queueing.increase").State);
    }

    [Fact]
    public void BandwidthPressureNeedsBothValuesInVerifiedUnits()
    {
        var withCapacity = new Synthetic(1200).Add("bandwidthConsumption", "Mbps", _ => 10).Add("availableBandwidth", "Mbps", _ => 50).Set("bandwidthConsumption", 300, 360, 48);
        var o = Only(withCapacity.Analyze(), "conn.bandwidth.pressure");
        Assert.Equal(2, o.Metrics.Count); Assert.Equal("Capacity", o.Metrics[1].Role); Assert.Contains("48 Mbps", o.Explanation); Assert.Contains("85 % of the available bandwidth", o.Explanation);
        Assert.Equal(0.85, o.Threshold.Absolute);

        var unverified = new Synthetic(1200).Add("bandwidthConsumption", "Mbps", _ => 48).Add("availableBandwidth", "raw (unit unverified)", _ => 50);
        var a = unverified.Analyze();
        Assert.Empty(a.Observations); Assert.Equal(RuleState.InsufficientData, Rule(a, "conn.bandwidth.pressure").State);

        var plenty = new Synthetic(1200).Add("bandwidthConsumption", "Mbps", _ => 10).Add("availableBandwidth", "Mbps", _ => 50);
        Assert.Empty(plenty.Analyze().Observations);
    }

    [Fact]
    public void BandwidthUnitsAreConvertedBeforeTheyAreCompared()
    {
        var s = new Synthetic(1200).Add("bandwidthConsumption", "Kbps", _ => 10_000).Add("availableBandwidth", "Mbps", _ => 50).Set("bandwidthConsumption", 300, 360, 48_000);
        Assert.Equal("Kbps", Only(s.Analyze(), "conn.bandwidth.pressure").Primary.Unit);
    }

    // ---------- graphics ----------

    [Fact]
    public void LowFpsDuringDemonstratedActivityIsDetectedWithItsActivityEvidence()
    {
        var a = new Synthetic(1200).Set("fps", 300, 380, 8).Analyze();
        var o = Only(a, "gfx.fps.low");
        Assert.Equal(ObservationKind.Sustained, o.Kind); Assert.Equal(Severity.Warning, o.Severity); Assert.Equal(HistoryGroup.Graphics, o.Domain);
        Assert.Equal(8, o.Primary.Minimum); Assert.Equal("FPS", o.Primary.Unit);
        var activity = Assert.Single(o.Metrics, m => m.Role == "Display activity");
        Assert.Equal("dirtyFps", activity.ChannelId); Assert.True(activity.P50 > 10);
        Assert.Contains("Display activity was present", o.Explanation);
        Assert.Contains(o.Limitations, l => l.Contains("workload value stored in an older recording"));
    }

    [Fact]
    public void VeryLowFpsIsCritical()
    {
        Assert.Equal(Severity.Critical, Only(new Synthetic(1200).Set("fps", 300, 380, 2).Analyze(), "gfx.fps.low").Severity);
    }

    [Fact]
    public void LowFpsOnAnIdleDesktopIsNotAnAnomaly()
    {
        // A static screen: very low frame rate and almost no dirty frames. This is expected, not a performance problem.
        var a = new Synthetic(1200).Set("fps", 300, 600, 2).Set("dirtyFps", 300, 600, 0.4).Analyze();
        Assert.Empty(a.Observations);
        Assert.Contains("static screen", Rule(a, "gfx.fps.low").Reason);
    }

    [Fact]
    public void ARecordingThatIsIdleThroughoutIsNotApplicableNotHealthy()
    {
        var a = new Synthetic(600).Set("fps", 0, 600, 2).Set("dirtyFps", 0, 600, 0.2).Analyze();
        var rule = Rule(a, "gfx.fps.low");
        Assert.Equal(RuleState.NotApplicable, rule.State); Assert.Empty(a.Observations);
        Assert.Equal(RuleState.NotApplicable, Rule(a, "gfx.dropped.high").State);
    }

    [Fact]
    public void WithoutTheDirtyFrameCounterActivityCannotBeEstablishedSoFpsIsNotJudged()
    {
        var a = new Synthetic(1200).Remove("dirtyFps").Set("fps", 300, 380, 3).Analyze();
        Assert.Empty(a.Observations);
        var rule = Rule(a, "gfx.fps.low");
        Assert.Equal(RuleState.InsufficientData, rule.State); Assert.Equal(AnalysisStatus.InsufficientData, rule.Status);
        Assert.Contains("Display activity cannot be established", rule.Reason);
    }

    [Fact]
    public void ADropFromAHighBaselineIsFoundEvenAboveTheAbsoluteFloor()
    {
        var s = new Synthetic(1200).Add("fps", "FPS", i => 60 + 2 * Math.Sin(i * 0.23)).Set("fps", 300, 380, 30);
        var o = Only(s.Analyze(), "gfx.fps.low");
        Assert.Equal(ThresholdBasis.AbsoluteAndBaseline, o.Threshold.Basis);
        Assert.InRange(o.Threshold.BaselineLimit!.Value, 34, 38);          // median 60 − 4 × (10 % of the median)
        Assert.Equal(30, o.Primary.Minimum);
    }

    [Fact]
    public void MissingFpsValuesAreNeverReadAsZeroFps()
    {
        // 90 seconds in which the frame-rate counter returned nothing while the screen was active: no samples, so no "0 FPS" anomaly
        var a = new Synthetic(1200).Missing("fps", 300, 390).Analyze();
        Assert.Empty(a.Observations);
        var coverage = Assert.Single(a.Coverage, c => c.ChannelId == "fps");
        Assert.Equal(90, coverage.Samples - coverage.ValidSamples);
        // and an episode that runs into missing values ends as coverage ending, not as a drop to zero or a recovery
        var o = Only(new Synthetic(1200).Set("fps", 300, 360, 6).Missing("fps", 360, 420).Analyze(), "gfx.fps.low");
        Assert.Equal(ObservationEnd.CoverageEnded, o.Ending); Assert.Equal(6, o.Primary.Minimum); Assert.Equal(6, o.Primary.Maximum);
    }

    [Fact]
    public void FpsDuringActivityThatStaysNormalIsNotReported()
    {
        Assert.Empty(new Synthetic(1200).Set("fps", 300, 380, 24).Analyze().Observations);
    }

    [Fact]
    public void IncreasedDroppedFramesAreDetectedOnlyDuringActivity()
    {
        var active = new Synthetic(1200).Set("droppedFrames", 300, 380, 9).Analyze();
        Assert.Equal(Severity.Warning, Only(active, "gfx.dropped.high").Severity);
        var idle = new Synthetic(1200).Set("droppedFrames", 300, 380, 9).Set("dirtyFps", 290, 400, 0.3).Analyze();
        Assert.Empty(idle.Observations);
    }

    // ---------- guest ----------

    [Fact]
    public void SustainedCpuSaturationIsDetectedAndNotAttributedToTheProtocol()
    {
        var a = new Synthetic(1200).Set("cpu", 300, 420, 99).Analyze();
        var o = Only(a, "guest.cpu.saturation");
        Assert.Single(a.Observations);                                   // the spike rule folds into the saturation
        Assert.Equal(Severity.Critical, o.Severity); Assert.Equal(HistoryGroup.Guest, o.Domain);
        Assert.Contains(o.Limitations, l => l.Contains("not attributed to the remote-display protocol"));
        Assert.Equal(Severity.Warning, Only(new Synthetic(1200).Set("cpu", 300, 420, 94).Analyze(), "guest.cpu.saturation").Severity);
    }

    [Fact]
    public void AShortCpuSpikeIsAnInformationalObservation()
    {
        var a = new Synthetic(1200).Set("cpu", 300, 340, 97).Analyze();
        var o = Assert.Single(a.Observations);
        Assert.Equal("guest.cpu.spike", o.RuleId); Assert.Equal(ObservationKind.Spike, o.Kind); Assert.Equal(Severity.Info, o.Severity);
        Assert.DoesNotContain(a.Observations, x => x.RuleId == "guest.cpu.saturation");
    }

    [Fact]
    public void HighCpuThatIsNormalForTheSessionIsStillSaturationButMildCpuIsNot()
    {
        Assert.Empty(new Synthetic(1200).Set("cpu", 300, 420, 70).Analyze().Observations);
        var always = new Synthetic(1200).Add("cpu", "%", i => 95 + Math.Sin(i * 0.3)).Analyze();
        var o = Only(always, "guest.cpu.saturation");                    // a constantly saturated guest is a finding even though it is the baseline
        Assert.Equal(ObservationKind.Sustained, o.Kind); Assert.True(o.Coverage > 0.99);
    }

    [Fact]
    public void SustainedMemoryUtilizationIsDetectedOnlyAfterTwoMinutes()
    {
        var a = new Synthetic(1200).Set("ram", 300, 480, 93).Analyze();
        var o = Only(a, "guest.ram.high");
        Assert.Equal(Severity.Warning, o.Severity); Assert.Contains(o.Limitations, l => l.Contains("not whether paging"));
        Assert.Equal(Severity.Critical, Only(new Synthetic(1200).Set("ram", 300, 480, 97).Analyze(), "guest.ram.high").Severity);
        Assert.Empty(new Synthetic(1200).Set("ram", 300, 360, 93).Analyze().Observations);          // one minute: too short
    }

    [Fact]
    public void MemoryPressureIsNotInferredFromMissingOrUnsupportedValues()
    {
        var missing = new Synthetic(1200).Missing("ram", 0, 1200).Set("cpu", 0, 1, 25).Analyze();
        Assert.Empty(missing.Observations); Assert.Equal(RuleState.InsufficientData, Rule(missing, "guest.ram.high").State);
        var wrongUnit = new Synthetic(1200).Unit("ram", "GB").Set("ram", 300, 480, 93).Analyze();
        Assert.Empty(wrongUnit.Observations); Assert.Equal(RuleState.InsufficientData, Rule(wrongUnit, "guest.ram.high").State);
    }

    [Fact]
    public void SustainedGpuSaturationIsDetectedWhenGpuMetricsExist()
    {
        var a = new Synthetic(1200).Add("gpu", "%", _ => 30).Set("gpu", 300, 420, 99).Analyze();
        Assert.Equal(Severity.Critical, Only(a, "guest.gpu.saturation").Severity);
        Assert.Equal(RuleState.InsufficientData, Rule(new Synthetic(1200).Analyze(), "guest.gpu.saturation").State);
    }

    [Fact]
    public void DiskThroughputAloneIsAnActivityObservationAndNeverADiskStall()
    {
        var a = new Synthetic(1200).Set("disk", 300, 330, 600).Analyze();
        var o = Assert.Single(a.Observations);
        Assert.Equal("guest.disk.throughput-spike", o.RuleId); Assert.Equal(Severity.Info, o.Severity); Assert.Equal("MB/s", o.Primary.Unit);
        Assert.Contains(o.Limitations, l => l.Contains("does not show disk latency or a disk problem"));
        var stall = Rule(a, "guest.disk.stall");
        Assert.Equal(RuleState.InsufficientData, stall.State); Assert.Contains("disk latency", stall.Reason);
        Assert.DoesNotContain(a.Observations, x => x.RuleId == "guest.disk.stall");
    }

    [Fact]
    public void ADiskStallIsReportedOnlyWhenALatencyMetricWasRecorded()
    {
        var s = new Synthetic(1200).Add("diskLatency", "ms", _ => 4).Set("diskLatency", 300, 330, 350);
        var o = Only(s.Analyze(), "guest.disk.stall");
        Assert.Equal(ObservationKind.Sustained, o.Kind); Assert.Equal(350, o.Primary.Maximum);
        Assert.Empty(new Synthetic(1200).Add("diskLatency", "ms", _ => 4).Set("diskLatency", 300, 305, 350).Analyze().Observations);   // five seconds is not sustained
    }

    // ---------- baseline / insufficient data ----------

    [Fact]
    public void AShortRecordingGivesInsufficientDataForAdaptiveRulesInsteadOfAnArbitraryThreshold()
    {
        var a = new Synthetic(90).Set("rtt", 30, 33, 400).Analyze();
        var rule = Rule(a, "conn.rtt.deviation");
        Assert.Equal(RuleState.InsufficientData, rule.State); Assert.Contains("Insufficient data for an adaptive baseline", rule.Reason);
        Assert.Empty(a.Observations);                                    // not turned into a spike with a made-up limit
        Assert.Equal(RuleState.Evaluated, Rule(a, "conn.rtt.high").State);
    }

    [Fact]
    public void WhenTheBaselineIsUnreliableTheAbsoluteThresholdIsUsedAndSaidSo()
    {
        var a = new Synthetic(100).Set("fps", 20, 70, 8).Analyze();
        var o = Only(a, "gfx.fps.low");
        Assert.Equal(ThresholdBasis.Absolute, o.Threshold.Basis); Assert.Null(o.Threshold.BaselineLimit);
        Assert.False(o.Threshold.Baseline!.Reliable);
        Assert.Contains(o.Limitations, l => l.StartsWith("Baseline unavailable"));
        Assert.DoesNotContain("session baseline", o.Threshold.Comparison);
    }

    [Fact]
    public void AnExtremeOutlierCannotDragTheBaselineWithIt()
    {
        // 8 % of the samples are 400 ms; a mean-based baseline would sit near 60 ms and hide a 100 ms episode as "normal".
        var s = new Synthetic(1200).Add("rtt", "ms", i => i % 12 == 0 ? 400 : 30 + 3 * Math.Sin(i * 0.37));
        s.Set("rtt", 700, 760, 130);
        var a = s.Analyze();
        var o = Assert.Single(a.Observations, x => x.Kind == ObservationKind.Sustained);
        Assert.InRange(o.Threshold.Baseline!.Median, 26, 32);
        Assert.InRange(o.Threshold.Baseline!.Sigma, 3, 8);
        Assert.InRange(o.Threshold.BaselineLimit!.Value, 50, 70);
    }

    [Fact]
    public void ABaselineSurvivesAQuarterOfTheSessionBeingDegraded()
    {
        var s = new Synthetic(1200).Set("rtt", 0, 300, 250);
        var a = s.Analyze();
        var o = Assert.Single(a.Observations, x => x.Start == Synthetic.T0.AddSeconds(0));
        Assert.True(o.Threshold.Baseline is null || o.Threshold.Baseline.Median < 60);
        // another episode later is still judged against the healthy bulk
        var b = new Synthetic(1200).Set("rtt", 0, 300, 250).Set("rtt", 800, 840, 110).Analyze();
        Assert.Contains(b.Observations, x => x.Start == Synthetic.T0.AddSeconds(800));
    }

    [Fact]
    public void ASessionWithVariableSamplingIntervalsIsAnalyzedByTimeNotByCount()
    {
        // Five-second sampling: 30 s is six samples. The episode needs duration, not 30 samples.
        var s = new Synthetic(600, stepSeconds: 5).Set("rtt", 1500, 1700, 220);
        var o = Only(s.Analyze(), "conn.rtt.high");
        Assert.Equal(40, o.SampleCount); Assert.True(o.Duration >= TimeSpan.FromSeconds(190));
        // the same wall-clock span at one-second sampling gives the same observation window
        var fast = new Synthetic(3000).Set("rtt", 1500, 1700, 220);
        Assert.Equal(Only(fast.Analyze(), "conn.rtt.high").Start, o.Start);
    }
}
