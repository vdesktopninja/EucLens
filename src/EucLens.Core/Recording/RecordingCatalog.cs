using System.Globalization;
using System.Text.Json;

namespace EucLens.Core;

/// <param name="IsComplete">False for a recording that was interrupted (crash, kill, power loss, logoff without finalizing): it is readable up to its last complete line.</param>
public sealed record RecordingSummary(string ShortId, DateTimeOffset StartedUtc, DateTimeOffset? EndedUtc, TimeSpan Duration, long Bytes, long Samples,
    int Warnings, int Criticals, long LostSamples, bool IsComplete, string EndReason, string Source, IReadOnlyList<string> Parts, string Directory, int Incidents = 0)
{
    public string Display => $"{StartedUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss} · {FormatDuration(Duration)} · {Samples:N0} samples";
    private static string FormatDuration(TimeSpan d) => $"{(int)d.TotalHours:00}:{d.Minutes:00}:{d.Seconds:00}";
}

/// <summary>Lists the recordings in a folder. Reads the small index files; scans a part only when the index is missing or the recording was interrupted.</summary>
public static class RecordingCatalog
{
    public static IReadOnlyList<RecordingSummary> Scan(IRecordingStorage storage, string directory)
    {
        var files = storage.List(directory).Where(f => RecordingFormat.IsRecordingFile(Path.GetFileName(f.Path))).ToArray();
        var result = new List<RecordingSummary>();
        foreach (var group in files.GroupBy(f => IdOf(Path.GetFileName(f.Path))))
        {
            var parts = group.Select(f => (File: f, Ok: RecordingFormat.TryParsePart(Path.GetFileName(f.Path), out _, out _, out var n), Number: n, Gz: f.Path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase)))
                .Where(x => x.Ok).GroupBy(x => x.Number).OrderBy(g => g.Key).Select(g => g.OrderBy(x => x.Gz).First()).ToArray();
            if (parts.Length == 0) continue; // an index without data
            var meta = group.FirstOrDefault(f => Path.GetFileName(f.Path).EndsWith(".meta.json", StringComparison.Ordinal));
            var summary = Summarize(storage, directory, group.Key, parts.Select(p => p.File.Path).ToArray(), group.Sum(f => f.Length), meta);
            if (summary is not null) result.Add(summary);
        }
        return result.OrderByDescending(s => s.StartedUtc).ToArray();
    }

    private static string IdOf(string name) => RecordingFormat.ShortIdOf(name);

    private static RecordingSummary? Summarize(IRecordingStorage storage, string directory, string shortId, string[] parts, long bytes, RecordingFileInfo? meta)
    {
        try
        {
            if (meta is not null && ReadMeta(storage, meta.Path) is { Complete: true } done)
                return new(shortId, done.Started, done.Ended, done.Ended - done.Started, bytes, done.Samples, done.Warnings, done.Criticals, done.Lost, true, done.Reason, done.Source, parts, directory, done.Incidents);
            // Interrupted, or no index yet. The index counts the parts closed before it was written; the rest is scanned.
            var known = meta is not null ? ReadMeta(storage, meta.Path) : null;
            long samples = known?.Samples ?? 0, lost = known?.Lost ?? 0;
            int warnings = known?.Warnings ?? 0, criticals = known?.Criticals ?? 0, incidents = known?.Incidents ?? 0, counted = 0;
            DateTimeOffset? first = known?.Started, last = known?.Ended; var source = known?.Source ?? "";
            (string Reason, DateTimeOffset Ended)? finalFooter = null;
            for (var i = Math.Min(known?.Parts ?? 0, parts.Length - 1); i < parts.Length; i++)
            {
                foreach (var (type, root) in RecordingFormat.ReadLines(storage, parts[i]))
                {
                    switch (type)
                    {
                        case "header" when first is null: first = root.GetProperty("startedUtc").GetDateTimeOffset(); source = root.GetProperty("source").GetString() ?? ""; break;
                        case "s": samples++; last = root.GetProperty("ts").GetDateTimeOffset(); break;
                        case "event" when root.GetProperty("kind").GetString() is "Detected" or "Escalated":
                            if (root.GetProperty("severity").GetString() == "Critical") criticals++; else if (root.GetProperty("severity").GetString() == "Warning") warnings++;
                            break;
                        case "mark" when root.TryGetProperty("kind", out var kind) && kind.GetString() == nameof(RecordingMarkKind.UserIncident): counted++; break;
                        case "footer":
                            lost = Math.Max(lost, root.GetProperty("lost").GetInt64());
                            if (root.TryGetProperty("incidents", out var footerIncidents)) incidents = Math.Max(incidents, (int)footerIncidents.GetInt64());
                            // The final footer of a recording whose index file is missing or damaged still says that it ended properly.
                            if (root.TryGetProperty("complete", out var complete) && complete.GetBoolean() && root.GetProperty("reason").GetString() is { } endReason && endReason != "Rotated")
                                finalFooter = (endReason, root.GetProperty("endedUtc").GetDateTimeOffset());
                            break;
                    }
                }
            }
            if (first is not { } started) return null;
            incidents = Math.Max(incidents, (known?.Incidents ?? 0) + counted);
            if (finalFooter is { } done2)
                return new(shortId, started, done2.Ended, done2.Ended - started, bytes, samples, warnings, criticals, lost, true, done2.Reason, source, parts, directory, incidents);
            return new(shortId, started, last, (last ?? started) - started, bytes, samples, warnings, criticals, lost, false, "Interrupted", source, parts, directory, incidents);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or KeyNotFoundException or InvalidOperationException or FormatException) { return null; }
    }

    private sealed record Meta(DateTimeOffset Started, DateTimeOffset Ended, bool Complete, string Reason, string Source, long Samples, int Warnings, int Criticals, long Lost, int Parts, int Incidents);

    private static Meta? ReadMeta(IRecordingStorage storage, string path)
    {
        try
        {
            using var stream = storage.OpenRead(path);
            using var document = JsonDocument.Parse(stream);
            var r = document.RootElement;
            return new(r.GetProperty("startedUtc").GetDateTimeOffset(), r.GetProperty("endedUtc").GetDateTimeOffset(), r.GetProperty("complete").GetBoolean(),
                r.TryGetProperty("reason", out var reason) ? reason.GetString() ?? "" : "", r.GetProperty("source").GetString() ?? "",
                r.GetProperty("samples").GetInt64(), (int)r.GetProperty("warnings").GetInt64(), (int)r.GetProperty("criticals").GetInt64(), r.GetProperty("lost").GetInt64(), (int)r.GetProperty("parts").GetInt64(),
                r.TryGetProperty("incidents", out var incidents) ? (int)incidents.GetInt64() : 0);
        }
        catch (Exception ex) when (ex is IOException or JsonException or KeyNotFoundException or InvalidOperationException) { return null; }
    }

    public static IEnumerable<SessionSnapshot> ReadSamples(IRecordingStorage storage, RecordingSummary recording) => RecordingFormat.ReadSamples(storage, recording.Parts);
}
