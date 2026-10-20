using EucLens.Core;

namespace EucLens.App;

public interface IDesktopServices
{
    string? SelectExportPath(bool json);
    bool ConfirmNewRecording();
    void ApplyTheme(bool dark);
    Task SavePreferencesAsync(Preferences preferences);
    Task<Preferences?> LoadPreferencesAsync();
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
