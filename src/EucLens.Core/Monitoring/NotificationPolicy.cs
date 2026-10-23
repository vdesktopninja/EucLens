namespace EucLens.Core;

public enum NotificationKind { Finding, RecordingStopped, StorageProblem }

/// <summary>Text of a desktop notification. Contains no user name, address, host name or measurement.</summary>
public sealed record NotificationRequest(NotificationKind Kind, string Title, string Body, string Key);

public sealed record NotificationOptions(TimeSpan Cooldown, TimeSpan MinimumGap)
{
    public static NotificationOptions Default { get; } = new(TimeSpan.FromMinutes(30), TimeSpan.FromSeconds(60));
}

/// <summary>
/// Decides whether the application raises a desktop notification. Only conclusions the deterministic engine has already confirmed
/// qualify (a detected or escalated warning or critical finding), plus unexpected recording stops and storage problems. A raw metric
/// spike never does. The same problem is announced at most once per cooldown, all notifications keep a minimum gap, nothing is shown
/// while the Windows session is locked, and the text is generic by design.
/// </summary>
public sealed class NotificationPolicy(TimeProvider clock, NotificationOptions? options = null)
{
    private readonly NotificationOptions options = options ?? NotificationOptions.Default;
    private readonly Dictionary<string, DateTimeOffset> lastShown = [];
    private DateTimeOffset? lastAny;
    public bool Enabled { get; set; }
    /// <summary>Set while the Windows session is locked: notifications are dropped, not queued.</summary>
    public bool SessionLocked { get; set; }

    public NotificationRequest? OnDiagnosticEvents(IEnumerable<DiagnosticEvent> events)
    {
        if (!Enabled || SessionLocked) return null;
        var confirmed = events.Where(e => e.Transition is FindingTransition.Detected or FindingTransition.Escalated
            && e.Finding is { IsActive: true, Severity: >= Severity.Warning } && e.Finding.Domain != DiagnosticDomain.DataQuality).ToArray();
        if (confirmed.Length == 0) return null;
        var worst = confirmed.OrderByDescending(e => e.Finding.Severity).First();
        var fresh = confirmed.Where(e => Allowed(e.Finding.RuleId)).ToArray();
        if (fresh.Length == 0 || !GapElapsed()) return null;
        foreach (var e in fresh) lastShown[e.Finding.RuleId] = clock.GetUtcNow();
        lastAny = clock.GetUtcNow();
        var severity = fresh.Max(e => e.Finding.Severity);
        var first = fresh.OrderByDescending(e => e.Finding.Severity).First();
        var what = fresh.Length == 1 ? Describe(first.Finding.RuleId) : $"{fresh.Length} diagnostic problems were detected";
        return new(NotificationKind.Finding, $"EUC Lens: {severity}", $"{what}. Open EUC Lens for details.", first.Finding.RuleId);
    }

    public NotificationRequest? OnRecordingStopped(RecordingEndReason reason)
    {
        if (!Enabled || SessionLocked) return null;
        var text = reason switch
        {
            RecordingEndReason.StorageError => "The recording stopped because of a storage problem.",
            RecordingEndReason.QuotaReached => "The recording stopped because the storage quota was reached.",
            RecordingEndReason.Unknown => "The recording stopped unexpectedly.",
            _ => null // user and application initiated endings are expected
        };
        return text is null ? null : Raise(NotificationKind.RecordingStopped, "recording.stopped:" + reason, "EUC Lens: Recording stopped", text);
    }

    public NotificationRequest? OnStorageProblem(RecordingFaultKind kind)
    {
        if (!Enabled || SessionLocked) return null;
        return Raise(NotificationKind.StorageProblem, "storage:" + kind, "EUC Lens: Storage problem", "Recording data could not be written. Open EUC Lens for details.");
    }

    private NotificationRequest? Raise(NotificationKind kind, string key, string title, string body)
    {
        if (!Allowed(key) || !GapElapsed()) return null;
        lastShown[key] = clock.GetUtcNow();
        lastAny = clock.GetUtcNow();
        return new(kind, title, body, key);
    }

    private bool Allowed(string key) => !lastShown.TryGetValue(key, out var at) || clock.GetUtcNow() - at >= options.Cooldown;
    private bool GapElapsed() => lastAny is not { } at || clock.GetUtcNow() - at >= options.MinimumGap;

    /// <summary>Fixed plain-language names; measurements are deliberately not included.</summary>
    public static string Describe(string ruleId) => ruleId switch
    {
        var r when r.StartsWith("connection.rtt", StringComparison.Ordinal) => "Sustained high round-trip time",
        var r when r.StartsWith("connection.packetloss", StringComparison.Ordinal) => "Sustained packet loss",
        var r when r.StartsWith("graphics.fps", StringComparison.Ordinal) || r.Contains("lowfps", StringComparison.Ordinal) => "Low frame rate during an active workload",
        var r when r.StartsWith("guest.", StringComparison.Ordinal) || r.StartsWith("host.", StringComparison.Ordinal) => "Guest resource pressure",
        var r when r.StartsWith("gpu.", StringComparison.Ordinal) => "GPU resource pressure",
        _ => "A diagnostic problem was detected"
    };
}
