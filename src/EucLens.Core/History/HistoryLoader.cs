using System.Security.Cryptography;
using System.Text;

namespace EucLens.Core;

public sealed record HistoryLoadProgress(long Samples, int PartsRead, int Parts);

/// <summary>Reads a recording into a <see cref="LoadedHistory"/>. Runs on a worker thread, checks the cancellation token while it reads, and keeps no raw lines.</summary>
public static class HistoryLoader
{
    /// <summary>More samples than this (about 69 hours at 1 Hz) are not loaded; the recording says so instead of using unbounded memory.</summary>
    public const int MaximumSamples = 250_000;
    private const int RecentSamples = 3;
    private const int MaximumRetainedIncidents = 400;

    public static Task<LoadedHistory> LoadAsync(IRecordingStorage storage, string directory, string shortId, string? activeShortId = null,
        IProgress<HistoryLoadProgress>? progress = null, CancellationToken cancellationToken = default, int maximumSamples = MaximumSamples) =>
        Task.Run(() => Load(storage, directory, shortId, activeShortId, progress, cancellationToken, maximumSamples), cancellationToken);

    public static string ProtocolName(RemoteProtocol protocol) => protocol switch
    {
        RemoteProtocol.CitrixHdx => "Citrix HDX", RemoteProtocol.OmnissaBlast => "Omnissa Horizon Blast", RemoteProtocol.MicrosoftRdp => "Microsoft RDP", _ => "Unknown protocol"
    };

    public static LoadedHistory Load(IRecordingStorage storage, string directory, string shortId, string? activeShortId = null,
        IProgress<HistoryLoadProgress>? progress = null, CancellationToken cancellationToken = default, int maximumSamples = MaximumSamples)
    {
        var issues = new List<HistoryIssue>();
        var parts = HistoryReader.ResolveParts(storage, directory, shortId, issues);
        if (parts.Count == 0) throw new RecordingFormatException($"No files of recording {shortId} were found in {directory}.");

        var channels = HistoryChannels.All;
        var ticks = new List<long>();
        var columns = channels.Select(_ => new List<double>()).ToArray();
        var units = new string?[channels.Count]; var reasons = new string?[channels.Count]; var valid = new int[channels.Count];

        var events = new List<HistoryEvent>();
        var seen = new HashSet<(string Rule, FindingTransition Transition, long Ticks)>();
        var incidents = new List<HistoryIncident>();
        var pending = new List<(int Index, HistoryIncident Incident)>();
        var recent = new Queue<SessionSnapshot>();
        var protocols = new List<string>();
        SessionInfo? lastSession = null; var sessionChanged = false;
        DateTimeOffset? started = null; string source = "", appVersion = ""; Guid? recordingId = null;
        FooterRecord? finalFooter = null; long footerLost = 0;
        var order = 0; var sorted = true; var partsRead = 0; var lastPart = 0;

        void Add(HistoryEventKind kind, DateTimeOffset at, string title, string detail = "", Severity? severity = null, string? rule = null, Guid? incident = null, int? sequence = null, TimeSpan? gap = null) =>
            events.Add(new(kind, at, title, detail, severity, rule, incident, sequence, gap, order++));

        foreach (var record in HistoryReader.Read(storage, parts, issues, cancellationToken))
        {
            if (record.Part != lastPart) { lastPart = record.Part; partsRead++; progress?.Report(new(ticks.Count, partsRead, parts.Count)); }
            switch (record)
            {
                case HeaderRecord h:
                    if (started is null)
                    {
                        started = h.StartedUtc; source = h.Source; appVersion = h.AppVersion; recordingId = h.RecordingId;
                        Add(HistoryEventKind.RecordingStarted, h.StartedUtc, "Recording started", h.Source == "Demo" ? "Demo data" : "Real session");
                    }
                    break;
                case SessionRecord sr:
                    var name = ProtocolName(sr.Session.Protocol);
                    if (!protocols.Contains(name)) protocols.Add(name);
                    if (lastSession is not null && (lastSession.Protocol != sr.Session.Protocol || lastSession.SessionId != sr.Session.SessionId)) sessionChanged = true;
                    lastSession = sr.Session;
                    break;
                case EventRecord e:
                    if (seen.Add((e.RuleId, e.Transition, e.At.UtcTicks))) AddFinding(Add, e.At, e.Transition, e.Severity, e.RuleId, e.Title);
                    break;
                case MarkRecord m:
                    AddMark(Add, m, shortId, incidents, pending, recent, MaximumRetainedIncidents);
                    break;
                case FooterRecord f:
                    footerLost = Math.Max(footerLost, f.Lost);
                    if (f.Complete && f.Reason != "Rotated") finalFooter = f;
                    break;
                case SampleRecord sample:
                    if (ticks.Count >= maximumSamples)
                    {
                        issues.Add(new(HistoryIssueKind.SampleCapReached, $"Only the first {maximumSamples:N0} samples were loaded; the recording continues after {HistoryLoader.Describe(ticks[^1])}.", record.Part));
                        goto done;
                    }
                    var s = sample.Sample; var t = s.Timestamp.UtcTicks;
                    if (ticks.Count > 0 && t < ticks[^1]) sorted = false;
                    ticks.Add(t);
                    for (var c = 0; c < channels.Count; c++)
                    {
                        var metric = channels[c].Select(s);
                        if (HistoryChannels.TryGetValid(metric, out var value)) { columns[c].Add(value); valid[c]++; units[c] ??= metric!.Unit; }
                        else { columns[c].Add(double.NaN); reasons[c] ??= metric is null ? "Not present in the recording." : (metric.UnavailableReason ?? metric.Error ?? metric.Status.ToString()); }
                    }
                    if (sessionChanged) { Add(HistoryEventKind.SessionChanged, s.Timestamp, "Session changed", "The protocol or session id changed."); sessionChanged = false; }
                    foreach (var diagnostic in s.Diagnostics?.Events ?? [])
                        if (seen.Add((diagnostic.Finding.RuleId, diagnostic.Transition, diagnostic.Timestamp.UtcTicks)))
                            AddFinding(Add, diagnostic.Timestamp.ToUniversalTime(), diagnostic.Transition, diagnostic.Finding.Severity, diagnostic.Finding.RuleId, diagnostic.Finding.Title);
                    for (var p = pending.Count - 1; p >= 0; p--)
                    {
                        incidents[pending[p].Index] = incidents[pending[p].Index] with { After = s };
                        pending.RemoveAt(p);
                    }
                    recent.Enqueue(s); if (recent.Count > RecentSamples) recent.Dequeue();
                    break;
            }
        }
        done:
        cancellationToken.ThrowIfCancellationRequested();
        if (started is null) throw new RecordingFormatException("This recording has no header; it is not an EUC Lens recording or it is damaged.");

        var tickArray = ticks.ToArray();
        var data = new double[channels.Count][];
        for (var c = 0; c < channels.Count; c++) data[c] = columns[c].ToArray();
        if (!sorted)
        {
            // The clock moved backwards: order by time (stable), so searching and drawing stay correct.
            var order2 = Enumerable.Range(0, tickArray.Length).OrderBy(i => tickArray[i]).ToArray();
            tickArray = order2.Select(i => tickArray[i]).ToArray();
            for (var c = 0; c < data.Length; c++) { var old = data[c]; data[c] = order2.Select(i => old[i]).ToArray(); }
            issues.Add(new(HistoryIssueKind.ClockChange, "The system clock moved backwards while this recording was made; samples are shown in time order."));
        }

        var channelData = new List<HistoryChannelData>();
        for (var c = 0; c < channels.Count; c++)
            channelData.Add(new(channels[c], units[c] ?? "", data[c], valid[c], valid[c] == 0 ? reasons[c] : null));

        if (finalFooter is not null) Add(HistoryEventKind.RecordingEnded, finalFooter.EndedUtc, "Recording ended", EndReasonText(finalFooter.Reason));

        var gapThreshold = ComputeGapThreshold(tickArray);
        var ordered = events.OrderBy(e => e.At).ThenBy(e => e.Order).ToArray();
        var gaps = FindGaps(tickArray, gapThreshold, ordered);
        double coverage = 1;
        if (tickArray.Length > 1)
        {
            var duration = tickArray[^1] - tickArray[0];
            long covered = 0;
            for (var i = 1; i < tickArray.Length; i++) covered += Math.Min(tickArray[i] - tickArray[i - 1], gapThreshold.Ticks);
            coverage = duration <= 0 ? 1 : Math.Clamp(covered / (double)duration, 0, 1);
        }

        var lastTime = tickArray.Length > 0 ? new DateTimeOffset(new DateTime(tickArray[^1], DateTimeKind.Utc)) : started.Value;
        if (ordered.Length > 0 && ordered[^1].At > lastTime && finalFooter is null) lastTime = ordered[^1].At;
        var ended = finalFooter?.EndedUtc ?? lastTime;
        var state = finalFooter is not null ? RecordingCompleteness.Complete : activeShortId == shortId ? RecordingCompleteness.InProgress : RecordingCompleteness.Interrupted;
        var warnings = ordered.Count(e => e.Kind is HistoryEventKind.FindingDetected or HistoryEventKind.FindingEscalated && e.Severity == Severity.Warning);
        var criticals = ordered.Count(e => e.Kind is HistoryEventKind.FindingDetected or HistoryEventKind.FindingEscalated && e.Severity == Severity.Critical);
        var info = new HistoryInfo(shortId, recordingId, started.Value, ended, finalFooter is not null, source, appVersion, protocols, tickArray.Length, warnings, criticals, incidents.Count, footerLost,
            parts.Count, state, finalFooter is not null ? EndReasonText(finalFooter.Reason) : state == RecordingCompleteness.InProgress ? "Recording in progress" : "Interrupted (no end of recording was written)",
            coverage, gaps.Count, TimeSpan.FromTicks(gaps.Sum(g => g.Length.Ticks)), directory);
        return new(info, tickArray, channelData, ordered, incidents, gaps, issues, gapThreshold);
    }

    private static string Describe(long ticks) => new DateTimeOffset(new DateTime(ticks, DateTimeKind.Utc)).ToString("u", System.Globalization.CultureInfo.InvariantCulture);

    public static string EndReasonText(string reason) => reason switch
    {
        "UserStopped" => "Stopped by the user", "ModeSwitched" => "Stopped when the data source was switched", "MonitoringStopped" => "Stopped because monitoring was stopped",
        "AppExit" => "Application exited", "SessionEnding" => "Windows session ended", "StorageError" => "Stopped because of a storage problem", "QuotaReached" => "Stopped because the storage quota was reached",
        "" => "Ended", _ => reason
    };

    private delegate void EventAdder(HistoryEventKind kind, DateTimeOffset at, string title, string detail = "", Severity? severity = null, string? rule = null, Guid? incident = null, int? sequence = null, TimeSpan? gap = null);

    private static void AddFinding(EventAdder add, DateTimeOffset at, FindingTransition transition, Severity severity, string rule, string title)
    {
        var (kind, label) = transition switch
        {
            FindingTransition.Detected => (HistoryEventKind.FindingDetected, $"{severity} detected"),
            FindingTransition.Escalated => (HistoryEventKind.FindingEscalated, $"Escalated to {severity}"),
            FindingTransition.Downgraded => (HistoryEventKind.FindingDowngraded, $"Downgraded to {severity}"),
            FindingTransition.Recovered => (HistoryEventKind.FindingRecovered, "Recovered"),
            _ => (HistoryEventKind.FindingExpired, "Expired")
        };
        add(kind, at, $"{label}: {title}", "", severity, rule);
    }

    private static void AddMark(EventAdder add, MarkRecord m, string shortId, List<HistoryIncident> incidents, List<(int Index, HistoryIncident Incident)> pending, Queue<SessionSnapshot> recent, int retainLimit)
    {
        switch (m.Kind)
        {
            case nameof(RecordingMarkKind.UserIncident):
                var sequence = m.Sequence ?? incidents.Count + 1;
                var id = m.IncidentId ?? StableId(shortId, sequence, m.At);
                var before = recent.LastOrDefault(s => s.Timestamp <= m.At) ?? recent.LastOrDefault();
                var retain = incidents.Count < retainLimit;
                var incident = new HistoryIncident(id, sequence, m.At, retain ? before : null, null);
                incidents.Add(incident);
                if (retain) pending.Add((incidents.Count - 1, incident));
                add(HistoryEventKind.UserIncident, m.At, $"Performance issue marked (no. {sequence})", m.Detail, null, null, id, sequence);
                break;
            case nameof(RecordingMarkKind.Paused): add(HistoryEventKind.MonitoringPaused, m.At, "Monitoring paused", m.Detail); break;
            case nameof(RecordingMarkKind.Resumed): add(HistoryEventKind.MonitoringResumed, m.At, "Monitoring resumed", m.Detail, null, null, null, null, m.Gap); break;
            case nameof(RecordingMarkKind.Availability): add(HistoryEventKind.AvailabilityChanged, m.At, "Metric availability changed", m.Detail); break;
            case nameof(RecordingMarkKind.SessionLocked): add(HistoryEventKind.SessionLocked, m.At, "Windows session locked", m.Detail); break;
            case nameof(RecordingMarkKind.SessionUnlocked): add(HistoryEventKind.SessionUnlocked, m.At, "Windows session unlocked", m.Detail); break;
            case nameof(RecordingMarkKind.RemoteDisconnected): add(HistoryEventKind.RemoteDisconnected, m.At, "Remote session disconnected", m.Detail); break;
            case nameof(RecordingMarkKind.RemoteReconnected): add(HistoryEventKind.RemoteReconnected, m.At, "Remote session reconnected", m.Detail); break;
            case nameof(RecordingMarkKind.DataLoss): add(HistoryEventKind.DataLoss, m.At, "Data loss", m.Detail, null, null, null, null, m.Gap); break;
            case nameof(RecordingMarkKind.Note): add(HistoryEventKind.Note, m.At, "Note", m.Detail); break;
            default: add(HistoryEventKind.Other, m.At, string.IsNullOrEmpty(m.Kind) ? "Recorded event" : m.Kind, m.Detail); break;
        }
    }

    /// <summary>An incident marker without an id (never written by this version) still gets the same id every time the recording is opened.</summary>
    private static Guid StableId(string shortId, int sequence, DateTimeOffset at) =>
        new(MD5.HashData(Encoding.UTF8.GetBytes($"{shortId}|{sequence}|{at.UtcTicks}")));

    private static TimeSpan ComputeGapThreshold(long[] ticks)
    {
        if (ticks.Length < 3) return TimeSpan.FromSeconds(5);
        var deltas = new List<long>(Math.Min(ticks.Length - 1, 2000));
        var step = Math.Max(1, (ticks.Length - 1) / 2000);
        for (var i = 1; i < ticks.Length; i += step) if (ticks[i] > ticks[i - 1]) deltas.Add(ticks[i] - ticks[i - 1]);
        if (deltas.Count == 0) return TimeSpan.FromSeconds(5);
        deltas.Sort();
        var median = deltas[deltas.Count / 2];
        return TimeSpan.FromTicks(Math.Max(TimeSpan.FromSeconds(5).Ticks, median * 3));
    }

    private static List<HistoryGap> FindGaps(long[] ticks, TimeSpan threshold, IReadOnlyList<HistoryEvent> events)
    {
        var gaps = new List<HistoryGap>();
        for (var i = 1; i < ticks.Length; i++)
        {
            if (ticks[i] - ticks[i - 1] <= threshold.Ticks) continue;
            var start = new DateTimeOffset(new DateTime(ticks[i - 1], DateTimeKind.Utc)); var end = new DateTimeOffset(new DateTime(ticks[i], DateTimeKind.Utc));
            var cause = events.FirstOrDefault(e => e.At >= start - TimeSpan.FromSeconds(1) && e.At <= end + TimeSpan.FromSeconds(1)
                && e.Kind is HistoryEventKind.MonitoringPaused or HistoryEventKind.DataLoss or HistoryEventKind.RemoteDisconnected or HistoryEventKind.SessionLocked);
            gaps.Add(new(start, end, cause?.Kind switch
            {
                HistoryEventKind.MonitoringPaused => "Monitoring was paused",
                HistoryEventKind.DataLoss => "Samples could not be written (data loss)",
                HistoryEventKind.RemoteDisconnected => "The remote session was disconnected",
                HistoryEventKind.SessionLocked => "The Windows session was locked",
                _ => "No samples were recorded"
            }));
        }
        return gaps;
    }
}
