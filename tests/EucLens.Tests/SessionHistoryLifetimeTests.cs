using EucLens.App.Presentation;
using EucLens.App.ViewModels;
using EucLens.Core;
using Xunit;
using Xunit.Abstractions;
using Presentation = EucLens.Tests.HistoryFindingsPresentationTests;

namespace EucLens.Tests;

/// <summary>
/// Session History under repeated and hostile use: navigation, switching between recordings, cancellation in flight and many open/leave cycles. For each the process must not keep more
/// memory (trend over batches), the objects of a recording that is no longer shown must be collectible (weak references), the number of subscriptions must not grow, and no
/// task may fail unobserved.
/// </summary>
[Collection(SerialPerformanceCollection.Name)]
public sealed class SessionHistoryLifetimeTests(ITestOutputHelper output)
{
    private static readonly string Dir = HistoryFixture.Dir;

    /// <summary>A second recording with other channels and other findings: no RTT, no frame-rate counters, a CPU episode.</summary>
    private static SessionSnapshot Other(int i) => HistoryFixture.Sample(i, rtt: null, fps: null, cpu: i is >= 900 and < 980 ? 97 : 20 + i % 3);

    private static async Task<(MemoryStorage Storage, RecordingSummary A, RecordingSummary B)> TwoRecordingsAsync()
    {
        var a = await HistoryFixture.RecordAsync(1800, Presentation.Busy);
        var b = await HistoryFixture.RecordAsync(1800, Other, storage: a.Storage);
        return (a.Storage, Presentation.Summary(a.Storage, a.ShortId), Presentation.Summary(a.Storage, b.ShortId));
    }

    private static readonly string[] Events = [nameof(ObservableObject.PropertyChanged)];

    /// <summary>How many handlers are attached to the long-lived objects of the page: this must not depend on how often the page was used.</summary>
    private static int[] Subscriptions(SessionHistoryViewModel h) =>
    [
        Count(h, "PropertyChanged"), Count(h.Findings, "PropertyChanged"), Count(h.Findings.VisibleObservations, "CollectionChanged"), Count(h.Events, "CollectionChanged"), Count(h.Charts, "CollectionChanged"),
        Count(h.Findings.ShowFullRecordingCommand, "CanExecuteChanged"), Count(h.ExportFindingsCsvCommand, "CanExecuteChanged"), Count(h.PreviousIncidentCommand, "CanExecuteChanged")
    ];

    private static int Count(object source, string eventName)
    {
        for (var type = source.GetType(); type is not null; type = type.BaseType)
            if (type.GetField(eventName, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.DeclaredOnly)?.GetValue(source) is Delegate d)
                return d.GetInvocationList().Length;
        return 0;
    }

    /// <summary>Weak references to everything the page holds for the recording that is open now. Built in its own frame so no local keeps them alive.</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static List<WeakReference> Capture(SessionHistoryViewModel h)
    {
        var weak = new List<WeakReference> { new(h.Loaded!) };
        if (h.Findings.CurrentAnalysis is { } analysis) { weak.Add(new(analysis)); weak.Add(new(analysis.Observations)); }
        foreach (var item in h.Findings.Observations.Take(3)) { weak.Add(new(item)); weak.Add(new(item.Observation)); weak.Add(new(item.Detail)); }
        foreach (var item in h.Findings.PerformanceCorrelations.Take(2)) weak.Add(new(item));
        if (h.Findings.SelectedObservation is { } selected) weak.Add(new(selected));
        foreach (var row in h.Charts.Take(3))
        {
            var objects = new ChartObjects(row.Data);                                       // what a chart view builds for this row
            objects.Line.Values = objects.Values;
            weak.Add(new(row)); weak.Add(new(row.Data)); weak.Add(new(objects)); weak.Add(new(objects.Line));
        }
        return weak;
    }

    // ---------- A: repeated navigation ----------

    [Fact]
    public async Task RepeatedFindingNavigationKeepsMemorySubscriptionsAndChartInstancesStable()
    {
        var rec = await HistoryFixture.RecordAsync(1800, Presentation.Busy);
        var h = Presentation.CreateHistory(rec.Storage);
        await h.OpenAsync(Presentation.Summary(rec.Storage, rec.ShortId)); await h.Settled;
        var f = h.Findings;
        var rows = h.Charts.ToArray(); var baseline = Subscriptions(h);
        var report = await RetentionProbe.MeasureAsync(async run =>
        {
            foreach (var row in f.Observations.ToArray()) f.SelectedObservation = row;       // no waiting between selections: the earlier chart refreshes are cancelled in flight
            f.SelectedCorrelation = f.PerformanceCorrelations.First();
            foreach (var severity in f.SeverityFilters) f.SelectedSeverity = severity;
            f.ShowFull();
            await h.Settled;
            Assert.Null(f.SelectedObservation); Assert.Null(f.ActiveCorrelation); Assert.False(h.HasEmphasis);   // no stale selection state
        }, runsPerBatch: 12, warmupBatches: 3, measuredBatches: 6);
        output.WriteLine($"navigation, {report.Runs} rounds per batch: {report}");
        RetentionProbe.AssertNoLeak(report);
        Assert.Equal(baseline, Subscriptions(h));                                           // no duplicate subscriptions
        Assert.True(rows.SequenceEqual(h.Charts), "the chart rows were recreated or duplicated");
        Assert.Equal(h.Charts.Count, h.Charts.Select(c => c.Id).Distinct().Count());
        Assert.Equal(h.SessionStart, h.ViewStart); Assert.Equal(h.SessionEnd, h.ViewEnd);
    }

    // ---------- B: switching between recordings ----------

    [Fact]
    public async Task SwitchingBetweenRecordingsShowsOnlyTheCurrentOneAndReleasesTheOthers()
    {
        var (storage, a, b) = await TwoRecordingsAsync();
        var h = Presentation.CreateHistory(storage);
        var baseline = Subscriptions(h);
        var generations = new List<List<WeakReference>>();
        for (var round = 0; round < 15; round++)
        {
            foreach (var (summary, expectRtt) in new[] { (a, true), (b, false) })
            {
                await h.OpenAsync(summary); await h.Settled;
                var f = h.Findings;
                Assert.Equal(summary.ShortId, h.Loaded!.Info.ShortId);
                Assert.Equal(expectRtt, f.Observations.Any(o => o.Observation.RuleId == "conn.rtt.high"));       // the findings are those of this recording
                Assert.Equal(expectRtt, h.Charts.Any(c => c.Id == "rtt"));                                       // so are the charts
                Assert.All(f.Observations, o => Assert.InRange(o.Observation.Start, h.SessionStart, h.SessionEnd));
                Assert.Null(f.SelectedObservation); Assert.False(h.HasEmphasis);
                var target = f.Observations.First(o => o.Observation.RuleId == (expectRtt ? "conn.rtt.high" : "guest.cpu.saturation"));
                f.SelectedObservation = target; await h.Settled;
                Assert.Equal([target.Observation.Primary.ChannelId], h.Charts.Where(c => c.IsPrimary).Select(c => c.Id));   // the right chart, from this recording
                Assert.True(h.ViewStart >= h.SessionStart && h.ViewEnd <= h.SessionEnd && h.ViewStart <= target.Observation.Start && h.ViewEnd >= target.Observation.End);
                generations.Add(Capture(h));
            }
        }
        var current = generations[^1].Count;
        await Lifetime.AssertCollectedAsync("objects of recordings that are no longer shown", generations.SkipLast(1).SelectMany(g => g).Where(w => !ReferenceEquals(w, null)));
        Assert.Equal(baseline, Subscriptions(h));
        GC.KeepAlive(h); Assert.True(current > 0);
    }

    // ---------- C: rapid cancellation ----------

    [Fact]
    public async Task OpeningLeavingAndOpeningAgainInQuickSuccessionPublishesNothingStaleAndFailsNothing()
    {
        var (storage, a, b) = await TwoRecordingsAsync();
        var unobserved = new List<Exception>();
        void OnUnobserved(object? sender, UnobservedTaskExceptionEventArgs e) { lock (unobserved) unobserved.Add(e.Exception); e.SetObserved(); }
        TaskScheduler.UnobservedTaskException += OnUnobserved;
        try
        {
            var h = Presentation.CreateHistory(storage);
            var weak = new List<WeakReference>();
            for (var round = 0; round < 12; round++)
            {
                // A is opened and, before it can finish, B replaces it; then the page is left while B is still loading or analyzing.
                var first = h.OpenAsync(round % 2 == 0 ? a : b);
                var second = h.OpenAsync(round % 2 == 0 ? b : a);
                if (round % 3 == 0) h.OnPageHidden();
                await Task.WhenAll(first, second); await h.Settled;
                if (round % 3 != 0)
                {
                    var wantsRtt = (round % 2 == 0 ? b : a).ShortId == a.ShortId;
                    Assert.Equal(wantsRtt, h.Findings.Observations.Any(o => o.Observation.RuleId == "conn.rtt.high"));   // only the newer recording's findings
                    weak.AddRange(Capture(h));
                    h.OnPageHidden(); await h.Settled;
                }
                Assert.False(h.HasHistory); Assert.Empty(h.Findings.Observations); Assert.Empty(h.Charts); Assert.Null(h.Findings.CurrentAnalysis);
            }
            // a recording left while its analysis may still run, and the page used again afterwards
            await h.OpenAsync(a); h.OnPageHidden(); await h.OpenAsync(b); await h.Settled;
            Assert.Equal(b.ShortId, h.Loaded!.Info.ShortId); Assert.DoesNotContain(h.Findings.Observations, o => o.Observation.RuleId == "conn.rtt.high");
            weak.AddRange(Capture(h)); h.OnPageHidden(); await h.Settled;
            await Lifetime.AssertCollectedAsync("objects of cancelled or released recordings", weak);
            for (var i = 0; i < 3; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); }          // an unobserved task exception is reported when the faulted task is finalized
            Assert.True(unobserved.Count == 0, "unobserved task exceptions: " + string.Join(" | ", unobserved.Select(e => e.GetBaseException().Message)));
        }
        finally { TaskScheduler.UnobservedTaskException -= OnUnobserved; }
    }

    // ---------- D: repeated page lifetime ----------

    [Fact]
    public async Task ManyCompleteOpenAndCloseCyclesRetainNothing()
    {
        var (storage, a, b) = await TwoRecordingsAsync();
        var h = Presentation.CreateHistory(storage);
        var baseline = Subscriptions(h);
        var generations = new List<List<WeakReference>>();
        var report = await RetentionProbe.MeasureAsync(async run =>
        {
            await h.OpenAsync(run % 2 == 0 ? a : b); await h.Settled;
            var f = h.Findings;
            if (f.Observations.Count > 0) { f.SelectedObservation = f.Observations[0]; await h.Settled; }
            if (run % 4 == 0 && f.PerformanceCorrelations.Count > 0) f.SelectedCorrelation = f.PerformanceCorrelations[0];
            f.ShowInChartsCommand.Execute(null);
            lock (generations) generations.Add(Capture(h));
            h.OnPageHidden(); await h.Settled;
        }, runsPerBatch: 6, warmupBatches: 2, measuredBatches: 5);
        output.WriteLine($"Session History, {generations.Count} complete open/select/leave cycles: {report}");
        RetentionProbe.AssertNoLeak(report);
        Assert.True(generations.Count >= 12 + 12);
        await Lifetime.AssertCollectedAsync("objects of closed pages", generations.SelectMany(g => g));
        Assert.Equal(baseline, Subscriptions(h));
    }

    // ---------- the page's own callbacks ----------

    [Fact]
    public async Task ACallbackPostedToTheUiContextDoesNotKeepAReleasedRecordingAlive()
    {
        // The page posts work to the UI context (re-selecting the open recording when the list is refreshed). Run the page under a single-threaded context, as the dispatcher does,
        // leave it while posted work is queued, and prove that nothing queued references the loaded history.
        var rec = await HistoryFixture.RecordAsync(1800, Presentation.Busy);
        List<WeakReference> weak = [];
        await SingleThreadContext.RunAsync(async () =>
        {
            var h = Presentation.CreateHistory(rec.Storage);                                // created on the UI-like thread, so it captures its context
            await h.OpenAsync(Presentation.Summary(rec.Storage, rec.ShortId)); await h.Settled;
            weak = Capture(h);
            await h.RefreshRecordingsCommand.ExecuteAsync();                                // rebuilds the list: posts the re-select callback to the UI context
            h.OnPageHidden(); await h.Settled;
        });
        await Lifetime.AssertCollectedAsync("objects reachable from queued UI callbacks", weak);
    }
}
