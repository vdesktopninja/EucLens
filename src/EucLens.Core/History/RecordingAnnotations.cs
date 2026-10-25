using System.Text;
using System.Text.Json;

namespace EucLens.Core;

public enum IncidentCategory { Unspecified, Freeze, SlowResponse, DisplayStutter, ApplicationUnresponsive, Other }

/// <summary>An administrator's note on a user incident. Kept beside the recording, never inside it.</summary>
public sealed record IncidentAnnotation(IncidentCategory Category, string Description, DateTimeOffset UpdatedUtc);

public sealed record RecordingAnnotations(Guid? RecordingId, IReadOnlyDictionary<Guid, IncidentAnnotation> Incidents)
{
    public static RecordingAnnotations Empty { get; } = new(null, new Dictionary<Guid, IncidentAnnotation>());
    public IncidentAnnotation? For(Guid incidentId) => Incidents.GetValueOrDefault(incidentId);
}

/// <summary>
/// The versioned annotation sidecar <c>EucLens-&lt;stamp&gt;-&lt;id8&gt;.annotations.json</c> (schema <c>euclens.annotations/1</c>). A recording is never rewritten to add a
/// description: the sidecar is replaced atomically (temporary file, then rename). It is part of the recording's file group, so retention removes it
/// together with the recording, and an old recording without one is simply a recording without annotations.
/// </summary>
public static class RecordingAnnotationStore
{
    public const string Schema = "euclens.annotations/1";
    public const int MaximumDescription = 500;

    public static string PathFor(string directory, string stamp, string shortId) => Path.Combine(directory, RecordingFormat.AnnotationName(stamp, shortId));

    public static string DisplayName(IncidentCategory category) => category switch
    {
        IncidentCategory.Freeze => "Freeze", IncidentCategory.SlowResponse => "Lag / slow response", IncidentCategory.DisplayStutter => "Display stutter",
        IncidentCategory.ApplicationUnresponsive => "Application unresponsive", IncidentCategory.Other => "Other", _ => "Not categorized"
    };

    /// <summary>Description text as it will be stored: trimmed, control characters removed. Throws when it is too long.</summary>
    public static string Normalize(string? description)
    {
        var text = new string((description ?? "").Where(c => !char.IsControl(c) || c is '\n' or '\t').ToArray()).Trim();
        if (text.Length > MaximumDescription) throw new ArgumentException($"A description can have at most {MaximumDescription} characters.", nameof(description));
        return text;
    }

    /// <summary>Reads the sidecar. A missing file is no annotations; an unreadable or newer one is reported through <paramref name="issues"/> and treated as empty.</summary>
    public static RecordingAnnotations Load(IRecordingStorage storage, string directory, string stamp, string shortId, List<HistoryIssue>? issues = null)
    {
        var path = PathFor(directory, stamp, shortId);
        try { return Read(storage, path) ?? RecordingAnnotations.Empty; }
        catch (Exception ex) when (ex is JsonException or RecordingFormatException or IOException or UnauthorizedAccessException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            issues?.Add(new(HistoryIssueKind.UnreadableAnnotations, $"The annotation file of this recording could not be used ({ex.Message}); annotations are not shown."));
            return RecordingAnnotations.Empty;
        }
    }

    private static RecordingAnnotations? Read(IRecordingStorage storage, string path)
    {
        Stream stream;
        try { stream = storage.OpenRead(path); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        using (stream)
        {
            using var document = JsonDocument.Parse(stream);
            var root = document.RootElement;
            var schema = root.GetProperty("schema").GetString();
            if (schema != Schema) throw new RecordingFormatException($"unsupported annotation format '{schema}'");
            Guid? recording = root.TryGetProperty("recordingId", out var rid) && Guid.TryParse(rid.GetString(), out var g) ? g : null;
            var map = new Dictionary<Guid, IncidentAnnotation>();
            if (root.TryGetProperty("incidents", out var incidents))
                foreach (var property in incidents.EnumerateObject())
                {
                    if (!Guid.TryParse(property.Name, out var id)) continue;
                    var category = Enum.TryParse<IncidentCategory>(property.Value.TryGetProperty("category", out var c) ? c.GetString() : null, out var parsed) ? parsed : IncidentCategory.Unspecified;
                    var description = property.Value.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "";
                    var updated = property.Value.TryGetProperty("updatedUtc", out var u) ? u.GetDateTimeOffset() : DateTimeOffset.MinValue;
                    map[id] = new(category, description.Length > MaximumDescription ? description[..MaximumDescription] : description, updated);
                }
            return new(recording, map);
        }
    }

    /// <summary>
    /// Sets (or, with null, removes) the annotation of one incident, keeping every other annotation exactly as it is. An unreadable existing file is moved aside, not overwritten.
    /// </summary>
    public static RecordingAnnotations Update(IRecordingStorage storage, string directory, string stamp, string shortId, Guid? recordingId, Guid incidentId, IncidentAnnotation? annotation)
    {
        if (annotation is not null) _ = Normalize(annotation.Description);
        var path = PathFor(directory, stamp, shortId);
        RecordingAnnotations current;
        try { current = Read(storage, path) ?? RecordingAnnotations.Empty; }
        catch (Exception ex) when (ex is JsonException or RecordingFormatException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            storage.Rename(path, path + ".unreadable-" + DateTimeOffset.UtcNow.UtcTicks);
            current = RecordingAnnotations.Empty;
        }
        var map = new Dictionary<Guid, IncidentAnnotation>(current.Incidents);
        if (annotation is null) map.Remove(incidentId); else map[incidentId] = annotation with { Description = Normalize(annotation.Description) };
        var result = new RecordingAnnotations(recordingId ?? current.RecordingId, map);
        Write(storage, directory, path, result);
        return result;
    }

    private static void Write(IRecordingStorage storage, string directory, string path, RecordingAnnotations annotations)
    {
        storage.EnsureDirectory(directory);
        var temporary = path + ".partial";
        try { storage.Delete(temporary); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        try
        {
            using (var stream = storage.Create(temporary))
            using (var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
            {
                w.WriteStartObject();
                w.WriteString("schema", Schema);
                if (annotations.RecordingId is { } id) w.WriteString("recordingId", id.ToString("D"));
                w.WriteStartObject("incidents");
                foreach (var (incident, note) in annotations.Incidents.OrderBy(i => i.Value.UpdatedUtc).ThenBy(i => i.Key))
                {
                    w.WriteStartObject(incident.ToString("D"));
                    w.WriteString("category", note.Category.ToString());
                    w.WriteString("description", note.Description);
                    w.WriteString("updatedUtc", note.UpdatedUtc.UtcDateTime.ToString("O"));
                    w.WriteEndObject();
                }
                w.WriteEndObject();
                w.WriteEndObject();
            }
            storage.Rename(temporary, path);
        }
        catch (Exception)
        {
            try { storage.Delete(temporary); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            throw;
        }
    }
}
