using System.Text.Json;
using EucLens.Core;
using Xunit;

namespace EucLens.Tests;

/// <summary>User-marked performance issues: persistence, identity, ordering, failure reporting, rotation and compatibility with 0.4.3 files.</summary>
public sealed class IncidentTests
{
    internal static readonly string Dir = Path.Combine(Path.GetTempPath(), "euclens-incident-test");

    internal static RecordingOptions Options(long segment = 64L << 20, bool compress = true) => new()
    {
        Directory = Dir, QuotaBytes = 512L << 20, MaxAge = TimeSpan.FromDays(14), SegmentBytes = segment, FlushInterval = TimeSpan.FromSeconds(5), QueueCapacity = 900,
        MinimumFreeBytes = 1 << 20, CompressClosedParts = compress, RetryInterval = TimeSpan.FromSeconds(5), GiveUpAfter = TimeSpan.FromSeconds(60)
    };

    internal static (RecordingWriter Writer, MemoryStorage Storage, FakeClock Clock, SessionRecorder Recorder) Create(long segment = 64L << 20, bool compress = true, MemoryStorage? storage = null)
    {
        storage ??= new();
        var clock = new FakeClock(storage.Now);
        var writer = new RecordingWriter(() => Options(segment, compress), storage, clock);
        return (writer, storage, clock, new SessionRecorder(writer, clock));
    }

    internal static IEnumerable<(string Type, JsonElement Root)> Lines(MemoryStorage storage) =>
        storage.Names(Dir).Where(n => RecordingFormat.TryParsePart(n, out _, out _, out _)).OrderBy(n => n, StringComparer.Ordinal)
            .SelectMany(n => RecordingFormat.ReadLines(storage, Path.Combine(Dir, n)));

    private static List<JsonElement> Incidents(MemoryStorage storage) =>
        Lines(storage).Where(l => l.Type == "mark" && l.Root.GetProperty("kind").GetString() == "UserIncident").Select(l => l.Root).ToList();

    [Fact]
    public async Task AnIncidentIsWrittenAsAStructuredMarkWithIdSequenceAndTheClocksUtcTime()
    {
        var (_, storage, clock, recorder) = Create(compress: false);
        recorder.Start(false);
        for (var i = 0; i < 3; i++) recorder.Add(TestSamples.Make(i));
        clock.Set(TestSamples.T0.AddSeconds(3.5));

        var result = await recorder.MarkIncidentAsync();

        Assert.True(result.Saved);
        Assert.Equal(IncidentOutcome.Saved, result.Outcome);
        Assert.Equal(1, result.Marker!.Sequence);
        Assert.Equal(TestSamples.T0.AddSeconds(3.5), result.Marker.At);
        Assert.Equal(TimeSpan.Zero, result.Marker.At.Offset); // UTC
        await recorder.StopAsync();
        var mark = Assert.Single(Incidents(storage));
        Assert.Equal(result.Marker.Id.ToString("D"), mark.GetProperty("id").GetString());
        Assert.Equal(1, mark.GetProperty("seq").GetInt32());
        Assert.Equal(result.Marker.At, mark.GetProperty("at").GetDateTimeOffset());
        Assert.Equal("Performance issue marked by the user.", mark.GetProperty("detail").GetString());
        // Distinguishable from ordinary notes and diagnostic findings, and carrying nothing personal.
        Assert.Equal("UserIncident", mark.GetProperty("kind").GetString());
        Assert.Equal(["at", "detail", "id", "kind", "seq", "t"], mark.EnumerateObject().Select(p => p.Name).Order().ToArray());
        // Nothing from the session identity (user name, client or server host name) is copied into the marker.
        var raw = mark.GetRawText();
        Assert.DoesNotContain(TestSamples.Session.ClientHostname, raw, StringComparison.Ordinal);
        Assert.DoesNotContain(TestSamples.Session.ServerHostname, raw, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheIncidentSitsBetweenTheSamplesCollectedBeforeAndAfterIt()
    {
        var (_, storage, clock, recorder) = Create(compress: false);
        recorder.Start(false);
        for (var i = 0; i < 5; i++) recorder.Add(TestSamples.Make(i));
        clock.Set(TestSamples.T0.AddSeconds(4.5));
        Assert.True((await recorder.MarkIncidentAsync()).Saved);
        for (var i = 5; i < 9; i++) recorder.Add(TestSamples.Make(i));
        await recorder.StopAsync();

        var order = Lines(storage).Where(l => l.Type is "s" or "mark").Select(l => l.Type == "s" ? "s" + l.Root.GetProperty("ts").GetDateTimeOffset().ToUnixTimeSeconds() % 100 : "incident").ToArray();
        var index = Array.IndexOf(order, "incident");
        Assert.Equal(5, index);                                   // five samples, then the marker, then the rest
        Assert.Equal(10, order.Length);
        // File order and time order agree.
        var times = Lines(storage).Where(l => l.Type is "s" or "mark").Select(l => l.Root.TryGetProperty("ts", out var ts) ? ts.GetDateTimeOffset() : l.Root.GetProperty("at").GetDateTimeOffset()).ToArray();
        Assert.Equal(times.Order().ToArray(), times);
    }

    [Fact]
    public async Task EveryIncidentGetsItsOwnStableIdAndTheIdsSurviveReadingTheFileAgain()
    {
        var (_, storage, clock, recorder) = Create(compress: false);
        recorder.Start(false);
        var markers = new List<IncidentMarker>();
        for (var i = 0; i < 60; i++)
        {
            recorder.Add(TestSamples.Make(i));
            clock.Advance(TimeSpan.FromSeconds(1));
            markers.Add((await recorder.MarkIncidentAsync()).Marker!);
        }
        await recorder.StopAsync();

        Assert.Equal(60, markers.Select(m => m.Id).Distinct().Count());
        Assert.Equal(Enumerable.Range(1, 60), markers.Select(m => m.Sequence));
        var written = Incidents(storage);
        Assert.Equal(markers.Select(m => m.Id.ToString("D")), written.Select(w => w.GetProperty("id").GetString()));
        Assert.Equal(markers.Select(m => m.At), written.Select(w => w.GetProperty("at").GetDateTimeOffset()));
    }

    [Fact]
    public async Task MultipleRapidIncidentsFromSeveralThreadsKeepSequenceAndFileOrder()
    {
        var (_, storage, clock, recorder) = Create(compress: false);
        recorder.Start(false);
        recorder.Add(TestSamples.Make(0));
        var results = await Task.WhenAll(Enumerable.Range(0, 80).Select(_ => Task.Run(() => recorder.MarkIncidentAsync())));
        await recorder.StopAsync();

        Assert.All(results, r => Assert.True(r.Saved));
        Assert.Equal(80, results.Select(r => r.Marker!.Id).Distinct().Count());
        var written = Incidents(storage);
        Assert.Equal(80, written.Count);
        // Queued under one lock: the file holds them exactly in sequence order.
        Assert.Equal(Enumerable.Range(1, 80), written.Select(w => w.GetProperty("seq").GetInt32()));
        Assert.Equal(80, Assert.Single(RecordingCatalog.Scan(storage, Dir)).Incidents);
    }

    [Fact]
    public async Task WithoutARunningRecordingNothingIsWrittenAndTheReasonIsExplained()
    {
        var (_, storage, _, recorder) = Create(compress: false);
        var idle = await recorder.MarkIncidentAsync();
        Assert.Equal(IncidentOutcome.NoRecording, idle.Outcome);
        Assert.Null(idle.Marker);
        Assert.Contains("Start a recording", idle.Message, StringComparison.Ordinal);
        Assert.Empty(storage.Names(Dir));

        var inMemory = new SessionRecorder();
        inMemory.Start();
        var result = await inMemory.MarkIncidentAsync();
        Assert.Equal(IncidentOutcome.NotPersistent, result.Outcome);
        Assert.Null(result.Marker);
        Assert.Contains("saved to disk", result.Message, StringComparison.Ordinal);

        recorder.Start(false);
        await recorder.StopAsync();
        Assert.Equal(IncidentOutcome.NoRecording, (await recorder.MarkIncidentAsync()).Outcome); // stopped again
    }

    [Fact]
    public async Task AFailingDiskIsReportedAsAFailureNeverAsSavedAndRecordingRecoversLater()
    {
        var (writer, storage, clock, recorder) = Create(compress: false);
        recorder.Start(false);
        recorder.Add(TestSamples.Make(0));
        await writer.FlushAsync();

        // 1. The disk fails exactly while the marker is written.
        storage.WriteFault = new IOException("There is not enough space on the disk.", unchecked((int)0x80070070));
        var first = await recorder.MarkIncidentAsync();
        Assert.Equal(IncidentOutcome.StorageFailed, first.Outcome);
        Assert.Null(first.Marker);
        Assert.False(first.Saved);
        Assert.DoesNotContain("saved at", first.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("could not be saved", first.Message, StringComparison.Ordinal);
        Assert.Equal(RecordingSinkState.Faulted, recorder.SinkStatus!.State);   // the data-loss state is visible

        // 2. The writer already knows the disk is failing: still a failure, with the reason.
        var second = await recorder.MarkIncidentAsync();
        Assert.Equal(IncidentOutcome.StorageFailed, second.Outcome);
        Assert.Contains("not writable", second.Message, StringComparison.Ordinal);

        // 3. Space is freed; the next samples reopen the file and the next marker is saved.
        storage.WriteFault = null;
        clock.Advance(TimeSpan.FromSeconds(10));
        recorder.Add(TestSamples.Make(1)); recorder.Add(TestSamples.Make(2));
        await writer.FlushAsyncSafe();
        var third = await recorder.MarkIncidentAsync();
        Assert.True(third.Saved);
        await recorder.StopAsync();
        var kept = Assert.Single(Incidents(storage));
        Assert.Equal(third.Marker!.Id.ToString("D"), kept.GetProperty("id").GetString());
        Assert.Contains(Lines(storage), l => l.Type == "mark" && l.Root.GetProperty("kind").GetString() == "DataLoss");
    }

    [Fact]
    public async Task AnIncidentQueuedBeforeStopIsWrittenBeforeTheRecordingIsFinalized()
    {
        var (_, storage, _, recorder) = Create(compress: false);
        recorder.Start(false);
        recorder.Add(TestSamples.Make(0));
        using var gate = new ManualResetEventSlim(false);
        storage.WriteGate = gate;                                  // the writer thread is stuck on the disk
        var pending = recorder.MarkIncidentAsync();
        var stop = recorder.StopAsync();                           // the recording is finalized after the marker in queue order
        gate.Set();
        var result = await pending; await stop;
        storage.WriteGate = null;
        Assert.True(result.Saved);                                 // it was queued first, so it is written first
        Assert.Single(Incidents(storage));
    }

    [Fact]
    public async Task IncidentsLandInTheRightPartAcrossRotationAndAreCountedInFooterMetaAndCatalog()
    {
        var (_, storage, clock, recorder) = Create(segment: 9_000);
        recorder.Start(false);
        var markers = new List<IncidentMarker>();
        for (var i = 0; i < 40; i++)
        {
            recorder.Add(TestSamples.Make(i));
            if (i % 7 == 3) { clock.Set(TestSamples.T0.AddSeconds(i + .5)); markers.Add((await recorder.MarkIncidentAsync()).Marker!); }
        }
        await recorder.StopAsync();

        Assert.True(storage.Names(Dir).Count(n => n.Contains("-part")) > 3, "the recording rotated");
        Assert.Equal(markers.Select(m => m.Id.ToString("D")), Incidents(storage).Select(m => m.GetProperty("id").GetString()));
        var summary = Assert.Single(RecordingCatalog.Scan(storage, Dir));
        Assert.Equal(markers.Count, summary.Incidents);
        Assert.True(summary.IsComplete);
        var footer = Lines(storage).Last(l => l.Type == "footer").Root;
        Assert.Equal(markers.Count, footer.GetProperty("incidents").GetInt32());
        var meta = storage.Names(Dir).Single(n => n.EndsWith(".meta.json", StringComparison.Ordinal));
        using var document = JsonDocument.Parse(storage.OpenRead(Path.Combine(Dir, meta)));
        Assert.Equal(markers.Count, document.RootElement.GetProperty("incidents").GetInt32());
    }

    [Fact]
    public async Task AnInterruptedRecordingStillCountsItsIncidents()
    {
        var (writer, storage, clock, recorder) = Create(segment: 9_000, compress: false);
        recorder.Start(false);
        for (var i = 0; i < 30; i++)
        {
            recorder.Add(TestSamples.Make(i));
            if (i % 5 == 4) { clock.Set(TestSamples.T0.AddSeconds(i + .5)); Assert.True((await recorder.MarkIncidentAsync()).Saved); }
        }
        await writer.FlushAsync();
        // Simulate a crash: take what is on disk now, without footer or final meta, into a fresh storage.
        var crashed = new MemoryStorage();
        foreach (var name in storage.Names(Dir).Where(n => n.Contains("-part", StringComparison.Ordinal)))
            crashed.Files[Path.Combine(Dir, name)] = storage.OpenRead(Path.Combine(Dir, name)) is { } s ? ReadAll(s) : [];
        foreach (var path in crashed.Files.Keys.ToArray()) crashed.Times[path] = storage.Now;
        await recorder.StopAsync();
        var last = crashed.Files.Keys.OrderBy(k => k, StringComparer.Ordinal).Last();
        crashed.Files[last] = crashed.Files[last][..^20];          // a torn last line

        var summary = Assert.Single(RecordingCatalog.Scan(crashed, Dir));
        Assert.False(summary.IsComplete);
        Assert.Equal("Interrupted", summary.EndReason);
        Assert.True(summary.Incidents is >= 5 and <= 6, $"incidents {summary.Incidents}");
        static byte[] ReadAll(Stream s) { using (s) { using var m = new MemoryStream(); s.CopyTo(m); return m.ToArray(); } }
    }

    [Fact]
    public void RecordingsFromTheEarlierVersionHaveNoIncidentsAndStayValid()
    {
        var storage = new MemoryStorage();
        var started = TestSamples.T0;
        const string id = "0a1b2c3d";
        var stamp = RecordingFormat.Stamp(started);
        // A 0.4.3 meta file: no "incidents" property.
        var meta = $$"""{"schema":"euclens.recording/1","id":"0a1b2c3d-0000-0000-0000-000000000000","startedUtc":"{{started.UtcDateTime:O}}","endedUtc":"{{started.AddMinutes(5).UtcDateTime:O}}","complete":true,"reason":"UserStopped","source":"RealSession","parts":1,"samples":300,"warnings":2,"criticals":0,"lost":0,"bytes":123456}""";
        storage.Files[Path.Combine(Dir, $"EucLens-{stamp}-{id}.meta.json")] = System.Text.Encoding.UTF8.GetBytes(meta);
        storage.Files[Path.Combine(Dir, $"EucLens-{stamp}-{id}-part001.jsonl")] = System.Text.Encoding.UTF8.GetBytes("{\"t\":\"header\",\"schema\":\"euclens.recording/1\",\"id\":\"0a1b2c3d-0000-0000-0000-000000000000\",\"part\":1,\"startedUtc\":\"" + started.UtcDateTime.ToString("O") + "\",\"source\":\"RealSession\"}\n");
        foreach (var path in storage.Files.Keys.ToArray()) storage.Times[path] = started;

        var summary = Assert.Single(RecordingCatalog.Scan(storage, Dir));
        Assert.Equal(0, summary.Incidents);
        Assert.True(summary.IsComplete);
        Assert.Equal(300, summary.Samples);
    }
}
