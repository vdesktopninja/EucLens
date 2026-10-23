using EucLens.Core;

namespace EucLens.App;

public interface IDesktopServices
{
    string? SelectExportPath(bool json);
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
    /// <summary>Asked when the application is about to end while a recording is running.</summary>
    bool ConfirmStopRecordingAndExit() => true;
}
public sealed record Preferences(bool Dark, RemoteProtocol Protocol, DemoScenario Scenario, WorkloadContext Workload, MonitoringMode Mode = MonitoringMode.RealSession);

/// <summary>Outcome of loading diagnostic thresholds; invalid files fall back to defaults and report why.</summary>
public sealed record ThresholdLoadResult(DiagnosticThresholds Thresholds, bool FromFile, IReadOnlyList<string> Errors, string Path);
public interface IDiagnosticSettingsStore
{
    string Path { get; }
    Task<ThresholdLoadResult> LoadAsync();
    /// <summary>Writes the default values to the settings file so they can be edited from a known-good state.</summary>
    Task<ThresholdLoadResult> RestoreDefaultsAsync();
}
