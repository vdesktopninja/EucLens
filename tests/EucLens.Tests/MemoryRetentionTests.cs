using System.Collections.ObjectModel;
using System.Reflection;
using EucLens.App.ViewModels;
using EucLens.Core;
using Xunit;
using Xunit.Abstractions;

namespace EucLens.Tests;

/// <summary>
/// Whether repeated work leaves memory behind. Two kinds of proof, each used for what it can prove: batches with a trend (<see cref="RetentionProbe"/>) say that the process does
/// not keep more and more; weak references (<see cref="Lifetime"/>) say that a particular object was released. The probe itself is tested against fixtures that leak on purpose, so a
/// passing result is evidence and not an accident of thresholds.
/// </summary>
[Collection(SerialPerformanceCollection.Name)]
public sealed class MemoryRetentionTests(ITestOutputHelper output)
{
    private static AnomalyInput Input(int samples) => new Synthetic(samples).Set("rtt", 500, 580, 210).Set("packetLoss", 510, 570, 3.5).Set("cpu", 900, 930, 97).Set("fps", 1200, 1290, 6).ToInput();

    [Fact]
    public void RepeatedAnalysisDoesNotRetainMemory()
    {
        var input = Input(40_795);
        var report = RetentionProbe.Measure(_ => AnomalyDetectionEngine.Analyze(input));
        output.WriteLine($"40,795 samples, {report.Runs} analyses per batch: {report}");
        RetentionProbe.AssertNoLeak(report);
    }

    [Fact]
    public void RepeatedAnalysisOfTheLargestRecordingDoesNotRetainMemoryEither()
    {
        var input = Input(250_000);
        var report = RetentionProbe.Measure(_ => AnomalyDetectionEngine.Analyze(input), runsPerBatch: 6, warmupBatches: 3, measuredBatches: 6);
        output.WriteLine($"250,000 samples, {report.Runs} analyses per batch: {report}");
        RetentionProbe.AssertNoLeak(report);
    }

    // ---------- the probe is only worth something if it fails on a real leak ----------

    private sealed class LongLivedPublisher { public event Action? Happened; public int Count => Happened?.GetInvocationList().Length ?? 0; public void Subscribe(Action handler) => Happened += handler; }

    [Fact]
    public void TheProbeDetectsRetainedResultsARetainedBufferAndAHandlerThatIsNeverRemoved()
    {
        var input = Input(40_795);
        var results = new List<AnomalyAnalysis>();                                           // a cache that is never emptied
        var retainedResults = RetentionProbe.Measure(_ => results.Add(AnomalyDetectionEngine.Analyze(input)));
        var buffers = new List<byte[]>();                                                    // a small buffer (16 KiB) kept per run
        var retainedBuffers = RetentionProbe.Measure(_ => buffers.Add(new byte[16 * 1024]));
        var publisher = new LongLivedPublisher();                                            // a view-like object that subscribed and was never unsubscribed
        var leakedHandlers = RetentionProbe.Measure(_ => { var scratch = new byte[16 * 1024]; publisher.Subscribe(() => scratch[0]++); });
        foreach (var (name, report) in new[] { ("retained results", retainedResults), ("retained buffers", retainedBuffers), ("leaked handlers", leakedHandlers) })
        {
            var (leaks, why) = RetentionProbe.Verdict(report);
            output.WriteLine($"{name}: {why}. {report}");
            Assert.True(leaks, $"{name} were not detected: {report}");
        }
        Assert.Equal(publisher.Count, leakedHandlers.Runs * (4 + 8));
        GC.KeepAlive(results); GC.KeepAlive(buffers);
    }

    [Fact]
    public void TheProbeDoesNotCallAnOperationThatOnlyAllocatesTemporarilyALeak()
    {
        var control = RetentionProbe.Measure(_ => { var temporary = new byte[512 * 1024]; temporary[0] = 1; GC.KeepAlive(temporary); });
        output.WriteLine("temporary 512 KiB per run: " + control);
        Assert.False(RetentionProbe.Verdict(control).Leaks, control.ToString());
    }

    [Fact]
    public void ABatchComparisonLikeTheOldTestCouldMissAGrowingLeakThatTheTrendAndTheTotalCatch()
    {
        // heap after each batch: a leak that shows in most batches but happens to be small in two of them
        long Mib(double m) => (long)(m * 1024 * 1024);
        var heaps = new[] { Mib(20), Mib(21.2), Mib(21.3), Mib(22.3), Mib(22.4), Mib(23.5), Mib(23.6), Mib(24.7), Mib(24.8) };
        var report = RetentionProbe.Summarize(heaps, 25);
        var second = report.BatchGrowth[1]; var third = report.BatchGrowth[2];
        Assert.True(Math.Min(second, third) < 1L * 1024 * 1024);                              // the previous assertion: passes
        var (leaks, why) = RetentionProbe.Verdict(report);
        Assert.True(leaks, "the trend and the total missed it");                              // the new verdict: fails
        output.WriteLine($"{why}. {report}");
        // a leak that grows 460 KiB per batch, hidden behind one reading far off in the first batch (measured: a large earlier collection right after the warm-up)
        var masked = RetentionProbe.Summarize([Mib(42.03), Mib(34.16), Mib(34.61), Mib(34.78), Mib(35.21), Mib(35.66), Mib(36.11), Mib(36.56), Mib(37.02)], 20);
        Assert.True(RetentionProbe.Verdict(masked).Leaks, "a rise masked by one outlier was not detected: " + masked);
        // and a flat heap after one large early drop is not a leak
        var drop = RetentionProbe.Summarize([Mib(42.03), Mib(34.16), Mib(34.17), Mib(34.16), Mib(34.18), Mib(34.17), Mib(34.16), Mib(34.17), Mib(34.17)], 20);
        Assert.False(RetentionProbe.Verdict(drop).Leaks, drop.ToString());
        // and noise around a flat heap is not a leak
        var flat = RetentionProbe.Summarize([Mib(20), Mib(20.3), Mib(20.1), Mib(20.2), Mib(20.0), Mib(20.25), Mib(20.1), Mib(20.2), Mib(20.15)], 25);
        Assert.False(RetentionProbe.Verdict(flat).Leaks, flat.ToString());
    }

    [Fact]
    public async Task TheWeakReferenceCheckFailsWhenSomethingStillHoldsTheObject()
    {
        var (weak, holder) = Make();
        await Assert.ThrowsAsync<Xunit.Sdk.XunitException>(() => Lifetime.AssertCollectedAsync("fixtures", [weak]));
        GC.KeepAlive(holder);
        var released = Make().Weak;
        await Lifetime.AssertCollectedAsync("fixtures", [released]);

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        static (WeakReference Weak, List<object> Holder) Make() { var o = new object(); return (new WeakReference(o), [o]); }
    }

    // ---------- objects of the analysis ----------

    [Fact]
    public async Task AnAnalysisAndItsInputAreCollectedOnceTheyAreNotUsedAnyMore()
    {
        var weak = Run();
        await Lifetime.AssertCollectedAsync("analysis objects", weak);

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        static List<WeakReference> Run()
        {
            var input = Input(21_300);
            var analysis = AnomalyDetectionEngine.Analyze(input);
            return [new(analysis), new(input), new(analysis.Observations), new(analysis.Observations[0]), new(analysis.Correlations), new(analysis.Coverage)];
        }
    }

    // ---------- the findings model ----------

    private static (AnomalyAnalysis Analysis, HistoryInfo Info) Sample(int variant)
    {
        var s = new Synthetic(3000).Set("rtt", 300 + variant, 360 + variant, 200).Set("packetLoss", 310 + variant, 350 + variant, 3).Set("cpu", 800, 880, 97).Set("fps", 1200, 1280, 6);
        var analysis = AnomalyDetectionEngine.Analyze(s.ToInput());
        var info = new HistoryInfo("abcd1234", Guid.NewGuid(), Synthetic.T0, Synthetic.T0.AddSeconds(2999), true, "Real", "0.4.4", ["Omnissa Horizon Blast"], 3000, 0, 0, 0, 0, 1,
            RecordingCompleteness.Complete, "Stopped by the user", 1, 0, TimeSpan.Zero, "folder");
        return (analysis, info);
    }

    private static int Subscribers(object source, string eventName)
    {
        for (var type = source.GetType(); type is not null; type = type.BaseType)
            if (type.GetField(eventName, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly)?.GetValue(source) is Delegate d) return d.GetInvocationList().Length;
        return 0;
    }

    [Fact]
    public void ShowingSelectingFilteringAndClearingTheFindingsManyTimesRetainsNothing()
    {
        var analyses = Enumerable.Range(0, 4).Select(Sample).ToArray();
        var navigated = 0;
        var f = new HistoryFindingsViewModel(TimeZoneInfo.Utc, _ => navigated++, () => { }, () => { });
        var propertyHandlers = Subscribers(f, nameof(f.PropertyChanged)); var collectionHandlers = Subscribers(f.VisibleObservations, nameof(f.VisibleObservations.CollectionChanged));
        var report = RetentionProbe.Measure(run =>
        {
            var (analysis, info) = analyses[run % analyses.Length];
            f.Show(analysis, info);
            foreach (var row in f.Observations) f.SelectedObservation = row;
            foreach (var correlation in f.PerformanceCorrelations) f.SelectedCorrelation = correlation;
            foreach (var option in f.SeverityFilters) f.SelectedSeverity = option;
            f.SelectedSeverity = f.SeverityFilters[0];
            f.ShowFull();
            f.Clear();
        }, runsPerBatch: 200);
        output.WriteLine($"findings model, {report.Runs} show/select/filter/clear cycles per batch: {report}");
        RetentionProbe.AssertNoLeak(report);
        Assert.Equal(propertyHandlers, Subscribers(f, nameof(f.PropertyChanged))); Assert.Equal(collectionHandlers, Subscribers(f.VisibleObservations, nameof(f.VisibleObservations.CollectionChanged)));
        Assert.True(navigated > 0);
    }

    [Fact]
    public async Task ClearingTheFindingsModelReleasesItsItemsItsSelectionAndTheAnalysis()
    {
        var f = new HistoryFindingsViewModel(TimeZoneInfo.Utc, _ => { });
        var weak = Fill(f);
        f.Clear();
        await Lifetime.AssertCollectedAsync("findings objects", weak);
        Assert.Empty(f.Observations); Assert.Null(f.CurrentAnalysis); Assert.Null(f.SelectedObservation); Assert.Null(f.ActiveCorrelation);

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        static List<WeakReference> Fill(HistoryFindingsViewModel f)
        {
            var (analysis, info) = Sample(0);
            f.Show(analysis, info);
            f.SelectedObservation = f.Observations[0];
            f.SelectedCorrelation = f.PerformanceCorrelations.FirstOrDefault();
            var detail = f.Observations[0].Detail;                                          // the lazily built evidence is released with its row too
            return [new(analysis), new(f.Observations[0]), new(f.Observations[0].Observation), new(detail), new(f.VisibleObservations[0]), new(f.PerformanceCorrelations[0]), new(info)];
        }
    }
}
