using System.IO.Compression;
using EucLens.Core;

namespace EucLens.Tests;

/// <summary>Synthetic recordings written through the real writer. No customer data, no Horizon Agent, no GPU.</summary>
internal static class HistoryFixture
{
    public static readonly string Dir = IncidentTests.Dir;
    public static DateTimeOffset T0 => TestSamples.T0;

    /// <summary>A realistic sample with controllable values; a null value is an unavailable measurement.</summary>
    /// <summary>The recorded diagnostic state of a sample: domain assessments and the findings active at that moment.</summary>
    public static DiagnosticSnapshot Diag(params string[] active) => new(
        [new(DiagnosticDomain.Connection, active.Length > 0 ? HealthState.Warning : HealthState.Healthy, AssessmentCoverage.Full, "evidence", "note", 60)], active, []);

    public static SessionSnapshot Sample(int i, DateTimeOffset? at = null, double? rtt = 30, double? cpu = 20, double? fps = 30, double? loss = 0, double? ram = 40, bool withEvent = false, string[]? active = null)
    {
        var s = TestSamples.Make(i, at ?? T0.AddSeconds(i), withEvent: withEvent);
        static Metric M(double? v, string unit) => v is { } x ? Metric.Available(x, unit) : Metric.Unavailable(unit, "No data in this sample");
        return s with
        {
            Connection = s.Connection with { RoundTripTime = M(rtt, "ms"), PacketLoss = M(loss, "%") },
            System = s.System with { CpuUtilization = M(cpu, "%"), RamUtilization = M(ram, "%") },
            Graphics = s.Graphics with { FramesPerSecond = M(fps, "FPS") },
            Diagnostics = active is not null && !withEvent ? Diag(active) : s.Diagnostics,
        };
    }

    public sealed record Recorded(MemoryStorage Storage, string ShortId, FakeClock Clock, List<IncidentMarker> Incidents);

    /// <summary>
    /// Records <paramref name="count"/> samples one second apart. <paramref name="step"/> runs before each sample and may mark incidents, pause and
    /// resume, write marks or change the storage.
    /// </summary>
    public static async Task<Recorded> RecordAsync(int count, Func<int, SessionSnapshot>? make = null, Func<int, RecordingContext, Task>? step = null, long segment = 64L << 20,
        bool compress = true, bool finish = true, MemoryStorage? storage = null, Func<int, bool>? skipSample = null)
    {
        var (writer, store, clock, recorder) = IncidentTests.Create(segment, compress, storage);
        var context = new RecordingContext(recorder, clock, writer, new());
        recorder.Start(false);
        for (var i = 0; i < count; i++)
        {
            clock.Set(T0.AddSeconds(i));
            if (step is not null) await step(i, context);
            if (skipSample?.Invoke(i) != true) recorder.Add((make ?? (n => Sample(n)))(i));
            if (i % 200 == 199) await writer.FlushAsync();
        }
        if (finish) await recorder.StopAsync(); else await writer.FlushAsync();
        var shortId = RecordingFormat.ShortId(recorder.SinkStatus!.RecordingId ?? Guid.Empty);
        if (shortId == "00000000") shortId = store.Names(Dir).Select(RecordingFormat.ShortIdOf).First(x => x.Length > 0);
        return new(store, shortId, clock, context.Incidents);
    }

    public sealed record RecordingContext(SessionRecorder Recorder, FakeClock Clock, RecordingWriter Writer, List<IncidentMarker> Incidents)
    {
        /// <summary>Marks an incident half a second before the sample that is about to be recorded, so it falls between two samples.</summary>
        public async Task MarkIncidentAsync(double secondsBeforeNextSample = 0.5)
        {
            Clock.Set(Clock.GetUtcNow() - TimeSpan.FromSeconds(secondsBeforeNextSample));
            var result = await Recorder.MarkIncidentAsync();
            if (!result.Saved) throw new InvalidOperationException(result.Message);
            Incidents.Add(result.Marker!);
        }
        public void Mark(RecordingMarkKind kind, string detail = "", TimeSpan? gap = null) => Recorder.Mark(new(kind, Clock.GetUtcNow(), detail, gap));
    }

    public static LoadedHistory Load(MemoryStorage storage, string shortId, string? active = null) => HistoryLoader.Load(storage, Dir, shortId, active);

    public static string ReadText(MemoryStorage storage, string path)
    {
        using var stream = RecordingFormat.Decompress(storage.OpenRead(path), path);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public static string[] PartPaths(MemoryStorage storage, string shortId) =>
        storage.Names(Dir).Where(n => RecordingFormat.TryParsePart(n, out _, out var id, out _) && id == shortId).Select(n => Path.Combine(Dir, n)).OrderBy(p => p, StringComparer.Ordinal).ToArray();

    public static void Replace(MemoryStorage storage, string path, string text, bool gzip)
    {
        using var buffer = new MemoryStream();
        if (gzip) { using var gz = new GZipStream(buffer, CompressionLevel.Fastest, true); gz.Write(System.Text.Encoding.UTF8.GetBytes(text)); }
        else buffer.Write(System.Text.Encoding.UTF8.GetBytes(text));
        storage.Files[path] = buffer.ToArray(); storage.Times[path] = storage.Now;
    }

    private static readonly Lazy<Task<Recorded>> Long = new(() => RecordAsync(8 * 3600, i => Sample(i, rtt: 30 + i % 7, cpu: 15 + i % 11), async (i, c) =>
    {
        if (i == 3 * 3600) await c.MarkIncidentAsync();
        if (i == 6 * 3600 + 100) await c.MarkIncidentAsync();
    }, segment: 4L << 20));
    /// <summary>An eight hour recording with two incidents (28,800 samples, several compressed parts); built once and only read.</summary>
    public static Task<Recorded> EightHours => Long.Value;
}
