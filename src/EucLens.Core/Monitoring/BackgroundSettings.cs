using System.Text.Json;

namespace EucLens.Core;

/// <summary>
/// Options of the background monitoring section. Defaults are conservative: nothing hides the window, no notification is shown,
/// nothing records on its own and nothing starts with Windows.
/// </summary>
public sealed record BackgroundSettings
{
    public const int MinimumQuotaMiB = 64;
    public const int MaximumQuotaMiB = 102_400;
    public bool MinimizeToTray { get; init; }
    public bool CloseToTray { get; init; }
    public bool Notifications { get; init; }
    /// <summary>Null selects the default folder in the user profile.</summary>
    public string? RecordingDirectory { get; init; }
    /// <summary>Upper bound for all EUC Lens recordings in the directory. Older recordings are removed first; the active one never is.</summary>
    public int RetentionQuotaMiB { get; init; } = 1024;
    /// <summary>Recordings older than this are removed. 0 keeps them until the quota requires removal.</summary>
    public int RetentionDays { get; init; } = 14;
    /// <summary>Start collecting when EUC Lens starts. On by default because that is how the application always behaved.</summary>
    public bool MonitorOnStart { get; init; } = true;
    /// <summary>Opt-in: begin a persistent recording when monitoring starts.</summary>
    public bool RecordOnStart { get; init; }
    /// <summary>The one-time explanation of background operation was shown.</summary>
    public bool TrayHintShown { get; init; }

    public static BackgroundSettings Defaults { get; } = new();

    public string EffectiveDirectory => RecordingLocation.Resolve(RecordingDirectory);

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (RetentionQuotaMiB is < MinimumQuotaMiB or > MaximumQuotaMiB) errors.Add($"Recording quota must be between {MinimumQuotaMiB} and {MaximumQuotaMiB} MiB.");
        if (RetentionDays is < 0 or > 3650) errors.Add("Retention must be between 0 and 3650 days (0 = until the quota requires removal).");
        if (!string.IsNullOrWhiteSpace(RecordingDirectory))
        {
            if (!Path.IsPathFullyQualified(RecordingDirectory)) errors.Add("The recording folder must be a full path such as D:\\Recordings or \\\\server\\share\\EucLens.");
            else if (RecordingDirectory.IndexOfAny(Path.GetInvalidPathChars()) >= 0) errors.Add("The recording folder contains characters that are not allowed in a path.");
        }
        return errors;
    }
}

public sealed record BackgroundSettingsLoad(BackgroundSettings Settings, bool FromFile, IReadOnlyList<string> Errors);

/// <summary>Persists <see cref="BackgroundSettings"/> as JSON next to the other EucLens settings. Invalid files fall back to defaults and say why.</summary>
public sealed class BackgroundSettingsStore(string path)
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
    };
    public string Path { get; } = path;

    public async Task<BackgroundSettingsLoad> LoadAsync()
    {
        if (!File.Exists(Path)) return new(BackgroundSettings.Defaults, false, []);
        try
        {
            await using var stream = File.OpenRead(Path);
            var loaded = await JsonSerializer.DeserializeAsync<BackgroundSettings>(stream, Options) ?? BackgroundSettings.Defaults;
            var errors = loaded.Validate();
            return errors.Count == 0 ? new(loaded, true, []) : new(BackgroundSettings.Defaults, true, errors);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        { return new(BackgroundSettings.Defaults, true, [ex.Message]); }
    }

    /// <summary>Throws <see cref="ArgumentException"/> for invalid settings and the usual I/O exceptions when the file cannot be written.</summary>
    public async Task SaveAsync(BackgroundSettings settings)
    {
        var errors = settings.Validate();
        if (errors.Count > 0) throw new ArgumentException(string.Join(" ", errors), nameof(settings));
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path))!);
        var temporary = Path + ".tmp";
        try
        {
            await using (var stream = File.Create(temporary)) await JsonSerializer.SerializeAsync(stream, settings, Options);
            File.Move(temporary, Path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

/// <summary>Where recordings live and what that means on a non-persistent VDI.</summary>
public static class RecordingLocation
{
    public static string DefaultDirectory => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EucLens", "Recordings");

    public static string Resolve(string? configured) => string.IsNullOrWhiteSpace(configured) ? DefaultDirectory : System.IO.Path.GetFullPath(configured);

    /// <summary>Plain-language note about the durability of the chosen folder, or null when nothing needs saying.</summary>
    public static string? DurabilityNote(string? configured)
    {
        var path = Resolve(configured);
        if (path.StartsWith(@"\\", StringComparison.Ordinal))
            return "Network location: recordings survive a VDI refresh, but the recording stops with a storage problem if the share becomes unreachable.";
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(configured) || path.StartsWith(profile, StringComparison.OrdinalIgnoreCase))
            return "This folder is in your user profile on this machine. On a non-persistent VDI, a refresh or reset can delete it. Choose a persistent drive or a network share for recordings you must keep.";
        return "Make sure this folder is on persistent storage if the desktop is refreshed or reset.";
    }
}
