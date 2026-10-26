using System.Text.Json;
using EucLens.Core;
using Xunit;

namespace EucLens.Tests;

/// <summary>
/// Failure injection for what the writer promises: an incident marker that was confirmed to the user stays in the recording whatever happens to compression,
/// finalization or the disk afterwards, and a marker is never reported as definitely lost when it may still be written.
/// </summary>
public sealed class RecordingDurabilityTests
{
    private static readonly string Dir = IncidentTests.Dir;

    private static (RecordingWriter Writer, MemoryStorage Storage, FakeClock Clock, SessionRecorder Recorder, List<RecordingFault> Faults) Create(MemoryStorage? storage = null,
        bool compress = false, TimeSpan? markTimeout = null, int queue = 900)
    {
        storage ??= new();
        var clock = new FakeClock(storage.Now);
        var writer = new RecordingWriter(() => IncidentTests.Options(compress: compress) with { DurableMarkTimeout = markTimeout ?? TimeSpan.FromSeconds(10), QueueCapacity = queue }, storage, clock);
        var faults = new List<RecordingFault>();
        writer.Faulted += f => { lock (faults) faults.Add(f); };
        return (writer, storage, clock, new SessionRecorder(writer, clock), faults);
    }

    private static List<JsonElement> Incidents(MemoryStorage storage) =>
        IncidentTests.Lines(storage).Where(l => l.Type == "mark" && l.Root.GetProperty("kind").GetString() == "UserIncident").Select(l => l.Root).ToList();

    private static IOException Io(string message = "The disk reported an error.") => new(message);

    /// <summary>Records a few samples and one confirmed incident, flushes, and returns the confirmed marker.</summary>
    private static async Task<IncidentMarker> RecordWithConfirmedIncidentAsync(SessionRecorder recorder, RecordingWriter writer, int samples = 5)
    {
        recorder.Start(false);
        for (var i = 0; i < samples; i++) recorder.Add(TestSamples.Make(i));
        var result = await recorder.MarkIncidentAsync();
        Assert.True(result.Saved);
        await writer.FlushAsync();
        return result.Marker!;
    }

    // ---------------------------------------------------------------- compression

    public static TheoryData<string> CompressionFailures => new() { "the compressed file cannot be created", "writing the compressed copy fails", "flushing the compressed copy fails", "the compressed copy is cut short", "renaming it into place fails" };

    [Theory]
    [MemberData(nameof(CompressionFailures))]
    public async Task AFailedCompressionKeepsThePlainPartAndEveryConfirmedIncident(string failure)
    {
        var (writer, storage, _, recorder, _) = Create(compress: true);
        var marker = await RecordWithConfirmedIncidentAsync(recorder, writer);
        bool Gz(string path) => path.EndsWith(".gz.partial", StringComparison.Ordinal);
        switch (failure)
        {
            case "the compressed file cannot be created": storage.CreateFault = p => Gz(p) ? Io() : null; break;
            case "writing the compressed copy fails": storage.WriteFaultFor = p => Gz(p) ? Io() : null; break;
            case "flushing the compressed copy fails": storage.FlushFaultFor = p => Gz(p) ? Io() : null; break;
            case "the compressed copy is cut short": storage.TruncateOnFlush = Gz; break;
            case "renaming it into place fails": storage.RenameFault = (from, _) => Gz(from) ? Io() : null; break;
        }

        await recorder.StopAsync();

        var names = storage.Names(Dir);
        Assert.Contains(names, n => n.EndsWith("-part001.jsonl", StringComparison.Ordinal));          // the original is still there
        Assert.DoesNotContain(names, n => n.EndsWith(".gz", StringComparison.Ordinal));               // no half-written compressed copy was put in its place
        Assert.DoesNotContain(names, n => n.EndsWith(".partial", StringComparison.Ordinal) && n.Contains("part", StringComparison.Ordinal));
        var kept = Assert.Single(Incidents(storage));
        Assert.Equal(marker.Id.ToString("D"), kept.GetProperty("id").GetString());
        var history = HistoryFixture.Load(storage, RecordingFormat.ShortIdOf(names.First(n => n.Contains("part001", StringComparison.Ordinal))));
        Assert.Equal(marker.Id, Assert.Single(history.Incidents).Id);
        Assert.Equal(RecordingCompleteness.Complete, history.Info.State);
    }

    [Fact]
    public async Task ACompressionThatSucceedsReplacesThePlainPartOnlyAfterTheCopyWasReadBackCompletely()
    {
        var (writer, storage, _, recorder, _) = Create(compress: true);
        var marker = await RecordWithConfirmedIncidentAsync(recorder, writer);
        var order = new List<string>();
        storage.RenameFault = (_, to) => { order.Add("rename " + to); return null; };
        storage.DeleteFault = p => { order.Add("delete " + p); return null; };

        await recorder.StopAsync();

        Assert.Contains(storage.Names(Dir), n => n.EndsWith("-part001.jsonl.gz", StringComparison.Ordinal));
        Assert.DoesNotContain(storage.Names(Dir), n => n.EndsWith("-part001.jsonl", StringComparison.Ordinal));
        Assert.True(order.FindIndex(o => o.StartsWith("rename ", StringComparison.Ordinal) && o.EndsWith(".jsonl.gz", StringComparison.Ordinal)) is var renamed and >= 0
            && order.FindIndex(o => o.StartsWith("delete ", StringComparison.Ordinal) && o.EndsWith(".jsonl", StringComparison.Ordinal)) > renamed, "the plain part was removed before the compressed copy was in place: " + string.Join("; ", order));
        Assert.Equal(marker.Id.ToString("D"), Assert.Single(Incidents(storage)).GetProperty("id").GetString());
    }

    [Fact]
    public async Task ACrashBetweenRenameAndDeleteLeavesBothFilesAndTheRecordingReadsOnceWithItsIncident()
    {
        var (writer, storage, _, recorder, _) = Create(compress: true);
        var marker = await RecordWithConfirmedIncidentAsync(recorder, writer);
        storage.DeleteFault = p => p.EndsWith(".jsonl", StringComparison.Ordinal) ? Io() : null;     // the plain part could not be removed
        await recorder.StopAsync();
        storage.DeleteFault = null;
        var plain = storage.Names(Dir).Single(n => n.EndsWith("-part001.jsonl", StringComparison.Ordinal));
        Assert.Contains(storage.Names(Dir), n => n.EndsWith("-part001.jsonl.gz", StringComparison.Ordinal));
        // A leftover of an interrupted compression is not mistaken for a part.
        storage.Files[Path.Combine(Dir, plain + ".gz.partial")] = [1, 2, 3]; storage.Times[Path.Combine(Dir, plain + ".gz.partial")] = storage.Now;

        var shortId = RecordingFormat.ShortIdOf(plain);
        var history = HistoryFixture.Load(storage, shortId);

        Assert.Equal(1, history.Info.Parts);
        Assert.Equal(marker.Id, Assert.Single(history.Incidents).Id);
        Assert.Equal(5, history.SampleCount);
        Assert.Equal(1, Assert.Single(RecordingCatalog.Scan(storage, Dir)).Incidents);
    }

    // ---------------------------------------------------------------- finalization

    public static TheoryData<string> FinalizationFailures => new() { "footer write", "flush", "close" };

    [Theory]
    [MemberData(nameof(FinalizationFailures))]
    public async Task AFailureWhileFinalizingIsReportedReleasesTheFileAndKeepsTheConfirmedIncident(string failure)
    {
        var (writer, storage, _, recorder, faults) = Create();
        var marker = await RecordWithConfirmedIncidentAsync(recorder, writer);
        switch (failure)
        {
            case "footer write": storage.WriteFault = Io("The disk is full."); break;
            case "flush": storage.FlushFaultFor = p => p.Contains("part001", StringComparison.Ordinal) ? Io() : null; break;
            case "close": storage.DisposeFaultFor = p => p.Contains("part001", StringComparison.Ordinal) ? Io() : null; break;
        }

        await recorder.StopAsync().WaitAsync(TimeSpan.FromSeconds(20));                  // completes: nothing waits for a writer that has stopped

        RecordingFault fault;
        lock (faults) fault = Assert.Single(faults, f => f.IsFatal);                     // reported through the fault mechanism, once
        Assert.Contains("could not be finalized", fault.Message, StringComparison.Ordinal);
        Assert.Equal(RecordingSinkState.Idle, writer.Status.State);
        Assert.Equal(0, storage.OpenHandles);                                            // the handle was released
        Assert.Equal(fault, writer.Status.LastFault);
        storage.WriteFault = null; storage.FlushFaultFor = null; storage.DisposeFaultFor = null;
        // A footer that did not reach the disk means no "complete" index; the incident that was confirmed earlier is in the part.
        var meta = storage.Names(Dir).FirstOrDefault(n => n.EndsWith(".meta.json", StringComparison.Ordinal));
        if (meta is not null) Assert.DoesNotContain("\"complete\": true", System.Text.Encoding.UTF8.GetString(storage.Files[Path.Combine(Dir, meta)]), StringComparison.Ordinal);
        Assert.Equal(marker.Id.ToString("D"), Assert.Single(Incidents(storage)).GetProperty("id").GetString());
        var shortId = RecordingFormat.ShortIdOf(storage.Names(Dir).First(n => n.Contains("part001", StringComparison.Ordinal)));
        Assert.Equal(marker.Id, Assert.Single(HistoryFixture.Load(storage, shortId).Incidents).Id);
    }

    [Fact]
    public async Task AFooterThatCouldNotBeWrittenLeavesAnInterruptedRecordingNotACompleteOne()
    {
        var (writer, storage, _, recorder, _) = Create();
        await RecordWithConfirmedIncidentAsync(recorder, writer);
        storage.FlushFaultFor = p => p.Contains("part001", StringComparison.Ordinal) ? Io() : null;
        await recorder.StopAsync();
        storage.FlushFaultFor = null;
        var shortId = RecordingFormat.ShortIdOf(storage.Names(Dir).First(n => n.Contains("part001", StringComparison.Ordinal)));
        var summary = RecordingCatalog.Scan(storage, Dir).Single(s => s.ShortId == shortId);
        Assert.False(summary.IsComplete);
        Assert.Equal(1, summary.Incidents);
    }

    // ---------------------------------------------------------------- durable marks that are slow

    [Fact]
    public async Task AMarkThatCouldNotEvenBeQueuedIsDefinitelyNotSavedAndNeverWrittenLater()
    {
        var (writer, storage, _, recorder, _) = Create(markTimeout: TimeSpan.FromMilliseconds(300), queue: 16);
        recorder.Start(false);
        recorder.Add(TestSamples.Make(0)); await writer.FlushAsync();
        var gate = storage.WriteGate = new ManualResetEventSlim(false);                 // the disk stops accepting writes
        for (var i = 1; i < 60; i++) recorder.Add(TestSamples.Make(i));                  // the writer blocks on the first; the queue (16) fills; the rest is counted as lost

        var result = await recorder.MarkIncidentAsync();

        Assert.Equal(IncidentOutcome.StorageFailed, result.Outcome);                      // definite: it was never queued
        Assert.Null(result.Marker);
        Assert.Contains("not saved", result.Message + " not saved", StringComparison.OrdinalIgnoreCase);
        gate.Set(); storage.WriteGate = null;
        await writer.FlushAsync();
        Assert.Empty(Incidents(storage));                                                  // and it does not appear later

        var next = await recorder.MarkIncidentAsync();
        Assert.True(next.Saved);
        Assert.Equal(2, next.Marker!.Sequence);                                           // the withdrawn marker's number is not reused: order and identity stay unambiguous
        await recorder.StopAsync();
        Assert.Equal(next.Marker.Id.ToString("D"), Assert.Single(Incidents(storage)).GetProperty("id").GetString());
    }

    [Fact]
    public async Task AMarkTheWriterNeverStartedOnIsWithdrawnWhenTheCallerGivesUpAndIsNotWrittenLater()
    {
        var (writer, storage, _, recorder, _) = Create(markTimeout: TimeSpan.FromSeconds(30));
        recorder.Start(false);
        recorder.Add(TestSamples.Make(0)); await writer.FlushAsync();
        var gate = storage.WriteGate = new ManualResetEventSlim(false);
        recorder.Add(TestSamples.Make(1));                                                // the writer blocks inside this sample; the marker queues behind it
        using var cancellation = new CancellationTokenSource();
        var pending = recorder.MarkIncidentAsync(cancellation.Token);
        await Task.Delay(100);
        cancellation.Cancel();

        var result = await pending;

        Assert.Equal(IncidentOutcome.StorageFailed, result.Outcome);                      // withdrawn before the writer started on it: definitely not saved
        gate.Set(); storage.WriteGate = null;
        await writer.FlushAsync();
        Assert.Empty(Incidents(storage));
        await recorder.StopAsync();
        Assert.Empty(Incidents(storage));
    }

    [Fact]
    public async Task AMarkTheDiskDidNotConfirmInTimeIsReportedAsUnknownAndIsInTheFileExactlyOnceWhenTheDiskAnswers()
    {
        var (writer, storage, _, recorder, _) = Create(markTimeout: TimeSpan.FromMilliseconds(300));
        recorder.Start(false);
        for (var i = 0; i < 3; i++) recorder.Add(TestSamples.Make(i));
        await writer.FlushAsync();
        var gate = storage.FlushGate = new ManualResetEventSlim(false);                  // the line is written to the stream, the flush does not return

        var result = await recorder.MarkIncidentAsync();

        Assert.Equal(IncidentOutcome.Unknown, result.Outcome);
        Assert.False(result.Saved);
        Assert.NotNull(result.Marker);                                                    // the identity it would have
        Assert.Contains("may or may not", result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("could not be saved", result.Message, StringComparison.Ordinal);
        gate.Set(); storage.FlushGate = null;                                             // the disk answers after all
        await writer.FlushAsync();
        var written = Assert.Single(Incidents(storage));                                  // it was committed, once, with the identity that was reported
        Assert.Equal(result.Marker!.Id.ToString("D"), written.GetProperty("id").GetString());
        Assert.Equal(result.Marker.Sequence, written.GetProperty("seq").GetInt32());

        var next = await recorder.MarkIncidentAsync();                                    // nothing was retried; the next marker follows in order
        Assert.True(next.Saved);
        Assert.Equal(result.Marker.Sequence + 1, next.Marker!.Sequence);
        await recorder.StopAsync();
        Assert.Equal([result.Marker.Id.ToString("D"), next.Marker.Id.ToString("D")], Incidents(storage).Select(i => i.GetProperty("id").GetString()!).ToArray());
    }

    [Fact]
    public async Task CancellingWhileTheDiskIsStillWorkingOnTheMarkReportsUnknownNotAFailure()
    {
        var (writer, storage, _, recorder, _) = Create(markTimeout: TimeSpan.FromSeconds(30));
        recorder.Start(false);
        recorder.Add(TestSamples.Make(0)); await writer.FlushAsync();
        var gate = storage.FlushGate = new ManualResetEventSlim(false);
        using var cancellation = new CancellationTokenSource();
        var pending = recorder.MarkIncidentAsync(cancellation.Token);
        await Task.Delay(200);                                                            // the writer has taken the marker and waits for the disk
        cancellation.Cancel();

        var result = await pending;

        Assert.Equal(IncidentOutcome.Unknown, result.Outcome);
        gate.Set(); storage.FlushGate = null;
        await writer.FlushAsync();
        Assert.Equal(result.Marker!.Id.ToString("D"), Assert.Single(Incidents(storage)).GetProperty("id").GetString());
        await recorder.StopAsync();
    }

    [Fact]
    public async Task AWriteThatFailsBeforeTheLineReachedTheStreamIsADefiniteFailureAndOneAfterItIsUnknown()
    {
        var (writer, storage, _, recorder, _) = Create();
        recorder.Start(false);
        recorder.Add(TestSamples.Make(0)); await writer.FlushAsync();

        storage.WriteFault = Io("The disk is full.");                                     // the write itself fails
        var notWritten = await recorder.MarkIncidentAsync();
        Assert.Equal(IncidentOutcome.StorageFailed, notWritten.Outcome);
        storage.WriteFault = null;

        // The line is accepted, then flushing fails: closing the failed file may still write it.
        var (writer2, storage2, clock2, recorder2, _) = Create();
        recorder2.Start(false);
        recorder2.Add(TestSamples.Make(0)); await writer2.FlushAsync();
        storage2.FlushFaultFor = p => p.Contains("part001", StringComparison.Ordinal) ? Io() : null;
        var uncertain = await recorder2.MarkIncidentAsync();
        Assert.Equal(IncidentOutcome.Unknown, uncertain.Outcome);
        Assert.NotNull(uncertain.Marker);
        _ = clock2;
    }

    [Fact]
    public async Task NewRecordingsCarryTheApplicationVersionAndOldOnesStillOpen()
    {
        Assert.Equal("0.4.4", AppVersion.Current);
        var (writer, storage, _, recorder, _) = Create();
        await RecordWithConfirmedIncidentAsync(recorder, writer);
        await recorder.StopAsync();
        var part = storage.Names(Dir).Single(n => n.EndsWith(".jsonl", StringComparison.Ordinal));
        using (var header = JsonDocument.Parse(HistoryFixture.ReadText(storage, Path.Combine(Dir, part)).Split('\n')[0]))
            Assert.Equal("0.4.4", header.RootElement.GetProperty("app").GetString());
        var history = HistoryFixture.Load(storage, RecordingFormat.ShortIdOf(part));
        Assert.Equal("0.4.4", history.Info.AppVersion);

        // A recording made by 0.4.3: stamped 0.4.3, no incident fields anywhere.
        var (oldWriter, oldStorage, _, oldRecorder, _) = Create();
        _ = oldWriter;
        var legacy = new RecordingWriter(() => IncidentTests.Options(compress: false), oldStorage, new FakeClock(oldStorage.Now), "0.4.3");
        var legacyRecorder = new SessionRecorder(legacy, new FakeClock(oldStorage.Now));
        legacyRecorder.Start(false);
        for (var i = 0; i < 4; i++) legacyRecorder.Add(TestSamples.Make(i));
        await legacyRecorder.StopAsync();
        _ = oldRecorder;
        var legacyPart = oldStorage.Names(Dir).Single(n => n.EndsWith(".jsonl", StringComparison.Ordinal));
        HistoryFixture.Replace(oldStorage, Path.Combine(Dir, legacyPart), HistoryFixture.ReadText(oldStorage, Path.Combine(Dir, legacyPart)).Replace(",\"incidents\":0", ""), gzip: false);
        var old = HistoryFixture.Load(oldStorage, RecordingFormat.ShortIdOf(legacyPart));
        Assert.Equal("0.4.3", old.Info.AppVersion);
        Assert.Equal(4, old.SampleCount);
        Assert.Empty(old.Incidents);
        Assert.Equal(RecordingCompleteness.Complete, old.Info.State);
    }
}
