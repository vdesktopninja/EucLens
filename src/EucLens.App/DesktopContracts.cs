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
public sealed record Preferences(bool Dark, RemoteProtocol Protocol, DemoScenario Scenario, WorkloadContext Workload);
