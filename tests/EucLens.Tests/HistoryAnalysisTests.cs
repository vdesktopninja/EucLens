using EucLens.Core;
using Xunit;

namespace EucLens.Tests;

/// <summary>Statistics, peak-preserving downsampling, annotations, deterministic incident analysis, time zones and the complete path.</summary>
public sealed class HistoryAnalysisTests
{
    private static readonly string Dir = HistoryFixture.Dir;
    private static long T(int seconds) => HistoryFixture.T0.AddSeconds(seconds).UtcTicks;
    private static long[] Times(int n) => Enumerable.Range(0, n).Select(T).ToArray();
    private static readonly long Gap = TimeSpan.FromSeconds(5).Ticks;

    // ---------- statistics ----------

    [Fact]
    public void StatisticsUseValidValuesOnlyAndCountMissingSeparately()
    {
        double[] values = [1, 2, double.NaN, 4, double.NaN, 10];
        var stats = HistoryStatistics.Compute(Times(6), values);
        Assert.Equal(6, stats.Samples); Assert.Equal(4, stats.Valid); Assert.Equal(2, stats.Missing);
        Assert.Equal(1, stats.Minimum); Assert.Equal(10, stats.Maximum);
        Assert.Equal(3, stats.P50);                                 // median of 1, 2, 4, 10
        Assert.Equal(HistoryFixture.T0.AddSeconds(5), stats.MaximumAt);
        Assert.Equal(1 + 0.95 * 3 - 2 == 0 ? 0 : 4 + (10 - 4) * (0.95 * 3 - 2), stats.P95!.Value, 9);   // interpolated like the diagnostic engine
    }

    [Fact]
    public void NoValidValuesGiveNoStatisticsAtAllNotZeros()
    {
        var stats = HistoryStatistics.Compute(Times(3), [double.NaN, double.NaN, double.NaN]);
        Assert.Equal(3, stats.Missing); Assert.Equal(0, stats.Valid);
        Assert.Null(stats.Minimum); Assert.Null(stats.P50); Assert.Null(stats.P95); Assert.Null(stats.Maximum); Assert.Null(stats.MaximumAt);
        Assert.Equal(RangeStatistics.Empty, HistoryStatistics.Compute([], []));
    }

    [Fact]
    public void ThePercentileDefinitionMatchesTheDiagnosticEngine()
    {
        double[] sorted = [10, 20, 30, 40, 50];
        Assert.Equal(30, HistoryStatistics.Percentile(sorted, 0.5));
        Assert.Equal(48, HistoryStatistics.Percentile(sorted, 0.95), 9);
        Assert.Equal(10, HistoryStatistics.Percentile(sorted, 0));
        Assert.Equal(50, HistoryStatistics.Percentile(sorted, 1));
    }

    // ---------- downsampling ----------

    private static (long[] Ticks, double[] Values) Flat(int n, double value = 30) => (Times(n), Enumerable.Repeat(value, n).ToArray());

    [Fact]
    public void ASingleSecondSpikeSurvivesAnEightHourReductionWhereAnAverageWouldHideIt()
    {
        var (ticks, values) = Flat(28_800);
        values[17_000] = 500;                                       // one sample, one second
        var points = PeakPreservingDownsampler.Reduce(ticks, values, ticks[0], ticks[^1], 600, Gap, null, out var decimated);

        Assert.True(decimated);
        Assert.True(points.Count <= 620, $"{points.Count} points");
        var peak = points.Where(p => p.Value is not null).MaxBy(p => p.Value)!;
        Assert.Equal(500, peak.Value);
        Assert.Equal(T(17_000), peak.Ticks);                         // at its own timestamp
        // An average-only reduction of the same buckets (48 s wide) would show at most 30 + 470/48 = about 40: the spike would be gone.
        Assert.True(30 + 470 / 48.0 < 45);
    }

    [Fact]
    public void ADipIsKeptAsWellAsAPeak()
    {
        var (ticks, values) = Flat(10_000, 58);
        values[4_321] = 3;
        var points = PeakPreservingDownsampler.Reduce(ticks, values, ticks[0], ticks[^1], 400, Gap, null, out _);
        Assert.Equal(3, points.Where(p => p.Value is not null).Min(p => p.Value));
        Assert.Equal(58, points.Where(p => p.Value is not null).Max(p => p.Value));
    }

    [Fact]
    public void ReducedPointsStayInTimeOrderAndTheInputIsUntouched()
    {
        var rnd = new Random(7);
        var ticks = Times(5000); var values = Enumerable.Range(0, 5000).Select(_ => rnd.NextDouble() * 100).ToArray();
        var copy = values.ToArray(); var timesCopy = ticks.ToArray();
        var points = PeakPreservingDownsampler.Reduce(ticks, values, ticks[0], ticks[^1], 300, Gap, null, out _);
        Assert.Equal(points.Select(p => p.Ticks).Order().ToArray(), points.Select(p => p.Ticks).ToArray());
        Assert.Equal(copy, values); Assert.Equal(timesCopy, ticks);
        Assert.Equal(values.Max(), points.Max(p => p.Value));
        Assert.Equal(values.Min(), points.Min(p => p.Value));
    }

    [Fact]
    public void WhatFitsIsShownRawAndZoomingInRaisesTheResolution()
    {
        var (ticks, values) = Flat(3000);
        for (var i = 0; i < 3000; i++) values[i] = 30 + i % 13;
        var whole = PeakPreservingDownsampler.Reduce(ticks, values, ticks[0], ticks[^1], 600, Gap, null, out var wholeDecimated);
        Assert.True(wholeDecimated);
        var zoom = PeakPreservingDownsampler.Reduce(ticks.AsSpan(1000, 300), values.AsSpan(1000, 300), ticks[1000], ticks[1299], 600, Gap, null, out var zoomDecimated);
        Assert.False(zoomDecimated);
        Assert.Equal(300, zoom.Count);                               // every recorded sample
        Assert.Equal(values.Skip(1000).Take(300).Select(v => (double?)v), zoom.Select(p => p.Value));
        Assert.True(300 / (double)(ticks[1299] - ticks[1000]) > whole.Count / (double)(ticks[^1] - ticks[0]));
    }

    [Fact]
    public void MissingDataBreaksTheLineAndIsNeverFilledWithZeros()
    {
        var (ticks, values) = Flat(6000, 25);
        for (var i = 2000; i < 2400; i++) values[i] = double.NaN;      // a missing run
        // An interval without any sample (paused monitoring): the times jump by 10 minutes.
        for (var i = 4000; i < 6000; i++) ticks[i] += TimeSpan.FromMinutes(10).Ticks;
        var points = PeakPreservingDownsampler.Reduce(ticks, values, ticks[0], ticks[^1], 500, Gap, null, out _);

        Assert.Equal(2, points.Count(p => p.Value is null));
        Assert.DoesNotContain(points, p => p.Value == 0);
        Assert.All(points.Where(p => p.Value is not null), p => Assert.Equal(25, p.Value));
        var firstGap = points.First(p => p.Value is null);
        Assert.InRange(firstGap.Ticks, T(2000), T(2400));
        // The same series, short enough to be shown raw, has the same two breaks.
        var shortRaw = PeakPreservingDownsampler.Reduce(ticks[1990..2410], values[1990..2410], ticks[1990], ticks[2409], 600, Gap, null, out var decimated);
        Assert.False(decimated);
        Assert.Single(shortRaw, p => p.Value is null);
    }

    [Fact]
    public void IncidentTimesAreBucketBoundariesSoTheMomentIsNeverSmearedIntoItsNeighbours()
    {
        var (ticks, values) = Flat(10_000);
        var incident = T(5_000) + TimeSpan.FromSeconds(0.5).Ticks;
        var without = PeakPreservingDownsampler.Reduce(ticks, values, ticks[0], ticks[^1], 200, Gap, null, out _);
        var with = PeakPreservingDownsampler.Reduce(ticks, values, ticks[0], ticks[^1], 200, Gap, [incident], out _);
        Assert.DoesNotContain(without, p => p.Ticks == T(5_001));
        Assert.Contains(with, p => p.Ticks == T(5_001));             // the first sample after the incident starts its own bucket
        Assert.True(with.Count == without.Count + 1);
    }

    [Fact]
    public void ThePointCountStaysBoundedEvenForAdversarialData()
    {
        var n = 20_000; var ticks = Times(n);
        var values = Enumerable.Range(0, n).Select(i => i % 2 == 0 ? (double)(i % 97) : double.NaN).ToArray();   // alternating valid and missing
        var points = PeakPreservingDownsampler.Reduce(ticks, values, ticks[0], ticks[^1], 500, Gap, null, out _);
        Assert.True(points.Count <= 4 * 500, $"{points.Count} points");
        Assert.Throws<ArgumentOutOfRangeException>(() => PeakPreservingDownsampler.Reduce(ticks, values, ticks[0], ticks[^1], 2, Gap, null, out _));
    }

    // ---------- annotations ----------

    private static string Stamp => RecordingFormat.Stamp(HistoryFixture.T0);

    [Fact]
    public async Task AnAnnotationIsStoredBesideTheRecordingAndTheRecordingIsNotRewritten()
    {
        var rec = await HistoryFixture.RecordAsync(40, compress: false, step: async (i, c) => { if (i == 20) await c.MarkIncidentAsync(); });
        var before = HistoryFixture.PartPaths(rec.Storage, rec.ShortId).ToDictionary(p => p, p => rec.Storage.Files[p].ToArray());
        var id = rec.Incidents[0].Id;
        var now = HistoryFixture.T0.AddHours(1);

        RecordingAnnotationStore.Update(rec.Storage, Dir, Stamp, rec.ShortId, null, id, new(IncidentCategory.Freeze, "  Screen froze for about ten seconds.  ", now));

        Assert.All(before, kv => Assert.Equal(kv.Value, rec.Storage.Files[kv.Key]));    // recording bytes unchanged
        var file = Path.Combine(Dir, RecordingFormat.AnnotationName(Stamp, rec.ShortId));
        Assert.True(rec.Storage.Files.ContainsKey(file));
        Assert.DoesNotContain(rec.Storage.Names(Dir), n => n.EndsWith(".partial", StringComparison.Ordinal));
        var loaded = RecordingAnnotationStore.Load(rec.Storage, Dir, Stamp, rec.ShortId);
        var note = loaded.For(id)!;
        Assert.Equal(IncidentCategory.Freeze, note.Category);
        Assert.Equal("Screen froze for about ten seconds.", note.Description);
        Assert.Equal(now, note.UpdatedUtc);
        // The recording still opens, the catalog still lists exactly one recording, and the sidecar is part of its group.
        Assert.Single(HistoryFixture.Load(rec.Storage, rec.ShortId).Incidents);
        Assert.Single(RecordingCatalog.Scan(rec.Storage, Dir));
        Assert.Equal(rec.ShortId, RecordingFormat.ShortIdOf(Path.GetFileName(file)));
    }

    [Fact]
    public async Task UpdatingOneAnnotationKeepsTheOthersAndRemovingWorks()
    {
        var storage = new MemoryStorage();
        var a = Guid.NewGuid(); var b = Guid.NewGuid();
        RecordingAnnotationStore.Update(storage, Dir, Stamp, "0a1b2c3d", null, a, new(IncidentCategory.SlowResponse, "slow", HistoryFixture.T0));
        RecordingAnnotationStore.Update(storage, Dir, Stamp, "0a1b2c3d", null, b, new(IncidentCategory.Other, "other", HistoryFixture.T0.AddMinutes(1)));
        RecordingAnnotationStore.Update(storage, Dir, Stamp, "0a1b2c3d", null, a, new(IncidentCategory.DisplayStutter, "stutter", HistoryFixture.T0.AddMinutes(2)));
        var loaded = RecordingAnnotationStore.Load(storage, Dir, Stamp, "0a1b2c3d");
        Assert.Equal(IncidentCategory.DisplayStutter, loaded.For(a)!.Category);
        Assert.Equal("other", loaded.For(b)!.Description);
        RecordingAnnotationStore.Update(storage, Dir, Stamp, "0a1b2c3d", null, b, null);
        Assert.Null(RecordingAnnotationStore.Load(storage, Dir, Stamp, "0a1b2c3d").For(b));
        Assert.NotNull(RecordingAnnotationStore.Load(storage, Dir, Stamp, "0a1b2c3d").For(a));
        await Task.CompletedTask;
    }

    [Fact]
    public void AnnotationTextIsValidatedAndUnreadableOrNewerFilesAreNeverOverwrittenSilently()
    {
        var storage = new MemoryStorage();
        var id = Guid.NewGuid();
        Assert.Throws<ArgumentException>(() => RecordingAnnotationStore.Update(storage, Dir, Stamp, "0a1b2c3d", null, id, new(IncidentCategory.Other, new string('x', 501), HistoryFixture.T0)));
        Assert.Equal("a b", RecordingAnnotationStore.Normalize("a\u0007 b\u0000"));
        Assert.Equal(["Not categorized", "Freeze", "Lag / slow response", "Display stutter", "Application unresponsive", "Other"],
            Enum.GetValues<IncidentCategory>().Select(RecordingAnnotationStore.DisplayName));

        var path = RecordingAnnotationStore.PathFor(Dir, Stamp, "0a1b2c3d");
        storage.Files[path] = "{ this is not json"u8.ToArray(); storage.Times[path] = storage.Now;
        var issues = new List<HistoryIssue>();
        Assert.Empty(RecordingAnnotationStore.Load(storage, Dir, Stamp, "0a1b2c3d", issues).Incidents);
        Assert.Single(issues, i => i.Kind == HistoryIssueKind.UnreadableAnnotations);

        RecordingAnnotationStore.Update(storage, Dir, Stamp, "0a1b2c3d", null, id, new(IncidentCategory.Freeze, "ok", HistoryFixture.T0));
        Assert.Contains(storage.Names(Dir), n => n.Contains(".unreadable-", StringComparison.Ordinal));  // the damaged file was moved aside, not destroyed
        Assert.Equal("ok", RecordingAnnotationStore.Load(storage, Dir, Stamp, "0a1b2c3d").For(id)!.Description);

        storage.Files[path] = "{\"schema\":\"euclens.annotations/9\",\"incidents\":{}}"u8.ToArray();
        var newer = new List<HistoryIssue>();
        Assert.Empty(RecordingAnnotationStore.Load(storage, Dir, Stamp, "0a1b2c3d", newer).Incidents);
        Assert.Contains("unsupported", Assert.Single(newer).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RetentionRemovesTheSidecarWithItsRecordingAndOnlyThat()
    {
        var storage = new MemoryStorage();
        var old = HistoryFixture.T0.AddDays(-30);
        string Put(string name) { var path = Path.Combine(Dir, name); storage.Files[path] = [1, 2, 3]; storage.Times[path] = old; return path; }
        var stamp = RecordingFormat.Stamp(old);
        Put($"EucLens-{stamp}-0a1b2c3d-part001.jsonl.gz"); Put($"EucLens-{stamp}-0a1b2c3d.meta.json"); Put($"EucLens-{stamp}-0a1b2c3d.annotations.json");
        var unrelated = Put("my-notes.json"); var other = Put("EucLens-notes.annotations.json");
        var result = RecordingRetention.Apply(storage, Dir, long.MaxValue, TimeSpan.FromDays(14), HistoryFixture.T0, activeId: null);
        Assert.Equal(1, result.RecordingsDeleted);
        Assert.Equal(new[] { Path.GetFileName(other), Path.GetFileName(unrelated) }.Order().ToArray(), storage.Names(Dir));
    }

    // ---------- deterministic incident analysis ----------

    private static async Task<LoadedHistory> SpikeHistory(Func<int, SessionSnapshot> make, int count = 1800, int incidentAt = 900, string[]? findings = null)
    {
        var rec = await HistoryFixture.RecordAsync(count, make, async (i, c) => { if (i == incidentAt) await c.MarkIncidentAsync(); }, compress: false);
        return HistoryFixture.Load(rec.Storage, rec.ShortId);
    }

    [Fact]
    public async Task ASpikeNextToTheIncidentIsReportedWithRecordedValuesAndNoCause()
    {
        var history = await SpikeHistory(i => HistoryFixture.Sample(i, rtt: i is >= 905 and <= 912 ? 400 : 30 + i % 6, cpu: 20 + i % 5));
        var analysis = IncidentAnalyzer.Analyze(history, history.Incidents[0], TimeSpan.FromMinutes(2), TimeZoneInfo.Utc);

        var rtt = Assert.Single(analysis.Changes);
        Assert.Equal("rtt", rtt.ChannelId);
        Assert.Equal(400, rtt.Extreme);
        Assert.Equal(HistoryFixture.T0.AddSeconds(905), rtt.ExtremeAt);
        Assert.Contains("Round-trip time reached 400 ms at 08:15:05", analysis.Summary, StringComparison.Ordinal);
        Assert.Contains("User reported a performance issue at 08:14:59", analysis.Summary, StringComparison.Ordinal);
        Assert.Contains("CPU utilization", analysis.Summary, StringComparison.Ordinal);      // listed as without a clear change
        Assert.Contains("No diagnostic findings were recorded in this interval.", analysis.Summary, StringComparison.Ordinal);
        Assert.Contains("not proof of its cause", analysis.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain("because", analysis.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("caused by", analysis.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("root cause", analysis.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WhenNothingStandsOutTheAnalysisSaysSoHonestly()
    {
        var history = await SpikeHistory(i => HistoryFixture.Sample(i, rtt: 30 + i % 6, cpu: 20 + i % 5, ram: 40, fps: 30));
        var analysis = IncidentAnalyzer.Analyze(history, history.Incidents[0], TimeSpan.FromMinutes(2), TimeZoneInfo.Utc);
        Assert.Empty(analysis.Changes);
        Assert.Contains("No measurement stood out in this interval.", analysis.Summary, StringComparison.Ordinal);
        Assert.Contains("Round-trip time stayed within this session's normal range", analysis.Summary, StringComparison.Ordinal);
        Assert.Contains("CPU utilization stayed within this session's normal range", analysis.Summary, StringComparison.Ordinal);
        Assert.Contains("Memory utilization stayed within", analysis.Summary, StringComparison.Ordinal);
        Assert.Contains("No diagnostic findings were recorded", analysis.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADropInFrameRateIsAChangeInTheOtherDirection()
    {
        var history = await SpikeHistory(i => HistoryFixture.Sample(i, fps: i is >= 895 and <= 905 ? 2 : 28 + i % 5));
        var analysis = IncidentAnalyzer.Analyze(history, history.Incidents[0], TimeSpan.FromMinutes(2), TimeZoneInfo.Utc);
        var fps = Assert.Single(analysis.Changes, c => c.ChannelId == "fps");
        Assert.Equal(2, fps.Extreme);
        Assert.Contains("Frames per second fell to 2 FPS", fps.Statement, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingMeasurementsAreSaidExplicitlyAndNotCountedAsNormal()
    {
        var history = await SpikeHistory(i => HistoryFixture.Sample(i, rtt: i is >= 860 and <= 960 ? null : 30 + i % 6));
        var analysis = IncidentAnalyzer.Analyze(history, history.Incidents[0], TimeSpan.FromMinutes(2), TimeZoneInfo.Utc);
        Assert.Contains(analysis.Missing, m => m.StartsWith("Round-trip time: 101 of", StringComparison.Ordinal));
        Assert.Empty(analysis.Changes);
    }

    [Fact]
    public async Task RecordedFindingsInTheIntervalAreListedAsRecordedAndTooLittleDataIsNotCompared()
    {
        var history = await SpikeHistory(i => HistoryFixture.Sample(i, withEvent: i == 880), count: 1800);
        var analysis = IncidentAnalyzer.Analyze(history, history.Incidents[0], TimeSpan.FromMinutes(2), TimeZoneInfo.Utc);
        var finding = Assert.Single(analysis.RecordedFindings);
        Assert.Equal(HistoryEventKind.FindingDetected, finding.Kind);
        Assert.Contains("1 diagnostic finding event(s) were recorded in this interval", analysis.Summary, StringComparison.Ordinal);

        var tiny = await SpikeHistory(i => HistoryFixture.Sample(i), count: 20, incidentAt: 10);
        var small = IncidentAnalyzer.Analyze(tiny, tiny.Incidents[0], TimeSpan.FromMinutes(2), TimeZoneInfo.Utc);
        Assert.Empty(small.Changes);
        Assert.Contains("not enough valid data", small.Summary, StringComparison.Ordinal);
    }

    // ---------- time zones ----------

    /// <summary>A fixed rule: clocks go back at 03:00 local on 2026-10-25 (UTC+2 to UTC+1), so 02:00 to 02:59 local occurs twice.</summary>
    private static TimeZoneInfo Zone()
    {
        var rule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(new DateTime(2026, 1, 1), new DateTime(2026, 12, 31), TimeSpan.FromHours(1),
            TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 2, 0, 0), 3, 29), TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 3, 0, 0), 10, 25));
        return TimeZoneInfo.CreateCustomTimeZone("Test/Zone", TimeSpan.FromHours(1), "Test", "Test standard", "Test summer", [rule]);
    }

    [Fact]
    public void LocalTimesAreConvertedPerInstantAndAmbiguousOnesCarryTheirOffset()
    {
        var zone = Zone();
        var firstTwoThirty = new DateTimeOffset(2026, 10, 25, 0, 30, 0, TimeSpan.Zero);     // 02:30 at UTC+2
        var secondTwoThirty = firstTwoThirty.AddHours(1);                                     // 02:30 again, at UTC+1
        Assert.Equal("02:30:00 (UTC+02:00)", HistoryTime.Format(firstTwoThirty, zone));
        Assert.Equal("02:30:00 (UTC+01:00)", HistoryTime.Format(secondTwoThirty, zone));
        Assert.Equal("03:30:00", HistoryTime.Format(secondTwoThirty.AddHours(1), zone));
        Assert.Equal("01:30:00", HistoryTime.Format(firstTwoThirty.AddHours(-1), zone));   // 23:30 UTC is 01:30 at UTC+2
        Assert.Equal("2026-10-25 02:30:00.500 (UTC+02:00)", HistoryTime.Format(firstTwoThirty.AddMilliseconds(500), zone, withDate: true, milliseconds: true));
        Assert.Equal("+01:00", HistoryTime.FormatOffset(TimeSpan.FromHours(1)));
        Assert.Equal("-05:30", HistoryTime.FormatOffset(-TimeSpan.FromHours(5.5)));
        Assert.Equal("02:30", HistoryTime.AxisLabel(secondTwoThirty.UtcTicks, zone, seconds: false));
        Assert.Equal("", HistoryTime.AxisLabel(0, zone));
    }

    [Fact]
    public async Task ARecordingThatCrossesTheClockChangeKeepsItsEventsInOrderAndShowsDistinctLocalTimes()
    {
        var zone = Zone();
        var start = new DateTimeOffset(2026, 10, 24, 23, 59, 0, TimeSpan.Zero);              // 01:59 local, before the change
        var (writer, storage, clock, recorder) = IncidentTests.Create(compress: false);
        recorder.Start(false);
        for (var i = 0; i < 4000; i += 20)
        {
            clock.Set(start.AddSeconds(i));
            recorder.Add(HistoryFixture.Sample(i, start.AddSeconds(i)));
            if (i is 1800 or 5400 / 2 + 900) { clock.Set(start.AddSeconds(i + 1)); Assert.True((await recorder.MarkIncidentAsync()).Saved); }
        }
        await recorder.StopAsync();
        var shortId = storage.Names(Dir).Select(RecordingFormat.ShortIdOf).First(x => x.Length > 0);

        var history = HistoryLoader.Load(storage, Dir, shortId);

        Assert.Equal(history.Events.OrderBy(e => e.At).ToArray(), history.Events.OrderBy(e => e.At).ThenBy(e => e.Order).ToArray());
        var local = history.Incidents.Select(i => HistoryTime.Format(i.At, zone)).ToArray();
        Assert.Equal(local.Length, local.Distinct().Count());
        Assert.True(history.Incidents.Zip(history.Incidents.Skip(1)).All(p => p.First.At < p.Second.At));
        Assert.Equal(TimeSpan.Zero, history.Info.StartedUtc.Offset);
    }

    // ---------- the complete path ----------

    [Fact]
    public async Task StartMarkContinueRotateStopReopenFindInspect()
    {
        var spikeAt = 1500;
        var rec = await HistoryFixture.RecordAsync(3600, i => HistoryFixture.Sample(i, rtt: i is >= 1498 and <= 1508 ? 350 : 30 + i % 6, cpu: 20 + i % 4),
            async (i, c) =>
            {
                if (i == spikeAt) await c.MarkIncidentAsync();
                if (i == 2000) c.Mark(RecordingMarkKind.Paused, "Monitoring paused by the user.");
                if (i == 2010) c.Mark(RecordingMarkKind.Resumed, "Monitoring resumed.", TimeSpan.FromSeconds(10));
                if (i == 3000) await c.MarkIncidentAsync();
            }, segment: 120_000, skipSample: i => i is >= 2000 and < 2010);   // rotates many times, closed parts are compressed; no samples while paused
        var parts = HistoryFixture.PartPaths(rec.Storage, rec.ShortId);
        Assert.True(parts.Length >= 4, $"{parts.Length} parts");
        Assert.True(parts.Count(p => p.EndsWith(".gz", StringComparison.Ordinal)) >= parts.Length - 1);

        var summary = Assert.Single(RecordingCatalog.Scan(rec.Storage, Dir));
        Assert.Equal(2, summary.Incidents);
        var history = await HistoryLoader.LoadAsync(rec.Storage, Dir, summary.ShortId);

        Assert.Equal(3590, history.Info.Samples);
        Assert.Equal(RecordingCompleteness.Complete, history.Info.State);
        Assert.Equal(2, history.Incidents.Count);
        Assert.Equal(rec.Incidents.Select(i => i.Id), history.Incidents.Select(i => i.Id));
        var incident = history.Incidents[0];
        Assert.Equal(HistoryFixture.T0.AddSeconds(spikeAt - 0.5), incident.At);
        var window = history.Series("rtt", incident.At - TimeSpan.FromMinutes(2), incident.At + TimeSpan.FromMinutes(2), 600, history.Incidents.Select(i => i.At).ToArray());
        Assert.Equal(350, window.Statistics.Maximum);
        Assert.Equal(HistoryFixture.T0.AddSeconds(1498), window.Statistics.MaximumAt);
        Assert.False(window.Decimated);                              // 240 s fit: every recorded sample is shown
        var analysis = IncidentAnalyzer.Analyze(history, incident, TimeSpan.FromMinutes(2), TimeZoneInfo.Utc);
        Assert.Equal("rtt", Assert.Single(analysis.Changes).ChannelId);
        Assert.Contains(history.Events, e => e.Kind == HistoryEventKind.MonitoringResumed && e.Gap == TimeSpan.FromSeconds(10));
        Assert.Contains(history.Gaps, g => g.Reason == "Monitoring was paused");
        Assert.True(history.Info.DataCoverage < 1);
    }
}
