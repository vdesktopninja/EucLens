using EucLens.Core;
using Xunit;

namespace EucLens.Tests;

/// <summary>Time windows, missing and invalid data, disconnects, gaps, ordering, units, protocols, limits and cancellation.</summary>
public sealed class AnomalyDataIntegrityTests
{
    private static AnomalyObservation Only(AnomalyAnalysis analysis, string ruleId) => Assert.Single(analysis.Observations, o => o.RuleId == ruleId);
    private static RuleEvaluation Rule(AnomalyAnalysis analysis, string ruleId) => Assert.Single(analysis.Rules, r => r.RuleId == ruleId);
    private static DateTimeOffset At(double s) => Synthetic.T0.AddSeconds(s);

    // ---------- grouping, recovery, coverage ----------

    [Fact]
    public void ManyAdjacentBadSamplesAreOneObservationNotAnAlertStorm()
    {
        var a = new Synthetic(1200).Set("rtt", 300, 600, 220).Set("rtt", 400, 401, 30).Set("rtt", 450, 451, 30).Analyze();   // 300 bad samples with two single good dips
        var o = Assert.Single(a.Observations);
        Assert.Equal("conn.rtt.high", o.RuleId); Assert.Equal(298, o.MatchingSamples); Assert.Equal(At(300), o.Start); Assert.Equal(At(599), o.End);
    }

    [Fact]
    public void TwoEpisodesSeparatedByRecoveryAreTwoObservationsInTimeOrder()
    {
        var a = new Synthetic(1200).Set("rtt", 300, 360, 220).Set("rtt", 400, 460, 220).Analyze();
        Assert.Equal(2, a.Observations.Count);
        Assert.All(a.Observations, o => { Assert.Equal("conn.rtt.high", o.RuleId); Assert.Equal(ObservationEnd.Recovered, o.Ending); });
        Assert.Equal(At(300), a.Observations[0].Start); Assert.Equal(At(400), a.Observations[1].Start);
        Assert.Equal(At(360), a.Observations[0].RecoveredAt);
    }

    [Fact]
    public void AnObservationEndedByMissingDataIsCoverageEndingNotRecovery()
    {
        var a = new Synthetic(1200).Set("rtt", 300, 360, 220).Missing("rtt", 360, 420).Analyze();
        var o = Only(a, "conn.rtt.high");
        Assert.Equal(ObservationEnd.CoverageEnded, o.Ending); Assert.Null(o.RecoveredAt);
        Assert.Contains("recovery is not confirmed", o.Explanation);
        Assert.DoesNotContain("back within the normal criterion", o.Explanation);
        Assert.Contains(o.Limitations, l => l.Contains("recovery is not confirmed"));
    }

    [Fact]
    public void AnObservationStillOpenAtTheEndOfTheRecordingIsNotRecovered()
    {
        var a = new Synthetic(400).Set("rtt", 340, 400, 220).Analyze();
        var o = Only(a, "conn.rtt.high");
        Assert.Equal(ObservationEnd.CoverageEnded, o.Ending); Assert.Equal(At(399), o.End); Assert.Null(o.RecoveredAt);
    }

    [Fact]
    public void RecoveryNeedsAValidNormalSampleRightAfterTheEpisode()
    {
        // one missing sample, then normal values: not proven to be a recovery at the end of the episode
        var a = new Synthetic(1200).Set("rtt", 300, 360, 220).Missing("rtt", 360, 361).Analyze();
        Assert.Equal(ObservationEnd.CoverageEnded, Only(a, "conn.rtt.high").Ending);
    }

    [Fact]
    public void ShortMissingValuesInsideAnEpisodeDoNotSplitItButLowerItsCoverage()
    {
        var a = new Synthetic(1200).Set("rtt", 300, 360, 220).Missing("rtt", 330, 334).Analyze();
        var o = Only(a, "conn.rtt.high");
        Assert.Equal(At(300), o.Start); Assert.Equal(At(359), o.End); Assert.Equal(56, o.SampleCount);
        Assert.Equal(56 / 60.0, o.Coverage, 6); Assert.Equal(AnalysisStatus.ConfirmedObservation, o.Status);
    }

    [Fact]
    public void AnEpisodeWithMostValuesMissingIsOnlyAQualifiedObservation()
    {
        var s = new Synthetic(1200).Set("rtt", 300, 400, 220);
        for (var i = 300; i < 400; i++) if (i % 2 == 0 && i % 10 != 0 || i % 3 == 0) s.Missing("rtt", i, i + 1);
        var o = Only(s.Analyze(), "conn.rtt.high");
        Assert.Equal(AnalysisStatus.QualifiedObservation, o.Status); Assert.True(o.Coverage < 0.8);
        Assert.Contains(o.Limitations, l => l.Contains("had a usable value"));
    }

    [Fact]
    public void ADegradationEndedByTheEndOfDisplayActivityIsNotCalledRecovery()
    {
        var a = new Synthetic(1200).Set("fps", 300, 600, 6).Set("dirtyFps", 360, 600, 0.3).Analyze();
        var o = Only(a, "gfx.fps.low");
        Assert.Equal(ObservationEnd.ContextEnded, o.Ending); Assert.Null(o.RecoveredAt);
        Assert.Contains("not a recovery", o.Explanation);
    }

    // ---------- gaps ----------

    [Fact]
    public void AShortSampleGapInsideAnEpisodeDoesNotSplitIt()
    {
        var a = new Synthetic(1200).Set("rtt", 300, 400, 220).Skip(340, 344).Analyze();     // 5 s between two samples: within the gap threshold
        var o = Only(a, "conn.rtt.high");
        Assert.Equal(At(300), o.Start); Assert.Equal(At(399), o.End); Assert.Equal(96, o.SampleCount);
    }

    [Fact]
    public void ALongSampleGapSplitsTheObservationAndTheTwoHalvesStayApart()
    {
        var a = new Synthetic(1200).Set("rtt", 300, 500, 220).Skip(380, 450).Analyze();
        var rtt = a.Observations.Where(o => o.RuleId == "conn.rtt.high").ToList();
        Assert.Equal(2, rtt.Count);
        Assert.Equal(At(300), rtt[0].Start); Assert.Equal(At(379), rtt[0].End); Assert.Equal(ObservationEnd.CoverageEnded, rtt[0].Ending); Assert.Null(rtt[0].RecoveredAt);
        Assert.Equal(At(450), rtt[1].Start); Assert.Equal(At(499), rtt[1].End);
        Assert.Contains(rtt[1].Limitations, l => l.Contains("first sample of a continuous stretch"));
        Assert.NotEqual(rtt[0].Id, rtt[1].Id);
        Assert.True(a.Limitations.Count == 0 || !a.Limitations.Any(l => l.Contains("continuous")));
    }

    [Fact]
    public void AGapMakesTheRecordingLessCoveredAndSaysSo()
    {
        var s = new Synthetic(1200);
        for (var k = 0; k < 6; k++) s.Skip(100 + k * 200, 190 + k * 200);               // 6 gaps of 90 s
        var a = s.Analyze();
        Assert.True(a.ReducedConfidence);
        Assert.Contains(a.Limitations, l => l.Contains("Samples cover only") && l.Contains("6 gap(s)"));
    }

    // ---------- disconnects ----------

    [Fact]
    public void ADisconnectedSessionDoesNotProduceZeroOrStaleValuedAnomalies()
    {
        var s = new Synthetic(1200).Event(HistoryEventKind.RemoteDisconnected, 200).Event(HistoryEventKind.RemoteReconnected, 300);
        // while disconnected the counters hold stale or zero values that must not be read as measurements
        s.Set("rtt", 200, 300, 500).Set("fps", 200, 300, 0).Set("packetLoss", 200, 300, 40).Set("outQueueing", 200, 300, 900_000);
        var a = s.Analyze();
        Assert.Empty(a.Observations);
        Assert.Equal(RuleState.Evaluated, Rule(a, "conn.rtt.high").State);
    }

    [Fact]
    public void GuestMetricsStayEligibleWhileTheRemoteSessionIsDisconnected()
    {
        var s = new Synthetic(1200).Event(HistoryEventKind.RemoteDisconnected, 200).Event(HistoryEventKind.RemoteReconnected, 400).Set("cpu", 250, 380, 99);
        var o = Only(s.Analyze(), "guest.cpu.saturation");
        Assert.Equal(At(250), o.Start); Assert.Equal(At(379), o.End);
        Assert.Equal(ObservationEnd.Recovered, o.Ending);
    }

    [Fact]
    public void ReconnectionRestoresEligibilityAndASplitObservationNeverSpansTheDisconnect()
    {
        var s = new Synthetic(1200).Set("rtt", 150, 350, 500).Event(HistoryEventKind.RemoteDisconnected, 200).Event(HistoryEventKind.RemoteReconnected, 300);
        var a = s.Analyze();
        var rtt = a.Observations.Where(o => o.RuleId == "conn.rtt.high").ToList();
        Assert.Equal(2, rtt.Count);
        Assert.Equal(At(150), rtt[0].Start); Assert.Equal(At(199), rtt[0].End); Assert.Equal(ObservationEnd.CoverageEnded, rtt[0].Ending);
        Assert.Equal(At(300), rtt[1].Start); Assert.Equal(At(349), rtt[1].End);
        Assert.All(a.Correlations, c => Assert.Equal(CorrelationPattern.AvailabilityOverlap, c.Pattern));
    }

    [Fact]
    public void AMissingBlastAgentAndRdpOrCitrixSessionsDoNotFabricateConnectionResults()
    {
        var s = new Synthetic(1200, healthy: false).Add("cpu", "%", _ => 25).Add("ram", "%", _ => 45);
        s.Protocols.Clear(); s.Protocols.Add("Microsoft RDP");
        var a = s.Analyze();
        Assert.Empty(a.Observations);
        foreach (var id in new[] { "conn.rtt.high", "conn.rtt.deviation", "conn.loss.sustained", "gfx.fps.low", "gfx.dropped.high" })
            Assert.Equal(RuleState.InsufficientData, Rule(a, id).State);
        Assert.Equal(RuleState.NotApplicable, Rule(a, "conn.queueing.increase").State);
    }

    [Fact]
    public void ACounterThatNeverHadAValidValueIsInsufficientDataNotAHealthyZero()
    {
        var s = new Synthetic(1200).Missing("rtt", 0, 1200).Missing("packetLoss", 0, 1200);
        var a = s.Analyze();
        Assert.Empty(a.Observations);
        Assert.Equal(RuleState.InsufficientData, Rule(a, "conn.rtt.high").State);
        var coverage = Assert.Single(a.Coverage, c => c.ChannelId == "rtt");
        Assert.False(coverage.Usable); Assert.Equal(0, coverage.ValidSamples); Assert.Null(coverage.FirstValid);
    }

    // ---------- invalid values and units ----------

    [Fact]
    public void NonFiniteAndOutOfRangeValuesAreCounterErrorsNotMeasurements()
    {
        var s = new Synthetic(1200).Set("rtt", 300, 360, double.PositiveInfinity).Set("cpu", 300, 420, 150).Set("packetLoss", 500, 560, -3).Set("ram", 600, 800, double.NaN);
        var a = s.Analyze();
        Assert.Empty(a.Observations);
        Assert.Equal(60, Assert.Single(a.Coverage, c => c.ChannelId == "rtt").Samples - Assert.Single(a.Coverage, c => c.ChannelId == "rtt").ValidSamples);
        Assert.Equal(120, Assert.Single(a.Coverage, c => c.ChannelId == "cpu").OutOfRangeSamples);
        Assert.Equal(60, Assert.Single(a.Coverage, c => c.ChannelId == "packetLoss").OutOfRangeSamples);
    }

    [Fact]
    public void UnsupportedOrUnverifiedUnitsNeverCorruptThresholds()
    {
        // 200 in "furlongs" or in a raw counter is not 200 ms.
        foreach (var unit in new[] { "furlongs", "raw (unit unverified)", "", "ticks" })
        {
            var a = new Synthetic(1200).Unit("rtt", unit).Set("rtt", 300, 360, 200).Analyze();
            Assert.DoesNotContain(a.Observations, o => o.Primary.ChannelId == "rtt");
            var rule = Rule(a, "conn.rtt.high");
            Assert.Equal(RuleState.InsufficientData, rule.State); Assert.Contains("not", rule.Reason);
            Assert.False(Assert.Single(a.Coverage, c => c.ChannelId == "rtt").Usable);
        }
        var cpu = new Synthetic(1200).Unit("cpu", "MHz").Set("cpu", 300, 420, 99).Analyze();
        Assert.DoesNotContain(cpu.Observations, o => o.Primary.ChannelId == "cpu");
    }

    [Fact]
    public void SupportedUnitsAreConvertedForThresholdsAndReportedInTheRecordedUnit()
    {
        var seconds = new Synthetic(1200).Add("rtt", "s", i => 0.03 + 0.003 * Math.Sin(i * 0.37)).Set("rtt", 300, 360, 0.2).Analyze();
        var o = Only(seconds, "conn.rtt.high");
        Assert.Equal("s", o.Primary.Unit); Assert.Equal(0.2, o.Primary.Maximum, 9); Assert.Equal(0.15, o.Threshold.Absolute!.Value, 9);
        Assert.Contains("0.2 s", o.Explanation);
        // the micro sign has two Unicode spellings; both are the same unit
        foreach (var unit in new[] { "µs", "μs", "us" })
            Assert.True(UnitTable.Milliseconds.TryGetFactor(unit, out var factor) && Math.Abs(factor - 0.001) < 1e-12, unit);
        Assert.False(UnitTable.Milliseconds.TryGetFactor("raw (unit unverified)", out _));
    }

    // ---------- ordering, duplicates, time zones ----------

    private static string Fingerprint(AnomalyAnalysis a) =>
        string.Join("\n", a.Observations.Select(o => $"{o.Id}|{o.RuleId}|{o.Start.UtcTicks}|{o.End.UtcTicks}|{o.Severity}|{o.Explanation}")) + "\n" + string.Join("\n", a.Correlations.Select(c => $"{c.Id}|{c.Pattern}|{c.Explanation}"));

    private static Synthetic Busy()
    {
        var s = new Synthetic(1200);
        s.Set("rtt", 300, 360, 220).Set("packetLoss", 310, 350, 3).Set("cpu", 700, 820, 96).Set("rtt", 900, 902, 300);
        return s;
    }

    [Fact]
    public void ResultsAreIdenticalAcrossRepeatedRunsWithStableIdsAndOrder()
    {
        var first = Busy().Analyze(); var second = Busy().Analyze();
        Assert.Equal(Fingerprint(first), Fingerprint(second));
        Assert.Equal(first.Observations.OrderBy(o => o.Start).ThenBy(o => o.End).ThenBy(o => o.RuleId, StringComparer.Ordinal).Select(o => o.Id), first.Observations.Select(o => o.Id));
        Assert.Equal(first.Observations.Select(o => o.Id).Distinct().Count(), first.Observations.Count);
        Assert.All(first.Observations, o => Assert.StartsWith("obs-", o.Id));
        Assert.All(first.Correlations, c => Assert.StartsWith("cor-", c.Id));
    }

    private static string AllText(AnomalyAnalysis a) => string.Join("\n", a.Observations.SelectMany(o => new[] { o.Id, o.Explanation, o.Threshold.Comparison }.Concat(o.Limitations))
        .Concat(a.Correlations.SelectMany(c => new[] { c.Id, c.Explanation }.Concat(c.Limitations))).Concat(a.Rules.Select(r => r.Reason ?? "")).Concat(a.Limitations).Concat(a.Coverage.Select(c => c.Reason ?? "")));

    [Fact]
    public void ResultsDoNotDependOnTheCurrentCulture()
    {
        string Run()
        {
            var shortSession = new Synthetic(600, stepSeconds: 0.1).Set("rtt", 20, 21, 400).Analyze();                 // a baseline refusal with a fractional-second span
            var gaps = new Synthetic(1200); for (var k = 0; k < 6; k++) gaps.Skip(100 + k * 200, 190 + k * 200);
            var many = new Synthetic(6000); for (var k = 0; k < 250; k++) many.Set("rtt", 20 + k * 20, 21 + k * 20, 300 + k);
            return AllText(Busy().Analyze()) + AllText(shortSession) + AllText(gaps.Analyze()) + AllText(many.Analyze(new AnomalyOptions { MaximumObservationsPerRule = 100 }));
        }
        var original = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
            var expected = Run();
            foreach (var name in new[] { "de-DE", "pl-PL", "fr-FR", "ar-SA" })
            {
                System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo(name);
                Assert.Equal(expected, Run());
            }
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = original; }
    }

    [Fact]
    public void TheIdOfAnObservationDependsOnItsRuleAndIntervalAndRecordingOnly()
    {
        var baseline = Only(Busy().Analyze(), "guest.cpu.saturation");
        var other = Busy(); other.Set("ram", 100, 110, 50);                 // unrelated change elsewhere
        Assert.Equal(baseline.Id, Only(other.Analyze(), "guest.cpu.saturation").Id);
        var moved = new Synthetic(1200).Set("cpu", 701, 820, 96);
        Assert.NotEqual(baseline.Id, Only(moved.Analyze(), "guest.cpu.saturation").Id);
        var sibling = Busy(); sibling.Key = "other-recording";
        Assert.NotEqual(baseline.Id, Only(sibling.Analyze(), "guest.cpu.saturation").Id);
    }

    [Fact]
    public void OutOfOrderSamplesGiveTheSameResultAsOrderedOnesAndAreReported()
    {
        var ordered = Busy().Analyze();
        var shuffled = Enumerable.Range(0, 1200).OrderBy(i => (i * 7919) % 1200).ToArray();       // a fixed permutation
        var a = AnomalyDetectionEngine.Analyze(Busy().ToInput(shuffled));
        Assert.Equal(Fingerprint(ordered), Fingerprint(a));
        Assert.True(a.ReducedConfidence); Assert.Contains(a.Limitations, l => l.Contains("not in time order"));
    }

    [Fact]
    public void DuplicateTimestampsAreCombinedDeterministicallyAndReported()
    {
        var order = Enumerable.Range(0, 1200).SelectMany(i => i % 10 == 0 ? new[] { i, i } : new[] { i }).ToArray();
        var a = AnomalyDetectionEngine.Analyze(Busy().ToInput(order));
        Assert.Equal(Fingerprint(Busy().Analyze()), Fingerprint(a));
        Assert.Contains(a.Limitations, l => l.Contains("120 sample(s) shared a timestamp"));
        Assert.Equal(1200, a.SamplesAnalyzed);
    }

    [Fact]
    public void WhenDuplicatesDisagreeTheFirstValidValueForThatTimeWins()
    {
        // 1,200 samples; the sample at second 600 is delivered twice with different values
        var times = Enumerable.Range(0, 1200).Select(i => At(i)).ToList(); times.Insert(601, At(600));
        AnomalyInput Make(double first, double second)
        {
            var values = Enumerable.Repeat(30.0, 1200).ToList(); values[600] = first; values.Insert(601, second);
            return AnomalyInput.Create("dup", times, [new("rtt", "ms", values.ToArray())]);
        }
        var a = AnomalyDetectionEngine.Analyze(Make(400, 30));
        Assert.Equal(1200, Assert.Single(a.Coverage, c => c.ChannelId == "rtt").ValidSamples);
        var spike = Assert.Single(a.Observations);                           // the first value (400 ms) is the one used
        Assert.Equal(400, spike.Primary.Maximum);
        Assert.Empty(AnomalyDetectionEngine.Analyze(Make(30, 400)).Observations);
        var filled = AnomalyDetectionEngine.Analyze(Make(double.NaN, 400));  // a still-missing value is filled by the later duplicate
        Assert.Equal(400, Assert.Single(filled.Observations).Primary.Maximum);
    }

    [Fact]
    public void DaylightSavingTransitionsDoNotShiftOrReorderUtcObservations()
    {
        var rule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(new DateTime(2026, 1, 1), new DateTime(2026, 12, 31), TimeSpan.FromHours(1),
            TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 2, 0, 0), 3, 29), TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 3, 0, 0), 10, 25));
        var zone = TimeZoneInfo.CreateCustomTimeZone("Test/Zone", TimeSpan.FromHours(1), "Test", "Test standard", "Test summer", [rule]);
        var utcStart = new DateTimeOffset(2026, 10, 25, 0, 0, 0, TimeSpan.Zero);                   // the clocks go back at 01:00 UTC: 02:00–02:59 local happens twice
        var times = Enumerable.Range(0, 7200).Select(i => TimeZoneInfo.ConvertTime(utcStart.AddSeconds(i), zone)).ToList();
        Assert.Contains(times, t => t.Offset == TimeSpan.FromHours(2)); Assert.Contains(times, t => t.Offset == TimeSpan.FromHours(1));   // the offset really changes
        var rtt = new double[7200]; Array.Fill(rtt, 30.0);
        for (var i = 3570; i < 3640; i++) rtt[i] = 210;                                          // 00:59:30Z .. 01:00:40Z across the change
        var a = AnomalyDetectionEngine.Analyze(AnomalyInput.Create("dst", times, [new("rtt", "ms", rtt)]));
        var o = Assert.Single(a.Observations);
        Assert.Equal(utcStart.AddSeconds(3570), o.Start); Assert.Equal(utcStart.AddSeconds(3639), o.End);
        Assert.Equal(TimeSpan.Zero, o.Start.Offset); Assert.Equal(TimeSpan.FromSeconds(69), o.Duration);
        Assert.Contains("00:59:30 and 01:00:39 UTC", o.Explanation);
        // the same instants given in shuffled local-time order are still ordered by UTC
        var reversed = Enumerable.Range(0, 7200).Reverse().Select(i => times[i]).ToList();
        var rev = AnomalyDetectionEngine.Analyze(AnomalyInput.Create("dst", reversed, [new("rtt", "ms", rtt.Reverse().ToArray())]));
        Assert.Equal(o.Id, Assert.Single(rev.Observations).Id);
    }

    [Fact]
    public void ARecordingSpanningSeveralDaysIsAnalyzedByTimeAndShowsDates()
    {
        // 200-second sampling over about 2.8 days; the episode crosses midnight UTC between 5 and 6 March (34 hours after the start)
        var s = new Synthetic(1200, stepSeconds: 200).Set("rtt", 122_400 - 2000, 122_400 + 2000, 250);
        var o = Only(s.Analyze(), "conn.rtt.high");
        Assert.Equal(new DateTime(2026, 3, 5), o.Start.UtcDateTime.Date); Assert.Equal(new DateTime(2026, 3, 6), o.End.UtcDateTime.Date);
        Assert.Contains("2026-03-05 23:", o.Explanation); Assert.Contains("2026-03-06 00:", o.Explanation);
        Assert.Equal(20, o.SampleCount);
    }

    // ---------- protocols ----------

    [Fact]
    public void AnRdpOrCitrixRecordingGetsNoHorizonSpecificWording()
    {
        foreach (var protocol in new[] { "Microsoft RDP", "Citrix HDX" })
        {
            var s = new Synthetic(1200, healthy: false).Add("rtt", "ms", _ => 30).Add("packetLoss", "%", _ => 0).Add("cpu", "%", _ => 25);
            s.Protocols.Clear(); s.Protocols.Add(protocol);
            s.Set("rtt", 300, 360, 220).Set("packetLoss", 300, 360, 3);
            var a = s.Analyze();
            Assert.NotEmpty(a.Observations);
            foreach (var o in a.Observations)
            {
                Assert.DoesNotContain("Blast", o.Explanation); Assert.DoesNotContain("Horizon", o.Explanation); Assert.DoesNotContain("uplink", o.Explanation);
                Assert.Contains(o.Limitations, l => l.Contains("No Horizon Blast evidence") && l.Contains(protocol));
            }
            foreach (var c in a.Correlations) { Assert.DoesNotContain("Blast", c.Explanation); Assert.DoesNotContain("Horizon", c.Explanation); }
            Assert.Contains(Only(a, "conn.loss.sustained").Limitations, l => l.Contains("does not state the direction"));
        }
    }

    [Fact]
    public void BlastEvidenceComesFromTheRecordedProtocolOrFromBlastOnlyCounters()
    {
        var named = new Synthetic(1200).Set("outQueueing", 300, 330, 120_000).Analyze();
        Assert.StartsWith("Blast", Only(named, "conn.queueing.increase").Explanation);
        var unnamed = new Synthetic(1200); unnamed.Protocols.Clear(); unnamed.Set("outQueueing", 300, 330, 120_000);
        Assert.Equal(RuleState.Evaluated, Rule(unnamed.Analyze(), "conn.queueing.increase").State);      // Blast-only counter values are evidence
        var bare = new Synthetic(1200, healthy: false).Add("cpu", "%", _ => 25); bare.Protocols.Clear();
        Assert.Equal(RuleState.NotApplicable, Rule(bare.Analyze(), "conn.queueing.increase").State);
        var mixed = new Synthetic(1200); mixed.Protocols.Add("Microsoft RDP"); mixed.Set("rtt", 300, 360, 220);
        Assert.DoesNotContain("Blast", Only(mixed.Analyze(), "conn.rtt.high").Explanation);                 // a mixed recording is not attributed to Blast
    }

    // ---------- partial input and limits ----------

    [Fact]
    public void PartialInputPropagatesItsLimitationsToTheResultAndToEveryObservation()
    {
        var s = new Synthetic(1200).Set("rtt", 300, 360, 220);
        s.Limitations.Add("The recording is only partly analyzed: the recording holds more than the 100 samples EUC Lens analyzes."); s.ReducedConfidence = true;
        var a = s.Analyze();
        Assert.True(a.ReducedConfidence); Assert.Contains(a.Limitations, l => l.Contains("only partly analyzed"));
        Assert.All(a.Observations, o => Assert.Contains(o.Limitations, l => l.Contains("incomplete or was reduced") && l.Contains("only partly analyzed")));
    }

    [Fact]
    public void TheAnalyzerRespectsItsSampleLimitAndReportsTheCut()
    {
        var s = new Synthetic(1200).Set("rtt", 300, 360, 220).Set("rtt", 900, 960, 220);
        var a = s.Analyze(new AnomalyOptions { MaximumSamples = 500 });
        Assert.Equal(500, a.SamplesAnalyzed); Assert.True(a.ReducedConfidence);
        Assert.Equal(At(499), a.To);
        Assert.Contains(a.Limitations, l => l.Contains("only the first 500 were analyzed"));
        Assert.Single(a.Observations);                                      // nothing after the limit is claimed
        Assert.True(HistoryLoader.MaximumSamples == new AnomalyOptions().MaximumSamples);
    }

    [Fact]
    public void ALargeNumberOfObservationsIsCappedDeterministicallyAndReportedAsTruncated()
    {
        var s = new Synthetic(6000);
        for (var k = 0; k < 250; k++) s.Set("rtt", 20 + k * 20, 21 + k * 20, 300 + k);               // 250 separate single-sample spikes
        var a = s.Analyze(new AnomalyOptions { MaximumObservationsPerRule = 100 });
        Assert.Equal(100, a.Observations.Count); Assert.True(a.IsTruncated); Assert.Equal(150, a.OmittedObservations);
        Assert.True(a.ReducedConfidence); Assert.Contains(a.Limitations, l => l.Contains("150") && l.Contains("not listed"));
        var again = s.Analyze(new AnomalyOptions { MaximumObservationsPerRule = 100 });
        Assert.Equal(Fingerprint(a), Fingerprint(again));
        var global = s.Analyze(new AnomalyOptions { MaximumObservations = 40 });
        Assert.Equal(40, global.Observations.Count); Assert.Equal(210, global.OmittedObservations);
    }

    [Fact]
    public void InvalidOptionsAreRejectedBeforeAnyAnalysis()
    {
        var input = new Synthetic(100).ToInput();
        Assert.Throws<ArgumentException>(() => AnomalyDetectionEngine.Analyze(input, new AnomalyOptions { BaselineTrimFraction = 0.9 }));
        Assert.Throws<ArgumentException>(() => AnomalyDetectionEngine.Analyze(input, new AnomalyOptions { Rules = [AnomalyRules.Default[0], AnomalyRules.Default[0]] }));
        Assert.Empty(new AnomalyOptions().Validate());
        Assert.Throws<ArgumentException>(() => AnomalyInput.Create("x", [Synthetic.T0], [new AnomalyChannel("rtt", "ms", [1, 2])]));
    }

    [Fact]
    public void AnEmptyRecordingProducesAnEmptyAnalysisWithInsufficientDataEverywhere()
    {
        var a = AnomalyDetectionEngine.Analyze(AnomalyInput.Create("empty", Array.Empty<long>(), [new AnomalyChannel("rtt", "ms", [])]));
        Assert.Empty(a.Observations); Assert.Equal(0, a.SamplesAnalyzed); Assert.Null(a.From);
        Assert.All(a.Rules, r => Assert.Equal(RuleState.InsufficientData, r.State));
        Assert.Contains(a.Limitations, l => l.Contains("no samples"));
    }

    // ---------- cancellation ----------

    [Fact]
    public async Task ACancelledTokenStopsTheAnalysisWithoutAResult()
    {
        using var cts = new CancellationTokenSource(); cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => AnomalyDetectionEngine.Analyze(Busy().ToInput(), null, cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AnomalyDetectionEngine.AnalyzeAsync(null!, null, cts.Token));
    }

    private sealed class CancelOnEnumeration(CancellationTokenSource cts) : IReadOnlyList<HistoryEvent>
    {
        public int Count => 1;
        public HistoryEvent this[int index] => new(HistoryEventKind.Note, Synthetic.T0, "note");
        public IEnumerator<HistoryEvent> GetEnumerator() { cts.Cancel(); yield return this[0]; }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    [Fact]
    public void CancellationDuringTheAnalysisStopsItCleanly()
    {
        var s = Busy();
        using var cts = new CancellationTokenSource();
        var plain = s.ToInput();
        var cancelling = AnomalyInput.Create("c", Enumerable.Range(0, 1200).Select(i => At(i)).ToList(), plain.Channels, new CancelOnEnumeration(cts), plain.Protocols);
        Assert.Throws<OperationCanceledException>(() => AnomalyDetectionEngine.Analyze(cancelling, null, cts.Token));
    }
}
