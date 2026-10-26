using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace EucLens.Core;

/// <summary>
/// The deterministic anomaly detector: recorded history in, structured evidence out. No UI, no clock, no network, no randomness; the same input and options always give the
/// same observations in the same order with the same ids. It reads at most <see cref="AnomalyOptions.MaximumSamples"/> samples, checks the cancellation token while it works and
/// keeps its temporary memory to a few arrays per rule that are returned to a pool.
/// </summary>
public static class AnomalyDetectionEngine
{
    /// <summary>Part of every result: bumped when the meaning of the output changes.</summary>
    public const int EngineVersion = 1;

    public static AnomalyAnalysis Analyze(LoadedHistory history, AnomalyOptions? options = null, CancellationToken cancellationToken = default) =>
        Analyze(AnomalyInput.From(history), options, cancellationToken);

    /// <summary>The same analysis on a worker thread, for callers that must not block (the loader's pattern).</summary>
    public static Task<AnomalyAnalysis> AnalyzeAsync(LoadedHistory history, AnomalyOptions? options = null, CancellationToken cancellationToken = default) =>
        Task.Run(() => Analyze(history, options, cancellationToken), cancellationToken);

    public static AnomalyAnalysis Analyze(AnomalyInput input, AnomalyOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        options ??= AnomalyOptions.Default;
        var errors = options.Validate();
        if (errors.Count > 0) throw new ArgumentException("Invalid options: " + string.Join(" ", errors), nameof(options));
        return new Run(input, options, cancellationToken).Execute();
    }

    private sealed class Run
    {
        private readonly AnomalyInput input;
        private readonly AnomalyOptions options;
        private readonly CancellationToken token;
        private readonly int count;
        private readonly AnomalyTimeline timeline;
        private readonly List<string> limitations = [];
        private readonly bool blast;
        private readonly Dictionary<string, int> finite = new(StringComparer.Ordinal);
        private byte[]? activity;
        private bool activityComputed;
        private int omitted;

        public Run(AnomalyInput input, AnomalyOptions options, CancellationToken token)
        {
            this.input = input; this.options = options; this.token = token;
            count = Math.Min(input.SampleCount, options.MaximumSamples);
            timeline = AnomalyTimeline.Build(input, count, token);
            blast = HasBlastEvidence();
        }

        private bool HasBlastEvidence()
        {
            var name = HistoryLoader.ProtocolName(RemoteProtocol.OmnissaBlast);
            if (input.Protocols.Count > 0) return input.Protocols.Contains(name);
            // A recording that names no protocol: Blast-only counters are the evidence.
            foreach (var id in new[] { "outQueueing", "dirtyFps", "imagingOutbound" })
                if (input.Channel(id) is { } c)
                    for (var i = 0; i < count; i++) if (double.IsFinite(c.Values[i])) return true;
            return false;
        }

        public AnomalyAnalysis Execute()
        {
            limitations.AddRange(input.Limitations);
            var reduced = input.ReducedConfidence;
            if (count < input.SampleCount)
            {
                limitations.Add($"The recording holds {AnomalyNarrative.Count(input.SampleCount)} samples; only the first {AnomalyNarrative.Count(count)} were analyzed (the analyzer's sample limit). Nothing after {HistoryTime.Format(FromTicks(timeline.Ticks[count - 1]), TimeZoneInfo.Utc, withDate: true)} UTC was examined.");
                reduced = true;
            }
            if (count > 1 && timeline.Coverage < options.MinimumRecordingCoverage)
            {
                limitations.Add($"Samples cover only {AnomalyNarrative.Percent(timeline.Coverage)} of the recording's time ({AnomalyNarrative.Count(timeline.Gaps.Count)} gap(s)); conditions inside the gaps could not be observed.");
                reduced = true;
            }
            if (count == 0) limitations.Add("The recording has no samples.");

            var coverage = BuildCoverage();
            var observations = new List<AnomalyObservation>(); var evaluations = new List<RuleEvaluation>();
            foreach (var rule in options.Rules)
            {
                token.ThrowIfCancellationRequested();
                var (evaluation, produced) = RunRule(rule);
                evaluations.Add(evaluation); observations.AddRange(produced);
            }

            observations = Suppress(observations);
            if (observations.Count > options.MaximumObservations)
            {
                var keep = observations.OrderByDescending(o => o.Severity).ThenByDescending(o => o.Duration).ThenBy(o => o.Start).ThenBy(o => o.Id, StringComparer.Ordinal)
                    .Take(options.MaximumObservations).ToList();
                omitted += observations.Count - keep.Count;
                observations = keep;
            }
            observations.Sort(CompareObservations);
            if (omitted > 0)
            {
                limitations.Add($"The result was limited to {AnomalyNarrative.Count(options.MaximumObservations)} observations ({AnomalyNarrative.Count(options.MaximumObservationsPerRule)} per rule); {AnomalyNarrative.Count(omitted)} lower-ranked observation(s) are not listed. Ranking: severity, then duration.");
                reduced = true;
            }

            var counts = observations.GroupBy(o => o.RuleId).ToDictionary(g => g.Key, g => g.Count());
            evaluations = evaluations.Select(e => e with { Observations = counts.GetValueOrDefault(e.RuleId) }).ToList();
            var correlations = CorrelationAnalyzer.Analyze(observations, timeline, input, options, blast, token);

            DateTimeOffset? from = count > 0 ? FromTicks(timeline.Ticks[0]) : null, to = count > 0 ? FromTicks(timeline.Ticks[count - 1]) : null;
            return new(input.RecordingKey, EngineVersion, from, to, count, reduced, observations, correlations, coverage, evaluations, limitations, omitted);
        }

        private static DateTimeOffset FromTicks(long ticks) => new(new DateTime(ticks, DateTimeKind.Utc));

        private static int CompareObservations(AnomalyObservation a, AnomalyObservation b)
        {
            var c = a.Start.CompareTo(b.Start);
            if (c != 0) return c;
            c = a.End.CompareTo(b.End);
            if (c != 0) return c;
            c = string.CompareOrdinal(a.RuleId, b.RuleId);
            return c != 0 ? c : string.CompareOrdinal(a.Id, b.Id);
        }

        // ----- coverage -----

        private IReadOnlyList<MetricCoverage> BuildCoverage()
        {
            var result = new List<MetricCoverage>();
            var referenced = options.Rules.SelectMany(r => new[] { r.ChannelId, r.RatioOfChannelId }).Where(id => id is not null).Select(id => id!).Concat(["dirtyFps"]).Distinct(StringComparer.Ordinal).ToList();
            foreach (var id in referenced.Concat(input.Channels.Select(c => c.Id)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
            {
                var channel = input.Channel(id);
                var title = channel?.DisplayTitle ?? HistoryChannels.Find(id)?.Title ?? id;
                var rules = options.Rules.Where(r => r.ChannelId == id || r.RatioOfChannelId == id).ToList();
                if (channel is null) { result.Add(new(id, title, "", false, 0, 0, 0, 0, null, null, "Not recorded.")); continue; }
                var units = rules.Count > 0 ? rules[0].Units : id == "dirtyFps" ? UnitTable.FramesPerSecond : null;
                var factor = 1.0; var supported = true; string? reason = null;
                if (units is null) { supported = false; reason = "No rule interprets this metric."; }
                else if (!units.TryGetFactor(channel.Unit, out factor))
                {
                    supported = false;
                    reason = HistoryChannels.IsUnverified(channel.Unit) ? $"The unit is not verified ({channel.Unit}); values are shown as recorded and never interpreted." : $"The recorded unit '{channel.Unit}' is not one the analyzer interprets (expected {units.Canonical}).";
                }
                int valid = 0, outOfRange = 0, finiteCount = 0; long first = 0, last = 0; var any = false;
                var min = rules.Count > 0 ? rules[0].MinimumValue : double.MinValue; var max = rules.Count > 0 ? rules[0].MaximumValue : double.MaxValue;
                for (var i = 0; i < count; i++)
                {
                    var v = channel.Values[i];
                    if (!double.IsFinite(v)) continue;
                    finiteCount++;
                    var c = v * factor;
                    if (supported && (c < min || c > max)) { outOfRange++; continue; }
                    valid++;
                    if (!any) { first = timeline.Ticks[i]; any = true; }
                    last = timeline.Ticks[i];
                }
                finite[id] = finiteCount;
                if (valid == 0 && reason is null) reason = outOfRange > 0 ? "Every recorded value was outside the plausible range." : "The metric never had a valid value in this recording.";
                result.Add(new(id, title, channel.Unit, supported && valid > 0, count, valid, outOfRange, count == 0 ? 0 : valid / (double)count, any ? FromTicks(first) : null, any ? FromTicks(last) : null, reason));
            }
            return result;
        }

        // ----- display activity -----

        private byte[]? Activity()
        {
            if (activityComputed) return activity;
            activityComputed = true;
            var channel = input.Channel("dirtyFps");
            if (channel is null || !UnitTable.FramesPerSecond.TryGetFactor(channel.Unit, out var factor)) return null;
            var ticks = timeline.Ticks;
            var sum = new double[count + 1]; var cnt = new int[count + 1];
            var anyValid = false;
            for (var i = 0; i < count; i++)
            {
                if ((i & 4095) == 0) token.ThrowIfCancellationRequested();
                var v = channel.Values[i] * factor;
                var ok = double.IsFinite(v) && v >= 0 && v <= 1000 && !timeline.Disconnected[i];
                sum[i + 1] = sum[i] + (ok ? v : 0); cnt[i + 1] = cnt[i] + (ok ? 1 : 0);
                anyValid |= ok;
            }
            if (!anyValid) return null;
            var segFirst = new int[timeline.Segment[Math.Max(count - 1, 0)] + 2]; Array.Fill(segFirst, -1);
            var segLast = new int[segFirst.Length];
            for (var i = 0; i < count; i++) { var s = timeline.Segment[i]; if (segFirst[s] < 0) segFirst[s] = i; segLast[s] = i; }
            var half = Math.Max(options.ActivityWindow.Ticks, 3 * timeline.StepTicks) / 2;
            var result = new byte[count];
            int lo = 0, hi = 0;
            for (var i = 0; i < count; i++)
            {
                if ((i & 4095) == 0) token.ThrowIfCancellationRequested();
                while (lo < count && ticks[lo] < ticks[i] - half) lo++;
                while (hi < count && ticks[hi] <= ticks[i] + half) hi++;
                var s = timeline.Segment[i];
                var a = Math.Max(lo, segFirst[s]); var b = Math.Min(hi, segLast[s] + 1);
                var n = cnt[b] - cnt[a];
                result[i] = n < options.MinimumActivitySamples ? (byte)0 : (sum[b] - sum[a]) / n >= options.ActivityDirtyFps ? (byte)2 : (byte)1;
            }
            return activity = result;
        }

        // ----- one rule -----

        private (RuleEvaluation Evaluation, List<AnomalyObservation> Observations) RunRule(AnomalyRule rule)
        {
            var none = new List<AnomalyObservation>();
            RuleEvaluation Result(RuleState state, string? reason, int eligible = 0, int excluded = 0, int produced = 0) =>
                new(rule.Id, rule.Version, rule.ChannelId, state, state == RuleState.InsufficientData ? AnalysisStatus.InsufficientData : AnalysisStatus.ConfirmedObservation, reason, produced, eligible, excluded);
            (RuleEvaluation, List<AnomalyObservation>) Skip(RuleState state, string reason) => (Result(state, reason, 0, count), none);

            if (count == 0) return Skip(RuleState.InsufficientData, "The recording has no samples.");
            if (rule.RequiresBlast && !blast) return Skip(RuleState.NotApplicable, "This counter exists only for Omnissa Horizon Blast sessions and the recording shows no Horizon Blast evidence.");
            var channel = input.Channel(rule.ChannelId);
            var missingWhat = rule.MissingMetricNote ?? $"{HistoryChannels.Find(rule.ChannelId)?.Title.ToLowerInvariant() ?? rule.ChannelId} metric";
            if (channel is null) return Skip(RuleState.InsufficientData, $"The recording has no {missingWhat}.");
            if (finite.GetValueOrDefault(channel.Id) == 0) return Skip(RuleState.InsufficientData, $"{channel.DisplayTitle} never had a valid value in this recording (missing, unavailable or a counter error).");
            if (!rule.Units.TryGetFactor(channel.Unit, out var factor))
                return Skip(RuleState.InsufficientData, HistoryChannels.IsUnverified(channel.Unit)
                    ? $"The unit of {channel.DisplayTitle} is not verified ({channel.Unit}); it is not interpreted."
                    : $"The recorded unit '{channel.Unit}' of {channel.DisplayTitle} is not interpreted by this rule (expected {rule.Units.Canonical}).");
            AnomalyChannel? capacity = null; var factor2 = 1.0;
            if (rule.RatioOfChannelId is { } capacityId)
            {
                capacity = input.Channel(capacityId);
                if (capacity is null || finite.GetValueOrDefault(capacity.Id) == 0) return Skip(RuleState.InsufficientData, $"The recording has no {missingWhat}.");
                if (!rule.Units.TryGetFactor(capacity.Unit, out factor2))
                    return Skip(RuleState.InsufficientData, $"The recorded unit of {capacity.DisplayTitle} ('{capacity.Unit}') is not verified or not interpreted, so consumption cannot be compared with it.");
            }
            byte[]? activityStates = null;
            if (rule.RequiresDisplayActivity)
            {
                activityStates = Activity();
                if (activityStates is null) return Skip(RuleState.InsufficientData, "Display activity cannot be established: the Dirty FPS counter is not recorded, unavailable or in an unverified unit. Low frame rate is not judged without it.");
            }

            var pool = ArrayPool<double>.Shared; var bytes = ArrayPool<byte>.Shared;
            var canonical = pool.Rent(Math.Max(count, 1)); var classes = bytes.Rent(Math.Max(count, 1)); var eligible = bytes.Rent(Math.Max(count, 1));
            try
            {
                var ticks = timeline.Ticks;
                var connectionBound = rule.Domain != HistoryGroup.Guest;
                var outOfRange = 0; var usable = 0; var idle = 0;
                for (var i = 0; i < count; i++)
                {
                    if ((i & 4095) == 0) token.ThrowIfCancellationRequested();
                    var v = channel.Values[i] * factor;
                    if (capacity is not null) { var d = capacity.Values[i] * factor2; v = double.IsFinite(v) && double.IsFinite(d) && d > 0 ? v / d : double.NaN; }
                    var inRange = !double.IsNaN(v) && (capacity is not null || (v >= rule.MinimumValue && v <= rule.MaximumValue));
                    if (!double.IsNaN(v) && !inRange) outOfRange++;
                    canonical[i] = inRange ? v : double.NaN;
                    var ok = inRange && !(connectionBound && timeline.Disconnected[i]);
                    byte state = SampleClass.Unusable;
                    if (ok && activityStates is not null)
                    {
                        if (activityStates[i] == 0) ok = false;
                        else if (activityStates[i] == 1) { ok = false; state = SampleClass.OutOfContext; idle++; }
                    }
                    eligible[i] = ok ? (byte)1 : (byte)0;
                    classes[i] = state;
                    if (ok) usable++;
                }
                if (usable == 0)
                {
                    return idle > 0
                        ? (Result(RuleState.NotApplicable, $"The screen was static throughout ({AnomalyNarrative.Count(idle)} sample(s) with Dirty FPS below {AnomalyRules.Describe(options.ActivityDirtyFps)}): low frame rate is expected there and is not an anomaly.", 0, count), none)
                        : (Result(RuleState.InsufficientData, outOfRange > 0 ? $"No valid sample: {AnomalyNarrative.Count(outOfRange)} value(s) were outside the plausible range (counter errors)." : "The metric has no valid sample that can be used (missing, unavailable, disconnected or outside the context the metric needs)."), none);
                }

                // ----- thresholds -----
                MetricBaseline? baseline = null; double? baselineLimit = null; var useBaseline = false; string? note = null;
                if (rule.Baseline is { } bc && rule.Criterion != RuleCriterion.Absolute)
                {
                    baseline = BaselineCalculator.Compute(rule.ChannelId, channel.Unit, factor, canonical.AsSpan(0, count), eligible.AsSpan(0, count), ticks.AsSpan(0, count), rule, options,
                        out var canonicalMedian, out var canonicalSigma);
                    if (baseline.Reliable)
                    {
                        useBaseline = true;
                        baselineLimit = rule.HigherIsWorse ? canonicalMedian + bc.Deviations * canonicalSigma : canonicalMedian - bc.Deviations * canonicalSigma;
                    }
                    else if (rule.Criterion is RuleCriterion.Baseline or RuleCriterion.Both)
                        return (Result(RuleState.InsufficientData, $"Insufficient data for an adaptive baseline of {channel.DisplayTitle}: {baseline.Reason}", usable, count - usable), none);
                    else note = $"Baseline unavailable ({baseline.Reason}); only the absolute threshold was applied.";
                }
                var abs = rule.Criterion == RuleCriterion.Baseline ? null : rule.AbsoluteThreshold;
                var useAbs = abs is not null;
                var basis = useAbs && useBaseline ? ThresholdBasis.AbsoluteAndBaseline : useBaseline ? ThresholdBasis.BaselineDeviation : ThresholdBasis.Absolute;
                var both = rule.Criterion == RuleCriterion.Both;

                bool Beyond(double v, double limit) => rule.HigherIsWorse ? v >= limit : v < limit;
                for (var i = 0; i < count; i++)
                {
                    if (eligible[i] == 0) continue;
                    var v = canonical[i];
                    var a = useAbs && Beyond(v, abs!.Value); var b = useBaseline && Beyond(v, baselineLimit!.Value);
                    var bad = both ? (!useAbs || a) && (!useBaseline || b) : (a || b);
                    classes[i] = bad ? SampleClass.Bad : SampleClass.Good;
                }

                // ----- episodes → observations -----
                var episodes = EpisodeDetector.Detect(timeline, classes.AsSpan(0, count), canonical.AsSpan(0, count), rule.HigherIsWorse, rule.CriticalThreshold, rule.MergeTolerance.Ticks, token);
                var candidates = new List<(Episode Episode, ObservationKind Kind, Severity Severity, TimeSpan Span)>();
                foreach (var e in episodes)
                {
                    var span = TimeSpan.FromTicks(ticks[e.Last] - ticks[e.First] + timeline.StepTicks);
                    ObservationKind kind;
                    if (span >= rule.SustainedAfter)
                    {
                        if (e.Matching >= rule.MinimumSamples && e.Matching >= rule.MinimumMatchingFraction * e.Valid) kind = ObservationKind.Sustained;
                        else if (rule.ShortKind is not null && e.Matching >= rule.ShortMinimumSamples) kind = ObservationKind.Intermittent;
                        else continue;
                    }
                    else if (rule.ShortKind is { } shortKind && e.Matching >= rule.ShortMinimumSamples) kind = shortKind;
                    else continue;
                    candidates.Add((e, kind, SeverityOf(rule, kind, e), span));
                }
                if (candidates.Count > options.MaximumObservationsPerRule)
                {
                    var before = candidates.Count;
                    candidates = candidates.OrderByDescending(c => c.Severity).ThenByDescending(c => c.Span).ThenBy(c => c.Episode.First).Take(options.MaximumObservationsPerRule).ToList();
                    omitted += before - candidates.Count;
                }
                var produced = new List<AnomalyObservation>(candidates.Count);
                foreach (var c in candidates.OrderBy(c => c.Episode.First))
                {
                    token.ThrowIfCancellationRequested();
                    produced.Add(Materialize(rule, channel, capacity, c.Episode, c.Kind, c.Severity, c.Span, classes, eligible, activityStates, basis, abs, baselineLimit, baseline, factor, note));
                }
                var reason = note;
                if (idle > 0) reason = (reason is null ? "" : reason + " ") + $"{AnomalyNarrative.Count(idle)} sample(s) during a static screen were not evaluated.";
                return (Result(RuleState.Evaluated, reason, usable, count - usable, produced.Count), produced);
            }
            finally { pool.Return(canonical); bytes.Return(classes); bytes.Return(eligible); }
        }

        private static Severity SeverityOf(AnomalyRule rule, ObservationKind kind, Episode e)
        {
            if (rule.AlwaysInfo) return Severity.Info;
            if (kind == ObservationKind.Sustained)
                return rule.CriticalThreshold is not null && e.Critical * 2 >= e.Matching ? Severity.Critical : Severity.Warning;
            return rule.CriticalThreshold is { } c && (rule.HigherIsWorse ? e.Peak >= c : e.Peak < c) ? Severity.Warning : Severity.Info;
        }

        // ----- evidence -----

        private AnomalyObservation Materialize(AnomalyRule rule, AnomalyChannel channel, AnomalyChannel? capacity, Episode e, ObservationKind kind, Severity severity, TimeSpan span,
            byte[] classes, byte[] eligible, byte[]? activityStates, ThresholdBasis basis, double? abs, double? baselineLimit, MetricBaseline? baseline, double factor, string? note)
        {
            var ticks = timeline.Ticks;
            var start = FromTicks(ticks[e.First]); var end = FromTicks(ticks[e.Last]);
            var higherIsWorse = rule.HigherIsWorse;
            var metrics = new List<MetricEvidence>
            {
                Evidence(channel, "Primary", e.First, e.Last, classes, !higherIsWorse)
            };
            if (capacity is not null) metrics.Add(Evidence(capacity, "Capacity", e.First, e.Last, null, false));
            MetricEvidence? activityEvidence = null;
            if (activityStates is not null && input.Channel("dirtyFps") is { } dirty)
            {
                activityEvidence = EvidenceOrNull(dirty, "Display activity", e.First, e.Last, null, false);
                if (activityEvidence is not null) metrics.Add(activityEvidence);
            }

            var events = AssociatedEvents(start - options.EventContext, end + options.EventContext, out var eventCount);
            var intervalSamples = e.Last - e.First + 1;
            var coverage = e.Valid / (double)intervalSamples;
            var status = coverage >= options.QualifiedCoverage ? AnalysisStatus.ConfirmedObservation : AnalysisStatus.QualifiedObservation;

            var f = rule.RatioOfChannelId is null ? factor : 1;
            var recordedBaseline = baseline;
            var threshold = new ThresholdEvidence(basis,
                abs is { } a ? BaselineCalculator.Report(a, f) : null,
                baselineLimit is { } l ? BaselineCalculator.Report(l, f) : null,
                recordedBaseline,
                AnomalyNarrative.Comparison(rule, basis, abs is { } a2 ? BaselineCalculator.Report(a2, f) : null, baselineLimit is { } l2 ? BaselineCalculator.Report(l2, f) : null, recordedBaseline, rule.RatioOfChannelId is null ? channel.Unit : ""));

            var label = AnomalyNarrative.Label(channel.Id, channel.DisplayTitle, blast && input.Protocols.Count <= 1);
            var ending = e.Ending; DateTimeOffset? recoveredAt = e.RecoveredAt is { } r ? FromTicks(r) : null;
            var explanation = AnomalyNarrative.Explain(rule, kind, label, metrics[0], start, end, span, e.Matching, e.Valid, threshold.Comparison, ending, recoveredAt, activityEvidence);

            var limits = new List<string>(AnomalyNarrative.RuleNotes(rule, blast));
            if (note is not null) limits.Add(note);
            if (status == AnalysisStatus.QualifiedObservation) limits.Add($"Only {AnomalyNarrative.Percent(coverage)} of the samples in this interval had a usable value; the condition may have been shorter or longer than shown.");
            if (e.First == 0 || timeline.Segment[e.First] != timeline.Segment[e.First - 1]) limits.Add("The interval begins at the first sample of a continuous stretch (start of the recording, a gap or a session change); the condition may have begun earlier.");
            if (ending == ObservationEnd.CoverageEnded) limits.Add("Measurements ended or were missing after this interval; recovery is not confirmed and nothing is known about the time that follows.");
            if (input.Protocols.Count > 0 && !blast) limits.Add($"Recorded protocol: {string.Join(", ", input.Protocols)}. No Horizon Blast evidence; nothing here is interpreted as a Horizon problem.");
            if (input.ReducedConfidence && input.Limitations.Count > 0) limits.Add($"The recording is incomplete or was reduced before analysis: {input.Limitations[0]}{(input.Limitations.Count > 1 ? $" (+{input.Limitations.Count - 1} more, see the analysis limitations)" : "")}");

            return new(Id("obs", rule.Id, rule.Version.ToString(CultureInfo.InvariantCulture), ticks[e.First].ToString(CultureInfo.InvariantCulture), ticks[e.Last].ToString(CultureInfo.InvariantCulture)), rule.Id, rule.Version, kind, rule.Domain, severity, status, rule.Title,
                start, end, ending, recoveredAt, metrics, e.Valid, e.Matching, coverage, threshold, events, eventCount, explanation, limits);
        }

        private string Id(string prefix, params string[] parts)
        {
            var text = input.RecordingKey + "|" + string.Join("|", parts);
            return prefix + "-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)), 0, 8).ToLowerInvariant();
        }

        private MetricEvidence Evidence(AnomalyChannel channel, string role, int first, int last, byte[]? classes, bool worstIsLow) =>
            EvidenceOrNull(channel, role, first, last, classes, worstIsLow) ?? new(channel.Id, channel.DisplayTitle, channel.Unit, 0, double.NaN, double.NaN, double.NaN, double.NaN, FromTicks(timeline.Ticks[first]), role);

        /// <summary>
        /// Statistics of the valid recorded values in [first, last], in the recorded unit. When classes are given only samples the rule used (normal or matching) count;
        /// <c>WorstAt</c> is the time of the highest value, or of the lowest when <paramref name="worstIsLow"/> is set.
        /// </summary>
        private MetricEvidence? EvidenceOrNull(AnomalyChannel channel, string role, int first, int last, byte[]? classes, bool worstIsLow)
        {
            var pool = ArrayPool<double>.Shared;
            var buffer = pool.Rent(last - first + 1);
            try
            {
                var m = 0; var worst = -1;
                for (var i = first; i <= last; i++)
                {
                    var v = channel.Values[i];
                    if (!double.IsFinite(v) || (classes is not null && classes[i] is not (SampleClass.Good or SampleClass.Bad))) continue;
                    buffer[m++] = v;
                    if (worst < 0 || (worstIsLow ? v < channel.Values[worst] : v > channel.Values[worst])) worst = i;
                }
                if (m == 0) return null;
                var sorted = buffer.AsSpan(0, m); sorted.Sort();
                return new(channel.Id, channel.DisplayTitle, channel.Unit, m, sorted[0], HistoryStatistics.Percentile(sorted, 0.5), HistoryStatistics.Percentile(sorted, 0.95), sorted[^1], FromTicks(timeline.Ticks[worst]), role);
            }
            finally { pool.Return(buffer); }
        }

        private static readonly HashSet<HistoryEventKind> Uninteresting = [HistoryEventKind.RecordingStarted, HistoryEventKind.RecordingEnded, HistoryEventKind.Note, HistoryEventKind.Other];

        private IReadOnlyList<HistoryEvent> AssociatedEvents(DateTimeOffset from, DateTimeOffset to, out int total)
        {
            var events = input.Events; var result = new List<HistoryEvent>();
            int lo = 0, hi = events.Count;
            while (lo < hi) { var mid = lo + (hi - lo) / 2; if (events[mid].At < from) lo = mid + 1; else hi = mid; }
            total = 0;
            for (var i = lo; i < events.Count && events[i].At <= to; i++)
            {
                if (Uninteresting.Contains(events[i].Kind)) continue;
                total++;
                if (result.Count < options.MaximumAssociatedEvents) result.Add(events[i]);
            }
            return result;
        }

        // ----- suppression -----

        private List<AnomalyObservation> Suppress(List<AnomalyObservation> observations)
        {
            var byRule = observations.GroupBy(o => o.RuleId).ToDictionary(g => g.Key, g => g.ToList());
            var result = new List<AnomalyObservation>(observations.Count);
            foreach (var rule in options.Rules)
            {
                if (!byRule.TryGetValue(rule.Id, out var own)) continue;
                if (rule.SuppressedBy.Count == 0) { result.AddRange(own); continue; }
                var others = rule.SuppressedBy.SelectMany(id => byRule.GetValueOrDefault(id) ?? []).ToList();
                var tolerance = rule.MergeTolerance;
                foreach (var o in own)
                    if (!others.Any(x => x.Start <= o.End + tolerance && o.Start <= x.End + tolerance && timeline.SegmentAt(x.Start.UtcTicks) == timeline.SegmentAt(o.Start.UtcTicks))) result.Add(o);
            }
            return result;
        }
    }
}
