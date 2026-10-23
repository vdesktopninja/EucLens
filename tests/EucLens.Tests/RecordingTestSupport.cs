using System.Text.Json;
using System.Text.Json.Serialization;
using EucLens.Core;

namespace EucLens.Tests;

/// <summary>In-memory storage with injectable failures. Bytes become visible to readers only when flushed, like a real file buffer.</summary>
internal sealed class MemoryStorage : IRecordingStorage
{
    private readonly object gate = new();
    public Dictionary<string, byte[]> Files { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, DateTimeOffset> Times { get; } = new(StringComparer.Ordinal);
    public Func<string, Exception?>? CreateFault { get; set; }
    public Exception? WriteFault { get; set; }
    public long? Free { get; set; } = long.MaxValue / 2;
    public ManualResetEventSlim? WriteGate { get; set; }
    public DateTimeOffset Now { get; set; } = new(2026, 10, 23, 8, 0, 0, TimeSpan.Zero);

    public void EnsureDirectory(string directory) { }
    public Stream Create(string path)
    {
        lock (gate)
        {
            if (Files.ContainsKey(path)) throw new IOException("The file exists: " + path);
            if (CreateFault?.Invoke(path) is { } fault) throw fault;
            Files[path] = [];
            Times[path] = Now;
            return new Handle(this, path);
        }
    }
    public Stream OpenRead(string path)
    {
        lock (gate) return Files.TryGetValue(path, out var bytes) ? new MemoryStream(bytes.ToArray(), writable: false) : throw new FileNotFoundException(path);
    }
    public IReadOnlyList<RecordingFileInfo> List(string directory)
    {
        lock (gate) return Files.Where(f => Path.GetDirectoryName(f.Key) == directory).Select(f => new RecordingFileInfo(f.Key, f.Value.Length, Times[f.Key])).ToArray();
    }
    public void Delete(string path) { lock (gate) { Files.Remove(path); Times.Remove(path); } }
    public void Rename(string from, string to) { lock (gate) { Files[to] = Files[from]; Times[to] = Times[from]; Files.Remove(from); Times.Remove(from); } }
    public long? FreeSpace(string directory) => Free;
    public long TotalBytes { get { lock (gate) return Files.Values.Sum(v => (long)v.Length); } }
    public string[] Names(string directory) { lock (gate) return Files.Keys.Where(k => Path.GetDirectoryName(k) == directory).Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal).ToArray()!; }

    private sealed class Handle(MemoryStorage owner, string path) : Stream
    {
        private readonly MemoryStream buffer = new();
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => buffer.Length;
        public override long Position { get => buffer.Position; set => throw new NotSupportedException(); }
        public override void Write(byte[] data, int offset, int count)
        {
            owner.WriteGate?.Wait();
            if (owner.WriteFault is { } fault) throw fault;
            buffer.Write(data, offset, count);
        }
        public override void Flush()
        {
            if (owner.WriteFault is { } fault) throw fault;
            lock (owner.gate) { if (owner.Files.ContainsKey(path)) { owner.Files[path] = buffer.ToArray(); owner.Times[path] = owner.Now; } }
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing && owner.WriteFault is null) { try { Flush(); } catch (IOException) { } }
            base.Dispose(disposing);
        }
        public override int Read(byte[] data, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}

internal sealed class FakeClock(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset now = start;
    public override DateTimeOffset GetUtcNow() => now;
    public void Advance(TimeSpan by) => now += by;
    public void Set(DateTimeOffset value) => now = value;
}

internal static class TestSamples
{
    public static readonly DateTimeOffset T0 = new(2026, 10, 23, 8, 0, 0, TimeSpan.Zero);
    public static readonly SessionInfo Session = new(RemoteProtocol.OmnissaBlast, 3, "user", "client", "vdi", "Windows 11", "N/A", T0, false, "Blast counters found");

    /// <summary>A realistic real-session sample: values, raw-unit counters, a failed counter with its reason, host metrics and diagnostics.</summary>
    public static SessionSnapshot Make(int i, DateTimeOffset? at = null, bool blast = true, bool withEvent = false, Severity eventSeverity = Severity.Warning)
    {
        var time = at ?? T0.AddSeconds(i);
        var s = DiagnosticsPresentationTests.Snap(time, rtt: 30 + i % 17, loss: i % 5 == 0 ? 0.5 : 0, fps: 24 + i % 9, cpu: 10 + i % 40, ram: 40, jitter: 3 + i % 4) with { Session = Session };
        var metrics = new Dictionary<string, Metric>
        {
            ["rtt"] = blast ? new Metric(30 + i % 17, "ms", null, @"\Horizon Blast Session Counters(Session ID: 3; x)\RTT", time.AddMilliseconds(-2), null, MetricStatus.Available, null, MetricScope.Session)
                : new Metric(null, "ms", "No Horizon Blast Session Counters instance for Windows session 3.", @"\Horizon Blast Session Counters(Session ID: 3; unavailable)\RTT", time, null, MetricStatus.Unavailable, "no instance", MetricScope.Session),
            ["jitter"] = blast ? new Metric(12.5, "raw (unit unverified)", null, @"\Horizon Blast Session Counters(Session ID: 3; x)\Jitter (Uplink)", time, 12, MetricStatus.Available, null, MetricScope.Session)
                : new Metric(null, "raw (unit unverified)", "No instance", @"\Horizon Blast Session Counters(Session ID: 3; unavailable)\Jitter (Uplink)", time, null, MetricStatus.Unavailable, null, MetricScope.Session),
            ["packetLoss"] = new Metric(null, "%", "Counter could not be read", @"\Horizon Blast Session Counters(Session ID: 3; x)\Packet Loss (Uplink)", time, null, MetricStatus.Failed, "Counter could not be read", MetricScope.Session),
        };
        s = s with { BlastMetrics = metrics, CollectionStatus = blast ? "REAL SESSION · 2/3 counters available" : "REAL SESSION · 0/3 counters available" };
        if (!blast) s = s with { Connection = s.Connection with { RoundTripTime = metrics["rtt"] } };
        if (withEvent)
        {
            var finding = new DiagnosticFinding("connection.rtt.sustained", DiagnosticDomain.Connection, eventSeverity, "Elevated Blast round-trip time", "description",
                time, time, null, null, 60, 58, 60, [new("Window P95", "165 ms", null, "≥ 150 ms")], "rule", ["cause"], ["action"], "limitations");
            s = s with { Diagnostics = new DiagnosticSnapshot([new(DiagnosticDomain.Connection, HealthState.Warning, AssessmentCoverage.Full, "evidence", "note", 60)],
                ["connection.rtt.sustained"], [new DiagnosticEvent(time, FindingTransition.Detected, finding)]) };
        }
        return s;
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false, Converters = { new JsonStringEnumConverter() } };
    public static string Json(IEnumerable<SessionSnapshot> samples) => JsonSerializer.Serialize(samples.ToArray(), JsonOptions);
}
