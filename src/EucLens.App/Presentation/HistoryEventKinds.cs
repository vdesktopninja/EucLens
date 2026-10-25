using EucLens.Core;

namespace EucLens.App.Presentation;

/// <summary>
/// Maps timeline events to their icon and label. WPF-free so the mapping is unit-tested. The icon tag is the value of <c>Tag</c> on the <c>EventIcon</c> style;
/// each kind has its own shape (findings reuse the severity shapes), and the label is always shown beside it: colour never carries the meaning alone.
/// </summary>
public static class HistoryEventKinds
{
    public static readonly IReadOnlyList<string> IconTags =
    [
        "Incident", "FindingInfo", "FindingWarning", "FindingCritical", "FindingRecovered", "Pause", "Resume", "Disconnect", "Reconnect", "Availability", "DataLoss",
        "Lock", "Unlock", "Start", "End", "Note", "SessionChange", "Other"
    ];

    public static string IconTag(HistoryEvent e) => e.Kind switch
    {
        HistoryEventKind.UserIncident => "Incident",
        HistoryEventKind.FindingRecovered or HistoryEventKind.FindingExpired => "FindingRecovered",
        HistoryEventKind.FindingDetected or HistoryEventKind.FindingEscalated or HistoryEventKind.FindingDowngraded =>
            e.Severity switch { Severity.Critical => "FindingCritical", Severity.Warning => "FindingWarning", _ => "FindingInfo" },
        HistoryEventKind.MonitoringPaused => "Pause",
        HistoryEventKind.MonitoringResumed => "Resume",
        HistoryEventKind.RemoteDisconnected => "Disconnect",
        HistoryEventKind.RemoteReconnected => "Reconnect",
        HistoryEventKind.AvailabilityChanged => "Availability",
        HistoryEventKind.DataLoss => "DataLoss",
        HistoryEventKind.SessionLocked => "Lock",
        HistoryEventKind.SessionUnlocked => "Unlock",
        HistoryEventKind.RecordingStarted => "Start",
        HistoryEventKind.RecordingEnded => "End",
        HistoryEventKind.Note => "Note",
        HistoryEventKind.SessionChanged => "SessionChange",
        _ => "Other"
    };

    /// <summary>The kind of event in words, shown next to the icon.</summary>
    public static string Label(HistoryEventKind kind) => kind switch
    {
        HistoryEventKind.UserIncident => "User incident",
        HistoryEventKind.FindingDetected or HistoryEventKind.FindingEscalated or HistoryEventKind.FindingDowngraded or HistoryEventKind.FindingRecovered or HistoryEventKind.FindingExpired => "Diagnostic finding",
        HistoryEventKind.MonitoringPaused or HistoryEventKind.MonitoringResumed => "Monitoring",
        HistoryEventKind.RemoteDisconnected or HistoryEventKind.RemoteReconnected => "Remote session",
        HistoryEventKind.AvailabilityChanged => "Availability",
        HistoryEventKind.DataLoss => "Data loss",
        HistoryEventKind.SessionLocked or HistoryEventKind.SessionUnlocked or HistoryEventKind.SessionChanged => "Windows session",
        HistoryEventKind.RecordingStarted or HistoryEventKind.RecordingEnded => "Recording",
        HistoryEventKind.Note => "Note",
        _ => "Event"
    };

    public static bool IsIncident(HistoryEvent e) => e.Kind == HistoryEventKind.UserIncident;
}

public enum HistoryEventFilter { All, Incidents, Findings, Other }

public static class HistoryEventFilters
{
    public static bool Matches(HistoryEventFilter filter, HistoryEvent e) => filter switch
    {
        HistoryEventFilter.Incidents => e.Kind == HistoryEventKind.UserIncident,
        HistoryEventFilter.Findings => e.IsFinding,
        HistoryEventFilter.Other => e.Kind != HistoryEventKind.UserIncident && !e.IsFinding,
        _ => true
    };
}
