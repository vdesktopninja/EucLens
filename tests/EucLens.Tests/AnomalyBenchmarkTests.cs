using System.Diagnostics;
using EucLens.Core;
using Xunit;
using Xunit.Abstractions;

namespace EucLens.Tests;

/// <summary>
/// Benchmarks of the analyzer on the target workloads and on adversarial recordings. Each measurement uses a fixed method so numbers can be compared between runs and machines:
/// the input is built <b>before</b> the clock starts, two warm-up analyses are discarded, then seven analyses are timed one by one; the table reports the median and the range, never one
/// run. Allocation is the bytes allocated by the analysing thread (the analysis is synchronous), retained memory is what the heap holds, after a full collection, for ten kept results
/// compared with none, per result. The assertions are limits several times above the measured values: they catch quadratic or runaway behaviour, not machine speed. The numbers are
/// printed to the test output, which CI keeps.
/// </summary>
[Collection(SerialPerformanceCollection.Name)]
public sealed class AnomalyBenchmarkTests(ITestOutputHelper output)
{
    private const int WarmUps = 2, Runs = 7;

    private sealed record Measurement(string Name, int Samples, double MedianMs, double MinMs, double MaxMs, double AllocatedMiB, double RetainedPerResultKiB, AnomalyAnalysis Analysis)
    {
        public override string ToString() =>
            $"{Name,-34} {Samples,8:N0} samples | median {MedianMs,7:0.0} ms (range {MinMs:0.0}–{MaxMs:0.0}) | allocated {AllocatedMiB,6:0.00} MiB | retained per result {RetainedPerResultKiB,7:0.0} KiB | " +
            $"{Analysis.Observations.Count} observations, {Analysis.Correlations.Count} correlations, {Analysis.OmittedObservations} omitted, coverage {Analysis.Coverage.Where(c => c.Usable).Select(c => c.Coverage).DefaultIfEmpty(0).Average():P1}";
    }

    private Measurement Run(string name, AnomalyInput input, AnomalyOptions? options = null)
    {
        for (var i = 0; i < WarmUps; i++) AnomalyDetectionEngine.Analyze(input, options);
        var times = new List<double>(); long allocated = 0; AnomalyAnalysis last = null!;
        for (var i = 0; i < Runs; i++)
        {
            GC.Collect(); GC.WaitForPendingFinalizers();
            var before = GC.GetAllocatedBytesForCurrentThread(); var clock = Stopwatch.StartNew();
            last = AnomalyDetectionEngine.Analyze(input, options);
            clock.Stop();
            times.Add(clock.Elapsed.TotalMilliseconds); allocated += GC.GetAllocatedBytesForCurrentThread() - before;
        }
        times.Sort();
        // retained per result: ten results kept alive against none, on a settled heap (the input is shared and counted in both)
        var baseline = RetentionProbe.SettledHeap();
        var kept = Enumerable.Range(0, 10).Select(_ => AnomalyDetectionEngine.Analyze(input, options)).ToArray();
        var withResults = RetentionProbe.SettledHeap();
        GC.KeepAlive(kept);
        var m = new Measurement(name, input.SampleCount, times[Runs / 2], times[0], times[^1], allocated / (double)Runs / 1048576, (withResults - baseline) / 10.0 / 1024, last);
        output.WriteLine(m.ToString());
        return m;
    }

    /// <summary>A long session with a recurring set of events (an RTT episode with loss, a CPU spike, an FPS drop, a gap, memory pressure).</summary>
    internal static Synthetic LongSession(int samples)
    {
        var s = new Synthetic(samples);
        for (var start = 500; start + 1500 < samples; start += 4000)
        {
            s.Set("rtt", start, start + 80, 210).Set("packetLoss", start + 10, start + 70, 3.5).Set("cpu", start + 300, start + 330, 97).Set("fps", start + 600, start + 700, 6)
                .Skip(start + 1000, start + 1100).Set("ram", start + 1200, start + 1400, 94);
        }
        return s;
    }

    [Theory]
    [InlineData(1_200, 20)] [InlineData(21_300, 150)] [InlineData(40_795, 300)] [InlineData(250_000, 2_000)]
    public void TheTargetWorkloadsAreAnalyzedWithinBoundsAndRetainOnlyTheirResult(int samples, int maximumMedianMs)
    {
        var m = Run($"workload {samples:N0}", LongSession(samples).ToInput());
        Assert.True(m.MedianMs < maximumMedianMs, m.ToString());
        Assert.True(m.RetainedPerResultKiB < 2048, $"a result retains {m.RetainedPerResultKiB:0} KiB: {m}");        // the result is a few hundred observations, not the recording
        Assert.True(m.AllocatedMiB < 32, m.ToString());
        if (samples >= 5000) Assert.NotEmpty(m.Analysis.Observations);
    }

    // ---------- adversarial recordings ----------

    private const int Big = 250_000;
    private const double LimitMs = 4_000;

    private static Synthetic Healthy(int n) => new(n);

    [Fact]
    public void ALongHealthyRecordingProducesNothingQuickly()
    {
        var m = Run("very long, healthy", Healthy(Big).ToInput());
        Assert.Empty(m.Analysis.Observations); Assert.Empty(m.Analysis.Correlations); Assert.True(m.MedianMs < LimitMs, m.ToString());
    }

    [Fact]
    public void FiveHundredObservationsAreRankedAndTheOverflowIsReported()
    {
        // five kinds of short episode, hundreds of each: the per-rule limit (100) and the global limit (500) both apply
        var s = new Synthetic(Big);
        for (var k = 0; k < 600; k++)
        {
            var at = 200 + k * 400;
            s.Set("rtt", at, at + 2, 320 + k % 50).Set("packetLoss", at + 50, at + 54, 6 + k % 5).Set("cpu", at + 100, at + 104, 97).Set("disk", at + 150, at + 152, 400 + k % 50).Set("outQueueing", at + 200, at + 220, 90_000);
        }
        var m = Run("500 observations (limits apply)", s.ToInput());
        Assert.InRange(m.Analysis.Observations.Count, 400, 500); Assert.True(m.Analysis.IsTruncated); Assert.True(m.Analysis.ReducedConfidence);
        Assert.Contains(m.Analysis.Limitations, l => l.Contains("not listed"));
        Assert.True(m.MedianMs < LimitMs, m.ToString());
        Assert.Equal(m.Analysis.Observations.Select(o => o.Id).Distinct().Count(), m.Analysis.Observations.Count);
    }

    [Fact]
    public void AMassOfSingleSampleAnomaliesIsGroupedCappedAndNotQuadratic()
    {
        Synthetic Spikes(int n) { var s = new Synthetic(n); for (var i = 100; i < n; i += 7) s.Set("rtt", i, i + 1, 400); return s; }
        var m = Run("35,000 single-sample spikes", Spikes(Big).ToInput());
        Assert.Equal(100, m.Analysis.Observations.Count(o => o.RuleId == "conn.rtt.deviation")); Assert.True(m.Analysis.OmittedObservations > 30_000, m.ToString());
        Assert.True(m.MedianMs < LimitMs, m.ToString());
        AssertLinear("single-sample spikes", Spikes);
    }

    [Fact]
    public void FrequentGapsAndReconnectionsAreHandledLinearly()
    {
        Synthetic Flaky(int n)
        {
            var s = new Synthetic(n);
            for (var at = 150; at + 60 < n; at += 200)
            {
                s.Set("rtt", at - 20, at, 220).Skip(at, at + 30);
                s.Event(HistoryEventKind.RemoteDisconnected, at).Event(HistoryEventKind.RemoteReconnected, at + 30).Event(HistoryEventKind.MonitoringResumed, at + 31);
            }
            return s;
        }
        var m = Run("1,250 gaps and reconnections", Flaky(Big).ToInput());
        Assert.True(m.Analysis.Coverage.Any(), m.ToString()); Assert.True(m.MedianMs < LimitMs, m.ToString());
        Assert.True(m.Analysis.Observations.All(o => o.Ending == ObservationEnd.CoverageEnded || o.Ending == ObservationEnd.Recovered));
        AssertLinear("gaps and reconnections", Flaky);
    }

    [Fact]
    public void ManyProtocolChangesAndManyEventsStayBounded()
    {
        var s = new Synthetic(Big);
        s.Protocols.Add("Microsoft RDP"); s.Protocols.Add("Citrix HDX");
        for (var k = 0; k < 500; k++) s.Event(HistoryEventKind.SessionChanged, 100 + k * 500);
        for (var k = 0; k < 20_000; k++) s.Event(k % 2 == 0 ? HistoryEventKind.FindingDetected : HistoryEventKind.AvailabilityChanged, 10.0 + k * 12);
        s.Set("rtt", 5000, 5100, 220).Set("rtt", 60_000, 60_100, 220);
        var m = Run("500 session changes + 20,000 events", s.ToInput());
        Assert.NotEmpty(m.Analysis.Observations); Assert.True(m.MedianMs < LimitMs, m.ToString());
        Assert.All(m.Analysis.Observations, o => Assert.True(o.AssociatedEvents.Count <= 10));
    }

    [Fact]
    public void MissingInvalidAndNonFiniteCountersNeverBecomeMeasurements()
    {
        var s = new Synthetic(Big);
        var seed = 12345u;
        double Next() { seed = seed * 1664525u + 1013904223u; return (seed >> 8) / (double)(1 << 24); }
        foreach (var id in new[] { "rtt", "packetLoss", "cpu", "ram", "fps", "droppedFrames" })
            s.Add(id, id == "fps" ? "FPS" : id == "rtt" ? "ms" : "%", i => { var r = Next(); return r < 0.45 ? double.NaN : r < 0.5 ? double.PositiveInfinity : r < 0.55 ? -50 : r < 0.6 ? 1e12 : id == "rtt" ? 30 : id == "fps" ? 30 : 20; });
        var m = Run("45% missing, 15% invalid values", s.ToInput());
        Assert.True(m.MedianMs < LimitMs, m.ToString());
        Assert.DoesNotContain(m.Analysis.Observations, o => o.Primary.Maximum > 1e6 || o.Primary.Minimum < 0);        // a counter error is never evidence
        Assert.True(m.Analysis.Coverage.Where(c => c.ChannelId is "rtt" or "cpu").All(c => c.OutOfRangeSamples > 0));
    }

    [Fact]
    public void AnEntirelyDisconnectedRecordingIsNotInterpretedAsHealthyOrAsFailing()
    {
        var s = new Synthetic(Big).Event(HistoryEventKind.RemoteDisconnected, 0).Set("rtt", 0, Big, 500).Set("packetLoss", 0, Big, 40).Set("fps", 0, Big, 0).Set("outQueueing", 0, Big, 900_000);
        var m = Run("disconnected throughout", s.ToInput());
        Assert.DoesNotContain(m.Analysis.Observations, o => o.Domain is HistoryGroup.Connection or HistoryGroup.Graphics);    // stale values of a disconnected session are not measurements
        Assert.True(m.MedianMs < LimitMs, m.ToString());
    }

    [Fact]
    public void HighlyIrregularSamplingIsAnalyzedByTimeAndStaysLinear()
    {
        AnomalyInput Irregular(int n)
        {
            var seed = 777u; var ticks = new long[n]; var t = Synthetic.T0.UtcTicks;
            for (var i = 0; i < n; i++)
            {
                seed = seed * 1664525u + 1013904223u;
                var step = (seed >> 8) % 100 < 2 ? 15.0 + (seed >> 12) % 20 : 0.2 + ((seed >> 8) % 1000) / 1000.0 * 11.8;      // mostly 0.2–12 s, a few long gaps
                t += (long)(step * TimeSpan.TicksPerSecond); ticks[i] = t;
            }
            double[] Series(Func<int, double> f) { var v = new double[n]; for (var i = 0; i < n; i++) v[i] = f(i); return v; }
            return AnomalyInput.Create("irregular", ticks, [new("rtt", "ms", Series(i => i % 997 < 60 ? 210 : 30)), new("cpu", "%", Series(i => 20 + i % 7)), new("packetLoss", "%", Series(i => i % 1301 < 40 ? 3 : 0))], protocols: ["Omnissa Horizon Blast"]);
        }
        var m = Run("irregular sampling (0.2–12 s, gaps)", Irregular(Big));
        Assert.True(m.MedianMs < LimitMs, m.ToString());
        var (small, large) = (Best(Irregular(25_000)), Best(Irregular(Big)));
        output.WriteLine($"irregular: 25,000 → {small:0.0} ms, 250,000 → {large:0.0} ms (ratio {large / Math.Max(small, 0.5):0.0})");
        Assert.True(large / Math.Max(small, 0.5) < 30, "run time is not linear in the number of samples");
    }

    [Fact]
    public void ManyUnavailableMetricsAreReportedAsInsufficientDataQuickly()
    {
        var s = new Synthetic(Big, healthy: false).Add("cpu", "%", i => 20 + i % 5);
        foreach (var id in new[] { "rtt", "packetLoss", "fps", "dirtyFps", "droppedFrames", "outQueueing", "ram", "disk", "gpu", "bandwidthConsumption", "availableBandwidth", "diskLatency" }) s.Add(id, "", _ => double.NaN);
        var m = Run("12 of 13 metrics unavailable", s.ToInput());
        Assert.Empty(m.Analysis.Observations);
        Assert.True(m.Analysis.Rules.Count(r => r.State == RuleState.InsufficientData) >= 10, m.ToString()); Assert.True(m.MedianMs < LimitMs, m.ToString());
    }

    // ---------- cancellation ----------

    [Fact]
    public async Task CancellingTheLargestAnalysisStopsItPromptly()
    {
        var input = LongSession(Big).ToInput();
        AnomalyDetectionEngine.Analyze(input); AnomalyDetectionEngine.Analyze(input);
        var latencies = new List<double>();
        for (var attempt = 0; attempt < 5; attempt++)
        {
            using var cts = new CancellationTokenSource();
            var started = new ManualResetEventSlim();
            var task = Task.Run(() => { started.Set(); return AnomalyDetectionEngine.Analyze(input, null, cts.Token); });
            started.Wait();
            await Task.Delay(3);
            var clock = Stopwatch.StartNew(); cts.Cancel();
            try { await task; latencies.Add(-1); }                                          // finished before the cancellation reached it: not a measurement
            catch (OperationCanceledException) { latencies.Add(clock.Elapsed.TotalMilliseconds); }
        }
        output.WriteLine($"cancellation latency of a 250,000-sample analysis (ms, -1 = finished first): {string.Join(", ", latencies.Select(l => l.ToString("0.0")))}");
        var measured = latencies.Where(l => l >= 0).ToArray();
        Assert.NotEmpty(measured);
        Assert.True(measured.Max() < 250, $"cancellation took {measured.Max():0} ms");
    }

    // ---------- helpers ----------

    private static double Best(AnomalyInput input)
    {
        AnomalyDetectionEngine.Analyze(input);
        return Enumerable.Range(0, 3).Select(_ => { var c = Stopwatch.StartNew(); AnomalyDetectionEngine.Analyze(input); return c.Elapsed.TotalMilliseconds; }).Min();
    }

    private void AssertLinear(string name, Func<int, Synthetic> build)
    {
        var (small, large) = (Best(build(25_000).ToInput()), Best(build(Big).ToInput()));
        output.WriteLine($"{name}: 25,000 → {small:0.0} ms, 250,000 → {large:0.0} ms (ratio {large / Math.Max(small, 0.5):0.0})");
        Assert.True(large / Math.Max(small, 0.5) < 30, $"{name}: run time is not linear in the number of samples");
    }
}
