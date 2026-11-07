using System.Globalization;
using System.Text;

namespace EucLens.Core;

/// <summary>
/// Optional export of the incident timeline. It is a separate file with its own columns: the session CSV and JSON exports keep exactly the schema and
/// meaning they always had. Text cells that a spreadsheet could read as a formula are prefixed with an apostrophe.
/// </summary>
public static class HistoryExport
{
    public const string Header = "recording,event_type,time_utc,time_local,severity,title,detail,incident_id,incident_no,category,description,gap_seconds";

    public static string TimelineCsv(LoadedHistory history, RecordingAnnotations? annotations, TimeZoneInfo? zone = null, bool incidentsOnly = false)
    {
        zone ??= TimeZoneInfo.Local;
        var text = new StringBuilder(Header).Append("\r\n");
        foreach (var e in history.Events)
        {
            if (incidentsOnly && e.Kind != HistoryEventKind.UserIncident) continue;
            var note = e.IncidentId is { } id ? annotations?.For(id) : null;
            text.Append(string.Join(',',
                Cell(history.Info.ShortId), Cell(e.Kind.ToString()), Cell(e.At.UtcDateTime.ToString("O", CultureInfo.InvariantCulture)),
                Cell(HistoryTime.ToLocal(e.At, zone).ToString("yyyy-MM-dd'T'HH:mm:ss.fffzzz", CultureInfo.InvariantCulture)),
                Cell(e.Severity?.ToString() ?? ""), Cell(e.Title), Cell(e.Detail), Cell(e.IncidentId?.ToString("D") ?? ""), Cell(e.Sequence?.ToString(CultureInfo.InvariantCulture) ?? ""),
                Cell(note is null ? "" : RecordingAnnotationStore.DisplayName(note.Category)), Cell(note?.Description ?? ""),
                Cell(e.Gap is { } gap ? Math.Round(gap.TotalSeconds, 3).ToString(CultureInfo.InvariantCulture) : ""))).Append("\r\n");
        }
        return text.ToString();
    }

    /// <summary>
    /// Writes <see cref="TimelineCsv"/> (UTF-8 without a byte-order mark, as always) through the same atomic writer as the findings export: a temporary file beside the target,
    /// flushed, then moved into place. A failure or cancellation leaves the previous file (or none) untouched and no temporary file behind, and an existing recording is never overwritten.
    /// </summary>
    public static Task WriteTimelineCsvAsync(string path, LoadedHistory history, RecordingAnnotations? annotations, TimeZoneInfo? zone = null, bool incidentsOnly = false,
        CancellationToken cancellationToken = default) =>
        FindingsExport.WriteAsync(path, () => new UTF8Encoding(false).GetBytes(TimelineCsv(history, annotations, zone, incidentsOnly)), cancellationToken);

    internal static string Cell(string value)
    {
        if (value.Length == 0) return "";
        value = CsvField.Guard(value);          // also catches a formula hidden behind leading whitespace, a line feed or invisible characters
        return value.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : value;
    }
}
