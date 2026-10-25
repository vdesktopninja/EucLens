using System.Text;
using EucLens.Core;
using Xunit;

namespace EucLens.Tests;

/// <summary>
/// The history reader meets files that are damaged or hostile: what a line says is never trusted for how much memory or time it may cost, and whatever cannot be
/// read is reported, never silently dropped while the analysis claims to be complete.
/// </summary>
public sealed class HistoryHardeningTests
{
    private static readonly string Dir = HistoryFixture.Dir;

    private static async Task<(HistoryFixture.Recorded Rec, string Path, string Text)> RecordedAsync(int count = 30, bool compress = false, long segment = 64L << 20,
        Func<int, SessionSnapshot>? make = null, Func<int, HistoryFixture.RecordingContext, Task>? step = null)
    {
        var rec = await HistoryFixture.RecordAsync(count, make, step, segment, compress);
        var path = HistoryFixture.PartPaths(rec.Storage, rec.ShortId)[0];
        return (rec, path, HistoryFixture.ReadText(rec.Storage, path));
    }

    /// <summary>Inserts lines after the header (the first line) of a plain part.</summary>
    private static void InsertAfterHeader(MemoryStorage storage, string path, string text, params string[] lines)
    {
        var all = text.Split('\n').ToList();
        all.InsertRange(1, lines);
        HistoryFixture.Replace(storage, path, string.Join('\n', all), gzip: false);
    }

    private static long Allocated(Action action)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        action();
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    // ---------------------------------------------------------------- string table ids

    [Fact]
    public async Task AFileControlledStringTableIdNeverAllocatesWithoutBound()
    {
        var (rec, path, text) = await RecordedAsync();
        InsertAfterHeader(rec.Storage, path, text,
            "{\"t\":\"str\",\"id\":2000000000,\"v\":\"x\"}",     // the id of the report: about 16 GB if it were honoured
            "{\"t\":\"str\",\"id\":-1,\"v\":\"x\"}",
            "{\"t\":\"str\",\"id\":-2147483648,\"v\":\"x\"}",
            "{\"t\":\"str\",\"id\":100000,\"v\":\"x\"}",         // the first id past the table limit
            "{\"t\":\"str\",\"id\":5000,\"v\":\"x\"}",           // extremely sparse: far past the end of a table that holds a few dozen entries
            "{\"t\":\"str\",\"id\":99999,\"v\":\"x\"}");

        LoadedHistory? history = null;
        var allocated = Allocated(() => history = HistoryFixture.Load(rec.Storage, rec.ShortId));

        Assert.True(allocated < 64L << 20, $"{allocated:N0} bytes were allocated for six lines.");
        Assert.Equal(30, history!.SampleCount);                                             // the recording itself is intact
        Assert.Equal(6, history.Issues.Count(i => i.Kind == HistoryIssueKind.DamagedLine && i.Message.Contains("out of range", StringComparison.Ordinal)));
        Assert.All(history.Issues.Where(i => i.Kind == HistoryIssueKind.DamagedLine), i => Assert.True(i.IsProblem));
    }

    [Fact]
    public async Task TheLegacySampleReaderRejectsAnOversizedStringTableIdInsteadOfRunningOutOfMemory()
    {
        var (rec, path, text) = await RecordedAsync();
        InsertAfterHeader(rec.Storage, path, text, "{\"t\":\"str\",\"id\":2000000000,\"v\":\"x\"}");
        var allocated = Allocated(() => Assert.Throws<InvalidDataException>(() => RecordingFormat.ReadSamples(rec.Storage, [path]).ToList()));
        Assert.True(allocated < 64L << 20);
    }

    [Fact]
    public async Task AnOrdinaryStringTableStillRoundTrips()
    {
        var (rec, _, _) = await RecordedAsync(count: 200, make: i => TestSamples.Make(i, withEvent: i % 50 == 0));
        var history = HistoryFixture.Load(rec.Storage, rec.ShortId);
        Assert.Equal(200, history.SampleCount);
        Assert.DoesNotContain(history.Issues, i => i.IsProblem);
    }

    // ---------------------------------------------------------------- line length, bytes read, collections

    [Fact]
    public async Task ALineLongerThanTheLimitIsSkippedAndReportedWithoutBuildingAStringOfItsSize()
    {
        var (rec, path, text) = await RecordedAsync();
        InsertAfterHeader(rec.Storage, path, text, "{\"t\":\"str\",\"id\":0,\"v\":\"" + new string('a', 3 << 20) + "\"}");     // 3 MiB: no recording line is

        LoadedHistory? history = null;
        var allocated = Allocated(() => history = HistoryFixture.Load(rec.Storage, rec.ShortId));

        Assert.Equal(30, history!.SampleCount);
        var issue = Assert.Single(history.Issues, i => i.Kind == HistoryIssueKind.DamagedLine);
        Assert.Contains("longer than", issue.Message, StringComparison.Ordinal);
        Assert.True(allocated < 24L << 20, $"{allocated:N0} bytes were allocated while skipping one 3 MiB line.");
    }

    [Fact]
    public async Task AFileWithoutLineBreaksIsReadWithBoundedMemoryAndEndsWithoutError()
    {
        var (rec, path, text) = await RecordedAsync(count: 10);
        HistoryFixture.Replace(rec.Storage, path, text.TrimEnd('\n') + "\n" + new string('z', 6 << 20), gzip: false);   // a 6 MiB tail with no newline
        LoadedHistory? history = null;
        var allocated = Allocated(() => history = HistoryFixture.Load(rec.Storage, rec.ShortId));
        Assert.Equal(10, history!.SampleCount);
        Assert.True(allocated < 24L << 20);
    }

    [Fact]
    public async Task ASmallCompressedFileThatExpandsPastTheLimitStopsTheReadAndIsReportedAsPartial()
    {
        var (rec, path, text) = await RecordedAsync(count: 20);
        // The recording's own lines, then 12 MiB that compress to a few kilobytes.
        var bomb = text.TrimEnd('\n').Replace("\n{\"t\":\"footer\"", "\n") + "\n" + new string('a', 12 << 20);
        rec.Storage.Files.Remove(path); rec.Storage.Times.Remove(path);
        HistoryFixture.Replace(rec.Storage, path + ".gz", bomb, gzip: true);
        Assert.True(rec.Storage.Files[path + ".gz"].Length < 64 * 1024, "the fixture is meant to be small when compressed");

        LoadedHistory? history = null;
        var allocated = Allocated(() => history = HistoryLoader.Load(rec.Storage, Dir, rec.ShortId, limits: new HistoryLimits(MaximumPartBytes: 2 << 20)));

        Assert.Equal(RecordingCompleteness.Partial, history!.Info.State);                  // never presented as complete
        Assert.True(history.Info.IsPartial);
        Assert.Contains(history.Issues, i => i.Kind == HistoryIssueKind.LimitReached && i.IsProblem);
        Assert.Equal(20, history.SampleCount);                                             // what came before the limit is kept
        Assert.Contains("limit", history.Info.EndReason, StringComparison.OrdinalIgnoreCase);
        Assert.True(allocated < 32L << 20, $"{allocated:N0} bytes were allocated.");
        Assert.DoesNotContain(history.Events, e => e.Kind == HistoryEventKind.RecordingEnded);
    }

    [Fact]
    public async Task TheTotalReadBudgetStopsAtAPartBoundaryAndSaysSo()
    {
        var rec = await HistoryFixture.RecordAsync(600, compress: false, segment: 20_000);
        var parts = HistoryFixture.PartPaths(rec.Storage, rec.ShortId);
        Assert.True(parts.Length >= 3);
        var first = rec.Storage.Files[parts[0]].Length;
        var history = HistoryLoader.Load(rec.Storage, Dir, rec.ShortId, limits: new HistoryLimits(MaximumTotalBytes: first + 100));
        Assert.Equal(RecordingCompleteness.Partial, history.Info.State);
        Assert.Contains(history.Issues, i => i.Kind == HistoryIssueKind.LimitReached);
        Assert.True(history.SampleCount is > 0 and < 600);
    }

    [Fact]
    public async Task TooManyTimelineEntriesGivePartialAnalysisNeverACompleteOne()
    {
        var (rec, _, _) = await RecordedAsync(count: 300, make: i => TestSamples.Make(i, withEvent: true));
        var history = HistoryLoader.Load(rec.Storage, Dir, rec.ShortId, limits: new HistoryLimits(MaximumEvents: 100));
        Assert.Equal(RecordingCompleteness.Partial, history.Info.State);
        Assert.InRange(history.Events.Count, 100, 110);
        Assert.Contains(history.Issues, i => i.Kind == HistoryIssueKind.LimitReached && i.Message.Contains("timeline entries", StringComparison.Ordinal));
        Assert.Contains("timeline entries", history.Info.LimitReason, StringComparison.Ordinal);
        Assert.DoesNotContain(history.Events, e => e.Kind == HistoryEventKind.RecordingEnded);
    }

    [Fact]
    public async Task TooManyMarkedIncidentsGivePartialAnalysis()
    {
        var (rec, _, _) = await RecordedAsync(count: 60, step: async (i, c) => { if (i % 10 == 5) await c.MarkIncidentAsync(); });
        var history = HistoryLoader.Load(rec.Storage, Dir, rec.ShortId, limits: new HistoryLimits(MaximumIncidents: 3));
        Assert.Equal(RecordingCompleteness.Partial, history.Info.State);
        Assert.Equal(3, history.Incidents.Count);
        Assert.Contains(history.Issues, i => i.Kind == HistoryIssueKind.LimitReached && i.Message.Contains("marked incidents", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FiveThousandEntriesStayCompleteAndAccessibleUnderTheDefaultLimits()
    {
        var (rec, _, _) = await RecordedAsync(count: 300, step: async (i, c) =>
        {
            if (i == 150) await c.MarkIncidentAsync();
            if (i == 100) for (var n = 0; n < 5000; n++) c.Mark(RecordingMarkKind.Note, $"Synthetic event {n}");
        });
        var history = HistoryFixture.Load(rec.Storage, rec.ShortId);
        Assert.Equal(RecordingCompleteness.Complete, history.Info.State);
        Assert.Equal(5000, history.Events.Count(e => e.Kind == HistoryEventKind.Note));
        Assert.True(history.Events.Count >= 5002);
        Assert.DoesNotContain(history.Issues, i => i.Kind == HistoryIssueKind.LimitReached);
    }

    [Fact]
    public async Task ThousandsOfDamagedLinesProduceAShortReportWithTheTrueCount()
    {
        var (rec, path, text) = await RecordedAsync(count: 40);
        InsertAfterHeader(rec.Storage, path, text, Enumerable.Range(0, 1000).Select(i => "not json " + i).ToArray());
        var history = HistoryFixture.Load(rec.Storage, rec.ShortId);
        Assert.Equal(40, history.SampleCount);
        var damaged = history.Issues.Where(i => i.Kind == HistoryIssueKind.DamagedLine).ToArray();
        Assert.True(damaged.Length <= 51, $"{damaged.Length} problems were listed.");
        Assert.Contains(damaged, i => i.Message.Contains("further problems", StringComparison.Ordinal) && i.Message.Contains("950", StringComparison.Ordinal));
    }

    // ---------------------------------------------------------------- values that are not what they claim to be

    [Fact]
    public async Task InvalidNumbersAndEnumValuesAreDamagedLinesNotCrashes()
    {
        var (rec, path, text) = await RecordedAsync();
        InsertAfterHeader(rec.Storage, path, text,
            "{\"t\":\"mark\",\"at\":\"2026-10-23T08:00:05Z\",\"kind\":\"Resumed\",\"gapSeconds\":1e300}",
            "{\"t\":\"mark\",\"at\":\"2026-10-23T08:00:05Z\",\"kind\":\"Note\",\"seq\":99999999999}",
            "{\"t\":\"event\",\"at\":\"2026-10-23T08:00:05Z\",\"kind\":\"999\",\"rule\":\"x\",\"severity\":\"Warning\",\"title\":\"t\"}",
            "{\"t\":\"event\",\"at\":\"2026-10-23T08:00:05Z\",\"kind\":\"Detected\",\"rule\":\"x\",\"severity\":\"Nope\",\"title\":\"t\"}",
            "{\"t\":\"event\",\"at\":\"2026-10-23T08:00:05Z\",\"kind\":\"Detected\",\"rule\":\"x\",\"severity\":\"-5\",\"title\":\"t\"}",
            "{\"t\":\"mark\",\"at\":\"not a date\",\"kind\":\"Note\"}",
            "{\"t\":\"event\",\"at\":\"2026-10-23T08:00:06Z\",\"kind\":\"Detected\",\"rule\":\"fine\",\"severity\":\"Warning\",\"title\":\"a valid one\"}");

        LoadedHistory? history = null;
        var thrown = Record.Exception(() => history = HistoryFixture.Load(rec.Storage, rec.ShortId));

        Assert.Null(thrown);
        Assert.Equal(30, history!.SampleCount);
        Assert.Equal(6, history.Issues.Count(i => i.Kind == HistoryIssueKind.DamagedLine));
        Assert.Contains(history.Events, e => e.Title.Contains("a valid one", StringComparison.Ordinal));      // the valid line next to them is used
    }

    // ---------------------------------------------------------------- damaged header

    [Fact]
    public async Task AHealthyPartIsRecoveredWhenAnotherPartHasADamagedHeader()
    {
        var rec = await HistoryFixture.RecordAsync(600, compress: false, segment: 20_000);
        var parts = HistoryFixture.PartPaths(rec.Storage, rec.ShortId);
        Assert.True(parts.Length >= 3);
        var whole = HistoryFixture.Load(rec.Storage, rec.ShortId);
        var damaged = HistoryFixture.ReadText(rec.Storage, parts[1]);
        var lines = damaged.Split('\n');
        var samplesInPart = lines.Count(l => l.StartsWith("{\"t\":\"s\"", StringComparison.Ordinal));
        lines[0] = "\u0001\u0002 the header was damaged";
        HistoryFixture.Replace(rec.Storage, parts[1], string.Join('\n', lines), gzip: false);

        var history = HistoryFixture.Load(rec.Storage, rec.ShortId);

        Assert.Equal(whole.SampleCount - samplesInPart, history.SampleCount);               // everything but that part
        Assert.Equal(whole.Info.Parts, history.Info.Parts);
        var issue = Assert.Single(history.Issues, i => i.Kind == HistoryIssueKind.DamagedHeader);
        Assert.Equal(2, issue.Part);
        Assert.True(issue.IsProblem);
    }

    [Fact]
    public async Task AnUnsupportedSchemaIsStillRejectedWhateverPartItIsIn()
    {
        var rec = await HistoryFixture.RecordAsync(600, compress: false, segment: 20_000);
        var parts = HistoryFixture.PartPaths(rec.Storage, rec.ShortId);
        var lines = HistoryFixture.ReadText(rec.Storage, parts[1]).Split('\n');
        lines[0] = lines[0].Replace("euclens.recording/1", "euclens.recording/9", StringComparison.Ordinal);
        HistoryFixture.Replace(rec.Storage, parts[1], string.Join('\n', lines), gzip: false);
        var rejected = Assert.Throws<RecordingFormatException>(() => HistoryFixture.Load(rec.Storage, rec.ShortId));
        Assert.Contains("unsupported format", rejected.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARecordingWhoseOnlyPartHasNoReadableHeaderIsRejected()
    {
        var (rec, path, text) = await RecordedAsync();
        var lines = text.Split('\n'); lines[0] = "garbage";
        HistoryFixture.Replace(rec.Storage, path, string.Join('\n', lines), gzip: false);
        var rejected = Assert.Throws<RecordingFormatException>(() => HistoryFixture.Load(rec.Storage, rec.ShortId));
        Assert.Contains("header", rejected.Message, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- cancellation during a long read

    private sealed class EndlessStream(byte[] prefix) : Stream
    {
        private int sent;
        public long Served;
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = 0;
            while (n < count) { buffer[offset + n] = sent < prefix.Length ? prefix[sent++] : (byte)'a'; n++; }   // a header, then a line that never ends
            Served += n;
            return n;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class EndlessStorage(MemoryStorage inner, string path, EndlessStream stream) : IRecordingStorage
    {
        public void EnsureDirectory(string directory) => inner.EnsureDirectory(directory);
        public Stream Create(string p) => inner.Create(p);
        public Stream OpenRead(string p) => p == path ? stream : inner.OpenRead(p);
        public IReadOnlyList<RecordingFileInfo> List(string directory) => inner.List(directory);
        public void Delete(string p) => inner.Delete(p);
        public void Rename(string from, string to) => inner.Rename(from, to);
        public long? FreeSpace(string directory) => inner.FreeSpace(directory);
    }

    [Fact]
    public async Task CancellingDuringAVeryLongLineStopsPromptlyInsteadOfReadingToTheEnd()
    {
        var (rec, path, text) = await RecordedAsync(count: 5);
        var header = Encoding.UTF8.GetBytes(text.Split('\n')[0] + "\n");
        var endless = new EndlessStream(header);
        var storage = new EndlessStorage(rec.Storage, path, endless);
        using var cancellation = new CancellationTokenSource();
        var started = System.Diagnostics.Stopwatch.StartNew();
        var load = HistoryLoader.LoadAsync(storage, Dir, rec.ShortId, cancellationToken: cancellation.Token, limits: new HistoryLimits(MaximumPartBytes: long.MaxValue, MaximumTotalBytes: long.MaxValue));
        await Task.Delay(150);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => load);

        Assert.True(started.Elapsed < TimeSpan.FromSeconds(10), $"The load took {started.Elapsed.TotalSeconds:0.0} s to stop.");
        Assert.True(endless.Served > 0);
    }
}
