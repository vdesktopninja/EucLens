using EucLens.Core;
using Xunit;
using Xunit.Abstractions;

namespace EucLens.Tests;

/// <summary>The analyzer on recordings that went through the real writer and the real loader: evidence must match what the history itself reports.</summary>
public sealed class AnomalyHistoryIntegrationTests(ITestOutputHelper output)
{
    private static SessionSnapshot S(int i, double rtt = 30, double loss = 0, double fps = 30, double cpu = 20, double dirty = 20)
    {
        var s = HistoryFixture.Sample(i, rtt: rtt + i % 5, loss: loss, fps: fps + i % 3, cpu: cpu + i % 4);
        var blast = new Dictionary<string, Metric>(s.BlastMetrics ?? new Dictionary<string, Metric>()) { ["dirtyFps"] = Metric.Available(dirty + i % 3, "FPS") };
        return s with { BlastMetrics = blast };
    }

    [Fact]
    public async Task ARealRecordingIsAnalyzedAndTheEvidenceMatchesTheHistoryItself()
    {
        var rec = await HistoryFixture.RecordAsync(900, i => i is >= 300 and < 370 ? S(i, rtt: 210, loss: 3) : S(i), async (i, ctx) => { if (i == 330) await ctx.MarkIncidentAsync(); });
        var history = HistoryFixture.Load(rec.Storage, rec.ShortId);
        var analysis = AnomalyDetectionEngine.Analyze(history);

        Assert.False(analysis.ReducedConfidence);
        var rtt = Assert.Single(analysis.Observations, o => o.RuleId == "conn.rtt.high");
        Assert.Equal(HistoryFixture.T0.AddSeconds(300), rtt.Start); Assert.Equal(HistoryFixture.T0.AddSeconds(369), rtt.End);
        var stats = history.Statistics("rtt", rtt.Start, rtt.End);                         // the history's own numbers for the same interval
        Assert.Equal(stats.Valid, rtt.SampleCount); Assert.Equal(stats.Maximum, rtt.Primary.Maximum); Assert.Equal(stats.Minimum, rtt.Primary.Minimum);
        Assert.Equal(stats.P50!.Value, rtt.Primary.P50, 9); Assert.Equal(stats.P95!.Value, rtt.Primary.P95, 9);
        Assert.Equal(history.Channel("rtt")!.Unit, rtt.Primary.Unit);
        Assert.Contains(rtt.AssociatedEvents, e => e.Kind == HistoryEventKind.UserIncident);   // the user's own marker is attached, not interpreted
        Assert.Single(analysis.Correlations, c => c.Pattern == CorrelationPattern.RttAndPacketLoss);
        Assert.Equal(history.SampleCount, analysis.SamplesAnalyzed);
        Assert.Equal(history.Info.RecordingId?.ToString("N") ?? history.Info.ShortId, analysis.RecordingKey);

        var again = AnomalyDetectionEngine.Analyze(HistoryFixture.Load(rec.Storage, rec.ShortId));
        Assert.Equal(analysis.Observations.Select(o => o.Id), again.Observations.Select(o => o.Id));       // reloading the file gives the same ids
    }

    [Fact]
    public async Task AHealthyRealRecordingHasNoObservationsAndUnavailableCountersAreNotZeros()
    {
        var rec = await HistoryFixture.RecordAsync(900, i => S(i));
        var analysis = AnomalyDetectionEngine.Analyze(HistoryFixture.Load(rec.Storage, rec.ShortId));
        Assert.Empty(analysis.Observations); Assert.Empty(analysis.Correlations);
        Assert.Equal(RuleState.InsufficientData, Assert.Single(analysis.Rules, r => r.RuleId == "conn.bandwidth.pressure").State);   // available bandwidth was never recorded
        Assert.Equal(RuleState.InsufficientData, Assert.Single(analysis.Rules, r => r.RuleId == "guest.disk.stall").State);
        var bandwidth = Assert.Single(analysis.Coverage, c => c.ChannelId == "availableBandwidth");
        Assert.False(bandwidth.Usable); Assert.Equal(0, bandwidth.ValidSamples);
    }

    [Fact]
    public async Task APartialHistoryReportsReducedConfidenceAndNeverClaimsTheCutOffPart()
    {
        var rec = await HistoryFixture.RecordAsync(900, i => i is >= 100 and < 170 or >= 600 and < 670 ? S(i, rtt: 210) : S(i));
        var history = HistoryLoader.Load(rec.Storage, HistoryFixture.Dir, rec.ShortId, null, null, default, 300);
        Assert.Equal(RecordingCompleteness.Partial, history.Info.State);
        var analysis = AnomalyDetectionEngine.Analyze(history);
        Assert.True(analysis.ReducedConfidence);
        Assert.Contains(analysis.Limitations, l => l.Contains("only partly analyzed") && l.Contains("300"));
        Assert.Equal(300, analysis.SamplesAnalyzed);
        var o = Assert.Single(analysis.Observations);                                      // the second episode lies beyond the loaded part
        Assert.Equal(HistoryFixture.T0.AddSeconds(100), o.Start);
        Assert.Contains(o.Limitations, l => l.Contains("incomplete or was reduced"));
    }

    [Fact]
    public async Task ADisconnectedRealSessionSplitsObservationsAndMarksTheContext()
    {
        var rec = await HistoryFixture.RecordAsync(700, i => S(i, rtt: i is >= 300 and < 600 ? 210 : 30),
            async (i, ctx) =>
            {
                if (i == 400) ctx.Mark(RecordingMarkKind.RemoteDisconnected, "Remote session disconnected");
                if (i == 470) ctx.Mark(RecordingMarkKind.RemoteReconnected, "Remote session reconnected");
                await Task.CompletedTask;
            }, skipSample: i => i is >= 400 and < 470);
        var history = HistoryFixture.Load(rec.Storage, rec.ShortId);
        Assert.NotEmpty(history.Gaps);
        var analysis = AnomalyDetectionEngine.Analyze(history);
        var rtt = analysis.Observations.Where(o => o.RuleId == "conn.rtt.high").ToList();
        Assert.Equal(2, rtt.Count);
        Assert.Equal(ObservationEnd.CoverageEnded, rtt[0].Ending);
        Assert.Contains(rtt[0].AssociatedEvents, e => e.Kind == HistoryEventKind.RemoteDisconnected);
        Assert.Contains(analysis.Correlations, c => c.Pattern == CorrelationPattern.AvailabilityOverlap);
        Assert.DoesNotContain(analysis.Correlations, c => c.Pattern != CorrelationPattern.AvailabilityOverlap && c.ComponentIds.Contains(rtt[0].Id) && c.ComponentIds.Contains(rtt[1].Id));
    }

    [Fact]
    public async Task ASixHourRecordingThroughTheRealWriterIsAnalyzedAndAllocationsAreBounded()
    {
        const int samples = 21_300;                                                         // the size of the six-hour validation recording
        var rec = await HistoryFixture.RecordAsync(samples, i => i is >= 5000 and < 5090 ? S(i, rtt: 220, loss: 4) : i is >= 12000 and < 12120 ? S(i, cpu: 99) : S(i));
        var history = HistoryFixture.Load(rec.Storage, rec.ShortId);
        Assert.Equal(samples, history.SampleCount);
        AnomalyDetectionEngine.Analyze(history);                                            // warm up
        var before = GC.GetAllocatedBytesForCurrentThread(); var clock = System.Diagnostics.Stopwatch.StartNew();
        var analysis = AnomalyDetectionEngine.Analyze(history);
        clock.Stop(); var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        output.WriteLine($"21,300 recorded samples: {clock.Elapsed.TotalMilliseconds:0.0} ms, {allocated / 1024.0 / 1024.0:0.00} MiB allocated, {analysis.Observations.Count} observations, {analysis.Correlations.Count} correlations");
        Assert.Contains(analysis.Observations, o => o.RuleId == "conn.rtt.high"); Assert.Contains(analysis.Observations, o => o.RuleId == "guest.cpu.saturation");
        Assert.Single(analysis.Correlations, c => c.Pattern == CorrelationPattern.RttAndPacketLoss);
        Assert.True(allocated < 40L * 1024 * 1024, $"{allocated:N0} bytes allocated");
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"{clock.Elapsed}");
    }

    [Fact]
    public async Task TheAnalysisDoesNotKeepTheLoadedHistoryAlive()
    {
        var rec = await HistoryFixture.RecordAsync(900, i => i is >= 300 and < 370 ? S(i, rtt: 210) : S(i));
        var (analysis, weak) = Run(rec);
        for (var i = 0; i < 3; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); }
        Assert.False(weak.TryGetTarget(out _), "the result keeps the loaded history in memory");
        Assert.NotEmpty(analysis.Observations);

        static (AnomalyAnalysis, WeakReference<LoadedHistory>) Run(HistoryFixture.Recorded rec)
        {
            var history = HistoryFixture.Load(rec.Storage, rec.ShortId);
            return (AnomalyDetectionEngine.Analyze(history), new WeakReference<LoadedHistory>(history));
        }
    }

    [Fact]
    public async Task AnalyzeAsyncCompletesAndHonorsCancellation()
    {
        var rec = await HistoryFixture.RecordAsync(900, i => i is >= 300 and < 370 ? S(i, rtt: 210) : S(i));
        var history = HistoryFixture.Load(rec.Storage, rec.ShortId);
        var analysis = await AnomalyDetectionEngine.AnalyzeAsync(history);
        Assert.NotEmpty(analysis.Observations);
        using var cts = new CancellationTokenSource(); cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AnomalyDetectionEngine.AnalyzeAsync(history, null, cts.Token));
    }
}
