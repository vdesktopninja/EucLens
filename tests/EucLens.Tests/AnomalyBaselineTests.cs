using EucLens.Core;
using Xunit;

namespace EucLens.Tests;

/// <summary>The adaptive baseline: robust statistics, explicit minimums, variable sampling and the refusal to guess.</summary>
public sealed class AnomalyBaselineTests
{
    private static AnomalyObservation Only(AnomalyAnalysis a, string ruleId) => Assert.Single(a.Observations, o => o.RuleId == ruleId);

    [Fact]
    public void TheBaselineIsTheMedianAndAScaledMedianAbsoluteDeviationOfTheHealthySide()
    {
        // a seven-level staircase between 30 and 60 ms: the median and MAD of the healthy side, sigma = 1.4826 × MAD (never below the rule's 5 ms minimum scale)
        var s = new Synthetic(1200).Add("rtt", "ms", i => 30 + 5 * (i % 7)).Set("rtt", 600, 604, 400);
        var b = Only(s.Analyze(), "conn.rtt.deviation").Threshold.Baseline!;
        Assert.True(b.Reliable); Assert.Null(b.Reason); Assert.Equal("ms", b.Unit);
        Assert.InRange(b.Median, 35, 50); Assert.True(b.Mad > 0);
        Assert.Equal(Math.Max(1.4826 * b.Mad, 5), b.Sigma, 6);
        Assert.Equal(1200, b.Samples); Assert.True(b.Span >= TimeSpan.FromSeconds(1199));
        Assert.True(b.P05 <= b.Median && b.Median <= b.P95);
    }

    [Fact]
    public void AFlatSessionDoesNotTurnNoiseIntoAnomalies()
    {
        // MAD is zero; the minimum scale keeps the limit at 30 + 6 × 5 = 60 ms, and the practical floor of 100 ms still applies
        var flat = new Synthetic(1200).Add("rtt", "ms", _ => 30).Set("rtt", 300, 360, 70);
        Assert.Empty(flat.Analyze().Observations);
        var b = Only(new Synthetic(1200).Add("rtt", "ms", _ => 30).Set("rtt", 300, 303, 140).Analyze(), "conn.rtt.deviation").Threshold;
        Assert.Equal(60, b.BaselineLimit); Assert.Equal(100, b.Absolute);
    }

    [Fact]
    public void ASessionThatIsAlwaysBadIsStillReportedByTheAbsoluteRuleButNotAsADeviation()
    {
        var a = new Synthetic(1200).Add("rtt", "ms", i => 200 + 3 * Math.Sin(i * 0.37)).Analyze();
        var o = Assert.Single(a.Observations);
        Assert.Equal("conn.rtt.high", o.RuleId);                       // 200 ms is the session's normal, so it is no deviation, but it is above 150 ms
        Assert.Equal(1200, o.SampleCount);
    }

    [Fact]
    public void EnoughSamplesInTooLittleTimeIsNotABaseline()
    {
        // 600 samples within 60 s: plenty of samples, but a baseline needs at least two minutes of data
        var s = new Synthetic(600, stepSeconds: 0.1).Set("rtt", 20, 21, 400);
        var a = s.Analyze();
        var rule = Assert.Single(a.Rules, r => r.RuleId == "conn.rtt.deviation");
        Assert.Equal(RuleState.InsufficientData, rule.State); Assert.Contains("Insufficient data for an adaptive baseline", rule.Reason); Assert.Contains("600 valid sample(s)", rule.Reason); Assert.Contains("at least 120 over 2 min", rule.Reason);
    }

    [Fact]
    public void TheMinimumsAreConfigurableAndTheDefaultsAreDocumented()
    {
        var shortSession = new Synthetic(90).Set("rtt", 30, 33, 400);
        Assert.Empty(shortSession.Analyze().Observations);
        var relaxed = shortSession.Analyze(new AnomalyOptions { MinimumBaselineSamples = 60, MinimumBaselineSpan = TimeSpan.FromSeconds(60) });
        Assert.Equal(ObservationKind.Spike, Only(relaxed, "conn.rtt.deviation").Kind);
        var d = new AnomalyOptions();
        Assert.Equal(120, d.MinimumBaselineSamples); Assert.Equal(TimeSpan.FromMinutes(2), d.MinimumBaselineSpan); Assert.Equal(0.10, d.BaselineTrimFraction);
    }

    [Fact]
    public void TheFrameRateBaselineIsBuiltFromActiveSamplesOnly()
    {
        // half of the session is a static screen at 2 FPS; the other half is active at 60 FPS and then drops to 30
        var s = new Synthetic(1200).Add("fps", "FPS", i => 60 + 2 * Math.Sin(i * 0.23))
            .Set("fps", 0, 600, 2).Set("dirtyFps", 0, 600, 0.3).Set("fps", 800, 880, 30);
        var o = Only(s.Analyze(), "gfx.fps.low");
        Assert.InRange(o.Threshold.Baseline!.Median, 56, 64);          // not dragged toward the idle 2 FPS
        Assert.InRange(o.Threshold.BaselineLimit!.Value, 33, 39);
        Assert.InRange(o.Threshold.Baseline.Samples, 590, 610);         // only the active samples (the edge of the smoothing window may add a few)
    }

    [Fact]
    public void ABaselineIsBuiltFromSamplesAfterGapsAndDisconnectsToo()
    {
        var s = new Synthetic(1200).Skip(100, 400).Event(HistoryEventKind.RemoteDisconnected, 500).Event(HistoryEventKind.RemoteReconnected, 600).Set("rtt", 900, 904, 400);
        var b = Only(s.Analyze(), "conn.rtt.deviation").Threshold.Baseline!;
        Assert.Equal(1200 - 300 - 100, b.Samples);                      // skipped samples and the disconnected stretch are not part of it
    }

    [Fact]
    public void AMetricThatNeverHadAValidValueSaysSoInsteadOfBlamingItsUnit()
    {
        var s = new Synthetic(1200).Missing("rtt", 0, 1200).Unit("rtt", "");     // what the loader hands over for a counter that was never available
        var rule = Assert.Single(s.Analyze().Rules, r => r.RuleId == "conn.rtt.high");
        Assert.Equal(RuleState.InsufficientData, rule.State); Assert.Contains("never had a valid value", rule.Reason);
    }

    [Fact]
    public void NoGeneratedTextNamesACauseOrMentionsHardwareOrNetworkFaults()
    {
        var s = new Synthetic(3000).Add("diskLatency", "ms", _ => 4).Add("gpu", "%", _ => 30).Add("bandwidthConsumption", "Mbps", _ => 10).Add("availableBandwidth", "Mbps", _ => 50)
            .Set("rtt", 300, 360, 220).Set("packetLoss", 310, 350, 6).Set("outQueueing", 310, 360, 200_000).Set("fps", 300, 380, 4).Set("droppedFrames", 300, 380, 20)
            .Set("cpu", 300, 440, 99).Set("ram", 320, 500, 97).Set("gpu", 300, 440, 99).Set("disk", 600, 630, 700).Set("diskLatency", 320, 360, 400)
            .Set("bandwidthConsumption", 310, 360, 49).Set("rtt", 1000, 1002, 500).Skip(1400, 1500);
        var a = s.Analyze();
        Assert.True(a.Observations.Count >= 12 && a.Correlations.Count >= 4);
        var banned = new[] { "caused by", "because of", "root cause is", "due to", "overloaded", "congestion", "congested", "firewall", "ISP", "driver", "contention", "faulty", "failure of", "VMware", "hypervisor", "router", "bottleneck" };
        var texts = a.Observations.SelectMany(o => new[] { o.Explanation, o.Title }.Concat(o.Limitations)).Concat(a.Correlations.SelectMany(c => new[] { c.Explanation, c.Title }.Concat(c.Limitations)))
            .Concat(a.Rules.Select(r => r.Reason ?? "")).Concat(a.Limitations);
        foreach (var text in texts)
            foreach (var word in banned)
                Assert.False(System.Text.RegularExpressions.Regex.IsMatch(text, $@"\b{System.Text.RegularExpressions.Regex.Escape(word)}\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase), $"'{word}' in: {text}");
        Assert.All(a.Observations, o => Assert.Contains("does not establish the underlying cause", o.Explanation));
    }
}
