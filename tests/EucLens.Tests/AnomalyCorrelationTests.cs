using EucLens.Core;
using Xunit;

namespace EucLens.Tests;

/// <summary>Each correlation pattern with positive and negative cases, plus the guarantees that apply to all of them: no causal wording, no duplicates, no joining across gaps.</summary>
public sealed class AnomalyCorrelationTests
{
    private static IEnumerable<AnomalyCorrelation> Of(AnomalyAnalysis a, CorrelationPattern p) => a.Correlations.Where(c => c.Pattern == p);

    // ---------- pattern 1: RTT and packet loss ----------

    [Fact]
    public void RttAndPacketLossInOverlappingIntervalsCorrelateWithoutNamingACause()
    {
        var a = new Synthetic(1200).Set("rtt", 300, 360, 185).Set("packetLoss", 310, 350, 3.2).Analyze();
        var c = Assert.Single(Of(a, CorrelationPattern.RttAndPacketLoss));
        Assert.Equal(2, c.ComponentIds.Count);
        Assert.All(c.ComponentIds, id => Assert.Contains(a.Observations, o => o.Id == id));
        Assert.Equal(Synthetic.T0.AddSeconds(300), c.Start); Assert.Equal(Synthetic.T0.AddSeconds(359), c.End);
        Assert.Equal(CorrelationTiming.CommonOverlap, c.Timing); Assert.Equal(Synthetic.T0.AddSeconds(310), c.CommonOverlapStart); Assert.Equal(Synthetic.T0.AddSeconds(349), c.CommonOverlapEnd);
        Assert.Equal(TimeSpan.FromSeconds(39), c.CommonOverlap);
        Assert.Equal(Severity.Warning, c.Severity); Assert.Equal(["packetLoss", "rtt"], c.Channels);
        Assert.Contains("RTT and packet loss degraded during overlapping intervals", c.Explanation);
        Assert.Contains("does not establish whether the endpoint, the network, the host or a gateway is involved", c.Explanation);
        Assert.Contains(c.Limitations, l => l.Contains("timing only"));
        AssertNoCausalClaim(c.Explanation);
    }

    private static void AssertNoCausalClaim(string text)
    {
        foreach (var banned in new[] { "caused by", "because of", "root cause is", "due to", "overloaded", "congestion", "firewall", "ISP", "driver", "contention", "faulty" })
            Assert.False(System.Text.RegularExpressions.Regex.IsMatch(text, $@"\b{System.Text.RegularExpressions.Regex.Escape(banned)}\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase), $"causal wording '{banned}' in: {text}");
    }

    [Fact]
    public void RttAndPacketLossFarApartDoNotCorrelate()
    {
        var a = new Synthetic(1200).Set("rtt", 300, 360, 185).Set("packetLoss", 700, 760, 3.2).Analyze();
        Assert.Equal(2, a.Observations.Count); Assert.Empty(a.Correlations);
    }

    [Fact]
    public void IntervalsWithinTheToleranceCorrelateButFurtherApartDoNot()
    {
        // the loss starts 3 s after the RTT episode ends: within the 5 s tolerance
        var near = new Synthetic(1200).Set("rtt", 300, 360, 185).Set("packetLoss", 362, 400, 3).Analyze();
        Assert.Single(Of(near, CorrelationPattern.RttAndPacketLoss));
        var far = new Synthetic(1200).Set("rtt", 300, 360, 185).Set("packetLoss", 380, 420, 3).Analyze();
        Assert.Empty(Of(far, CorrelationPattern.RttAndPacketLoss));
    }

    // ---------- the overlap contract: common to all components only when it truly is ----------

    [Fact]
    public void ChainedObservationsAreOneCorrelationWithoutACommonInterval()
    {
        // RTT episode A (100-159) overlaps the loss episode B (150-259); B overlaps the RTT episode C (240-299); A and C are 80 s apart
        var a = new Synthetic(1200).Set("rtt", 100, 160, 200).Set("packetLoss", 150, 260, 3).Set("rtt", 240, 300, 200).Analyze();
        var c = Assert.Single(Of(a, CorrelationPattern.RttAndPacketLoss));
        Assert.Equal(3, c.ComponentIds.Count);
        Assert.Equal(CorrelationTiming.Chained, c.Timing);
        Assert.Null(c.CommonOverlapStart); Assert.Null(c.CommonOverlapEnd); Assert.Null(c.CommonOverlap);
        Assert.Equal(Synthetic.T0.AddSeconds(100), c.Start); Assert.Equal(Synthetic.T0.AddSeconds(299), c.End);       // the span of the cluster, not an overlap
        Assert.Equal(2, c.Links.Count);
        Assert.All(c.Links, l => { Assert.NotNull(l.OverlapStart); Assert.Equal(TimeSpan.Zero, l.Gap); Assert.True(l.OverlapStart <= l.OverlapEnd); });
        Assert.Contains(c.Links, l => l.OverlapStart == Synthetic.T0.AddSeconds(150) && l.OverlapEnd == Synthetic.T0.AddSeconds(159));
        Assert.Contains(c.Links, l => l.OverlapStart == Synthetic.T0.AddSeconds(240) && l.OverlapEnd == Synthetic.T0.AddSeconds(259));
        Assert.Contains("No instant is common to all 3 components", c.Explanation);
        Assert.DoesNotContain("all components were present", c.Explanation);
        Assert.DoesNotContain("overlapping intervals", c.Explanation);
        Assert.Contains(c.Limitations, l => l.Contains("No instant is common to all components"));
        AssertNoCausalClaim(c.Explanation);
        // the two RTT episodes really are separate observations that do not overlap each other
        var rtt = a.Observations.Where(o => o.RuleId == "conn.rtt.high").OrderBy(o => o.Start).ToList();
        Assert.True(rtt[0].End < rtt[1].Start);
    }

    [Fact]
    public void TwoObservationsThatOnlyLieWithinTheToleranceAreLinkedWithoutAnOverlap()
    {
        var a = new Synthetic(1200).Set("rtt", 300, 360, 185).Set("packetLoss", 362, 400, 3).Analyze();      // 3 s between the end of the RTT episode and the start of the loss
        var c = Assert.Single(Of(a, CorrelationPattern.RttAndPacketLoss));
        Assert.Equal(CorrelationTiming.WithinTolerance, c.Timing);
        Assert.Null(c.CommonOverlapStart); Assert.Null(c.CommonOverlapEnd); Assert.Null(c.CommonOverlap);
        var link = Assert.Single(c.Links);
        Assert.Null(link.OverlapStart); Assert.Null(link.OverlapEnd); Assert.Equal(TimeSpan.FromSeconds(3), link.Gap);
        Assert.Contains("do not overlap but lie 3 s apart", c.Explanation);
        Assert.DoesNotContain("overlapping intervals", c.Explanation); Assert.DoesNotContain("all components were present", c.Explanation);
        Assert.Contains(c.Limitations, l => l.Contains("do not overlap"));
        AssertNoCausalClaim(c.Explanation);
    }

    [Fact]
    public void ACommonIntervalIsReportedOnlyWhenEveryComponentWasPresentInIt()
    {
        // FPS 300-379, RTT 310-389, loss 320-369: all three overlap in 320-369
        var a = new Synthetic(1200).Set("fps", 300, 380, 6).Set("rtt", 310, 390, 210).Set("packetLoss", 320, 370, 3).Analyze();
        var c = Assert.Single(Of(a, CorrelationPattern.GraphicsAndConnection));
        Assert.Equal(3, c.ComponentIds.Count); Assert.Equal(CorrelationTiming.CommonOverlap, c.Timing);
        Assert.Equal(Synthetic.T0.AddSeconds(320), c.CommonOverlapStart); Assert.Equal(Synthetic.T0.AddSeconds(369), c.CommonOverlapEnd);
        Assert.Contains("all components were present", c.Explanation); Assert.Contains("14:05:20 and 14:06:09 UTC", c.Explanation);
        foreach (var id in c.ComponentIds)
        {
            var o = a.Observations.Single(x => x.Id == id);
            Assert.True(o.Start <= c.CommonOverlapStart && c.CommonOverlapEnd <= o.End);
        }
        AssertNoCausalClaim(c.Explanation);
    }

    [Fact]
    public void TheCommonIntervalInvariantHoldsForEveryCorrelationOfManyArrangements()
    {
        // Arrangements of an RTT episode, two loss episodes and a second RTT episode at several offsets: whatever the cluster looks like, a reported
        // common interval lies inside every component and no common interval is reported when the components have no instant in common.
        var checkedCorrelations = 0; var chained = 0; var common = 0;
        foreach (var gap1 in new[] { 0, 10, 30, 60 })
            foreach (var shift in new[] { -40, 0, 30, 80, 120 })
            {
                var s = new Synthetic(1200).Set("rtt", 100, 160, 200).Set("packetLoss", 150 + gap1, 220 + gap1, 3).Set("rtt", 200 + shift + gap1, 260 + shift + gap1, 200);
                var a = s.Analyze();
                foreach (var cor in a.Correlations.Where(x => x.Pattern != CorrelationPattern.AvailabilityOverlap))
                {
                    var parts = cor.ComponentIds.Select(id => a.Observations.Single(o => o.Id == id)).ToList();
                    var maxStart = parts.Max(p => p.Start); var minEnd = parts.Min(p => p.End);
                    if (cor.CommonOverlapStart is { } cs && cor.CommonOverlapEnd is { } ce)
                    {
                        common++;
                        Assert.Equal(CorrelationTiming.CommonOverlap, cor.Timing);
                        Assert.All(parts, p => Assert.True(p.Start <= cs && ce <= p.End));
                        Assert.Equal(maxStart, cs); Assert.Equal(minEnd, ce);
                    }
                    else
                    {
                        Assert.True(maxStart > minEnd, "a common instant exists but none was reported");
                        Assert.NotEqual(CorrelationTiming.CommonOverlap, cor.Timing);
                        if (parts.Count > 2) { chained++; Assert.Equal(CorrelationTiming.Chained, cor.Timing); }
                    }
                    Assert.NotEmpty(cor.Links);
                    Assert.All(cor.Links, l => Assert.True(l.OverlapStart is null ? l.Gap > TimeSpan.Zero : l.Gap == TimeSpan.Zero));
                    checkedCorrelations++;
                }
            }
        Assert.True(checkedCorrelations >= 10 && chained >= 1 && common >= 1, $"{checkedCorrelations} correlations, {chained} chained, {common} common");
    }

    [Fact]
    public void CorrelationIdsAreUnaffectedByHowTheComponentsAreRelated()
    {
        Synthetic Chain() => new Synthetic(1200).Set("rtt", 100, 160, 200).Set("packetLoss", 150, 260, 3).Set("rtt", 240, 300, 200);
        var first = Assert.Single(Of(Chain().Analyze(), CorrelationPattern.RttAndPacketLoss));
        var second = Assert.Single(Of(Chain().Analyze(), CorrelationPattern.RttAndPacketLoss));
        Assert.Equal(first.Id, second.Id); Assert.Equal(first.Explanation, second.Explanation);
        Assert.Equal(first.Links.Select(l => (l.FirstId, l.SecondId)), second.Links.Select(l => (l.FirstId, l.SecondId)));
        Assert.StartsWith("cor-", first.Id);
        // the same components give the same id whatever the unrelated rest of the recording holds
        var other = Chain(); other.Set("ram", 700, 710, 50);
        Assert.Equal(first.Id, Assert.Single(Of(other.Analyze(), CorrelationPattern.RttAndPacketLoss)).Id);
    }

    [Fact]
    public void ARttSpikeInsideALossBurstCorrelates()
    {
        var a = new Synthetic(1200).Set("rtt", 500, 501, 420).Set("packetLoss", 498, 504, 9).Analyze();
        Assert.Single(Of(a, CorrelationPattern.RttAndPacketLoss));
    }

    [Fact]
    public void OneContinuingProblemWithSeveralComponentsIsOneCorrelationNotSeveral()
    {
        var s = new Synthetic(1200).Set("rtt", 300, 460, 200);
        for (var k = 0; k < 4; k++) s.Set("packetLoss", 310 + k * 30, 314 + k * 30, 8);          // four loss bursts inside the RTT episode
        var a = s.Analyze();
        var c = Assert.Single(Of(a, CorrelationPattern.RttAndPacketLoss));
        Assert.Equal(5, c.ComponentIds.Count);
        Assert.Equal(c.ComponentIds.Distinct().Count(), c.ComponentIds.Count);
    }

    [Fact]
    public void ObservationsOnEitherSideOfAGapAreNeverCorrelatedAsOneEvent()
    {
        var a = new Synthetic(1200).Set("rtt", 100, 400, 200).Set("packetLoss", 100, 400, 3).Skip(240, 300).Analyze();
        var pairs = Of(a, CorrelationPattern.RttAndPacketLoss).ToList();
        Assert.Equal(2, pairs.Count);                                                           // one per continuous stretch
        Assert.All(pairs, c => Assert.Equal(2, c.ComponentIds.Count));
        Assert.True(pairs.Max(c => c.Start) > pairs.Min(c => c.End));
        Assert.Equal(pairs.Count, pairs.Select(c => c.Id).Distinct().Count());
    }

    [Fact]
    public void ObservationsSeparatedOnlyByASessionBoundaryAreNotCorrelatedEitherEvenWhenTheyTouch()
    {
        // no missing samples, but monitoring was paused and resumed at second 200: the RTT episode before it and the loss episode after it are different stretches of data
        var a = new Synthetic(1200).Set("rtt", 140, 200, 200).Set("packetLoss", 200, 260, 3)
            .Event(HistoryEventKind.MonitoringPaused, 199.5).Event(HistoryEventKind.MonitoringResumed, 200).Analyze();
        Assert.Equal(2, a.Observations.Count);
        Assert.Empty(Of(a, CorrelationPattern.RttAndPacketLoss));
    }

    [Fact]
    public void AQualifiedObservationDoesNotTakePartInACorrelation()
    {
        var s = new Synthetic(1200).Set("rtt", 300, 400, 200).Set("packetLoss", 300, 400, 3);
        for (var i = 300; i < 400; i++) if (i % 4 != 0) s.Missing("packetLoss", i, i + 1);           // most loss values are missing
        var a = s.Analyze();
        Assert.Equal(AnalysisStatus.QualifiedObservation, a.Observations.Single(o => o.RuleId == "conn.loss.sustained").Status);
        Assert.Empty(Of(a, CorrelationPattern.RttAndPacketLoss));
    }

    // ---------- pattern 2: graphics and connection ----------

    [Fact]
    public void LowFpsDuringActivityWithElevatedRttCorrelates()
    {
        var a = new Synthetic(1200).Set("fps", 300, 380, 6).Set("rtt", 310, 390, 210).Analyze();
        var c = Assert.Single(Of(a, CorrelationPattern.GraphicsAndConnection));
        Assert.Contains(c.ComponentIds, id => a.Observations.Single(o => o.Id == id).RuleId == "gfx.fps.low");
        Assert.Contains("with display activity established", c.Explanation); Assert.Contains("does not show that one caused the other", c.Explanation);
        AssertNoCausalClaim(c.Explanation);
    }

    [Fact]
    public void FpsWithQueueingOrLossAlsoCorrelatesButIdleFpsNeverDoes()
    {
        var queue = new Synthetic(1200).Set("fps", 300, 380, 6).Set("outQueueing", 305, 380, 150_000).Analyze();
        Assert.Single(Of(queue, CorrelationPattern.GraphicsAndConnection));
        var loss = new Synthetic(1200).Set("fps", 300, 380, 6).Set("packetLoss", 305, 380, 3).Analyze();
        Assert.Single(Of(loss, CorrelationPattern.GraphicsAndConnection));
        var idle = new Synthetic(1200).Set("fps", 300, 380, 2).Set("dirtyFps", 290, 400, 0.3).Set("rtt", 310, 390, 210).Analyze();
        Assert.Empty(Of(idle, CorrelationPattern.GraphicsAndConnection));
        Assert.DoesNotContain(idle.Observations, o => o.RuleId == "gfx.fps.low");
    }

    [Fact]
    public void AWeakConnectionSideDoesNotCorrelateWithAFrameRateProblem()
    {
        // a single-sample RTT spike next to a long frame-rate problem is not a convincing link
        var a = new Synthetic(1200).Set("fps", 300, 380, 6).Set("rtt", 340, 341, 420).Analyze();
        Assert.Empty(Of(a, CorrelationPattern.GraphicsAndConnection));
        Assert.Contains(a.Observations, o => o.RuleId == "conn.rtt.deviation");
    }

    // ---------- pattern 3: FPS and guest CPU ----------

    [Fact]
    public void LowFpsWithSustainedCpuPressureCorrelatesWithoutBlamingTheCpu()
    {
        var a = new Synthetic(1200).Set("fps", 300, 420, 7).Set("cpu", 290, 430, 98).Analyze();
        var c = Assert.Single(Of(a, CorrelationPattern.FpsAndGuestCpu));
        Assert.Contains("does not show that CPU was the cause", c.Explanation);
        AssertNoCausalClaim(c.Explanation);
    }

    [Fact]
    public void ACpuSpikeWithoutGraphicsDegradationInventsNoCorrelation()
    {
        var a = new Synthetic(1200).Set("cpu", 300, 340, 98).Analyze();
        Assert.Single(a.Observations); Assert.Empty(a.Correlations);
        var sustained = new Synthetic(1200).Set("cpu", 300, 420, 98).Analyze();
        Assert.Empty(sustained.Correlations);                                                  // sustained CPU, frame rate normal
    }

    [Fact]
    public void ACpuSpikeNextToLowFpsIsNotEnoughForAFpsCpuCorrelation()
    {
        var a = new Synthetic(1200).Set("fps", 300, 420, 7).Set("cpu", 340, 360, 98).Analyze();
        Assert.Empty(Of(a, CorrelationPattern.FpsAndGuestCpu));
    }

    // ---------- pattern 4: several guest resources ----------

    [Fact]
    public void OverlappingCpuAndMemoryPressureCorrelate()
    {
        var a = new Synthetic(1200).Set("cpu", 300, 480, 97).Set("ram", 330, 500, 94).Analyze();
        var c = Assert.Single(Of(a, CorrelationPattern.MultipleGuestResources));
        Assert.Equal(["cpu", "ram"], c.Channels); Assert.Contains("does not show which of them", c.Explanation);
    }

    [Fact]
    public void DiskThroughputActivityDoesNotCountAsADiskPerformanceAnomalyButALatencyStallDoes()
    {
        var activity = new Synthetic(1200).Set("cpu", 300, 480, 97).Set("disk", 310, 360, 700).Analyze();
        Assert.Empty(Of(activity, CorrelationPattern.MultipleGuestResources));
        var stall = new Synthetic(1200).Add("diskLatency", "ms", _ => 4).Set("diskLatency", 320, 360, 400).Set("cpu", 300, 480, 97).Analyze();
        var c = Assert.Single(Of(stall, CorrelationPattern.MultipleGuestResources));
        Assert.Equal(["cpu", "diskLatency"], c.Channels);
    }

    [Fact]
    public void GuestResourcesThatDoNotOverlapDoNotCorrelate()
    {
        var a = new Synthetic(1200).Set("cpu", 300, 420, 97).Set("ram", 700, 900, 94).Analyze();
        Assert.Empty(a.Correlations);
    }

    // ---------- pattern 5: availability ----------

    [Fact]
    public void AnObservationNextToAGapOrASessionChangeIsReportedWithThatContext()
    {
        var gap = new Synthetic(1200).Set("rtt", 300, 380, 200).Skip(380, 450).Analyze();
        var c = Assert.Single(Of(gap, CorrelationPattern.AvailabilityOverlap));
        Assert.Equal(Severity.Info, c.Severity); Assert.Single(c.ComponentIds);
        Assert.Contains("no samples for", c.Explanation); Assert.Contains("not treated as one continuous event", c.Explanation);

        var disconnect = new Synthetic(1200).Set("rtt", 300, 380, 200).Event(HistoryEventKind.RemoteDisconnected, 380, "Remote session disconnected").Analyze();
        var d = Assert.Single(Of(disconnect, CorrelationPattern.AvailabilityOverlap));
        Assert.Contains("Remote session disconnected at 14:06:20 UTC", d.Explanation);
    }

    [Fact]
    public void AnObservationFarFromAnyGapGetsNoAvailabilityCorrelation()
    {
        var a = new Synthetic(1200).Set("rtt", 300, 360, 200).Skip(900, 960).Analyze();
        Assert.Empty(Of(a, CorrelationPattern.AvailabilityOverlap));
    }

    // ---------- all patterns ----------

    [Fact]
    public void CorrelationsAreDeterministicOrderedAndNeverDuplicated()
    {
        var s = new Synthetic(2400).Set("rtt", 300, 360, 185).Set("packetLoss", 310, 350, 3.2).Set("fps", 300, 380, 6).Set("cpu", 300, 420, 97).Set("ram", 320, 480, 95)
            .Set("rtt", 1200, 1260, 185).Set("packetLoss", 1210, 1250, 3.2);
        var first = s.Analyze(); var second = s.Analyze();
        Assert.Equal(first.Correlations.Select(c => c.Id), second.Correlations.Select(c => c.Id));
        Assert.Equal(first.Correlations.Select(c => c.Id).Distinct().Count(), first.Correlations.Count);
        Assert.Equal(first.Correlations.OrderBy(c => c.Start).ThenBy(c => c.End).ThenBy(c => c.Pattern).ThenBy(c => c.Id, StringComparer.Ordinal).Select(c => c.Id), first.Correlations.Select(c => c.Id));
        Assert.Equal(2, Of(first, CorrelationPattern.RttAndPacketLoss).Count());                 // two separate episodes, two correlations
        Assert.All(first.Correlations, c => AssertNoCausalClaim(c.Explanation));
        Assert.All(first.Correlations, c => Assert.All(c.ComponentIds, id => Assert.Contains(first.Observations, o => o.Id == id)));    // evidence links back
    }
}
