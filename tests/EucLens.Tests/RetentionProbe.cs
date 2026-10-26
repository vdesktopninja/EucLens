using System.Runtime;

namespace EucLens.Tests;

/// <summary>
/// Measures whether repeating an operation makes the process keep more memory, without mistaking noise for a leak or a leak for noise.
/// <para>
/// A single "heap after N runs" number proves little: the runtime, the array pool, the JIT and other work in the process all move it by a few megabytes. The probe therefore
/// runs the operation in <b>batches</b> and records the managed heap <b>after a full blocking collection</b> following each batch (what is still reachable, not what was allocated).
/// A first group of warm-up batches is discarded (JIT, tiered compilation, pool and cache sizing settle there). From the measured batches it derives the growth of every batch
/// and the (Theil–Sen, outlier-resistant) <b>slope</b> of the heap over the batches: a leak of L bytes per run adds L × runs to <i>every</i> batch, so the slope reveals it even when one batch looks
/// quiet, while one-off noise barely moves a slope fitted over many batches.
/// </para>
/// Retained memory is a property of the whole process (<see cref="GC.GetTotalMemory(bool)"/>), so measurements belong in the serial test collection. It says what is reachable; whether a
/// <i>particular</i> object was released is proven separately with weak references (<see cref="Lifetime"/>).
/// </summary>
internal static class RetentionProbe
{
    internal sealed record Report(IReadOnlyList<long> Heaps, IReadOnlyList<long> BatchGrowth, double SlopePerBatch, long Cumulative, long LargestBatch, int Runs)
    {
        public double SlopePerRun => SlopePerBatch / Runs;
        public override string ToString() =>
            $"heaps {string.Join(" → ", Heaps.Select(h => (h / 1024.0 / 1024.0).ToString("0.00")))} MiB; growth per batch {string.Join(", ", BatchGrowth.Select(g => (g / 1024.0).ToString("0")))} KiB; " +
            $"slope {SlopePerBatch / 1024.0:0.0} KiB per batch of {Runs} runs ({SlopePerRun / 1024.0:0.00} KiB per run); cumulative {Cumulative / 1024.0:0} KiB";
    }

    /// <summary>What a retained heap must not exceed: both the trend and the total over all measured batches.</summary>
    internal sealed record Limits(double MaximumSlopePerRun, long MaximumCumulative, long MaximumSingleBatch);

    /// <summary>Per run: 6 KiB of retained memory would be a leak (a result list kept, a handler left subscribed); the total over the measured batches stays below 2 MiB; no single batch retains more than 1.5 MiB.</summary>
    internal static Limits Default { get; } = new(6 * 1024, 2L << 20, 1536L << 10);

    internal static long SettledHeap()
    {
        for (var i = 0; i < 3; i++) { GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true); GC.WaitForPendingFinalizers(); }
        return GC.GetTotalMemory(forceFullCollection: true);
    }

    internal static Report Measure(Action<int> run, int runsPerBatch = 20, int warmupBatches = 4, int measuredBatches = 8)
    {
        var counter = 0;
        for (var b = 0; b < warmupBatches; b++) for (var r = 0; r < runsPerBatch; r++) run(counter++);
        var heaps = new List<long> { SettledHeap() };
        for (var b = 0; b < measuredBatches; b++)
        {
            for (var r = 0; r < runsPerBatch; r++) run(counter++);
            heaps.Add(SettledHeap());
        }
        return Summarize(heaps, runsPerBatch);
    }

    /// <summary>The same measurement for work that needs to await (opening a recording, navigating a page).</summary>
    internal static async Task<Report> MeasureAsync(Func<int, Task> run, int runsPerBatch = 6, int warmupBatches = 3, int measuredBatches = 6)
    {
        var counter = 0;
        for (var b = 0; b < warmupBatches; b++) for (var r = 0; r < runsPerBatch; r++) await run(counter++);
        await Lifetime.ChurnAsync();
        var heaps = new List<long> { SettledHeap() };
        for (var b = 0; b < measuredBatches; b++)
        {
            for (var r = 0; r < runsPerBatch; r++) await run(counter++);
            await Lifetime.ChurnAsync();
            heaps.Add(SettledHeap());
        }
        return Summarize(heaps, runsPerBatch);
    }

    internal static Report Summarize(IReadOnlyList<long> heaps, int runsPerBatch)
    {
        var growth = heaps.Zip(heaps.Skip(1), (a, b) => b - a).ToArray();
        // Theil–Sen slope: the median of the slopes between every pair of readings. One reading that is far off (a large collection of earlier garbage right after the warm-up,
        // a pool trimming itself) cannot hide a rise that the other readings show, which a least-squares line through all of them would let it do.
        var slopes = new List<double>();
        for (var i = 0; i < heaps.Count; i++) for (var j = i + 1; j < heaps.Count; j++) slopes.Add((heaps[j] - heaps[i]) / (double)(j - i));
        slopes.Sort();
        var slope = slopes.Count == 0 ? 0 : slopes.Count % 2 == 1 ? slopes[slopes.Count / 2] : (slopes[slopes.Count / 2 - 1] + slopes[slopes.Count / 2]) / 2;
        // How much more is held at the end than at the lowest reading, not than at the first one: a first reading that was inflated must not turn a rise into a fall.
        return new(heaps, growth, slope, heaps[^1] - heaps.Min(), growth.Length == 0 ? 0 : growth.Max(), runsPerBatch);
    }

    /// <summary>The verdict, with the reason: a leak when the trend, the total or any single batch is beyond the limits.</summary>
    internal static (bool Leaks, string Why) Verdict(Report report, Limits? limits = null)
    {
        limits ??= Default;
        if (report.SlopePerRun > limits.MaximumSlopePerRun) return (true, $"the heap rises by {report.SlopePerRun / 1024.0:0.0} KiB per run (limit {limits.MaximumSlopePerRun / 1024.0:0.0})");
        if (report.Cumulative > limits.MaximumCumulative) return (true, $"{report.Cumulative / 1024.0:0} KiB more is retained after the measured batches (limit {limits.MaximumCumulative / 1024.0:0})");
        if (report.LargestBatch > limits.MaximumSingleBatch) return (true, $"one batch retained {report.LargestBatch / 1024.0:0} KiB (limit {limits.MaximumSingleBatch / 1024.0:0})");
        return (false, "within limits");
    }

    internal static void AssertNoLeak(Report report, Limits? limits = null)
    {
        var (leaks, why) = Verdict(report, limits);
        if (leaks) throw new Xunit.Sdk.XunitException($"Memory is retained across repeated runs: {why}. {report}");
    }
}

/// <summary>Lifetime proofs with weak references: an object that should be gone is collected, and the test fails when something still reaches it.</summary>
internal static class Lifetime
{
    /// <summary>A worker thread that ran work may still hold its last result in a stale slot until it has run something else, and queued continuations may still reference old state: let both settle.</summary>
    internal static async Task ChurnAsync(int rounds = 12)
    {
        for (var i = 0; i < rounds; i++) await Task.Run(() => Thread.Sleep(3));
        await Task.Yield();
    }

    internal static async Task<IReadOnlyList<int>> AliveAfterCollectionAsync(IEnumerable<WeakReference> references)
    {
        var list = references.ToArray();
        await ChurnAsync();
        for (var i = 0; i < 4; i++) { GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true, true); GC.WaitForPendingFinalizers(); }
        return Enumerable.Range(0, list.Length).Where(i => list[i].IsAlive).ToArray();
    }

    internal static async Task AssertCollectedAsync(string what, IEnumerable<WeakReference> references)
    {
        var list = references.ToArray();
        Xunit.Assert.NotEmpty(list);
        var alive = await AliveAfterCollectionAsync(list);
        if (alive.Count > 0) throw new Xunit.Sdk.XunitException($"{alive.Count} of {list.Length} {what} are still reachable after a full collection.");
    }
}
