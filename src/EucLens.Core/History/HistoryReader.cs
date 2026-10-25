using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace EucLens.Core;

/// <summary>One part of a recording as found on disk. A part that is being compressed exists twice for a moment; it is still one part.</summary>
public sealed record HistoryPart(int Number, string? PlainPath, string? CompressedPath);

/// <summary>One decoded line of a recording, in file order.</summary>
public abstract record HistoryRecord(int Part);
public sealed record HeaderRecord(int Part, DateTimeOffset StartedUtc, string Source, string AppVersion, Guid? RecordingId) : HistoryRecord(Part);
public sealed record SessionRecord(int Part, SessionInfo Session) : HistoryRecord(Part);
public sealed record SampleRecord(int Part, SessionSnapshot Sample) : HistoryRecord(Part);
public sealed record EventRecord(int Part, DateTimeOffset At, FindingTransition Transition, string RuleId, Severity Severity, string Title) : HistoryRecord(Part);
public sealed record MarkRecord(int Part, DateTimeOffset At, string Kind, string Detail, TimeSpan? Gap, Guid? IncidentId, int? Sequence) : HistoryRecord(Part);
public sealed record FooterRecord(int Part, DateTimeOffset EndedUtc, string Reason, bool Complete, long Samples, long Lost, int Incidents) : HistoryRecord(Part);

/// <summary>
/// Streams a recording's parts as <see cref="HistoryRecord"/>s. Unlike <see cref="RecordingFormat.ReadSamples"/> it keeps every line type and does not stop
/// quietly at damage: the only silent recovery is a truncated last line of a part without footer (an interrupted write); anything else is reported
/// through <paramref name="issues"/> and skipped, and a recording that is not readable at all raises <see cref="RecordingFormatException"/>.
/// </summary>
public static class HistoryReader
{
    /// <summary>The parts of one recording, one representation per part number, in order. Gaps in the numbering are reported.</summary>
    public static IReadOnlyList<HistoryPart> ResolveParts(IRecordingStorage storage, string directory, string shortId, List<HistoryIssue>? issues = null)
    {
        var found = storage.List(directory)
            .Select(f => (File: f, Ok: RecordingFormat.TryParsePart(Path.GetFileName(f.Path), out _, out var id, out var number), Id: id, Number: number))
            .Where(x => x.Ok && x.Id == shortId)
            .GroupBy(x => x.Number)
            .OrderBy(g => g.Key)
            .Select(g => new HistoryPart(g.Key,
                g.FirstOrDefault(x => !x.File.Path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase)).File?.Path,
                g.FirstOrDefault(x => x.File.Path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase)).File?.Path))
            .ToArray();
        if (issues is not null && found.Length > 0)
            for (var expected = 1; expected <= found[^1].Number; expected++)
                if (found.All(p => p.Number != expected))
                    issues.Add(new(HistoryIssueKind.MissingPart, $"Part {expected} of this recording is missing; the measurements it held are not available.", expected));
        return found;
    }

    /// <summary>Lists problems with an upper bound per kind, so a file with millions of damaged lines produces a short report, not millions of entries.</summary>
    private sealed class IssueLog(List<HistoryIssue> target, int maximumPerKind)
    {
        private readonly Dictionary<HistoryIssueKind, int> counts = [];
        public void Add(HistoryIssue issue)
        {
            counts.TryGetValue(issue.Kind, out var count);
            counts[issue.Kind] = ++count;
            if (count <= maximumPerKind) target.Add(issue);
        }
        /// <summary>Adds one summary line for every kind that had more problems than were listed.</summary>
        public void Flush()
        {
            foreach (var (kind, count) in counts)
                if (count > maximumPerKind) target.Add(new(kind, $"{(count - maximumPerKind).ToString("N0", CultureInfo.InvariantCulture)} further problems of this kind ({kind}) were found and are not listed."));
            counts.Clear();
        }
    }

    private sealed class PartState { public long BytesRead; public bool LimitReached; }

    /// <summary>A part whose first readable line is not its header. Not an unsupported format: the part is damaged, and the other parts may be fine.</summary>
    private sealed class MissingHeaderException : Exception;

    public static IEnumerable<HistoryRecord> Read(IRecordingStorage storage, IReadOnlyList<HistoryPart> parts, List<HistoryIssue> issues, CancellationToken cancellationToken = default, HistoryLimits? limits = null)
    {
        limits ??= new();
        var log = new IssueLog(issues, limits.MaximumListedIssuesPerKind);
        long total = 0;
        try
        {
            for (var index = 0; index < parts.Count; index++)
            {
                var part = parts[index];
                var isLast = index == parts.Count - 1;
                Stream? raw; string? openedPath;
                try { (raw, openedPath) = Open(storage, part); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    log.Add(new(HistoryIssueKind.PartUnavailable, $"Part {part.Number} could not be opened ({ex.Message}).", part.Number));
                    continue;
                }
                using var owned = raw;
                var budget = Math.Min(limits.MaximumPartBytes, limits.MaximumTotalBytes - total);
                if (budget <= 0)
                {
                    log.Add(new(HistoryIssueKind.LimitReached, $"Reading stopped before part {part.Number}: this recording needs more than {limits.MaximumTotalBytes >> 20:N0} MiB of reading, which is the limit. The rest was not analyzed.", part.Number));
                    yield break;
                }
                var state = new PartState();
                foreach (var record in ReadPart(owned, openedPath!, part.Number, isLast, log, limits, budget, state, cancellationToken)) yield return record;
                total += state.BytesRead;
                if (state.LimitReached) yield break;
            }
        }
        finally { log.Flush(); }
    }

    /// <summary>The plain file is preferred (a compressed copy that exists next to it may be a moment old); if it disappears while opening, the compressed one is used.</summary>
    private static (Stream Stream, string Path) Open(IRecordingStorage storage, HistoryPart part)
    {
        if (part.PlainPath is not null)
        {
            try { return (storage.OpenRead(part.PlainPath), part.PlainPath); }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException && part.CompressedPath is not null) { /* compressed meanwhile */ }
        }
        var path = part.CompressedPath ?? throw new FileNotFoundException("Part not found.", part.PlainPath);
        return (storage.OpenRead(path), path);
    }

    private static IEnumerable<HistoryRecord> ReadPart(Stream raw, string path, int number, bool isLast, IssueLog issues, HistoryLimits limits, long budget, PartState state, CancellationToken cancellationToken)
    {
        var codec = new RecordingFormat.Codec();
        SessionInfo? session = null;
        var seenHeader = false; var seenFooter = false; var headerReported = false;
        var lineNumber = 0; var badLine = 0; var counter = 0;
        using var stream = RecordingFormat.Decompress(raw, path);
        var reader = new BoundedLineReader(stream, limits.MaximumLineBytes, budget, cancellationToken);
        try
        {
            while (true)
            {
                string? line; bool tooLong; bool more;
                try { more = reader.TryReadLine(out line, out tooLong); }
                catch (InvalidDataException) { issues.Add(new(HistoryIssueKind.DamagedCompressedData, $"Part {number} contains damaged compressed data; what was readable before the damage is used.", number)); yield break; }
                catch (IOException ex) { issues.Add(new(HistoryIssueKind.PartUnavailable, $"Part {number} could not be read to its end ({ex.Message}); what was read before is used.", number)); yield break; }
                catch (HistoryLimitException ex)
                {
                    issues.Add(new(HistoryIssueKind.LimitReached, $"Reading of part {number} stopped: {ex.Message}, which is the limit. The rest of this recording was not analyzed.", number));
                    state.LimitReached = true;
                    yield break;
                }
                if (!more) break;
                lineNumber++;
                if ((++counter & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
                if (!tooLong && line!.Length == 0) continue;
                // A bad line that is followed by anything else was not the end of the file: that is damage, not a cut-off write.
                if (badLine != 0) { issues.Add(new(HistoryIssueKind.DamagedLine, $"Line {badLine} of part {number} could not be read and was skipped.", number)); badLine = 0; }
                if (tooLong) { issues.Add(new(HistoryIssueKind.DamagedLine, $"Line {lineNumber} of part {number} is longer than {limits.MaximumLineBytes:N0} bytes, which no recording line is, and was skipped.", number)); continue; }
                JsonDocument document;
                try { document = JsonDocument.Parse(line!); }
                catch (JsonException) { badLine = lineNumber; continue; }
                HistoryRecord? record = null;
                using (document)
                {
                    try { record = Decode(document.RootElement, number, codec, ref session, ref seenHeader, ref seenFooter); }
                    catch (RecordingFormatException) { throw; }
                    catch (MissingHeaderException)
                    {
                        issues.Add(new(HistoryIssueKind.DamagedHeader, $"Part {number} has no readable header (its first line is damaged); this part was skipped and the other parts are used.", number));
                        headerReported = true;
                        yield break;
                    }
                    catch (Exception ex) when (ex is not (OperationCanceledException or OutOfMemoryException))
                    {
                        issues.Add(new(HistoryIssueKind.DamagedLine, $"Line {lineNumber} of part {number} could not be decoded ({ex.Message}) and was skipped.", number));
                    }
                }
                if (record is not null) yield return record;
            }
            if (path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase) && !seenFooter && seenHeader)
            {
                // A compressed part is only ever made from a closed part, which ends with a footer: one that ends early was damaged afterwards.
                issues.Add(new(HistoryIssueKind.DamagedCompressedData, $"Part {number} is compressed but ends early; what was readable before the damage is used.", number));
            }
            else if (badLine != 0)
            {
                if (!seenFooter || isLast) issues.Add(new(HistoryIssueKind.TruncatedTail, $"Part {number} ends with an incomplete line; it was cut off when the recording was interrupted. Everything before it is intact.", number));
                else issues.Add(new(HistoryIssueKind.DamagedLine, $"The last line of part {number} could not be read and was skipped.", number));
            }
            if (!seenHeader && !headerReported)
                issues.Add(new(HistoryIssueKind.DamagedHeader, $"Part {number} has no header; it is not an EUC Lens recording part or it is damaged, and it was skipped.", number));
        }
        finally { state.BytesRead = reader.BytesRead; }
    }

    /// <summary>Only names (or numbers) of values the enum really has: a number outside the enum is a damaged line, not a value.</summary>
    private static T DefinedEnum<T>(string? text) where T : struct, Enum =>
        Enum.TryParse<T>(text, ignoreCase: false, out var value) && Enum.IsDefined(value) ? value : throw new InvalidDataException($"'{text}' is not a valid {typeof(T).Name}.");

    /// <summary>A gap of more than a year, or a number that is not finite, cannot be a measurement gap.</summary>
    private static TimeSpan GapOf(double seconds) =>
        double.IsFinite(seconds) && Math.Abs(seconds) <= 366 * 86400d ? TimeSpan.FromSeconds(seconds) : throw new InvalidDataException("A gap length is out of range.");

    private static HistoryRecord? Decode(JsonElement root, int number, RecordingFormat.Codec codec, ref SessionInfo? session, ref bool seenHeader, ref bool seenFooter)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("t", out var type)) throw new InvalidDataException("A line has no type.");
        var kind = type.GetString();
        if (!seenHeader && kind != "header") throw new MissingHeaderException();
        switch (kind)
        {
            case "header":
                var schema = root.TryGetProperty("schema", out var s) ? s.GetString() : null;
                if (schema != RecordingFormat.Schema)
                    throw new RecordingFormatException($"This recording uses an unsupported format ('{schema ?? "unknown"}'); this version of EUC Lens reads '{RecordingFormat.Schema}'.");
                seenHeader = true; codec.Table.Reset();
                return new HeaderRecord(number, root.GetProperty("startedUtc").GetDateTimeOffset().ToUniversalTime(), root.TryGetProperty("source", out var src) ? src.GetString() ?? "" : "",
                    root.TryGetProperty("app", out var app) ? app.GetString() ?? "" : "", root.TryGetProperty("id", out var id) && Guid.TryParse(id.GetString(), out var guid) ? guid : null);
            case "str":
                codec.Table.Set(root.GetProperty("id").GetInt32(), root.GetProperty("v").GetString() ?? "");
                return null;
            case "session":
                session = root.GetProperty("d").Deserialize<SessionInfo>(codec.Options) ?? throw new InvalidDataException("Empty session.");
                return new SessionRecord(number, session);
            case "s":
                var at = root.GetProperty("ts").GetDateTimeOffset();
                codec.Metrics.SampleTime = at;
                var body = root.GetProperty("d").Deserialize<RecordingFormat.SampleBody>(codec.Options) ?? throw new InvalidDataException("Empty sample.");
                return new SampleRecord(number, body.ToSnapshot(at.ToUniversalTime(), session ?? throw new InvalidDataException("A sample precedes its session line.")));
            case "event":
                return new EventRecord(number, root.GetProperty("at").GetDateTimeOffset().ToUniversalTime(), DefinedEnum<FindingTransition>(root.GetProperty("kind").GetString()),
                    root.GetProperty("rule").GetString() ?? "", DefinedEnum<Severity>(root.GetProperty("severity").GetString()), root.TryGetProperty("title", out var title) ? title.GetString() ?? "" : "");
            case "mark":
                return new MarkRecord(number, root.GetProperty("at").GetDateTimeOffset().ToUniversalTime(), root.GetProperty("kind").GetString() ?? "",
                    root.TryGetProperty("detail", out var detail) ? detail.GetString() ?? "" : "",
                    root.TryGetProperty("gapSeconds", out var gap) ? GapOf(gap.GetDouble()) : null,
                    root.TryGetProperty("id", out var markId) && Guid.TryParse(markId.GetString(), out var incident) ? incident : null,
                    root.TryGetProperty("seq", out var seq) ? seq.GetInt32() : null);
            case "footer":
                seenFooter = true;
                return new FooterRecord(number, root.GetProperty("endedUtc").GetDateTimeOffset().ToUniversalTime(), root.TryGetProperty("reason", out var reason) ? reason.GetString() ?? "" : "",
                    root.TryGetProperty("complete", out var complete) && complete.GetBoolean(), root.TryGetProperty("samples", out var samples) ? samples.GetInt64() : 0,
                    root.TryGetProperty("lost", out var lost) ? lost.GetInt64() : 0, root.TryGetProperty("incidents", out var incidents) ? (int)incidents.GetInt64() : 0);
            default:
                return null; // a line type of a later version: ignored, as the format promises
        }
    }
}
