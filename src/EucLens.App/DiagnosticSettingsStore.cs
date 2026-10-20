using System.IO;
using System.Text.Json;
using EucLens.Core;

namespace EucLens.App;

/// <summary>Thresholds in %LOCALAPPDATA%\EucLens\diagnostics.json. Missing properties keep their defaults.</summary>
public sealed class DiagnosticSettingsStore : IDiagnosticSettingsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true,
        // Typos in property names are reported instead of silently ignored.
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
    };
    public string Path { get; } = System.IO.Path.Combine(DesktopServices.DataDirectory, "diagnostics.json");

    public async Task<ThresholdLoadResult> LoadAsync()
    {
        if (!File.Exists(Path)) return new(DiagnosticThresholds.Defaults, false, [], Path);
        try
        {
            await using var stream = File.OpenRead(Path);
            var loaded = await JsonSerializer.DeserializeAsync<DiagnosticThresholds>(stream, Options) ?? DiagnosticThresholds.Defaults;
            var errors = loaded.Validate();
            return errors.Count == 0 ? new(loaded, true, [], Path) : new(DiagnosticThresholds.Defaults, true, errors, Path);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return new(DiagnosticThresholds.Defaults, true, [ex.Message], Path);
        }
    }

    public async Task<ThresholdLoadResult> RestoreDefaultsAsync()
    {
        Directory.CreateDirectory(DesktopServices.DataDirectory);
        var temporary = Path + ".tmp";
        await using (var stream = File.Create(temporary)) await JsonSerializer.SerializeAsync(stream, DiagnosticThresholds.Defaults, Options);
        File.Move(temporary, Path, true);
        return new(DiagnosticThresholds.Defaults, true, [], Path);
    }
}
