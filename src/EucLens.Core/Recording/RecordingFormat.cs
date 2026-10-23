using System.Buffers;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Text.RegularExpressions;

namespace EucLens.Core;

/// <summary>
/// The persistent recording format, <c>euclens.recording/1</c>: UTF-8 JSON Lines, one object per line, optionally gzip-compressed
/// after a file is closed (<c>.jsonl.gz</c>). Every file ("part") is self-contained: it starts with a header, the session and the
/// string table it needs. Unavailable measurements are written without a value, never as zero; unverified raw units keep their raw value.
/// <list type="bullet">
/// <item><c>{"t":"header",...}</c> schema, recording id, part number, start time, data source</item>
/// <item><c>{"t":"session","d":{...}}</c> session identity; repeated only when it changes (and at the start of each part)</item>
/// <item><c>{"t":"str","id":7,"v":"..."}</c> string table: counter paths and reasons are written once per part</item>
/// <item><c>{"t":"s","ts":"...","d":{...}}</c> one sample; metrics are <c>{"v":value,"u":unit,"k":source,"x":reason,"e":error,"t":ticks since ts,"r":raw,"z":scope,"q":status}</c></item>
/// <item><c>{"t":"event",...}</c> finding detected, escalated, downgraded, recovered or expired (also inside the sample)</item>
/// <item><c>{"t":"mark",...}</c> pause, resume with the gap length, availability change, session lock, disconnect, data loss</item>
/// <item><c>{"t":"footer",...}</c> end reason and totals; a file without footer was interrupted and is read up to its last complete line</item>
/// </list>
/// </summary>
public static partial class RecordingFormat
{
    public const string Schema = "euclens.recording/1";
    public const string FilePrefix = "EucLens-";

    [GeneratedRegex(@"^EucLens-(?<stamp>\d{8}-\d{6})-(?<id>[0-9a-f]{8})-part(?<part>\d{3})\.jsonl(?<gz>\.gz)?$", RegexOptions.CultureInvariant)]
    private static partial Regex PartPattern();
    [GeneratedRegex(@"^EucLens-(?<stamp>\d{8}-\d{6})-(?<id>[0-9a-f]{8})\.meta\.json$", RegexOptions.CultureInvariant)]
    private static partial Regex MetaPattern();

    public static string ShortId(Guid id) => id.ToString("N")[..8];
    public static string Stamp(DateTimeOffset started) => started.UtcDateTime.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
    public static string PartName(DateTimeOffset started, Guid id, int part) => $"{FilePrefix}{Stamp(started)}-{ShortId(id)}-part{part:000}.jsonl";
    public static string MetaName(DateTimeOffset started, Guid id) => $"{FilePrefix}{Stamp(started)}-{ShortId(id)}.meta.json";

    /// <summary>True for files EUC Lens wrote as recordings; retention never touches anything else.</summary>
    public static bool IsRecordingFile(string fileName) => PartPattern().IsMatch(fileName) || MetaPattern().IsMatch(fileName);

    public static bool TryParsePart(string fileName, out string stamp, out string id, out int part)
    {
        var m = PartPattern().Match(fileName);
        stamp = m.Groups["stamp"].Value; id = m.Groups["id"].Value; part = m.Success ? int.Parse(m.Groups["part"].Value, CultureInfo.InvariantCulture) : 0;
        return m.Success;
    }

    public static bool TryParseMeta(string fileName, out string stamp, out string id)
    {
        var m = MetaPattern().Match(fileName);
        stamp = m.Groups["stamp"].Value; id = m.Groups["id"].Value;
        return m.Success;
    }

    /// <summary>Strings that repeat in every sample (counter paths, reasons) are stored once per part.</summary>
    internal sealed class StringTable
    {
        private readonly Dictionary<string, int> ids = new(StringComparer.Ordinal);
        private readonly List<string> byId = [];
        private readonly List<(int Id, string Value)> pending = [];
        public int Add(string value)
        {
            if (ids.TryGetValue(value, out var id)) return id;
            id = byId.Count; byId.Add(value); ids[value] = id; pending.Add((id, value));
            return id;
        }
        public string Get(int id) => id >= 0 && id < byId.Count ? byId[id] : throw new InvalidDataException($"Unknown string table entry {id}.");
        public void Set(int id, string value) { while (byId.Count <= id) byId.Add(""); byId[id] = value; }
        public void Reset() { ids.Clear(); byId.Clear(); pending.Clear(); }
        public IReadOnlyList<(int Id, string Value)> TakePending() { var copy = pending.ToArray(); pending.Clear(); return copy; }
        public IEnumerable<(int Id, string Value)> All() => byId.Select((v, i) => (i, v));
    }

    /// <summary>Compact metric encoding; time stamps are offsets from the sample so they stay lossless and short.</summary>
    internal sealed class MetricConverter(StringTable table) : JsonConverter<Metric>
    {
        public DateTimeOffset SampleTime { get; set; }

        public override void Write(Utf8JsonWriter w, Metric m, JsonSerializerOptions options)
        {
            w.WriteStartObject();
            if (m.Value is { } v && double.IsFinite(v)) w.WriteNumber("v", v);
            w.WriteString("u", m.Unit);
            if (m.Source is not null) w.WriteNumber("k", table.Add(m.Source));
            if (m.UnavailableReason is not null) w.WriteNumber("x", table.Add(m.UnavailableReason));
            if (m.Error is not null) w.WriteNumber("e", table.Add(m.Error));
            if (m.Timestamp is { } t) w.WriteNumber("t", (t - SampleTime).Ticks);
            if (m.RawValue is { } r) w.WriteNumber("r", r);
            if (m.Scope is { } z) w.WriteString("z", z.ToString());
            if (m.SampleStatus is { } q) w.WriteString("q", q.ToString());
            w.WriteEndObject();
        }

        public override Metric Read(ref Utf8JsonReader r, Type type, JsonSerializerOptions options)
        {
            if (r.TokenType != JsonTokenType.StartObject) throw new JsonException("A metric must be an object.");
            double? value = null; string unit = ""; string? source = null, reason = null, error = null; DateTimeOffset? at = null; long? raw = null;
            MetricScope? scope = null; MetricStatus? status = null;
            while (r.Read() && r.TokenType != JsonTokenType.EndObject)
            {
                var name = r.GetString();
                r.Read();
                switch (name)
                {
                    case "v": value = r.GetDouble(); break;
                    case "u": unit = r.GetString() ?? ""; break;
                    case "k": source = table.Get(r.GetInt32()); break;
                    case "x": reason = table.Get(r.GetInt32()); break;
                    case "e": error = table.Get(r.GetInt32()); break;
                    case "t": at = SampleTime.AddTicks(r.GetInt64()); break;
                    case "r": raw = r.GetInt64(); break;
                    case "z": scope = Enum.Parse<MetricScope>(r.GetString()!); break;
                    case "q": status = Enum.Parse<MetricStatus>(r.GetString()!); break;
                    default: r.Skip(); break;
                }
            }
            return new(value, unit, reason, source, at, raw, status, error, scope);
        }
    }

    /// <summary>A sample without its session (written separately) and without values derived from other fields.</summary>
    internal sealed class SampleBody
    {
        public WorkloadContext Workload { get; set; }
        public ConnectionMetrics Connection { get; set; } = null!;
        public GraphicsMetrics Graphics { get; set; } = null!;
        public GpuMetrics Gpu { get; set; } = null!;
        public SystemMetrics System { get; set; } = null!;
        public DemoScenario? DemoScenario { get; set; }
        public IReadOnlyDictionary<string, Metric>? BlastMetrics { get; set; }
        public string? CollectionStatus { get; set; }
        public IReadOnlyList<MetricEntry>? HostMetrics { get; set; }
        public DiagnosticSnapshot? Diagnostics { get; set; }

        public static SampleBody From(SessionSnapshot s) => new()
        {
            Workload = s.Workload, Connection = s.Connection, Graphics = s.Graphics, Gpu = s.Gpu, System = s.System, DemoScenario = s.DemoScenario,
            BlastMetrics = s.BlastMetrics, CollectionStatus = s.CollectionStatus, HostMetrics = s.HostMetrics, Diagnostics = s.Diagnostics
        };

        public SessionSnapshot ToSnapshot(DateTimeOffset at, SessionInfo session) =>
            new(at, session, Workload, Connection, Graphics, Gpu, System, DemoScenario, BlastMetrics, CollectionStatus, HostMetrics, Diagnostics);
    }

    private static void RemoveDerived(JsonTypeInfo info)
    {
        static void Drop(JsonTypeInfo i, Type type, string property) { if (i.Type == type) i.Properties.Remove(i.Properties.Single(p => p.Name == property)); }
        Drop(info, typeof(MetricEntry), nameof(MetricEntry.Display));
        Drop(info, typeof(DomainAssessment), nameof(DomainAssessment.StateLabel));
        Drop(info, typeof(DiagnosticFinding), nameof(DiagnosticFinding.IsActive));
    }

    /// <summary>Serializer options bound to one string table; not thread-safe, used by a single writer or reader.</summary>
    internal sealed class Codec
    {
        public StringTable Table { get; } = new();
        public MetricConverter Metrics { get; }
        public JsonSerializerOptions Options { get; }
        public Codec()
        {
            Metrics = new(Table);
            Options = new()
            {
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                TypeInfoResolver = new DefaultJsonTypeInfoResolver { Modifiers = { RemoveDerived } },
                Converters = { Metrics, new JsonStringEnumConverter() },
            };
        }
    }

    internal static readonly JsonWriterOptions WriterOptions = new() { Indented = false, SkipValidation = false };

    /// <summary>Opens a part for reading, transparently decompressing <c>.gz</c>.</summary>
    public static Stream Decompress(Stream stream, string path) => path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase) ? new GZipStream(stream, CompressionMode.Decompress) : stream;

    /// <summary>
    /// Replays parts as <see cref="SessionSnapshot"/>s, in order. A truncated last line (interrupted write) ends the part without error.
    /// </summary>
    public static IEnumerable<SessionSnapshot> ReadSamples(IRecordingStorage storage, IEnumerable<string> partPaths)
    {
        foreach (var path in partPaths)
        {
            var codec = new Codec();
            SessionInfo? session = null;
            using var stream = Decompress(storage.OpenRead(path), path);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            while (reader.ReadLine() is { } line)
            {
                if (line.Length == 0) continue;
                JsonDocument document;
                try { document = JsonDocument.Parse(line); }
                catch (JsonException) { yield break; } // interrupted last line: everything before it is intact
                using (document)
                {
                    var root = document.RootElement;
                    switch (root.GetProperty("t").GetString())
                    {
                        case "header": codec.Table.Reset(); break;
                        case "str": codec.Table.Set(root.GetProperty("id").GetInt32(), root.GetProperty("v").GetString() ?? ""); break;
                        case "session": session = root.GetProperty("d").Deserialize<SessionInfo>(codec.Options); break;
                        case "s":
                            var at = root.GetProperty("ts").GetDateTimeOffset();
                            codec.Metrics.SampleTime = at;
                            var body = root.GetProperty("d").Deserialize<SampleBody>(codec.Options) ?? throw new InvalidDataException("Empty sample.");
                            yield return body.ToSnapshot(at, session ?? throw new InvalidDataException("A sample precedes its session line."));
                            break;
                    }
                }
            }
        }
    }

    /// <summary>One line of a part with its decoded type, used by the catalog and by tools that scan a recording.</summary>
    public static IEnumerable<(string Type, JsonElement Root)> ReadLines(IRecordingStorage storage, string path)
    {
        using var stream = Decompress(storage.OpenRead(path), path);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (reader.ReadLine() is { } line)
        {
            if (line.Length == 0) continue;
            JsonDocument document;
            try { document = JsonDocument.Parse(line); }
            catch (JsonException) { yield break; }
            // The element is cloned so it outlives its document.
            var root = document.RootElement.Clone();
            document.Dispose();
            yield return (root.GetProperty("t").GetString() ?? "", root);
        }
    }

    internal static byte[] Line(Action<Utf8JsonWriter> write)
    {
        var buffer = new ArrayBufferWriter<byte>(1024);
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions)) write(writer);
        buffer.GetSpan(1)[0] = (byte)'\n';
        buffer.Advance(1);
        return buffer.WrittenSpan.ToArray();
    }
}
