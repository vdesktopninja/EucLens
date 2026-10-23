namespace EucLens.Core;

/// <summary>Whether EUC Lens is collecting measurements. Independent of the window: a hidden window keeps <see cref="Running"/>.</summary>
public enum MonitoringState { Stopped, Running, Paused }

/// <summary>What the notification-area icon shows. Derived from real service state only, never from a page or window.</summary>
public enum TrayState { Idle, Monitoring, Recording, Warning, Paused }

/// <summary>Real operational state of the application, the single input of <see cref="TrayStateResolver"/>.</summary>
/// <param name="StorageProblem">Short text while a recording write problem is unresolved; null otherwise.</param>
public sealed record MonitoringStatus(MonitoringState Monitoring, bool IsRecording, int ActiveProblems = 0, int CriticalProblems = 0, string? StorageProblem = null)
{
    public static MonitoringStatus Initial { get; } = new(MonitoringState.Stopped, false);
}

/// <param name="IconKey">Matches the file <c>tray-{IconKey}.ico</c>.</param>
public sealed record TrayAppearance(TrayState State, string IconKey, string Tooltip)
{
    public string IconFile => $"tray-{IconKey}.ico";
}

/// <summary>
/// Deterministic tray state. Priority, highest first: Paused, Recording (with an amber triangle added while problems exist),
/// Warning (problems or a storage problem), Monitoring, Idle. Recording only counts while monitoring is running, so the icon
/// never suggests recording after it has stopped.
/// </summary>
public static class TrayStateResolver
{
    public const int MaximumTooltipLength = 127; // NotifyIcon.Text limit

    public static TrayAppearance Resolve(MonitoringStatus status)
    {
        var problems = status.ActiveProblems > 0;
        var storage = status.StorageProblem is not null;
        string Problems() => status.ActiveProblems == 1 ? "1 active problem" : $"{status.ActiveProblems} active problems";
        (TrayState State, string Key, string Text) result = status.Monitoring switch
        {
            MonitoringState.Stopped => (TrayState.Idle, "idle", "not monitoring"),
            MonitoringState.Paused => (TrayState.Paused, "paused", status.IsRecording ? "monitoring paused, recording suspended" : "monitoring paused"),
            _ when status.IsRecording => problems
                ? (TrayState.Recording, "recording-warning", $"recording · {Problems()}")
                : (TrayState.Recording, "recording", "recording"),
            _ when problems || storage => (TrayState.Warning, "warning", problems ? $"monitoring · {Problems()}" : "monitoring"),
            _ => (TrayState.Monitoring, "monitoring", "monitoring"),
        };
        var text = "EUC Lens: " + result.Text;
        if (storage && status.Monitoring != MonitoringState.Stopped) text += " · storage problem";
        if (text.Length > MaximumTooltipLength) text = text[..MaximumTooltipLength];
        return new(result.State, result.Key, text);
    }

    public static IReadOnlyList<string> IconKeys { get; } = ["idle", "monitoring", "recording", "recording-warning", "warning", "paused"];
}
