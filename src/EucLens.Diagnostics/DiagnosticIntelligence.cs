using System.Globalization;
using EucLens.Core;

namespace EucLens.Diagnostics;

/// <summary>Read-only inputs for one evaluation. Rules never collect metrics themselves.</summary>
public sealed class DiagnosticContext(SessionSnapshot snapshot, MetricsHistory history, DiagnosticThresholds thresholds, double now)
{
    public SessionSnapshot Snapshot { get; } = snapshot;
    public MetricsHistory History { get; } = history;
    public DiagnosticThresholds Thresholds { get; } = thresholds;
    /// <summary>Monotonic elapsed seconds; independent of wall-clock changes and metric timestamps.</summary>
    public double Now { get; } = now;
    public WorkloadContext Workload => Snapshot.Workload;
    public bool IsActiveWorkload => Workload is WorkloadContext.InteractiveGraphics or WorkloadContext.VideoPlayback;
    /// <summary>True: hardware GPU; false: software rendering (supported); null: unknown.</summary>
    public bool? HardwareGpu => Snapshot.Gpu.HardwareAcceleration;
    public int? LogicalProcessors => Snapshot.HostMetrics?.FirstOrDefault(e => e.Id == "cpu.logicalProcessors")?.Metric.Value is { } v ? (int)v : null;

    private readonly Dictionary<(string, double), WindowStats> cache = [];
    public WindowStats Stats(string key, double window)
    {
        if (cache.TryGetValue((key, window), out var stats)) return stats;
        return cache[(key, window)] = History.Has(key)
            ? History[key].Analyze(Now, window, Thresholds.MaximumSampleGapSeconds)
            : new(key, window, 0, 0, 0, 0, 0, null, null, null, null, null, null, null, null, "Metric not collected in this mode.");
    }
    public double Share(string key, double window, Func<double, bool> predicate) =>
        History.Has(key) ? History[key].ShareOfCoveredTime(Now, window, Thresholds.MaximumSampleGapSeconds, predicate) : 0;

    /// <summary>Screen-activity evidence from Blast dirty frames; null when the counter is not available.</summary>
    public bool? ScreenActivity
    {
        get
        {
            var dirty = Stats(DiagnosticMetrics.DirtyFps, Thresholds.StandardWindowSeconds);
            return dirty.IsSufficient(Thresholds) ? dirty.Mean >= Thresholds.ActivityDirtyFps : null;
        }
    }
}

public enum RuleVerdict { InsufficientData, Normal, Raised, Recovered, NotApplicable }

public sealed record FindingContent(string Title, string Description, double WindowSeconds, int ValidSamples, int TotalSamples,
    IReadOnlyList<DiagnosticEvidence> Evidence, string DecisionRule, IReadOnlyList<string> PossibleCauses,
    IReadOnlyList<string> RecommendedActions, string Limitations);

public sealed record RuleOutcome(RuleVerdict Verdict, Severity Severity = Severity.Info, FindingContent? Content = null, string? Reason = null)
{
    public static RuleOutcome Insufficient(string reason) => new(RuleVerdict.InsufficientData, Reason: reason);
    public static RuleOutcome Normal { get; } = new(RuleVerdict.Normal);
    public static RuleOutcome NotApplicable(string reason) => new(RuleVerdict.NotApplicable, Reason: reason);
    public static RuleOutcome Recovered(string reason) => new(RuleVerdict.Recovered, Reason: reason);
    public static RuleOutcome Raise(Severity severity, FindingContent content) => new(RuleVerdict.Raised, severity, content);
}

/// <summary>A self-contained diagnostic rule. New rules are added by registering another implementation.</summary>
public interface IDomainRule
{
    string Id { get; }
    DiagnosticDomain Domain { get; }
    IReadOnlyList<string> RequiredMetrics { get; }
    RuleOutcome Evaluate(DiagnosticContext context);
}

/// <summary>
/// Finding lifecycle with hysteresis: raise or escalate immediately on a sustained condition; downgrade only after the
/// higher severity has not held for the recovery window; close only on explicit recovery evidence, or expire when
/// no usable data arrives for <see cref="DiagnosticThresholds.StaleFindingSeconds"/>.
/// </summary>
public sealed class FindingTracker
{
    private const int RecoveredHistory = 50;
    private sealed class State
    {
        public required DiagnosticFinding Finding;
        public double DetectedAt, LastConfirmedAt;
        public double? BelowSeveritySince;
    }
    private readonly Dictionary<string, State> active = new(StringComparer.Ordinal);
    private readonly Queue<DiagnosticFinding> recovered = new();

    public IEnumerable<DiagnosticFinding> Active => active.Values.Select(s => s.Finding);
    public IEnumerable<DiagnosticFinding> Recovered => recovered;

    public DiagnosticEvent? Apply(IDomainRule rule, RuleOutcome outcome, double now, DateTimeOffset wall, DiagnosticThresholds t)
    {
        active.TryGetValue(rule.Id, out var state);
        switch (outcome.Verdict)
        {
            case RuleVerdict.Raised:
                var content = outcome.Content!;
                if (state is null)
                {
                    var finding = Create(rule, outcome.Severity, content, wall, wall);
                    active[rule.Id] = new State { Finding = finding, DetectedAt = now, LastConfirmedAt = now };
                    return new(wall, FindingTransition.Detected, finding);
                }
                state.LastConfirmedAt = now;
                var current = state.Finding.Severity;
                if (outcome.Severity > current)
                {
                    state.BelowSeveritySince = null;
                    state.Finding = Update(state.Finding, outcome.Severity, content, wall);
                    return new(wall, FindingTransition.Escalated, state.Finding);
                }
                if (outcome.Severity < current)
                {
                    state.BelowSeveritySince ??= now;
                    if (now - state.BelowSeveritySince >= t.RecoveryWindowSeconds)
                    {
                        state.BelowSeveritySince = null;
                        state.Finding = Update(state.Finding, outcome.Severity, content, wall);
                        return new(wall, FindingTransition.Downgraded, state.Finding);
                    }
                    state.Finding = Update(state.Finding, current, content, wall);
                    return null;
                }
                state.BelowSeveritySince = null;
                state.Finding = Update(state.Finding, current, content, wall);
                return null;
            case RuleVerdict.Normal:
                // Inside the hysteresis band: below the raise threshold but recovery not yet proven.
                if (state is null) return null;
                state.LastConfirmedAt = now;
                state.BelowSeveritySince ??= now;
                if (state.Finding.Severity == Severity.Critical && now - state.BelowSeveritySince >= t.RecoveryWindowSeconds)
                {
                    state.BelowSeveritySince = now;
                    state.Finding = state.Finding with { Severity = Severity.Warning, LastConfirmedAt = wall };
                    return new(wall, FindingTransition.Downgraded, state.Finding);
                }
                return null;
            case RuleVerdict.Recovered:
            case RuleVerdict.NotApplicable:
                return state is null ? null : Close(rule.Id, state, wall, FindingTransition.Recovered, outcome.Reason ?? "Recovery condition met.");
            default:
                if (state is not null && now - state.LastConfirmedAt >= t.StaleFindingSeconds)
                    return Close(rule.Id, state, wall, FindingTransition.Expired,
                        string.Create(CultureInfo.InvariantCulture, $"Expired: no usable measurements for {t.StaleFindingSeconds:0} s ({outcome.Reason})."));
                return null;
        }
    }

    private DiagnosticEvent Close(string id, State state, DateTimeOffset wall, FindingTransition transition, string reason)
    {
        active.Remove(id);
        var closed = state.Finding with { RecoveredAt = wall, RecoveryReason = reason };
        recovered.Enqueue(closed);
        while (recovered.Count > RecoveredHistory) recovered.Dequeue();
        return new(wall, transition, closed);
    }

    private static DiagnosticFinding Create(IDomainRule rule, Severity severity, FindingContent c, DateTimeOffset detected, DateTimeOffset confirmed) =>
        new(rule.Id, rule.Domain, severity, c.Title, c.Description, detected, confirmed, null, null, c.WindowSeconds, c.ValidSamples,
            c.TotalSamples, c.Evidence, c.DecisionRule, c.PossibleCauses, c.RecommendedActions, c.Limitations);

    private static DiagnosticFinding Update(DiagnosticFinding f, Severity severity, FindingContent c, DateTimeOffset confirmed) =>
        f with
        {
            Severity = severity, Title = c.Title, Description = c.Description, LastConfirmedAt = confirmed, ObservationSeconds = c.WindowSeconds,
            ValidSamples = c.ValidSamples, TotalSamples = c.TotalSamples, Evidence = c.Evidence, DecisionRule = c.DecisionRule,
            PossibleCauses = c.PossibleCauses, RecommendedActions = c.RecommendedActions, Limitations = c.Limitations
        };

    public void Clear() { active.Clear(); recovered.Clear(); }
}

/// <summary>
/// Diagnostic engine for Real Session and Demo Mode. Thread-safe: the sampling loop evaluates off the UI thread
/// while mode changes call <see cref="Reset"/>. History is bounded per metric.
/// </summary>
public sealed class DiagnosticIntelligenceEngine : IDiagnosticEngine, IConfigurableDiagnostics
{
    private readonly object gate = new();
    private readonly IDomainRule[] rules;
    private readonly TimeProvider clock;
    private readonly long origin;
    private readonly MetricsHistory history = new();
    private readonly FindingTracker tracker = new();
    private DiagnosticThresholds thresholds = DiagnosticThresholds.Defaults;
    private IReadOnlyList<DomainAssessment> lastAssessments = [];
    private IReadOnlyList<string> lastActive = [];

    public DiagnosticIntelligenceEngine(IEnumerable<IDomainRule> rules, TimeProvider? clock = null, DiagnosticThresholds? thresholds = null)
    {
        this.rules = rules.ToArray();
        if (this.rules.Select(r => r.Id).Distinct(StringComparer.Ordinal).Count() != this.rules.Length)
            throw new ArgumentException("Diagnostic rule identifiers must be unique.", nameof(rules));
        this.clock = clock ?? TimeProvider.System;
        origin = this.clock.GetTimestamp();
        Configure(thresholds ?? DiagnosticThresholds.Defaults);
    }

    public DiagnosticThresholds Thresholds { get { lock (gate) return thresholds; } }
    public IReadOnlyList<IDomainRule> Rules => rules;

    /// <summary>Applies validated thresholds and clears history so old evidence is not judged by new rules.</summary>
    public void Configure(DiagnosticThresholds value)
    {
        var errors = value.Validate();
        if (errors.Count > 0) throw new ArgumentException("Invalid diagnostic thresholds: " + string.Join(" ", errors), nameof(value));
        lock (gate) { thresholds = value; ResetCore(); }
    }

    public void Reset() { lock (gate) ResetCore(); }
    private void ResetCore() { history.Clear(); tracker.Clear(); lastAssessments = []; lastActive = []; }

    public DiagnosticReport Evaluate(SessionSnapshot snapshot)
    {
        lock (gate)
        {
            var now = clock.GetElapsedTime(origin).TotalSeconds;
            history.Record(snapshot, now);
            var context = new DiagnosticContext(snapshot, history, thresholds, now);
            var events = new List<DiagnosticEvent>();
            foreach (var rule in rules)
            {
                RuleOutcome outcome;
                try { outcome = rule.Evaluate(context); }
                catch (Exception ex) when (ex is ArithmeticException or InvalidOperationException or ArgumentException or KeyNotFoundException)
                { outcome = RuleOutcome.Insufficient($"Rule error: {ex.Message}"); }
                if (tracker.Apply(rule, outcome, now, snapshot.Timestamp, thresholds) is { } e) events.Add(e);
            }
            var active = tracker.Active.OrderByDescending(f => f.Severity).ThenBy(f => f.Domain).ThenBy(f => f.RuleId, StringComparer.Ordinal).ToArray();
            var assessments = HealthEvaluator.Evaluate(context, active);
            // Reuse unchanged lists so long recordings do not store a copy per sample.
            if (!assessments.SequenceEqual(lastAssessments)) lastAssessments = assessments;
            var ids = active.Select(f => $"{f.RuleId}:{f.Severity}").ToArray();
            if (!ids.SequenceEqual(lastActive)) lastActive = ids;
            var hypotheses = HypothesisBuilder.Build(context, active, lastAssessments);
            var findings = active.Concat(tracker.Recovered.Reverse()).ToArray();
            static HealthStatus Legacy(HealthState s) => s switch
            {
                HealthState.Healthy => HealthStatus.Healthy, HealthState.Warning => HealthStatus.Warning,
                HealthState.Critical => HealthStatus.Critical, _ => HealthStatus.Unavailable
            };
            var states = lastAssessments.Select(a => a.State).ToArray();
            var overall = states.Contains(HealthState.Critical) ? HealthStatus.Critical : states.Contains(HealthState.Warning) ? HealthStatus.Warning
                : states.Length > 0 && states.All(s => s == HealthState.Healthy) ? HealthStatus.Healthy : HealthStatus.Unavailable;
            var snapshotState = new DiagnosticSnapshot(lastAssessments, lastActive, events.Count == 0 ? [] : events);
            return new(overall, [], Legacy(Domain(DiagnosticDomain.Connection)), Legacy(Domain(DiagnosticDomain.Guest)),
                lastAssessments, findings, hypotheses, snapshotState);

            HealthState Domain(DiagnosticDomain d) => lastAssessments.FirstOrDefault(a => a.Domain == d)?.State ?? HealthState.InsufficientData;
        }
    }
}

/// <summary>Turns findings and metric sufficiency into three explainable domain assessments.</summary>
public static class HealthEvaluator
{
    private static readonly IReadOnlyDictionary<string, string> Names = new Dictionary<string, string>
    {
        [DiagnosticMetrics.Rtt] = "RTT", [DiagnosticMetrics.PacketLoss] = "packet loss (uplink)", [DiagnosticMetrics.Reconnects] = "reconnects",
        [DiagnosticMetrics.Fps] = "FPS", [DiagnosticMetrics.DirtyFps] = "dirty FPS", [DiagnosticMetrics.QueueingUs] = "out queueing time",
        [DiagnosticMetrics.Cpu] = "CPU", [DiagnosticMetrics.Ram] = "RAM", [DiagnosticMetrics.AvailableRamGiB] = "available RAM",
        [DiagnosticMetrics.DiskActive] = "disk active time", [DiagnosticMetrics.Gpu] = "GPU", [DiagnosticMetrics.Vram] = "VRAM", [DiagnosticMetrics.Encoder] = "encoder",
    };

    public static DomainAssessment[] Evaluate(DiagnosticContext c, IReadOnlyList<DiagnosticFinding> active)
    {
        var t = c.Thresholds;
        var gpuOptional = c.HardwareGpu == true ? new[] { DiagnosticMetrics.Gpu, DiagnosticMetrics.Vram, DiagnosticMetrics.Encoder } : [];
        var connectionNotes = new List<string>();
        if (c.History.Has(DiagnosticMetrics.JitterRaw)) connectionNotes.Add("jitter and estimated bandwidth units unverified (shown raw, not evaluated)");
        if (c.Snapshot.BlastMetrics is not null) connectionNotes.Add("bandwidth saturation not assessed (no verified capacity metric)");
        var guestNotes = new List<string>();
        guestNotes.Add(c.HardwareGpu switch
        {
            false => "software rendering — GPU analysis not applicable",
            null => "GPU inventory unknown",
            _ => "hardware GPU evaluated where counters exist",
        });
        guestNotes.Add("measured inside the VDI guest, not the hypervisor host");
        return
        [
            Assess(c, active, DiagnosticDomain.Connection, [DiagnosticMetrics.Rtt], [DiagnosticMetrics.PacketLoss, DiagnosticMetrics.Reconnects], connectionNotes),
            Assess(c, active, DiagnosticDomain.Graphics, [DiagnosticMetrics.Fps], [DiagnosticMetrics.DirtyFps, DiagnosticMetrics.QueueingUs, DiagnosticMetrics.Cpu],
                [$"workload is user-selected ({c.Workload})"]),
            Assess(c, active, DiagnosticDomain.Guest, [DiagnosticMetrics.Cpu, DiagnosticMetrics.Ram],
                [DiagnosticMetrics.AvailableRamGiB, DiagnosticMetrics.DiskActive, .. gpuOptional], guestNotes),
        ];
    }

    private static DomainAssessment Assess(DiagnosticContext c, IReadOnlyList<DiagnosticFinding> active, DiagnosticDomain domain,
        string[] required, string[] optional, List<string> notes)
    {
        var t = c.Thresholds;
        var window = t.StandardWindowSeconds;
        bool InDomain(DiagnosticFinding f) => f.Domain == domain || (domain == DiagnosticDomain.Guest && f.Domain == DiagnosticDomain.Gpu);
        var worst = active.Where(f => InDomain(f) && f.Severity > Severity.Info).Select(f => (Severity?)f.Severity).Max();
        var requiredStats = required.Select(k => c.Stats(k, window)).ToArray();
        var optionalStats = optional.Select(k => c.Stats(k, window)).ToArray();
        var missingRequired = requiredStats.Where(s => !s.IsSufficient(t)).ToArray();
        var missingOptional = optionalStats.Where(s => !s.IsSufficient(t)).Select(s => Names.GetValueOrDefault(s.Key, s.Key)).ToArray();
        var state = worst switch
        {
            Severity.Critical => HealthState.Critical,
            Severity.Warning => HealthState.Warning,
            _ => missingRequired.Length > 0 ? HealthState.InsufficientData : HealthState.Healthy
        };
        var coverage = missingRequired.Length > 0 ? AssessmentCoverage.None
            : missingOptional.Length > 0 || notes.Any(n => n.Contains("unverified", StringComparison.Ordinal) || n.Contains("not assessed", StringComparison.Ordinal))
                ? AssessmentCoverage.Partial : AssessmentCoverage.Full;
        var evidence = missingRequired.Length > 0
            ? string.Join(" · ", missingRequired.Select(s => string.Create(CultureInfo.InvariantCulture,
                $"{Names.GetValueOrDefault(s.Key, s.Key)}: {s.ValidSamples} valid samples, {s.Coverage:P0} of {window:0} s covered{(s.LatestReason is null ? "" : $" ({s.LatestReason})")}")))
            : worst is not null
                ? string.Join(" · ", active.Where(f => InDomain(f) && f.Severity > Severity.Info).Select(f => $"{f.Severity}: {f.Title}"))
                : string.Join(" · ", requiredStats.Concat(optionalStats).Where(s => s.IsSufficient(t)).Select(Summary)) + " — within configured thresholds";
        var coverageNote = string.Join("; ", missingOptional.Select(m => $"{m} unavailable").Concat(notes));
        return new(domain, state, coverage, evidence, coverageNote, window);
    }

    private static string Summary(WindowStats s)
    {
        var name = Names.GetValueOrDefault(s.Key, s.Key);
        var unit = s.Key switch
        {
            DiagnosticMetrics.Rtt => " ms", DiagnosticMetrics.Fps or DiagnosticMetrics.DirtyFps => " FPS", DiagnosticMetrics.QueueingUs => " µs",
            DiagnosticMetrics.AvailableRamGiB => " GiB", DiagnosticMetrics.Reconnects => "", _ => " %"
        };
        var value = s.Key is DiagnosticMetrics.AvailableRamGiB or DiagnosticMetrics.PacketLoss ? s.Mean : s.Key == DiagnosticMetrics.Reconnects ? s.Latest : s.P95;
        var label = s.Key is DiagnosticMetrics.AvailableRamGiB or DiagnosticMetrics.PacketLoss ? "mean" : s.Key == DiagnosticMetrics.Reconnects ? "total" : "P95";
        return string.Create(CultureInfo.InvariantCulture, $"{name} {label} {value:0.##}{unit}");
    }
}
