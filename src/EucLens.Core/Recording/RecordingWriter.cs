using System.IO.Compression;
using System.Text.Json;
using System.Threading.Channels;

namespace EucLens.Core;

public sealed record RecordingOptions
{
    public required string Directory { get; init; }
    public long QuotaBytes { get; init; } = 1024L << 20;
    public TimeSpan? MaxAge { get; init; } = TimeSpan.FromDays(14);
    /// <summary>A part is closed and a new one opened at this size.</summary>
    public long SegmentBytes { get; init; } = 64L << 20;
    public TimeSpan FlushInterval { get; init; } = TimeSpan.FromSeconds(5);
    /// <summary>Samples waiting for the disk. At 1 Hz the default holds fifteen minutes.</summary>
    public int QueueCapacity { get; init; } = 900;
    /// <summary>A recording refuses to start, and stops, rather than fill the volume below this.</summary>
    public long MinimumFreeBytes { get; init; } = 256L << 20;
    public bool CompressClosedParts { get; init; } = true;
    public TimeSpan RetryInterval { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan GiveUpAfter { get; init; } = TimeSpan.FromSeconds(60);
    /// <summary>How long a caller waits for a durable mark: first for room in the queue, then for the disk to confirm.</summary>
    public TimeSpan DurableMarkTimeout { get; init; } = TimeSpan.FromSeconds(10);
}

/// <summary>
/// Streams one recording to disk as <c>euclens.recording/1</c> parts (see <see cref="RecordingFormat"/>). The sampling loop only
/// enqueues: a bounded queue feeds one background task that buffers writes and flushes at a fixed interval. Nothing is dropped
/// silently: samples the queue cannot take, or that arrive while the disk is failing, are counted and written as a data-loss mark
/// as soon as writing works again. After <see cref="RecordingOptions.GiveUpAfter"/> of failed writes the recording ends with a footer-less
/// part and the end reason <see cref="RecordingEndReason.StorageError"/>.
/// </summary>
public sealed class RecordingWriter : IRecordingSink, IAsyncDisposable
{
    private abstract record Item;
    private sealed record SampleItem(SessionSnapshot Sample) : Item;
    private sealed record MarkItem(RecordingMark Mark) : Item;
    private sealed record DurableMarkItem(RecordingMark Mark, TaskCompletionSource<MarkWriteResult> Done) : Item
    {
        private int claim; // 0 queued, 1 taken by the writer, 2 withdrawn by the caller: exactly one of the last two wins
        public bool TryClaim() => Interlocked.CompareExchange(ref claim, 1, 0) == 0;
        public bool TryWithdraw() => Interlocked.CompareExchange(ref claim, 2, 0) == 0;
    }
    private sealed record FlushItem(TaskCompletionSource Done) : Item;
    private sealed record EndItem(RecordingEndReason Reason, TaskCompletionSource Done) : Item;

    private readonly Func<RecordingOptions> optionsProvider;
    private readonly IRecordingStorage storage;
    private readonly TimeProvider clock;
    private readonly string appVersion;
    private readonly object gate = new();

    private RecordingOptions options = null!;
    private Channel<Item>? channel;
    private Task loop = Task.CompletedTask;
    private readonly List<Task> compressions = [];
    private RecordingFormat.Codec codec = new();
    private Stream? file;
    private string? currentPath;
    private Guid id;
    private string shortId = "";
    private DateTimeOffset started;
    private bool isDemo;
    private int part;
    private long partBytes, bytesTotal, otherBytes, samplesWritten, lost, lostSinceMark, warnings, criticals, incidents;
    private SessionInfo? lastSession;
    private DateTimeOffset lastFlush, lastRetry;
    private DateTimeOffset? faultStart;
    private bool overflowReported;
    private RecordingSinkState state = RecordingSinkState.Idle;
    private RecordingFault? lastFault;

    public RecordingWriter(Func<RecordingOptions> optionsProvider, IRecordingStorage? storage = null, TimeProvider? clock = null, string? appVersion = null)
    {
        this.optionsProvider = optionsProvider;
        this.storage = storage ?? new PhysicalRecordingStorage();
        this.clock = clock ?? TimeProvider.System;
        this.appVersion = appVersion ?? AppVersion.Current;
    }

    public event Action<RecordingFault>? Faulted;
    public event Action<RecordingEndReason>? Ended;

    public RecordingSinkStatus Status
    {
        get
        {
            lock (gate) return new(state, state == RecordingSinkState.Idle && samplesWritten == 0 ? null : id, currentPath, part, bytesTotal, Interlocked.Read(ref samplesWritten), Interlocked.Read(ref lost), lastFault);
        }
    }

    public void Begin(RecordingStartInfo info)
    {
        try { loop.Wait(TimeSpan.FromSeconds(10)); }
        catch (AggregateException) { /* a failed previous loop already reported its fault */ }
        lock (gate)
        {
            if (state != RecordingSinkState.Idle || !loop.IsCompleted) throw new InvalidOperationException("The previous recording is still being finalized.");
            options = optionsProvider();
            id = info.Id; shortId = RecordingFormat.ShortId(id); started = info.StartedAt; isDemo = info.IsDemo;
            part = 0; partBytes = bytesTotal = samplesWritten = lost = lostSinceMark = warnings = criticals = incidents = 0;
            lastSession = null; faultStart = null; lastFault = null; overflowReported = false;
            codec = new();
            storage.EnsureDirectory(options.Directory);
            var free = storage.FreeSpace(options.Directory);
            if (free is { } f && f < options.MinimumFreeBytes)
                throw new IOException($"Only {f >> 20} MiB are free in the recording folder; at least {options.MinimumFreeBytes >> 20} MiB are required.");
            var retention = RecordingRetention.Apply(storage, options.Directory, Math.Max(0, options.QuotaBytes - options.SegmentBytes), options.MaxAge, clock.GetUtcNow(), shortId);
            otherBytes = retention.BytesKept;
            channel = Channel.CreateBounded<Item>(new BoundedChannelOptions(Math.Max(16, options.QueueCapacity)) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
            OpenPart(); // surfaces an unusable folder to the caller
            lastFlush = clock.GetUtcNow();
            state = RecordingSinkState.Active;
            var reader = channel.Reader;
            loop = Task.Run(() => RunAsync(reader));
        }
    }

    public Task<MarkWriteResult> MarkDurableAsync(RecordingMark mark, CancellationToken cancellationToken = default)
    {
        Channel<Item>? c; TimeSpan timeout;
        lock (gate) { if (state == RecordingSinkState.Idle) return Task.FromResult(new MarkWriteResult(false, "No recording is active.")); c = channel; timeout = options.DurableMarkTimeout; }
        var done = new TaskCompletionSource<MarkWriteResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var item = new DurableMarkItem(mark, done);
        // Queued before this method returns: callers that hold a lock around the call keep their order.
        if (c!.Writer.TryWrite(item)) return AwaitDurableAsync(item, timeout, cancellationToken);
        return QueueThenAwaitAsync(c, item, timeout, cancellationToken);
    }

    private static async Task<MarkWriteResult> QueueThenAwaitAsync(Channel<Item> c, DurableMarkItem item, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var waiting = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        waiting.CancelAfter(timeout);
        try { await c.Writer.WriteAsync(item, waiting.Token).ConfigureAwait(false); }
        catch (ChannelClosedException) { return new(false, "The recording ended before the marker could be written."); }
        catch (OperationCanceledException)
        {
            // A write that was cancelled never enters the queue: the marker is not in the recording and never will be.
            if (cancellationToken.IsCancellationRequested) throw;
            return new(false, "The disk is too slow: the write queue is full. The marker was not saved and will not be written later.");
        }
        return await AwaitDurableAsync(item, timeout, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Waits for the writer. When the wait ends without an answer the marker is withdrawn: if the writer has not started on it, it will not be written (a definite
    /// failure); if it has, the outcome is honestly unknown, because it may complete at any moment. The same marker is never written twice and never retried.
    /// </summary>
    private static async Task<MarkWriteResult> AwaitDurableAsync(DurableMarkItem item, TimeSpan timeout, CancellationToken cancellationToken)
    {
        try { return await item.Done.Task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false); }
        catch (TimeoutException) { return Withdraw(item, "The disk did not confirm the write in time."); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            var result = Withdraw(item, "The marker was cancelled.");
            if (result.OutcomeUnknown) return result;
            throw;
        }
    }

    private static MarkWriteResult Withdraw(DurableMarkItem item, string reason)
    {
        if (item.TryWithdraw()) return new(false, $"{reason} The marker was withdrawn before the writer started on it and will not be written.");
        if (item.Done.Task.IsCompletedSuccessfully) return item.Done.Task.Result;   // the answer arrived while the wait was ending
        return new(false, $"{reason} The writer had already started on the marker, so it may or may not end up in the recording. It is not retried.") { OutcomeUnknown = true };
    }

    public void Add(SessionSnapshot sample) => Enqueue(new SampleItem(sample), isSample: true);
    public void Mark(RecordingMark mark) => Enqueue(new MarkItem(mark), isSample: false);

    private void Enqueue(Item item, bool isSample)
    {
        Channel<Item>? c;
        lock (gate) { if (state == RecordingSinkState.Idle) return; c = channel; }
        if (c!.Writer.TryWrite(item)) { overflowReported = false; return; }
        // Backpressure: the producer never waits. Samples are counted; marks are rare, so a full queue waits for them.
        if (isSample)
        {
            Interlocked.Increment(ref lost); Interlocked.Increment(ref lostSinceMark);
            if (!overflowReported)
            {
                overflowReported = true;
                Raise(new RecordingFault(RecordingFaultKind.QueueOverflow, "The disk is too slow: samples are being dropped and counted as lost.", clock.GetUtcNow(), false));
            }
        }
        else _ = c.Writer.WriteAsync(item).AsTask();
    }

    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        Channel<Item>? c;
        lock (gate) { if (state == RecordingSinkState.Idle) return; c = channel; }
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task running;
        lock (gate) running = loop;
        try { await c!.Writer.WriteAsync(new FlushItem(done), cancellationToken).ConfigureAwait(false); }
        catch (ChannelClosedException) { return; } // the recording already ended itself
        // The loop may end (fatal storage error) while the request is queued; it then completes the request as it drains.
        await Task.WhenAny(done.Task, running).WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task EndAsync(RecordingEndReason reason, CancellationToken cancellationToken = default)
    {
        Channel<Item>? c; Task running;
        lock (gate) { if (state == RecordingSinkState.Idle) { running = loop; c = null; } else { c = channel; running = loop; } }
        if (c is not null)
        {
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            try { await c.Writer.WriteAsync(new EndItem(reason, done), cancellationToken).ConfigureAwait(false); }
            catch (ChannelClosedException) { /* the loop already ended itself */ }
            await Task.WhenAny(done.Task, running).WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        try { await running.WaitAsync(cancellationToken).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException) { /* reported through Faulted */ }
    }

    public async ValueTask DisposeAsync() { try { await EndAsync(RecordingEndReason.AppExit).ConfigureAwait(false); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } }

    public IEnumerable<SessionSnapshot> ReadSamples()
    {
        string directory; string wanted;
        lock (gate) { directory = options?.Directory ?? ""; wanted = shortId; }
        if (directory.Length == 0) return [];
        var parts = storage.List(directory)
            .Select(f => (File: f, Ok: RecordingFormat.TryParsePart(Path.GetFileName(f.Path), out _, out var pid, out var n), Id: pid, Number: n, Gz: f.Path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase)))
            .Where(x => x.Ok && x.Id == wanted)
            .GroupBy(x => x.Number)
            .OrderBy(g => g.Key)
            // A part being compressed exists twice for a moment; the plain file is complete, the .gz may not be yet.
            .Select(g => g.OrderBy(x => x.Gz).First().File.Path)
            .ToArray();
        return RecordingFormat.ReadSamples(storage, parts);
    }

    // ---- writer task ----

    private async Task RunAsync(ChannelReader<Item> reader)
    {
        try
        {
            await foreach (var item in reader.ReadAllAsync().ConfigureAwait(false))
            {
                switch (item)
                {
                    case SampleItem s: if (!await WriteSampleAsync(s.Sample).ConfigureAwait(false)) return; break;
                    case MarkItem m: await GuardedAsync(() => WriteMarkAsync(m.Mark, flush: true), countAsLostSample: false).ConfigureAwait(false); break;
                    case DurableMarkItem d:
                        if (d.TryClaim()) await WriteDurableMarkAsync(d).ConfigureAwait(false);
                        else d.Done.TrySetResult(new(false, "The marker was withdrawn before it was written."));
                        break;
                    case FlushItem f: await GuardedAsync(() => FlushFileAsync(force: true), false).ConfigureAwait(false); f.Done.TrySetResult(); break;
                    case EndItem e:
                        await FinishAsync(e.Reason, reportFailure: true).ConfigureAwait(false);
                        e.Done.TrySetResult();
                        return;
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The writer itself failed unexpectedly: nothing may keep waiting for it, and the user is told the recording stopped.
            Raise(new RecordingFault(RecordingFaultKind.Io, $"The recording writer stopped unexpectedly: {ex.Message}", clock.GetUtcNow(), true));
            AbandonFile();
            try { Ended?.Invoke(RecordingEndReason.StorageError); } catch (Exception) { }
        }
        finally
        {
            lock (gate) { if (state != RecordingSinkState.Idle) state = RecordingSinkState.Idle; }
            // Whatever is still queued will never be written: requests are released and samples are counted as lost.
            channel?.Writer.TryComplete();
            while (reader.TryRead(out var rest))
            {
                switch (rest)
                {
                    case FlushItem f: f.Done.TrySetResult(); break;
                    case EndItem e: e.Done.TrySetResult(); break;
                    case DurableMarkItem d: d.Done.TrySetResult(new(false, "The recording ended before the marker could be written.")); break;
                    case SampleItem: CountLost(); break;
                }
            }
        }
    }

    /// <returns>False when the recording ended itself.</returns>
    private async Task<bool> WriteSampleAsync(SessionSnapshot sample)
    {
        var now = clock.GetUtcNow();
        if (faultStart is not null)
        {
            if (now - faultStart >= options.GiveUpAfter) { await FatalAsync(RecordingEndReason.StorageError, RecordingFaultKind.Io, "Writing to the recording folder kept failing; the recording was stopped.").ConfigureAwait(false); return false; }
            if (now - lastRetry < options.RetryInterval) { CountLost(); return true; }
            lastRetry = now;
            try { await ReopenAsync(now).ConfigureAwait(false); }
            catch (Exception ex) when (IsStorageError(ex)) { CountLost(); return true; }
        }
        try
        {
            // Samples the queue could not take (the disk was too slow) are announced in the file before the next sample.
            var dropped = Interlocked.Exchange(ref lostSinceMark, 0);
            if (dropped > 0) await WriteMarkAsync(new RecordingMark(RecordingMarkKind.DataLoss, now, $"{dropped} sample(s) were dropped because the disk could not keep up."), flush: false).ConfigureAwait(false);
            await WriteSampleLinesAsync(sample).ConfigureAwait(false);
            if (otherBytes + bytesTotal > options.QuotaBytes)
            { await FatalAsync(RecordingEndReason.QuotaReached, RecordingFaultKind.QuotaReached, $"The recording reached the storage quota of {options.QuotaBytes >> 20} MiB and was stopped.").ConfigureAwait(false); return false; }
            if (partBytes >= options.SegmentBytes)
            {
                if (storage.FreeSpace(options.Directory) is { } free && free < options.MinimumFreeBytes)
                { await FatalAsync(RecordingEndReason.StorageError, RecordingFaultKind.LowDiskSpace, $"Less than {options.MinimumFreeBytes >> 20} MiB are free; the recording was stopped to protect the disk.").ConfigureAwait(false); return false; }
                await RotateAsync().ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (IsStorageError(ex)) { OnWriteFailure(ex); CountLost(); }
        return true;
    }

    /// <summary>
    /// A mark the caller waits for: success is reported only after the line is flushed to disk. A failure is a definite "not saved" while nothing of the line reached the
    /// stream; once the full line was handed to it, closing the failed file may still write it, so the outcome is reported as unknown.
    /// </summary>
    private async Task WriteDurableMarkAsync(DurableMarkItem item)
    {
        if (faultStart is not null)
        {
            string? reason; lock (gate) reason = lastFault?.Message;
            item.Done.TrySetResult(new(false, $"The recording folder is not writable right now ({reason ?? "storage unavailable"})."));
            return;
        }
        var handedToStream = false;
        try
        {
            await WriteMarkAsync(item.Mark, flush: true, onBuffered: () => handedToStream = true).ConfigureAwait(false);
            if (file is FileStream fs) fs.Flush(flushToDisk: true);
            item.Done.TrySetResult(new(true, null));
        }
        catch (Exception ex) when (IsStorageError(ex))
        {
            OnWriteFailure(ex);
            item.Done.TrySetResult(new(false, ex.Message) { OutcomeUnknown = handedToStream });
        }
    }

    private async Task GuardedAsync(Func<Task> action, bool countAsLostSample)
    {
        if (faultStart is not null) { if (countAsLostSample) CountLost(); return; }
        try { await action().ConfigureAwait(false); }
        catch (Exception ex) when (IsStorageError(ex)) { OnWriteFailure(ex); if (countAsLostSample) CountLost(); }
    }

    private void CountLost() { Interlocked.Increment(ref lost); Interlocked.Increment(ref lostSinceMark); }

    private static bool IsStorageError(Exception ex) => ex is IOException or UnauthorizedAccessException or ObjectDisposedException or NotSupportedException;

    private void OnWriteFailure(Exception ex)
    {
        var now = clock.GetUtcNow();
        faultStart ??= now; lastRetry = now;
        try { file?.Dispose(); } catch (Exception) { /* the handle is already broken */ }
        file = null;
        lock (gate) state = RecordingSinkState.Faulted;
        Raise(new RecordingFault(Classify(ex), ex.Message, now, false));
    }

    public static RecordingFaultKind Classify(Exception ex) => ex switch
    {
        UnauthorizedAccessException => RecordingFaultKind.AccessDenied,
        DirectoryNotFoundException => RecordingFaultKind.Unavailable,
        IOException io when (io.HResult & 0xFFFF) is 0x70 or 0x27 => RecordingFaultKind.DiskFull,
        IOException io when (io.HResult & 0xFFFF) is 0x03 or 0x15 or 0x35 or 0x33 or 0x40 or 0x43 or 0x4C3 => RecordingFaultKind.Unavailable,
        _ => RecordingFaultKind.Io
    };

    private void Raise(RecordingFault fault)
    {
        lock (gate) lastFault = fault;
        try { Faulted?.Invoke(fault); } catch (Exception) { /* a subscriber must not break the writer */ }
    }

    private async Task FatalAsync(RecordingEndReason reason, RecordingFaultKind kind, string message)
    {
        var fault = new RecordingFault(kind, message, clock.GetUtcNow(), true);
        await FinishAsync(reason, reportFailure: false).ConfigureAwait(false);   // best effort: the part stays readable up to its last line
        Raise(fault);
        try { Ended?.Invoke(reason); } catch (Exception) { }
        lock (gate) state = RecordingSinkState.Idle;
        channel?.Writer.TryComplete();
    }

    private async Task ReopenAsync(DateTimeOffset now)
    {
        OpenPart();
        var gap = faultStart is { } from ? now - from : (TimeSpan?)null;
        var count = Interlocked.Exchange(ref lostSinceMark, 0);
        faultStart = null;
        lock (gate) state = RecordingSinkState.Active;
        try { await WriteMarkAsync(new RecordingMark(RecordingMarkKind.DataLoss, now, $"{count} sample(s) could not be written and are lost.", gap), flush: true).ConfigureAwait(false); }
        catch (Exception ex) when (IsStorageError(ex))
        {
            // The announcement of the loss did not reach the file: the loss is still owed and the writer is not healthy.
            Interlocked.Add(ref lostSinceMark, count);
            OnWriteFailure(ex);
            throw;
        }
    }

    // ---- file operations ----

    private void OpenPart()
    {
        part++;
        codec.Table.Reset();
        var path = Path.Combine(options.Directory, RecordingFormat.PartName(started, id, part));
        file = storage.Create(path);
        currentPath = path; partBytes = 0; lastSession = null;
        var header = RecordingFormat.Line(w =>
        {
            w.WriteStartObject();
            w.WriteString("t", "header"); w.WriteString("schema", RecordingFormat.Schema); w.WriteString("app", appVersion);
            w.WriteString("id", id); w.WriteNumber("part", part); w.WriteString("startedUtc", started.UtcDateTime.ToString("O"));
            w.WriteString("source", isDemo ? "Demo" : "RealSession");
            w.WriteString("note", "Unavailable measurements have no value (never zero); raw-unit counters keep their raw value.");
            w.WriteEndObject();
        });
        file.Write(header, 0, header.Length);
        partBytes += header.Length; bytesTotal += header.Length;
    }

    private async Task WriteAsync(byte[] line)
    {
        await file!.WriteAsync(line).ConfigureAwait(false);
        partBytes += line.Length; bytesTotal += line.Length;
    }

    private async Task FlushFileAsync(bool force)
    {
        var now = clock.GetUtcNow();
        if (!force && now - lastFlush < options.FlushInterval) return;
        if (file is not null) await file.FlushAsync().ConfigureAwait(false);
        lastFlush = now;
    }

    private async Task WriteSampleLinesAsync(SessionSnapshot s)
    {
        if (lastSession is null || !Equals(lastSession, s.Session))
        {
            await WriteAsync(RecordingFormat.Line(w => { w.WriteStartObject(); w.WriteString("t", "session"); w.WritePropertyName("d"); JsonSerializer.Serialize(w, s.Session, codec.Options); w.WriteEndObject(); })).ConfigureAwait(false);
            lastSession = s.Session;
        }
        codec.Metrics.SampleTime = s.Timestamp;
        var body = RecordingFormat.Line(w =>
        {
            w.WriteStartObject();
            w.WriteString("t", "s"); w.WriteString("ts", s.Timestamp.ToString("O"));
            w.WritePropertyName("d"); JsonSerializer.Serialize(w, RecordingFormat.SampleBody.From(s), codec.Options);
            w.WriteEndObject();
        });
        foreach (var (stringId, value) in codec.Table.TakePending())
            await WriteAsync(RecordingFormat.Line(w => { w.WriteStartObject(); w.WriteString("t", "str"); w.WriteNumber("id", stringId); w.WriteString("v", value); w.WriteEndObject(); })).ConfigureAwait(false);
        foreach (var e in s.Diagnostics?.Events ?? [])
        {
            if (e.Transition is FindingTransition.Detected or FindingTransition.Escalated)
            { if (e.Finding.Severity == Severity.Critical) criticals++; else if (e.Finding.Severity == Severity.Warning) warnings++; }
            await WriteAsync(RecordingFormat.Line(w =>
            {
                w.WriteStartObject();
                w.WriteString("t", "event"); w.WriteString("at", e.Timestamp.ToString("O")); w.WriteString("kind", e.Transition.ToString());
                w.WriteString("rule", e.Finding.RuleId); w.WriteString("severity", e.Finding.Severity.ToString()); w.WriteString("title", e.Finding.Title);
                w.WriteEndObject();
            })).ConfigureAwait(false);
        }
        await WriteAsync(body).ConfigureAwait(false);
        Interlocked.Increment(ref samplesWritten);
        await FlushFileAsync(force: false).ConfigureAwait(false);
    }

    private async Task WriteMarkAsync(RecordingMark mark, bool flush, Action? onBuffered = null)
    {
        await WriteAsync(RecordingFormat.Line(w =>
        {
            w.WriteStartObject();
            w.WriteString("t", "mark"); w.WriteString("at", mark.At.ToString("O")); w.WriteString("kind", mark.Kind.ToString());
            if (mark.Detail.Length > 0) w.WriteString("detail", mark.Detail);
            if (mark.Gap is { } gap) w.WriteNumber("gapSeconds", Math.Round(gap.TotalSeconds, 3));
            if (mark.IncidentId is { } incident) w.WriteString("id", incident.ToString("D"));
            if (mark.Sequence is { } sequence) w.WriteNumber("seq", sequence);
            w.WriteEndObject();
        })).ConfigureAwait(false);
        onBuffered?.Invoke();
        if (mark.Kind == RecordingMarkKind.UserIncident) Interlocked.Increment(ref incidents);
        if (flush) await FlushFileAsync(force: true).ConfigureAwait(false);
    }

    private async Task WriteFooterAsync(string reason, bool complete)
    {
        await WriteAsync(RecordingFormat.Line(w =>
        {
            w.WriteStartObject();
            w.WriteString("t", "footer"); w.WriteString("endedUtc", clock.GetUtcNow().UtcDateTime.ToString("O")); w.WriteString("reason", reason);
            w.WriteNumber("part", part); w.WriteNumber("samples", Interlocked.Read(ref samplesWritten)); w.WriteNumber("warnings", warnings); w.WriteNumber("criticals", criticals);
            w.WriteNumber("lost", Interlocked.Read(ref lost)); w.WriteNumber("bytes", bytesTotal); w.WriteBoolean("complete", complete);
            w.WriteNumber("incidents", Interlocked.Read(ref incidents));
            w.WriteEndObject();
        })).ConfigureAwait(false);
    }

    private async Task CloseFileAsync()
    {
        if (file is null) return;
        await file.FlushAsync().ConfigureAwait(false);
        if (file is FileStream fs) fs.Flush(flushToDisk: true);
        await file.DisposeAsync().ConfigureAwait(false);
        file = null;
    }

    private async Task RotateAsync()
    {
        await WriteFooterAsync("Rotated", complete: false).ConfigureAwait(false);
        var closed = currentPath!;
        await CloseFileAsync().ConfigureAwait(false);
        WriteMeta(complete: false, reason: null);
        if (options.CompressClosedParts) compressions.Add(Task.Run(() => Compress(closed)));
        var retention = RecordingRetention.Apply(storage, options.Directory, Math.Max(0, options.QuotaBytes - bytesTotal - options.SegmentBytes), options.MaxAge, clock.GetUtcNow(), shortId);
        otherBytes = Math.Max(0, retention.BytesKept - bytesTotal);
        OpenPart();
        lastFlush = clock.GetUtcNow();
    }

    /// <summary>
    /// Writes the footer, closes and indexes the last part. A failure at any step is reported as a fault (when <paramref name="reportFailure"/>), never as a successful
    /// finalization: the part stays as it is (readable up to its last complete line, without a footer), the handle is released and the waiting callers are completed.
    /// </summary>
    private async Task FinishAsync(RecordingEndReason reason, bool reportFailure)
    {
        Exception? failure = null;
        if (file is not null)
        {
            try
            {
                await WriteFooterAsync(reason.ToString(), complete: true).ConfigureAwait(false);
                await CloseFileAsync().ConfigureAwait(false);
                WriteMeta(complete: true, reason);
                if (options.CompressClosedParts && currentPath is { } last) compressions.Add(Task.Run(() => Compress(last)));
            }
            catch (Exception ex) when (IsStorageError(ex)) { failure = ex; }
            finally { AbandonFile(); }
        }
        try { await Task.WhenAll(compressions).ConfigureAwait(false); } catch (Exception) { /* a part that failed to compress stays plain */ }
        compressions.Clear();
        // After a fatal storage error the caller announces the end first and then sets the state, as it always did.
        if (failure is null || reportFailure) lock (gate) state = RecordingSinkState.Idle;
        if (failure is not null && reportFailure)
            Raise(new RecordingFault(Classify(failure), $"The recording could not be finalized ({failure.Message}). It was not closed properly; what was written is kept and can be opened as an interrupted recording.", clock.GetUtcNow(), true));
    }

    /// <summary>Releases the current file handle whatever state it is in.</summary>
    private void AbandonFile()
    {
        if (file is null) return;
        try { file.Dispose(); } catch (Exception) { /* the handle is already broken */ }
        file = null;
    }

    /// <summary>
    /// Gzip a closed part. The compressed copy is written to a temporary file, flushed to disk and read back completely (it must expand to exactly the plain
    /// bytes) before it is renamed into place, and only then is the plain file removed. A failure at any step leaves the plain part, with its confirmed
    /// incident lines, untouched; a crash between the rename and the delete leaves both, and readers prefer the plain one.
    /// </summary>
    private void Compress(string path)
    {
        var temporary = path + ".gz.partial";
        try
        {
            long expected;
            using (var input = storage.OpenRead(path))
            using (var output = storage.Create(temporary))
            {
                expected = input.CanSeek ? input.Length : -1;
                using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true)) input.CopyTo(gzip);
                output.Flush();
                if (output is FileStream fs) fs.Flush(flushToDisk: true);
            }
            VerifyCompressed(temporary, expected);
            storage.Rename(temporary, path + ".gz");
            storage.Delete(path);
        }
        catch (Exception ex) when (IsStorageError(ex) || ex is InvalidDataException)
        {
            try { storage.Delete(temporary); } catch (Exception) { }
        }
    }

    private void VerifyCompressed(string compressedPath, long expectedBytes)
    {
        using var stream = new GZipStream(storage.OpenRead(compressedPath), CompressionMode.Decompress);
        var buffer = new byte[64 * 1024]; long total = 0; int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0) total += read;
        if (expectedBytes >= 0 && total != expectedBytes) throw new InvalidDataException($"The compressed copy holds {total} bytes, the part {expectedBytes}.");
    }

    private void WriteMeta(bool complete, RecordingEndReason? reason)
    {
        try
        {
            var path = Path.Combine(options.Directory, RecordingFormat.MetaName(started, id));
            var temporary = path + ".partial";
            using (var stream = storage.Create(temporary))
            using (var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
            {
                w.WriteStartObject();
                w.WriteString("schema", RecordingFormat.Schema); w.WriteString("id", id); w.WriteString("startedUtc", started.UtcDateTime.ToString("O"));
                w.WriteString("endedUtc", clock.GetUtcNow().UtcDateTime.ToString("O")); w.WriteBoolean("complete", complete);
                if (reason is { } r) w.WriteString("reason", r.ToString());
                w.WriteString("source", isDemo ? "Demo" : "RealSession");
                w.WriteNumber("parts", part); w.WriteNumber("samples", Interlocked.Read(ref samplesWritten));
                w.WriteNumber("warnings", warnings); w.WriteNumber("criticals", criticals); w.WriteNumber("lost", Interlocked.Read(ref lost)); w.WriteNumber("bytes", bytesTotal);
                w.WriteNumber("incidents", Interlocked.Read(ref incidents));
                w.WriteEndObject();
            }
            storage.Rename(temporary, path);
        }
        catch (Exception ex) when (IsStorageError(ex)) { /* the meta file is an index; the parts are the data */ }
    }
}
