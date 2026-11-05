using EucLens.Core;

namespace EucLens.App;

public interface IDesktopServices
{
    string? SelectExportPath(bool json);
    /// <summary>The save dialog of an export that is not the session recording: its own title and file name. Implementations without one fall back to the recording export dialog.</summary>
    string? SelectExportPath(bool json, string title, string fileStem) => SelectExportPath(json);
    bool ConfirmNewRecording();
    void ApplyTheme(bool dark);
    Task SavePreferencesAsync(Preferences preferences);
    Task<Preferences?> LoadPreferencesAsync();
    /// <summary>Places text on the clipboard; false when the clipboard is unavailable.</summary>
    bool CopyText(string text) => false;
    /// <summary>Lets the user pick a folder; null when cancelled or unsupported.</summary>
    string? SelectFolder(string? initialDirectory) => null;
    /// <summary>Opens a folder, or selects a file in its folder, in Windows Explorer. False when that is not possible.</summary>
    bool OpenPath(string path, bool selectFile = false) => false;
    /// <summary>Opens a page of EUC Lens's own repository (HTTPS, github.com) in the system browser. False for any other address or when no browser can be started: nothing is ever opened for a reply from a server.</summary>
    bool OpenUrl(Uri url) => false;
    /// <summary>Asked when the application is about to end while a recording is running.</summary>
    bool ConfirmStopRecordingAndExit() => true;
}
/// <summary>
/// Saved user preferences: the theme only. preferences.json written by 0.5.0 and earlier also holds Protocol, Scenario, Workload and Mode
/// (Real Session or Demo Mode); those properties are not part of this record, so they are ignored when read and dropped when saved again.
/// </summary>
public sealed record Preferences(bool Dark);

/// <summary>Outcome of loading diagnostic thresholds; invalid files fall back to defaults and report why.</summary>
public sealed record ThresholdLoadResult(DiagnosticThresholds Thresholds, bool FromFile, IReadOnlyList<string> Errors, string Path);
public interface IDiagnosticSettingsStore
{
    string Path { get; }
    Task<ThresholdLoadResult> LoadAsync();
    /// <summary>Writes the default values to the settings file so they can be edited from a known-good state.</summary>
    Task<ThresholdLoadResult> RestoreDefaultsAsync();
}
