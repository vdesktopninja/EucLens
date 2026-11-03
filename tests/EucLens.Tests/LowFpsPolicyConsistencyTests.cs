using EucLens.Core;
using EucLens.Diagnostics;
using Xunit;

namespace EucLens.Tests;

/// <summary>
/// Live Diagnostics and Session History must reach the same conclusion about low frame rate for the same measurements. Both judge it only while the content changes at
/// least as often as the frame-rate threshold (dirty FPS ≥ 15), so typing, slowly changing content, a static screen and a missing dirty-frame counter are never a
/// low-FPS finding in either place, and content that really demands the frames is one in both.
/// </summary>
public sealed class LowFpsPolicyConsistencyTests
{
    private sealed class Clock : TimeProvider
    {
        private long ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => ticks;
        public override DateTimeOffset GetUtcNow() => new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero).AddTicks(ticks);
        public void Advance(double seconds) => ticks += (long)(seconds * TimeSpan.TicksPerSecond);
    }

    private const int Healthy = 60, Scenario = 120, Tail = 60;

    /// <summary>The live engine, fed one sample per second: a healthy minute, then the scenario. True when low FPS is an active finding at the end of the scenario.</summary>
    private static bool LiveRaises(double fps, double? dirty)
    {
        var clock = new Clock();
        var engine = new DiagnosticIntelligenceEngine(DiagnosticRuleSet.Default(), clock);
        DiagnosticReport? last = null;
        for (var i = 0; i < Healthy + Scenario; i++)
        {
            clock.Advance(1);
            var inScenario = i >= Healthy;
            last = engine.Evaluate(DiagnosticsPresentationTests.Snap(clock.GetUtcNow(), fps: inScenario ? fps : 30, dirty: inScenario ? dirty : (dirty is null ? null : 20)));
        }
        return last!.Findings!.Any(f => f.RuleId == DiagnosticRuleIds.LowFps && f.IsActive);
    }

    /// <summary>The same measurements as a recording analysed by Session History.</summary>
    private static bool HistoryRaises(double fps, double? dirty)
    {
        var s = new Synthetic(Healthy + Scenario + Tail).Set("fps", Healthy, Healthy + Scenario, fps);
        if (dirty is { } d) s.Set("dirtyFps", Healthy, Healthy + Scenario, d); else s.Remove("dirtyFps");
        return s.Analyze().Observations.Any(o => o.RuleId == "gfx.fps.low");
    }

    [Theory]
    [InlineData("video: 30 content changes per second, 8 frames delivered", 8, 30.0, true)]
    [InlineData("on the demand threshold: 15 changes per second, 10 frames delivered", 10, 15.0, true)]
    [InlineData("video delivered well: 30 changes, 28 frames", 28, 30.0, false)]
    [InlineData("typing: 8 changes per second, 8 frames delivered", 8, 8.0, false)]
    [InlineData("scrolling a document slowly: 12 changes, 6 frames", 6, 12.0, false)]
    [InlineData("just below the threshold: 14 changes, 9 frames", 9, 14.0, false)]
    [InlineData("static screen: 0.4 changes, 2 frames", 2, 0.4, false)]
    [InlineData("no dirty-frame counter: nothing is known about the content", 3, null, false)]
    public void LiveDiagnosticsAndSessionHistoryReachTheSameLowFpsConclusion(string scenario, double fps, double? dirty, bool expected)
    {
        Assert.True(LiveRaises(fps, dirty) == expected, $"live Diagnostics, {scenario}: expected {(expected ? "a finding" : "no finding")}.");
        Assert.True(HistoryRaises(fps, dirty) == expected, $"Session History, {scenario}: expected {(expected ? "an observation" : "no observation")}.");
    }

    [Fact]
    public void SessionHistoryExplainsWhyTypingAndSlowContentWereNotJudged()
    {
        var typing = new Synthetic(240).Set("fps", 60, 180, 8).Set("dirtyFps", 60, 180, 8).Analyze();
        var rule = typing.Rules.Single(r => r.RuleId == "gfx.fps.low");
        // The healthy minutes around the scenario were judged, the scenario itself was not: nothing is claimed about it either way.
        Assert.Empty(typing.Observations);
        Assert.Equal(RuleState.Evaluated, rule.State);
        Assert.Contains("slowly changing content", rule.Reason);

        var slow = new Synthetic(240).Set("fps", 0, 240, 6).Set("dirtyFps", 0, 240, 12).Analyze();
        var all = slow.Rules.Single(r => r.RuleId == "gfx.fps.low");
        Assert.Equal(RuleState.NotApplicable, all.State);
        Assert.Contains("fewer than 15 times per second", all.Reason);
        Assert.Contains("static screen", all.Reason);
    }

    [Fact]
    public void ADemandingStretchIsStillReportedWithTheGateAndTheLimitationStated()
    {
        var a = new Synthetic(240).Set("fps", 60, 180, 8).Set("dirtyFps", 60, 180, 30).Analyze();
        var o = Assert.Single(a.Observations, x => x.RuleId == "gfx.fps.low");
        Assert.Contains(o.Limitations, l => l.Contains("only while the content changed at least as often as the frame-rate threshold"));
        Assert.Equal(2, a.Rules.Single(r => r.RuleId == "gfx.fps.low").RuleVersion);
    }

    [Fact]
    public void DroppedFramesKeepTheLowerActivityGateBecauseTheLiveEngineHasNoDroppedFrameRule()
    {
        // Intentional difference: dropped frames are a loss ratio, not a rate the content can explain, so the screen only has to be changing (dirty FPS ≥ 5).
        var typing = new Synthetic(240).Set("droppedFrames", 60, 180, 9).Set("dirtyFps", 60, 180, 8).Analyze();
        Assert.Contains(typing.Observations, o => o.RuleId == "gfx.dropped.high");
        var idle = new Synthetic(240).Set("droppedFrames", 60, 180, 9).Set("dirtyFps", 60, 180, 0.3).Analyze();
        Assert.DoesNotContain(idle.Observations, o => o.RuleId == "gfx.dropped.high");
    }
}
