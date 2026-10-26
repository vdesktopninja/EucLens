using System.Diagnostics;
using EucLens.Core;
using Xunit;
using Xunit.Abstractions;

namespace EucLens.Tests;

/// <summary>
/// Run time and allocations of the analyzer on the target recording sizes. The numbers are written to the test output (they are in the CI results); the limits asserted
/// here are deliberately several times above what was measured, so they catch a quadratic or leaking implementation without depending on the speed of the machine.
/// </summary>
public sealed class AnomalyPerformanceTests(ITestOutputHelper output)
{
    /// <summary>A long session with a recurring set of events: an RTT episode with loss, a CPU spike, a frame-rate drop, a short gap and memory pressure every ~4,000 samples.</summary>
    private static Synthetic LongSession(int samples)
    {
        var s = new Synthetic(samples);
        for (var start = 500; start + 1500 < samples; start += 4000)
        {
            s.Set("rtt", start, start + 80, 210).Set("packetLoss", start + 10, start + 70, 3.5);
            s.Set("cpu", start + 300, start + 330, 97);
            s.Set("fps", start + 600, start + 700, 6);
            s.Skip(start + 1000, start + 1100);
            s.Set("ram", start + 1200, start + 1400, 94);
        }
        return s;
    }

    private static (AnomalyAnalysis Analysis, double Milliseconds, long Allocated) Measure(AnomalyInput input)
    {
        AnomalyDetectionEngine.Analyze(input);                                               // JIT and pools
        var before = GC.GetAllocatedBytesForCurrentThread(); var clock = Stopwatch.StartNew();
        var analysis = AnomalyDetectionEngine.Analyze(input);
        clock.Stop();
        return (analysis, clock.Elapsed.TotalMilliseconds, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Theory]
    [InlineData(1_200, 25, 1)]
    [InlineData(21_300, 250, 4)]
    [InlineData(40_795, 400, 8)]
    [InlineData(250_000, 2_000, 32)]
    public void LongRecordingsAreAnalyzedQuicklyWithBoundedAllocations(int samples, int maximumMilliseconds, int maximumMiB)
    {
        var input = LongSession(samples).ToInput();
        var (analysis, ms, allocated) = Measure(input);
        output.WriteLine($"{samples,7:N0} samples ({input.SampleCount:N0} after gaps): {ms,7:0.0} ms, {allocated / 1024.0 / 1024.0,6:0.00} MiB allocated, {analysis.Observations.Count} observations, {analysis.Correlations.Count} correlations, {analysis.OmittedObservations} omitted");
        Assert.Equal(input.SampleCount, analysis.SamplesAnalyzed);
        if (samples >= 5000) Assert.NotEmpty(analysis.Observations);
        Assert.True(ms < maximumMilliseconds, $"{ms:0.0} ms");
        Assert.True(allocated < maximumMiB * 1024L * 1024, $"{allocated / 1024.0 / 1024.0:0.0} MiB");
    }

    [Fact]
    public void RunTimeGrowsLinearlyWithTheNumberOfSamples()
    {
        double Best(int n) { var input = LongSession(n).ToInput(); return Enumerable.Range(0, 3).Select(_ => Measure(input).Milliseconds).Min(); }
        var small = Math.Max(Best(25_000), 0.5); var large = Best(250_000);
        output.WriteLine($"25,000: {small:0.0} ms; 250,000: {large:0.0} ms; ratio {large / small:0.0} for 10x the samples");
        Assert.True(large / small < 40, $"ratio {large / small:0.0}: not linear");
    }

    [Fact]
    public void ManyObservationsAndEventsDoNotMakeTheAnalysisQuadratic()
    {
        // 20,000 recorded events and about 1,500 episodes over 250,000 samples: events are looked up by binary search, never scanned per sample.
        var s = new Synthetic(250_000);
        for (var k = 0; k < 1500; k++) s.Set("rtt", 100 + k * 160, 101 + k * 160, 300 + k % 200);
        for (var k = 0; k < 20_000; k++) s.Event(k % 3 == 0 ? HistoryEventKind.FindingDetected : HistoryEventKind.AvailabilityChanged, k * 12.0);
        var input = s.ToInput();
        var (analysis, ms, allocated) = Measure(input);
        output.WriteLine($"250,000 samples, 20,000 events: {ms:0.0} ms, {allocated / 1024.0 / 1024.0:0.00} MiB, {analysis.Observations.Count} observations (+{analysis.OmittedObservations} omitted)");
        Assert.Equal(100, analysis.Observations.Count(o => o.RuleId == "conn.rtt.deviation"));    // the per-rule cap
        Assert.True(analysis.IsTruncated);
        Assert.All(analysis.Observations, o => Assert.True(o.AssociatedEvents.Count <= 10));
        Assert.True(ms < 3_000, $"{ms:0.0} ms");
    }

    [Fact]
    public void RepeatedAnalysisDoesNotGrowTheHeap()
    {
        var input = LongSession(40_795).ToInput();
        for (var i = 0; i < 3; i++) AnomalyDetectionEngine.Analyze(input);
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        var before = GC.GetTotalMemory(true);
        for (var i = 0; i < 25; i++) AnomalyDetectionEngine.Analyze(input);
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        var growth = GC.GetTotalMemory(true) - before;
        output.WriteLine($"Heap growth after 25 analyses of 40,795 samples: {growth / 1024.0:0.0} KiB");
        Assert.True(growth < 2L * 1024 * 1024, $"{growth:N0} bytes");
    }

    [Fact]
    public void AnalysisOfAHugeInputStaysWithinTheSampleLimit()
    {
        var a = LongSession(280_000).Analyze();
        Assert.Equal(HistoryLoader.MaximumSamples, a.SamplesAnalyzed);
        Assert.True(a.ReducedConfidence); Assert.Contains(a.Limitations, l => l.Contains("sample limit"));
    }
}
