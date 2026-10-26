using System.IO.Compression;
using System.Text;
using EucLens.Core;
using Xunit;

namespace EucLens.Tests;

/// <summary>Persistent recording: format round trip, streaming, rotation, finalization, failures, quota, retention and export compatibility.</summary>
public sealed class RecordingStreamingTests
{
    private static readonly string Dir = Path.Combine(Path.GetTempPath(), "euclens-recordings-test");

    private static RecordingOptions Options(Action<RecordingOptionsBuilder>? tune = null)
    {
        var b = new RecordingOptionsBuilder();
        tune?.Invoke(b);
        return new RecordingOptions
        {
            Directory = Dir, QuotaBytes = b.Quota, MaxAge = b.MaxAge, SegmentBytes = b.Segment, FlushInterval = TimeSpan.FromSeconds(5), QueueCapacity = b.Queue,
            MinimumFreeBytes = b.MinFree, CompressClosedParts = b.Compress, RetryInterval = TimeSpan.FromSeconds(5), GiveUpAfter = TimeSpan.FromSeconds(60)
        };
    }
    private sealed class RecordingOptionsBuilder
    {
        public long Quota = 512L << 20; public TimeSpan? MaxAge = TimeSpan.FromDays(14); public long Segment = 64L << 20; public int Queue = 900; public long MinFree = 1 << 20; public bool Compress = true;
    }

    private static (RecordingWriter Writer, MemoryStorage Storage, FakeClock Clock, SessionRecorder Recorder) Create(Action<RecordingOptionsBuilder>? tune = null, MemoryStorage? storage = null)
    {
        storage ??= new();
        var clock = new FakeClock(storage.Now);
        var writer = new RecordingWriter(() => Options(tune), storage, clock);
        return (writer, storage, clock, new SessionRecorder(writer, clock));
    }

    private static IEnumerable<(string Type, System.Text.Json.JsonElement Root)> Lines(MemoryStorage st)
    {
        foreach (var name in st.Names(Dir).Where(n => RecordingFormat.TryParsePart(n, out _, out _, out _)).OrderBy(n => n, StringComparer.Ordinal))
            foreach (var line in RecordingFormat.ReadLines(st, Path.Combine(Dir, name))) yield return line;
    }

    private static string[] Kinds(IEnumerable<System.Text.Json.JsonElement> marks) => marks.Select(m => m.GetProperty("kind").GetString()!).ToArray();

    // ---------- format ----------

    [Fact]
    public async Task EverySampleRoundTripsLosslesslyIncludingGapsRawValuesReasonsAndEvents()
    {
        var (writer, storage, _, recorder) = Create();
        var originals = Enumerable.Range(0, 40).Select(i => TestSamples.Make(i, blast: i % 10 < 7, withEvent: i == 5)).ToArray();
        recorder.Start(isDemo: false);
        foreach (var s in originals) recorder.Add(s);
        await recorder.StopAsync();

        var back = writer.ReadSamples().ToArray();
        Assert.Equal(originals.Length, back.Length);
        Assert.Equal(TestSamples.Json(originals), TestSamples.Json(back));
        // An unavailable measurement has no value and is never written as zero.
        var unavailable = back[8].BlastMetrics!["rtt"];
        Assert.Null(unavailable.Value); Assert.Equal(MetricStatus.Unavailable, unavailable.Status);
        Assert.Equal("No Horizon Blast Session Counters instance for Windows session 3.", unavailable.UnavailableReason);
        // Raw-unit counters keep their raw value and unverified unit.
        Assert.Equal(12, back[0].BlastMetrics!["jitter"].RawValue);
        Assert.Equal("raw (unit unverified)", back[0].BlastMetrics!["jitter"].Unit);
        Assert.Equal(originals[0].BlastMetrics!["rtt"].Timestamp, back[0].BlastMetrics!["rtt"].Timestamp);
    }

    [Fact]
    public async Task FileStructureIsSelfDescribingWithHeaderSessionEventsAndFooter()
    {
        var (_, storage, _, recorder) = Create(b => b.Compress = false);
        recorder.Start(false);
        recorder.Add(TestSamples.Make(0));
        recorder.Add(TestSamples.Make(1, withEvent: true, eventSeverity: Severity.Critical));
        await recorder.StopAsync();
        var types = Lines(storage).Select(l => l.Type).ToArray();
        Assert.Equal("header", types[0]);
        Assert.Equal(1, types.Count(t => t == "session")); // identical session is not repeated
        Assert.Equal(2, types.Count(t => t == "s"));
        Assert.Equal("footer", types[^1]);
        var header = Lines(storage).First().Root;
        Assert.Equal(RecordingFormat.Schema, header.GetProperty("schema").GetString());
        Assert.Equal("RealSession", header.GetProperty("source").GetString());
        var ev = Lines(storage).Single(l => l.Type == "event").Root;
        Assert.Equal("Detected", ev.GetProperty("kind").GetString()); Assert.Equal("Critical", ev.GetProperty("severity").GetString());
        var footer = Lines(storage).Last().Root;
        Assert.Equal("UserStopped", footer.GetProperty("reason").GetString());
        Assert.Equal(2, footer.GetProperty("samples").GetInt64()); Assert.Equal(1, footer.GetProperty("criticals").GetInt64()); Assert.True(footer.GetProperty("complete").GetBoolean());
        // Strings that repeat in every sample are stored once.
        Assert.Equal(1, Lines(storage).Count(l => l.Type == "str" && l.Root.GetProperty("v").GetString()!.Contains("RTT", StringComparison.Ordinal) && l.Root.GetProperty("v").GetString()!.Contains("x)", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task SamplesAreCompactAndMemoryStaysBoundedWhileTheFileGrows()
    {
        var (writer, storage, _, recorder) = Create(b => b.Compress = false);
        recorder.Start(false);
        for (var i = 0; i < 3000; i++) { recorder.Add(TestSamples.Make(i)); if (i % 100 == 99) await writer.FlushAsync(); } // a real sampler produces 1 sample per second
        await recorder.StopAsync();
        Assert.Equal(3000, recorder.SampleCount);
        Assert.Equal(SessionRecorder.PersistedTailSamples, recorder.Samples.Count); // hours of samples are never held in RAM
        var perSample = storage.TotalBytes / 3000d;
        Assert.InRange(perSample, 300, 2500);
        Assert.Equal(3000, writer.ReadSamples().Count());
    }

    [Fact]
    public async Task WritesAreBufferedAndFlushedAtTheConfiguredInterval()
    {
        var (writer, storage, clock, recorder) = Create(b => b.Compress = false);
        recorder.Start(false);
        for (var i = 0; i < 6; i++) { clock.Advance(TimeSpan.FromSeconds(1)); recorder.Add(TestSamples.Make(i)); }
        await Task.Delay(100);
        var before = storage.TotalBytes; // the header and session may be visible, the samples are still buffered
        for (var i = 6; i < 12; i++) { clock.Advance(TimeSpan.FromSeconds(1)); recorder.Add(TestSamples.Make(i)); }
        await writer.FlushAsync();
        Assert.True(storage.TotalBytes > before);
        await recorder.StopAsync();
    }

    // ---------- rotation, finalization, recovery ----------

    [Fact]
    public async Task PartsRotateAreSelfContainedCompressAfterClosingAndReadBackInOrder()
    {
        var (writer, storage, _, recorder) = Create(b => b.Segment = 12_000);
        recorder.Start(false);
        for (var i = 0; i < 60; i++) recorder.Add(TestSamples.Make(i));
        await recorder.StopAsync();
        var names = storage.Names(Dir);
        var parts = names.Where(n => RecordingFormat.TryParsePart(n, out _, out _, out _)).ToArray();
        Assert.True(parts.Length >= 3, string.Join(", ", names));
        Assert.All(parts, n => Assert.EndsWith(".jsonl.gz", n)); // closed parts are compressed, plain ones removed
        Assert.DoesNotContain(names, n => n.EndsWith(".partial", StringComparison.Ordinal));
        var samples = writer.ReadSamples().ToArray();
        Assert.Equal(60, samples.Length);
        Assert.Equal(Enumerable.Range(0, 60).Select(i => TestSamples.T0.AddSeconds(i)), samples.Select(s => s.Timestamp));
        // Each part stands alone: header, then its own session and string table before the first sample.
        foreach (var name in parts)
        {
            var types = RecordingFormat.ReadLines(storage, Path.Combine(Dir, name)).Select(l => l.Type).ToArray();
            Assert.Equal("header", types[0]);
            if (types.Contains("s")) Assert.True(Array.IndexOf(types, "session") < Array.IndexOf(types, "s"));
        }
        using var gz = new GZipStream(storage.OpenRead(Path.Combine(Dir, parts[0])), CompressionMode.Decompress);
        using var reader = new StreamReader(gz); Assert.StartsWith("{\"t\":\"header\"", reader.ReadLine());
    }

    [Fact]
    public async Task FinalizedRecordingIsListedAsCompleteWithCountsAndAnIndex()
    {
        var (_, storage, clock, recorder) = Create();
        recorder.Start(false);
        for (var i = 0; i < 10; i++) recorder.Add(TestSamples.Make(i, withEvent: i is 2 or 6, eventSeverity: i == 2 ? Severity.Warning : Severity.Critical));
        clock.Advance(TimeSpan.FromMinutes(1));
        await recorder.StopAsync();
        var summary = Assert.Single(RecordingCatalog.Scan(storage, Dir));
        Assert.True(summary.IsComplete);
        Assert.Equal("UserStopped", summary.EndReason);
        Assert.Equal(10, summary.Samples); Assert.Equal(1, summary.Warnings); Assert.Equal(1, summary.Criticals); Assert.Equal(0, summary.LostSamples);
        Assert.Equal("RealSession", summary.Source);
        Assert.Contains(storage.Names(Dir), n => n.EndsWith(".meta.json", StringComparison.Ordinal));
        Assert.True(summary.Bytes > 0);
    }

    [Fact]
    public async Task InterruptedRecordingIsRecoveredUpToItsLastCompleteLine()
    {
        var (writer, storage, _, recorder) = Create(b => b.Compress = false);
        recorder.Start(false);
        for (var i = 0; i < 20; i++) recorder.Add(TestSamples.Make(i, withEvent: i == 3));
        await writer.FlushAsync(); // the process is killed here: no footer, no finalization
        var crashed = new MemoryStorage();
        var part = storage.Files.Single(f => f.Key.EndsWith(".jsonl", StringComparison.Ordinal));
        // The last line is cut in half, as after a power loss.
        var bytes = part.Value; var truncated = bytes[..(bytes.Length - 40)];
        crashed.Files[part.Key] = truncated; crashed.Times[part.Key] = storage.Now;
        await recorder.StopAsync();

        var summary = Assert.Single(RecordingCatalog.Scan(crashed, Dir));
        Assert.False(summary.IsComplete);
        Assert.Equal("Interrupted", summary.EndReason);
        Assert.Equal(19, summary.Samples);
        Assert.Equal(1, summary.Warnings);
        Assert.Equal(19, RecordingCatalog.ReadSamples(crashed, summary).Count());
    }

    // ---------- pause, resume, marks ----------

    [Fact]
    public async Task PauseAndResumeAreExplicitMarksWithTheGapNotInventedSamples()
    {
        var (_, storage, clock, recorder) = Create(b => b.Compress = false);
        var coordinator = new MonitoringCoordinator(clock, startRunning: true);
        var service = new MonitoringService(coordinator, recorder, clock: clock);
        recorder.Start(false);
        recorder.Add(TestSamples.Make(0));
        service.PauseMonitoring();
        clock.Advance(TimeSpan.FromMinutes(7));
        service.ResumeMonitoring();
        recorder.Add(TestSamples.Make(1, TestSamples.T0.AddMinutes(7)));
        await recorder.StopAsync();
        var marks = Lines(storage).Where(l => l.Type == "mark").Select(l => l.Root).ToArray();
        Assert.Equal(new[] { "Paused", "Resumed" }, Kinds(marks));
        Assert.Equal(7 * 60, marks[1].GetProperty("gapSeconds").GetDouble());
        Assert.Equal(2, Lines(storage).Count(l => l.Type == "s")); // nothing was recorded during the pause
    }

    [Fact]
    public async Task AvailabilityChangesAndSessionEventsAreRecordedAsMarksNotAsNetworkFailures()
    {
        var (_, storage, clock, recorder) = Create(b => b.Compress = false);
        var service = new MonitoringService(new MonitoringCoordinator(clock), recorder, clock: clock);
        recorder.Start(false);
        service.OnSample(TestSamples.Make(0, blast: true), null);
        service.OnSample(TestSamples.Make(1, blast: false), null);   // Horizon disconnect: counters vanish
        service.OnSessionEvent(SessionEventKind.RemoteDisconnected);
        service.OnSample(TestSamples.Make(2, blast: false), null);   // no duplicate mark while it stays unavailable
        service.OnSessionEvent(SessionEventKind.RemoteReconnected);
        service.OnSample(TestSamples.Make(3, blast: true), null);
        service.OnSessionEvent(SessionEventKind.Locked);
        service.OnSessionEvent(SessionEventKind.Unlocked);
        await recorder.StopAsync();
        var marks = Lines(storage).Where(l => l.Type == "mark").Select(l => l.Root).ToArray();
        Assert.Equal(new[] { "Availability", "RemoteDisconnected", "RemoteReconnected", "Availability", "SessionLocked", "SessionUnlocked" }, Kinds(marks));
        Assert.Contains("not evidence of a network fault", marks[0].GetProperty("detail").GetString());
        Assert.Contains("available again", marks[3].GetProperty("detail").GetString());
    }

    // ---------- storage failures ----------

    [Fact]
    public void UnusableFolderIsReportedAtStartAndNothingIsRecorded()
    {
        var storage = new MemoryStorage { CreateFault = _ => new UnauthorizedAccessException("Access to the path is denied.") };
        var (_, _, _, recorder) = Create(storage: storage);
        Assert.Throws<UnauthorizedAccessException>(() => recorder.Start(false));
        Assert.False(recorder.IsRecording);
        storage.CreateFault = null; storage.Free = 10 << 10;
        var ex = Assert.Throws<IOException>(() => recorder.Start(false));
        Assert.Contains("free", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(recorder.IsRecording);
    }

    [Fact]
    public async Task DiskFullIsCountedNeverSilentAndRecoveryWritesALossMark()
    {
        var (writer, storage, clock, recorder) = Create(b => b.Compress = false);
        var faults = new List<RecordingFault>();
        writer.Faulted += faults.Add;
        recorder.Start(false);
        for (var i = 0; i < 5; i++) recorder.Add(TestSamples.Make(i));
        await writer.FlushAsync();
        storage.WriteFault = new IOException("There is not enough space on the disk.", unchecked((int)0x80070070));
        for (var i = 5; i < 15; i++) { clock.Advance(TimeSpan.FromSeconds(1)); recorder.Add(TestSamples.Make(i)); await writer.FlushAsyncSafe(); }
        Assert.Contains(faults, f => f is { Kind: RecordingFaultKind.DiskFull, IsFatal: false });
        Assert.Equal(RecordingSinkState.Faulted, recorder.SinkStatus!.State);
        var lostWhileFailing = recorder.SinkStatus.SamplesLost;
        Assert.True(lostWhileFailing >= 9);

        storage.WriteFault = null;                       // space was freed
        clock.Advance(TimeSpan.FromSeconds(10));         // past the retry interval
        recorder.Add(TestSamples.Make(15)); recorder.Add(TestSamples.Make(16));
        await recorder.StopAsync();
        var status = recorder.SinkStatus!;
        // Every sample is accounted for: written or counted as lost.
        Assert.Equal(17, status.SamplesWritten + status.SamplesLost);
        var loss = Lines(storage).Single(l => l.Type == "mark" && l.Root.GetProperty("kind").GetString() == "DataLoss").Root;
        Assert.Contains($"{lostWhileFailing}", loss.GetProperty("detail").GetString());
        Assert.True(loss.GetProperty("gapSeconds").GetDouble() > 0);
        Assert.True(Lines(storage).Count(l => l.Type == "header") >= 2); // a new part was opened after the failure
        var summary = Assert.Single(RecordingCatalog.Scan(storage, Dir));
        Assert.True(summary.LostSamples >= lostWhileFailing);
    }

    [Fact]
    public async Task WritesThatKeepFailingStopTheRecordingWithAStorageError()
    {
        var (writer, storage, clock, recorder) = Create();
        var ended = new List<RecordingEndReason>();
        recorder.Ended += ended.Add;
        var faults = new List<RecordingFault>(); writer.Faulted += faults.Add;
        recorder.Start(false);
        recorder.Add(TestSamples.Make(0));
        await writer.FlushAsync();
        storage.WriteFault = new UnauthorizedAccessException("Access denied");
        for (var i = 1; i <= 80 && recorder.IsRecording; i++) { clock.Advance(TimeSpan.FromSeconds(1)); recorder.Add(TestSamples.Make(i)); await writer.FlushAsyncSafe(); }
        for (var wait = 0; wait < 50 && recorder.IsRecording; wait++) await Task.Delay(20);
        Assert.False(recorder.IsRecording);
        Assert.Equal([RecordingEndReason.StorageError], ended);
        Assert.Contains(faults, f => f is { Kind: RecordingFaultKind.AccessDenied, IsFatal: false });
        Assert.Contains(faults, f => f.IsFatal);
    }

    [Theory]
    [InlineData(0x70, RecordingFaultKind.DiskFull)]
    [InlineData(0x27, RecordingFaultKind.DiskFull)]
    [InlineData(0x35, RecordingFaultKind.Unavailable)]
    [InlineData(0x40, RecordingFaultKind.Unavailable)]
    [InlineData(0x15, RecordingFaultKind.Unavailable)]
    [InlineData(0x1F, RecordingFaultKind.Io)]
    public void StorageErrorsAreClassifiedByTheirWindowsErrorCode(int win32, RecordingFaultKind expected) =>
        Assert.Equal(expected, RecordingWriter.Classify(new IOException("x", unchecked((int)(0x80070000 | (uint)win32)))));

    [Fact]
    public async Task ASlowDiskDropsSamplesWithCountAndLossMarkNotSilently()
    {
        var storage = new MemoryStorage { WriteGate = new ManualResetEventSlim(true) };
        var (writer, _, _, recorder) = Create(storage: storage, tune: b => { b.Queue = 16; b.Compress = false; });
        var faults = new List<RecordingFault>(); writer.Faulted += faults.Add;
        recorder.Start(false);
        recorder.Add(TestSamples.Make(0)); await writer.FlushAsync();
        storage.WriteGate.Reset();                   // from now on the disk blocks
        for (var i = 1; i <= 100; i++) recorder.Add(TestSamples.Make(i)); // the writer is blocked, the queue (16) overflows
        var lost = recorder.SinkStatus!.SamplesLost;
        Assert.True(lost > 50, $"lost {lost}");
        Assert.Contains(faults, f => f.Kind == RecordingFaultKind.QueueOverflow);
        storage.WriteGate.Set();
        recorder.Add(TestSamples.Make(101));
        await recorder.StopAsync();
        var status = recorder.SinkStatus!;
        Assert.Equal(102, status.SamplesWritten + status.SamplesLost);
        Assert.Contains(Lines(storage).Where(l => l.Type == "mark"), l => l.Root.GetProperty("kind").GetString() == "DataLoss");
    }

    [Fact]
    public async Task RecordingStopsBeforeFillingTheVolume()
    {
        var (writer, storage, _, recorder) = Create(b => { b.Segment = 8_000; b.MinFree = 5 << 20; b.Compress = false; });
        var ended = new List<RecordingEndReason>(); recorder.Ended += ended.Add;
        var faults = new List<RecordingFault>(); writer.Faulted += faults.Add;
        storage.Free = 6 << 20;
        recorder.Start(false);
        storage.Free = 4 << 20;                      // another process filled the disk
        for (var i = 0; i < 20 && recorder.IsRecording; i++) { recorder.Add(TestSamples.Make(i)); await writer.FlushAsyncSafe(); }
        for (var wait = 0; wait < 50 && recorder.IsRecording; wait++) await Task.Delay(20);
        Assert.False(recorder.IsRecording);
        Assert.Contains(faults, f => f is { Kind: RecordingFaultKind.LowDiskSpace, IsFatal: true });
        Assert.Equal([RecordingEndReason.StorageError], ended);
        // The stopped recording is finalized and readable.
        Assert.True(Assert.Single(RecordingCatalog.Scan(storage, Dir)).Samples >= 1);
    }

    // ---------- quota and retention ----------

    private static void AddRecording(MemoryStorage storage, string id, DateTimeOffset when, int bytes)
    {
        var started = when;
        var path = Path.Combine(Dir, $"EucLens-{when.UtcDateTime:yyyyMMdd-HHmmss}-{id}-part001.jsonl");
        storage.Files[path] = new byte[bytes]; storage.Times[path] = when;
        var meta = Path.Combine(Dir, $"EucLens-{when.UtcDateTime:yyyyMMdd-HHmmss}-{id}.meta.json");
        storage.Files[meta] = Encoding.UTF8.GetBytes("{}"); storage.Times[meta] = when;
        _ = started;
    }

    [Fact]
    public void RetentionRemovesOldestRecordingsFirstAndNeverTouchesUnrelatedFilesOrTheActiveRecording()
    {
        var storage = new MemoryStorage();
        var now = storage.Now;
        AddRecording(storage, "aaaaaaaa", now.AddDays(-5), 400_000);
        AddRecording(storage, "bbbbbbbb", now.AddDays(-3), 400_000);
        AddRecording(storage, "cccccccc", now.AddDays(-1), 400_000);
        AddRecording(storage, "dddddddd", now.AddMinutes(-5), 400_000);  // the active one
        var notes = Path.Combine(Dir, "notes.txt"); storage.Files[notes] = new byte[5_000_000]; storage.Times[notes] = now.AddDays(-400);
        var lookalike = Path.Combine(Dir, "EucLens-backup.zip"); storage.Files[lookalike] = new byte[1000]; storage.Times[lookalike] = now.AddDays(-400);

        var result = RecordingRetention.Apply(storage, Dir, allowedBytes: 900_000, maxAge: null, now, activeId: "dddddddd");
        Assert.Equal(2, result.RecordingsDeleted);
        var remaining = storage.Names(Dir);
        Assert.DoesNotContain(remaining, n => n.Contains("aaaaaaaa") || n.Contains("bbbbbbbb"));
        Assert.Contains(remaining, n => n.Contains("cccccccc"));
        Assert.Contains(remaining, n => n.Contains("dddddddd"));
        Assert.Contains("notes.txt", remaining);               // unrelated files are never deleted
        Assert.Contains("EucLens-backup.zip", remaining);

        // Age limit: the old recording goes even though the quota is not exceeded.
        var aged = RecordingRetention.Apply(storage, Dir, allowedBytes: long.MaxValue, maxAge: TimeSpan.FromHours(12), now, activeId: "dddddddd");
        Assert.Equal(1, aged.RecordingsDeleted);
        Assert.DoesNotContain(storage.Names(Dir), n => n.Contains("cccccccc"));
        // The active recording is protected even when the quota is too small for it.
        RecordingRetention.Apply(storage, Dir, allowedBytes: 0, maxAge: TimeSpan.Zero, now.AddYears(1), activeId: "dddddddd");
        Assert.Contains(storage.Names(Dir), n => n.Contains("dddddddd"));
    }

    [Fact]
    public async Task StartingARecordingMakesRoomWithinTheQuotaAndAQuotaTooSmallEndsItExplicitly()
    {
        var storage = new MemoryStorage();
        AddRecording(storage, "11111111", storage.Now.AddDays(-2), 700_000);
        AddRecording(storage, "22222222", storage.Now.AddDays(-1), 700_000);
        var (writer, _, _, recorder) = Create(b => { b.Quota = 1_000_000; b.Segment = 100_000; b.Compress = false; }, storage);
        var ended = new List<RecordingEndReason>(); recorder.Ended += ended.Add;
        var faults = new List<RecordingFault>(); writer.Faulted += faults.Add;
        recorder.Start(false);
        Assert.DoesNotContain(storage.Names(Dir), n => n.Contains("11111111")); // the oldest was removed to make room
        // The recording alone outgrows the quota: it stops with an explicit reason instead of deleting its own data or the disk.
        for (var i = 0; i < 600 && recorder.IsRecording; i++) { recorder.Add(TestSamples.Make(i)); if (i % 20 == 0) await writer.FlushAsyncSafe(); }
        for (var wait = 0; wait < 100 && recorder.IsRecording; wait++) await Task.Delay(20);
        Assert.False(recorder.IsRecording);
        Assert.Equal([RecordingEndReason.QuotaReached], ended);
        Assert.Contains(faults, f => f is { Kind: RecordingFaultKind.QuotaReached, IsFatal: true });
        Assert.Contains(Lines(storage), l => l.Type == "footer" && l.Root.GetProperty("reason").GetString() == "QuotaReached");
    }

    // ---------- export compatibility ----------

    [Fact]
    public async Task ExportsFromAPersistedRecordingAreIdenticalToExportsOfTheSameSamplesInMemory()
    {
        var samples = Enumerable.Range(0, 25).Select(i => TestSamples.Make(i, blast: i % 6 != 5, withEvent: i == 4)).ToArray();
        var memory = new SessionRecorder();
        memory.Start(); foreach (var s in samples) memory.Add(s); memory.Stop();
        var (_, storage, _, persisted) = Create(b => b.Segment = 9_000);
        persisted.Start(false); foreach (var s in samples) persisted.Add(s);
        await persisted.StopAsync();
        Assert.True(storage.Names(Dir).Count(n => n.Contains("-part")) > 1);

        var directory = Path.Combine(Path.GetTempPath(), "euclens-export-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            await memory.ExportJsonAsync(Path.Combine(directory, "m.json")); await persisted.ExportJsonAsync(Path.Combine(directory, "p.json"));
            await memory.ExportCsvAsync(Path.Combine(directory, "m.csv")); await persisted.ExportCsvAsync(Path.Combine(directory, "p.csv"));
            Assert.Equal(File.ReadAllText(Path.Combine(directory, "m.csv")), File.ReadAllText(Path.Combine(directory, "p.csv")));
            Assert.Equal(File.ReadAllText(Path.Combine(directory, "m.json")), File.ReadAllText(Path.Combine(directory, "p.json")));
            Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
        }
        finally { Directory.Delete(directory, true); }
    }

    // ---------- long sessions ----------

    [Fact]
    public async Task AcceleratedEightHourSessionRotatesCompressesAndStaysBounded()
    {
        // 28,800 samples at 1 Hz on a fake clock: an accelerated simulation, not a real eight-hour run.
        var (writer, storage, clock, recorder) = Create(b => { b.Segment = 1 << 20; b.Quota = 256L << 20; b.Queue = 900; });
        var coordinator = new MonitoringCoordinator(clock);
        var service = new MonitoringService(coordinator, recorder, clock: clock);
        recorder.Start(false);
        var written = 0;
        for (var second = 0; second < 8 * 3600; second++)
        {
            clock.Set(TestSamples.T0.AddSeconds(second));
            var transition = false;
            if (second == 2 * 3600) { service.PauseMonitoring(); transition = true; }
            if (second == 2 * 3600 + 300) { service.ResumeMonitoring(); transition = true; }
            if (second == 5 * 3600) { service.OnSessionEvent(SessionEventKind.RemoteDisconnected); transition = true; }
            if (second == 5 * 3600 + 120) { service.OnSessionEvent(SessionEventKind.RemoteReconnected); transition = true; }
            // Pacing: a real writer drains between 1 s ticks, this loop does not wait for it. The checkpoint therefore sits BEFORE the paused-state
            // `continue` (a flush that falls in a pause must not be skipped, or about 900 samples pile up across the pause and the outcome depends on
            // how fast the writer thread happens to run), and a state transition is flushed as well. The queue depth is then bounded by this test, not by CPU speed.
            if (transition || second % 600 == 0) await writer.FlushAsync();
            if (coordinator.State != MonitoringState.Running) continue;
            var sample = TestSamples.Make(second, clock.GetUtcNow(), blast: !(second >= 5 * 3600 && second < 5 * 3600 + 120), withEvent: second % 5400 == 100);
            recorder.Add(sample); written++;
            service.OnSample(sample, null);
        }
        await recorder.StopAsync();

        Assert.Equal(written, recorder.SampleCount);
        Assert.Equal(written, (int)recorder.SinkStatus!.SamplesWritten + (int)recorder.SinkStatus.SamplesLost);
        Assert.Equal(0, recorder.SinkStatus.SamplesLost);            // paced as a real writer is: nothing may be dropped here (overload is covered by its own tests)
        Assert.Equal(SessionRecorder.PersistedTailSamples, recorder.Samples.Count);
        var summary = Assert.Single(RecordingCatalog.Scan(storage, Dir));
        Assert.True(summary.IsComplete);
        Assert.Equal(recorder.SinkStatus.SamplesWritten, summary.Samples);
        Assert.True(summary.Parts.Count > 3, $"parts {summary.Parts.Count}");
        Assert.True(summary.Duration > TimeSpan.FromHours(7.9));
        Assert.Equal(6, summary.Warnings); // one finding every 90 minutes (100 s, 5,500 s, ...), counted from the recorded events
        var kinds = Kinds(Lines(storage).Where(l => l.Type == "mark").Select(l => l.Root));
        Assert.Equal(new[] { "Paused", "Resumed", "RemoteDisconnected", "Availability", "RemoteReconnected", "Availability" }, kinds);
        Assert.Equal(summary.Samples, writer.ReadSamples().LongCount());
        Assert.True(storage.TotalBytes < 40L << 20, $"{storage.TotalBytes >> 20} MiB on disk after compression");
    }

    [Fact]
    public async Task ManyShortRecordingsStayWithinTheQuotaAcrossTheSimulatedWeek()
    {
        var storage = new MemoryStorage();
        var clock = new FakeClock(storage.Now);
        const long quota = 3L << 20;
        for (var day = 0; day < 12; day++)
        {
            storage.Now = TestSamples.T0.AddDays(day); clock.Set(storage.Now);
            var writer = new RecordingWriter(() => Options(b => { b.Quota = quota; b.Segment = 400_000; b.MaxAge = TimeSpan.FromDays(7); }), storage, clock);
            var recorder = new SessionRecorder(writer, clock);
            recorder.Start(false);
            for (var i = 0; i < 600; i++) { recorder.Add(TestSamples.Make(i, storage.Now.AddSeconds(i))); if (i % 100 == 99) await writer.FlushAsync(); }
            await recorder.StopAsync();
            Assert.True(storage.TotalBytes <= quota + 400_000, $"day {day}: {storage.TotalBytes} bytes");
        }
        var listed = RecordingCatalog.Scan(storage, Dir);
        Assert.InRange(listed.Count, 1, 8); // old recordings were removed by age and quota, the newest is always there
        Assert.True(listed[0].StartedUtc >= TestSamples.T0.AddDays(11));
    }
}

internal static class WriterTestExtensions
{
    /// <summary>Waits for the writer to process what is queued even when it is faulted (a flush then does not reach the disk).</summary>
    public static async Task FlushAsyncSafe(this RecordingWriter writer)
    {
        try { await writer.FlushAsync().WaitAsync(TimeSpan.FromSeconds(5)); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TimeoutException) { }
    }
}
