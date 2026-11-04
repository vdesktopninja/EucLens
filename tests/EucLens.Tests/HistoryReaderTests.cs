using System.Text;
using EucLens.Core;
using Xunit;

namespace EucLens.Tests;

/// <summary>The historical timeline reader: parts, compression, events, interruption, damage, compatibility, cancellation and memory.</summary>
// Runs alone, like the other memory measurements: its eight-hour test reads the heap of the whole process (24 MiB limit, unchanged), which other test classes allocate into while it runs.
[Collection(SerialPerformanceCollection.Name)]
public sealed class HistoryReaderTests
{
    private static readonly string Dir = HistoryFixture.Dir;

    [Fact]
    public async Task AMultiPartCompressedRecordingIsReadOnceAndInOrder()
    {
        var rec = await HistoryFixture.RecordAsync(400, i => HistoryFixture.Sample(i, rtt: 30 + i % 5), segment: 9_000);
        var parts = HistoryFixture.PartPaths(rec.Storage, rec.ShortId);
        Assert.True(parts.Length > 3);
        Assert.All(parts, p => Assert.EndsWith(".jsonl.gz", p, StringComparison.Ordinal));

        var history = HistoryFixture.Load(rec.Storage, rec.ShortId);

        Assert.Equal(400, history.Info.Samples);
        Assert.Equal(400, history.SampleCount);
        Assert.Equal(parts.Length, history.Info.Parts);
        Assert.Equal(RecordingCompleteness.Complete, history.Info.State);
        Assert.Equal(HistoryFixture.T0, history.FirstSample);
        Assert.Equal(HistoryFixture.T0.AddSeconds(399), history.LastSample);
        Assert.Empty(history.Issues);
        var rtt = history.Channel("rtt")!;
        Assert.Equal(400, rtt.Valid);
        Assert.Equal(30 + 399 % 5, history.Series("rtt", history.FirstSample, history.LastSample, 1000).Points[^1].Value);
    }

    [Fact]
    public async Task APartThatExistsAsPlainAndCompressedIsCountedOnce()
    {
        var rec = await HistoryFixture.RecordAsync(300, segment: 9_000);
        var parts = HistoryFixture.PartPaths(rec.Storage, rec.ShortId);
        var gz = parts[1];
        var plain = gz[..^3];
        rec.Storage.Files[plain] = Decompress(rec.Storage.Files[gz]);   // the moment during compression when both exist
        rec.Storage.Times[plain] = rec.Storage.Now;

        var history = HistoryFixture.Load(rec.Storage, rec.ShortId);

        Assert.Equal(300, history.SampleCount);
        Assert.Equal(parts.Length, history.Info.Parts);
        Assert.Empty(history.Issues);
        Assert.Equal(300, history.Events.Count(e => e.Kind == HistoryEventKind.RecordingStarted) * 300);   // exactly one start event
        static byte[] Decompress(byte[] bytes) { using var gz = new System.IO.Compression.GZipStream(new MemoryStream(bytes), System.IO.Compression.CompressionMode.Decompress); using var m = new MemoryStream(); gz.CopyTo(m); return m.ToArray(); }
    }

    [Fact]
    public async Task AFindingTransitionIsOneTimelineEntryEvenThoughTheFileHoldsItTwice()
    {
        var rec = await HistoryFixture.RecordAsync(20, i => HistoryFixture.Sample(i, withEvent: i is 5), compress: false);
        var text = string.Join("\n", HistoryFixture.PartPaths(rec.Storage, rec.ShortId).Select(p => HistoryFixture.ReadText(rec.Storage, p)));
        Assert.Contains("\"t\":\"event\"", text, StringComparison.Ordinal);                 // the standalone line ...
        Assert.Contains("\"Events\"", text, StringComparison.Ordinal);                      // ... and the copy inside the sample

        var history = HistoryFixture.Load(rec.Storage, rec.ShortId);

        var detected = history.Events.Where(e => e.Kind == HistoryEventKind.FindingDetected).ToArray();
        var one = Assert.Single(detected);
        Assert.Equal(Severity.Warning, one.Severity);
        Assert.Equal("connection.rtt.sustained", one.RuleId);
        Assert.Equal(1, history.Info.Warnings);
        Assert.Equal(0, history.Info.Criticals);
    }

    [Fact]
    public async Task EventsThatOnlyExistInsideSampleSnapshotsStillAppearOnce()
    {
        var rec = await HistoryFixture.RecordAsync(20, i => HistoryFixture.Sample(i, withEvent: i == 5), compress: false);
        var path = Assert.Single(HistoryFixture.PartPaths(rec.Storage, rec.ShortId));
        var stripped = string.Join("\n", HistoryFixture.ReadText(rec.Storage, path).Split('\n').Where(l => !l.StartsWith("{\"t\":\"event\"", StringComparison.Ordinal)));
        HistoryFixture.Replace(rec.Storage, path, stripped, gzip: false);

        var history = HistoryFixture.Load(rec.Storage, rec.ShortId);

        Assert.Single(history.Events, e => e.Kind == HistoryEventKind.FindingDetected);
    }

    [Fact]
    public async Task EveryRecordedMarkBecomesATimelineEntryWithItsOriginalTimeAndKind()
    {
        var rec = await HistoryFixture.RecordAsync(60, step: async (i, c) =>
        {
            if (i == 10) c.Mark(RecordingMarkKind.Paused, "Monitoring paused by the user.");
            if (i == 15) c.Mark(RecordingMarkKind.Resumed, "Monitoring resumed.", TimeSpan.FromSeconds(5));
            if (i == 20) c.Mark(RecordingMarkKind.RemoteDisconnected);
            if (i == 21) c.Mark(RecordingMarkKind.Availability, "Horizon Blast counters are unavailable.");
            if (i == 25) c.Mark(RecordingMarkKind.RemoteReconnected);
            if (i == 30) await c.MarkIncidentAsync();
            if (i == 40) c.Mark(RecordingMarkKind.SessionLocked);
            if (i == 41) c.Mark(RecordingMarkKind.SessionUnlocked);
            if (i == 45) c.Mark(RecordingMarkKind.DataLoss, "3 sample(s) could not be written.", TimeSpan.FromSeconds(3));
            if (i == 50) c.Mark(RecordingMarkKind.Note, "A note");
        }, compress: false);

        var history = HistoryFixture.Load(rec.Storage, rec.ShortId);

        var kinds = history.Events.Select(e => e.Kind).ToArray();
        Assert.Equal(new[]
        {
            HistoryEventKind.RecordingStarted, HistoryEventKind.MonitoringPaused, HistoryEventKind.MonitoringResumed, HistoryEventKind.RemoteDisconnected, HistoryEventKind.AvailabilityChanged,
            HistoryEventKind.RemoteReconnected, HistoryEventKind.UserIncident, HistoryEventKind.SessionLocked, HistoryEventKind.SessionUnlocked, HistoryEventKind.DataLoss, HistoryEventKind.Note,
            HistoryEventKind.RecordingEnded
        }, kinds);
        Assert.Equal(history.Events.OrderBy(e => e.At).ThenBy(e => e.Order).ToArray(), history.Events.ToArray());
        Assert.Equal(TimeSpan.FromSeconds(5), history.Events.Single(e => e.Kind == HistoryEventKind.MonitoringResumed).Gap);
        var incident = history.Events.Single(e => e.Kind == HistoryEventKind.UserIncident);
        Assert.Equal(rec.Incidents[0].Id, incident.IncidentId);
        Assert.Equal(rec.Incidents[0].At, incident.At);
        Assert.Equal(1, history.Info.Incidents);
        Assert.Equal("Stopped by the user", history.Events[^1].Detail);
    }

    [Fact]
    public async Task AnIncidentKeepsItsIdAndTheRecordedStateAroundItWhenTheRecordingIsOpenedAgain()
    {
        var rec = await HistoryFixture.RecordAsync(30, i => HistoryFixture.Sample(i, active: i >= 9 ? ["connection.rtt.sustained"] : []), step: async (i, c) => { if (i == 12) await c.MarkIncidentAsync(); }, compress: false);

        var first = HistoryFixture.Load(rec.Storage, rec.ShortId);
        var second = HistoryFixture.Load(rec.Storage, rec.ShortId);

        var incident = Assert.Single(first.Incidents);
        Assert.Equal(rec.Incidents[0].Id, incident.Id);
        Assert.Equal(incident.Id, Assert.Single(second.Incidents).Id);
        Assert.Equal(HistoryFixture.T0.AddSeconds(11.5), incident.At);
        Assert.Equal(HistoryFixture.T0.AddSeconds(11), incident.Before!.Timestamp);
        Assert.Equal(HistoryFixture.T0.AddSeconds(12), incident.After!.Timestamp);
        Assert.Equal(["connection.rtt.sustained"], incident.RecordedState!.ActiveFindings);
        Assert.Equal(HealthState.Warning, incident.RecordedState.Assessments[0].State);
        Assert.Equal(["connection.rtt.sustained"], incident.After.Diagnostics!.ActiveFindings);
    }

    [Fact]
    public async Task AnInterruptedRecordingIsReadUpToItsLastCompleteLineAndNeverCalledComplete()
    {
        var rec = await HistoryFixture.RecordAsync(100, finish: false, compress: false, step: async (i, c) => { if (i == 50) await c.MarkIncidentAsync(); });
        var path = Assert.Single(HistoryFixture.PartPaths(rec.Storage, rec.ShortId));
        rec.Storage.Files[path] = rec.Storage.Files[path][..^15];     // power loss in the middle of the last line

        var history = HistoryFixture.Load(rec.Storage, rec.ShortId);

        Assert.Equal(RecordingCompleteness.Interrupted, history.Info.State);
        Assert.False(history.Info.HasEnded);
        Assert.Contains("Interrupted", history.Info.EndReason, StringComparison.Ordinal);
        Assert.InRange(history.SampleCount, 90, 99);
        var tail = Assert.Single(history.Issues);
        Assert.Equal(HistoryIssueKind.TruncatedTail, tail.Kind);
        Assert.False(tail.IsProblem);
        Assert.DoesNotContain(history.Events, e => e.Kind == HistoryEventKind.RecordingEnded);
        Assert.Single(history.Incidents);

        var live = HistoryFixture.Load(rec.Storage, rec.ShortId, active: rec.ShortId);
        Assert.Equal(RecordingCompleteness.InProgress, live.Info.State);
    }

    [Fact]
    public async Task ARecordingWithoutItsIndexFileIsStillReadAndStillCompleteBecauseItsFooterSaysSo()
    {
        var rec = await HistoryFixture.RecordAsync(300, segment: 9_000, step: async (i, c) => { if (i == 150) await c.MarkIncidentAsync(); });
        var meta = rec.Storage.Names(Dir).Single(n => n.EndsWith(".meta.json", StringComparison.Ordinal));
        rec.Storage.Delete(Path.Combine(Dir, meta));

        var summary = Assert.Single(RecordingCatalog.Scan(rec.Storage, Dir));
        Assert.True(summary.IsComplete);
        Assert.Equal("UserStopped", summary.EndReason);
        Assert.Equal(300, summary.Samples);
        Assert.Equal(1, summary.Incidents);
        var history = HistoryFixture.Load(rec.Storage, rec.ShortId);
        Assert.Equal(RecordingCompleteness.Complete, history.Info.State);
        Assert.Equal(300, history.SampleCount);
        Assert.Equal(summary.EndedUtc, history.Info.EndedUtc);

        // A damaged index is treated the same way.
        rec.Storage.Files[Path.Combine(Dir, meta)] = "{ not json"u8.ToArray(); rec.Storage.Times[Path.Combine(Dir, meta)] = rec.Storage.Now;
        Assert.True(Assert.Single(RecordingCatalog.Scan(rec.Storage, Dir)).IsComplete);
    }

    [Fact]
    public async Task AFileThatLooksLikeARecordingButIsNotOneNeverStopsTheListFromWorking()
    {
        var good = await HistoryFixture.RecordAsync(50, compress: false);
        var stamp = RecordingFormat.Stamp(HistoryFixture.T0.AddHours(1));
        string Put(string id, string text) { var path = Path.Combine(Dir, $"EucLens-{stamp}-{id}-part001.jsonl"); good.Storage.Files[path] = Encoding.UTF8.GetBytes(text); good.Storage.Times[path] = good.Storage.Now; return id; }
        Put("11111111", "{\"t\":\"header\"}\n");                                                       // a header without any of its fields
        Put("22222222", "{\"t\":\"header\",\"schema\":\"euclens.recording/9\",\"startedUtc\":\"not a date\"}\n");
        Put("33333333", "\0\0\0 binary \u0001 garbage");

        var listed = RecordingCatalog.Scan(good.Storage, Dir);

        Assert.Contains(listed, s => s.ShortId == good.ShortId && s.IsComplete);              // the real recording is still listed
        Assert.DoesNotContain(listed, s => s.ShortId is "11111111" or "22222222" or "33333333" && s.IsComplete);
        foreach (var id in new[] { "11111111", "22222222", "33333333" })
            Assert.Throws<RecordingFormatException>(() => HistoryFixture.Load(good.Storage, id));
    }

    [Fact]
    public async Task DamageInTheMiddleOfAFileIsReportedAndSkippedNotSilentlyAccepted()
    {
        var rec = await HistoryFixture.RecordAsync(60, compress: false);
        var path = Assert.Single(HistoryFixture.PartPaths(rec.Storage, rec.ShortId));
        var lines = HistoryFixture.ReadText(rec.Storage, path).Split('\n').ToList();
        var sampleLine = lines.FindIndex(30, l => l.StartsWith("{\"t\":\"s\"", StringComparison.Ordinal));
        lines[sampleLine] = lines[sampleLine][..40] + "<<damaged>>";
        HistoryFixture.Replace(rec.Storage, path, string.Join("\n", lines), gzip: false);

        var history = HistoryFixture.Load(rec.Storage, rec.ShortId);

        Assert.Equal(59, history.SampleCount);
        var issue = Assert.Single(history.Issues);
        Assert.Equal(HistoryIssueKind.DamagedLine, issue.Kind);
        Assert.True(issue.IsProblem);
        Assert.Contains("skipped", issue.Message, StringComparison.Ordinal);
        Assert.Equal(RecordingCompleteness.Complete, history.Info.State);
    }

    [Fact]
    public async Task AMissingPartIsReportedAndTheRestIsStillUsable()
    {
        var rec = await HistoryFixture.RecordAsync(300, segment: 9_000);
        var parts = HistoryFixture.PartPaths(rec.Storage, rec.ShortId);
        rec.Storage.Delete(parts[1]);

        var history = HistoryFixture.Load(rec.Storage, rec.ShortId);

        Assert.Contains(history.Issues, i => i.Kind == HistoryIssueKind.MissingPart && i.Part == 2);
        Assert.True(history.SampleCount is > 0 and < 300);
        Assert.NotEmpty(history.Gaps);
        Assert.True(history.Info.DataCoverage < 1);
    }

    [Fact]
    public async Task ADamagedCompressedPartIsReportedWithTheDataBeforeTheDamage()
    {
        var rec = await HistoryFixture.RecordAsync(300, segment: 9_000);
        var parts = HistoryFixture.PartPaths(rec.Storage, rec.ShortId);
        var bytes = rec.Storage.Files[parts[2]];
        rec.Storage.Files[parts[2]] = bytes[..(bytes.Length / 2)];

        var history = HistoryFixture.Load(rec.Storage, rec.ShortId);

        Assert.Contains(history.Issues, i => i.Kind == HistoryIssueKind.DamagedCompressedData);
        Assert.True(history.SampleCount > 0);
    }

    [Fact]
    public void RecordingsThatCannotBeReadGiveUnderstandableErrors()
    {
        var storage = new MemoryStorage();
        var stamp = RecordingFormat.Stamp(HistoryFixture.T0);
        string Put(string id, string content) { var path = Path.Combine(Dir, $"EucLens-{stamp}-{id}-part001.jsonl"); storage.Files[path] = Encoding.UTF8.GetBytes(content); storage.Times[path] = storage.Now; return id; }

        var future = Assert.Throws<RecordingFormatException>(() => HistoryFixture.Load(storage, Put("aaaaaaaa", "{\"t\":\"header\",\"schema\":\"euclens.recording/2\",\"startedUtc\":\"2026-10-23T08:00:00Z\"}\n")));
        Assert.Contains("unsupported format", future.Message, StringComparison.Ordinal);
        Assert.Contains("euclens.recording/2", future.Message, StringComparison.Ordinal);

        var headerless = Assert.Throws<RecordingFormatException>(() => HistoryFixture.Load(storage, Put("bbbbbbbb", "{\"t\":\"s\",\"ts\":\"2026-10-23T08:00:00Z\",\"d\":{}}\n")));
        Assert.Contains("header", headerless.Message, StringComparison.Ordinal);

        var empty = Assert.Throws<RecordingFormatException>(() => HistoryFixture.Load(storage, Put("cccccccc", "")));
        Assert.Contains("no header", empty.Message, StringComparison.Ordinal);

        var none = Assert.Throws<RecordingFormatException>(() => HistoryFixture.Load(storage, "dddddddd"));
        Assert.Contains("No files", none.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFileFromTheEarlierVersionWithUnknownLinesAndNoIncidentFieldsLoads()
    {
        var rec = await HistoryFixture.RecordAsync(50, compress: false);
        var path = Assert.Single(HistoryFixture.PartPaths(rec.Storage, rec.ShortId));
        var lines = HistoryFixture.ReadText(rec.Storage, path).Split('\n').Where(l => l.Length > 0).ToList();
        lines.Insert(3, "{\"t\":\"a-line-type-of-a-later-version\",\"x\":1}");
        lines[^1] = lines[^1].Replace(",\"incidents\":0", "", StringComparison.Ordinal);   // 0.4.3 footers have no incident count
        HistoryFixture.Replace(rec.Storage, path, string.Join("\n", lines) + "\n", gzip: false);

        var history = HistoryFixture.Load(rec.Storage, rec.ShortId);

        Assert.Equal(50, history.SampleCount);
        Assert.Empty(history.Incidents);
        Assert.Empty(history.Issues);
        Assert.Equal(RecordingCompleteness.Complete, history.Info.State);
    }

    [Fact]
    public async Task OnlyMetricsThatHaveValuesAreOfferedAndMissingValuesStayMissing()
    {
        var rec = await HistoryFixture.RecordAsync(100, i => HistoryFixture.Sample(i, rtt: i is >= 40 and < 50 ? null : 30, cpu: 20, fps: null), compress: false);

        var history = HistoryFixture.Load(rec.Storage, rec.ShortId);

        var ids = history.Channels.Select(c => c.Channel.Id).ToArray();
        Assert.Contains("rtt", ids); Assert.Contains("cpu", ids);
        Assert.DoesNotContain("fps", ids);                                  // never had a value: not drawn as zero
        var fps = Assert.Single(history.UnavailableChannels, c => c.Channel.Id == "fps");
        Assert.Contains("No data", fps.UnavailableReason, StringComparison.Ordinal);
        var rtt = history.Channel("rtt")!;
        Assert.Equal(90, rtt.Valid); Assert.Equal(10, rtt.Missing);
        var window = history.Series("rtt", history.FirstSample, history.LastSample, 1000);
        Assert.Contains(window.Points, p => p.Value is null);               // a break at the missing run
        Assert.DoesNotContain(window.Points, p => p.Value == 0);
        Assert.Equal(10, window.Statistics.Missing);
    }

    [Fact]
    public async Task RawCounterUnitsKeepTheirUnverifiedLabelAndAreNeverConverted()
    {
        var rec = await HistoryFixture.RecordAsync(50, i =>
        {
            var s = HistoryFixture.Sample(i);
            return s with { Connection = s.Connection with { AvailableBandwidth = new Metric(12.5, "raw (unit unverified)", RawValue: 12) } };
        }, compress: false);

        var history = HistoryFixture.Load(rec.Storage, rec.ShortId);

        var channel = history.Channel("availableBandwidth")!;
        Assert.Equal("raw (unit unverified)", channel.Unit);
        Assert.False(channel.UnitVerified);
        var window = history.Series("availableBandwidth", history.FirstSample, history.LastSample, 100);
        Assert.False(window.UnitVerified);
        Assert.All(window.Points.Where(p => p.Value is not null), p => Assert.Equal(12.5, p.Value));
        Assert.Equal("raw (unit unverified)", window.Unit);
        Assert.True(history.Channel("rtt")!.UnitVerified);
    }

    [Fact]
    public async Task TheSampleCapIsReportedInsteadOfLoadingWithoutBound()
    {
        var rec = await HistoryFixture.RecordAsync(500, compress: false);
        var history = await HistoryLoader.LoadAsync(rec.Storage, Dir, rec.ShortId, maximumSamples: 120);
        Assert.Equal(120, history.SampleCount);
        var issue = Assert.Single(history.Issues);
        Assert.Equal(HistoryIssueKind.SampleCapReached, issue.Kind);
        Assert.True(issue.IsProblem);
        // Never presented as the whole session: not complete, not interrupted, but explicitly partial.
        Assert.Equal(RecordingCompleteness.Partial, history.Info.State);
        Assert.True(history.Info.IsPartial);
        Assert.Equal(120, history.Info.SampleLimit);
        Assert.False(history.Info.HasEnded);
        Assert.Equal(history.LastSample, history.Info.EndedUtc);              // the end of what was analyzed, not the end of the recording
        Assert.DoesNotContain(history.Events, e => e.Kind == HistoryEventKind.RecordingEnded);
        Assert.Contains("sample limit", history.Info.EndReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASampleStampedBeforeItsPredecessorIsOrderedAndReported()
    {
        var rec = await HistoryFixture.RecordAsync(40, i => HistoryFixture.Sample(i, at: i == 20 ? HistoryFixture.T0.AddSeconds(5) : HistoryFixture.T0.AddSeconds(i)), compress: false);
        var history = HistoryFixture.Load(rec.Storage, rec.ShortId);
        Assert.Contains(history.Issues, i => i.Kind == HistoryIssueKind.ClockChange);
        var window = history.Series("rtt", history.FirstSample, history.LastSample, 1000);
        Assert.Equal(window.Points.Select(p => p.Ticks).Order().ToArray(), window.Points.Select(p => p.Ticks).ToArray());
    }

    // ---------- cancellation, memory, repeated navigation ----------

    private sealed class CancellingStorage(MemoryStorage inner, CancellationTokenSource cts) : IRecordingStorage
    {
        public void EnsureDirectory(string directory) => inner.EnsureDirectory(directory);
        public Stream Create(string path) => inner.Create(path);
        public Stream OpenRead(string path) => new Tripwire(inner.OpenRead(path), cts);
        public IReadOnlyList<RecordingFileInfo> List(string directory) => inner.List(directory);
        public void Delete(string path) => inner.Delete(path);
        public void Rename(string from, string to) => inner.Rename(from, to);
        public long? FreeSpace(string directory) => inner.FreeSpace(directory);
        private sealed class Tripwire(Stream stream, CancellationTokenSource cts) : Stream
        {
            private int reads;
            public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
            public override long Length => stream.Length; public override long Position { get => stream.Position; set => throw new NotSupportedException(); }
            public override int Read(byte[] buffer, int offset, int count) { if (++reads == 3) cts.Cancel(); return stream.Read(buffer, offset, count); }
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            protected override void Dispose(bool disposing) { if (disposing) stream.Dispose(); base.Dispose(disposing); }
        }
    }

    [Fact]
    public async Task LoadingCanBeCancelledWhileItReadsAndLeavesNothingRunning()
    {
        var rec = await HistoryFixture.RecordAsync(6000, compress: false);
        using var cts = new CancellationTokenSource();
        var task = HistoryLoader.LoadAsync(new CancellingStorage(rec.Storage, cts), Dir, rec.ShortId, cancellationToken: cts.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task.WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.True(task.IsCompleted);                                  // the worker is finished, not left reading in the background
        Assert.True(task.IsCanceled);

        using var before = new CancellationTokenSource(); before.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => HistoryLoader.LoadAsync(rec.Storage, Dir, rec.ShortId, cancellationToken: before.Token));
    }

    [Fact]
    public async Task AnEightHourRecordingLoadsIntoBoundedMemoryAndChartsStayBounded()
    {
        var rec = await HistoryFixture.EightHours;
        Assert.True(HistoryFixture.PartPaths(rec.Storage, rec.ShortId).Length >= 3);
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        var before = GC.GetTotalMemory(true);

        var history = await HistoryLoader.LoadAsync(rec.Storage, Dir, rec.ShortId);

        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        var retained = GC.GetTotalMemory(true) - before;
        Assert.Equal(28_800, history.SampleCount);
        Assert.Equal(RecordingCompleteness.Complete, history.Info.State);
        Assert.Equal(2, history.Incidents.Count);
        Assert.Empty(history.Issues);
        Assert.Equal(1.0, history.Info.DataCoverage, 3);
        Assert.True(history.ApproximateBytes < 8L << 20, $"{history.ApproximateBytes} bytes of columns");
        Assert.True(retained < 24L << 20, $"{retained >> 20} MiB retained by an eight hour history (raw lines are not kept)");
        foreach (var window in history.View(history.FirstSample, history.LastSample, 600))
            Assert.True(window.Points.Count <= 600 + 8, $"{window.Channel.Id}: {window.Points.Count} points");
        GC.KeepAlive(history);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static WeakReference OpenAndUseOnce(HistoryFixture.Recorded rec)
    {
        // Synchronous on purpose: an async method would keep its last local alive in its state machine and make the test blame the history for it.
        var history = HistoryLoader.LoadAsync(rec.Storage, Dir, rec.ShortId).GetAwaiter().GetResult();
        _ = history.View(history.FirstSample, history.LastSample, 500);
        _ = IncidentAnalyzer.Analyze(history, history.Incidents[0], TimeSpan.FromMinutes(2));
        return new WeakReference(history);
    }

    [Fact]
    public async Task OpeningAndClosingHistoriesRepeatedlyReleasesThem()
    {
        var rec = await HistoryFixture.RecordAsync(1500, compress: false, step: async (i, c) => { if (i == 700) await c.MarkIncidentAsync(); });
        var weak = new List<WeakReference>();
        for (var round = 0; round < 25; round++) weak.Add(OpenAndUseOnce(rec));
        for (var i = 0; i < 3; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); }
        Assert.All(weak, w => Assert.False(w.IsAlive));
    }
}
