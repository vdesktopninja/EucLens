using System.Globalization;
using System.Text;
using System.Text.Json;

namespace EucLens.Core;

/// <summary>
/// Export of the deterministic M1 findings of one recording, as CSV (one row per observation and per correlation) or JSON (the complete structure). It is a separate,
/// optional export with its own schema, <see cref="Schema"/>; the session, incident and timeline exports are untouched. Everything written comes from the
/// <see cref="AnomalyAnalysis"/> and the recording's own <see cref="HistoryInfo"/>: no value is invented, an unavailable value is empty (CSV) or null (JSON), never zero, and the
/// explanations are the engine's own text. Nothing that identifies the machine or the folder (the recording's path, user or host names) is written. All times are UTC (ISO 8601),
/// all numbers invariant. The JSON keeps every string exactly as recorded; the CSV protects text cells against spreadsheet formulas (see <see cref="CsvField"/>).
/// </summary>
public static class FindingsExport
{
    public const string Schema = "euclens.findings/1";

    /// <summary>The CSV columns, in this order. Columns are only ever appended in later schema versions.</summary>
    public static IReadOnlyList<string> CsvColumns { get; } =
    [
        "schema", "recording_id", "engine_version", "record_type", "record_id", "rule_or_pattern", "rule_version", "domain", "severity", "analysis_status", "kind",
        "start_utc", "end_utc", "duration_seconds", "ending", "recovered_at_utc",
        "metric_id", "metric", "unit", "minimum", "p50", "p95", "maximum", "worst_at_utc", "valid_samples", "matching_samples", "coverage",
        "threshold_basis", "absolute_threshold", "baseline_limit", "baseline_median", "baseline_sigma", "baseline_samples", "baseline_reliable",
        "supporting_metrics", "correlation_timing", "common_overlap_start_utc", "common_overlap_end_utc", "component_ids", "channels",
        "reduced_confidence", "recording_state", "explanation", "limitations"
    ];

    private static string Utc(DateTimeOffset at) => at.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

    // ---------- CSV ----------

    public static string Csv(AnomalyAnalysis analysis, HistoryInfo info)
    {
        ArgumentNullException.ThrowIfNull(analysis); ArgumentNullException.ThrowIfNull(info);
        var text = new StringBuilder(string.Join(',', CsvColumns)).Append("\r\n");
        var common = new[] { Schema, analysis.RecordingKey, analysis.EngineVersion.ToString(CultureInfo.InvariantCulture) };
        string Row(string[] cells) => string.Join(',', cells);
        string T(string? value) => CsvField.Text(value);
        var reduced = analysis.ReducedConfidence ? "true" : "false";
        var state = info.State.ToString();
        foreach (var o in analysis.Observations)
        {
            var p = o.Primary; var t = o.Threshold; var b = t.Baseline;
            var hasValues = p.ValidSamples > 0;
            text.Append(Row(
            [
                T(common[0]), T(common[1]), common[2], "observation", T(o.Id), T(o.RuleId), CsvField.Number(o.RuleVersion), o.Domain.ToString(), o.Severity.ToString(), o.Status.ToString(), o.Kind.ToString(),
                Utc(o.Start), Utc(o.End), CsvField.Number(Math.Round(o.Duration.TotalSeconds, 3)), o.Ending.ToString(), o.RecoveredAt is { } r ? Utc(r) : "",
                T(p.ChannelId), T(p.Title), T(p.Unit), hasValues ? CsvField.Number(p.Minimum) : "", hasValues ? CsvField.Number(p.P50) : "", hasValues ? CsvField.Number(p.P95) : "",
                hasValues ? CsvField.Number(p.Maximum) : "", hasValues ? Utc(p.WorstAt) : "", CsvField.Number(p.ValidSamples), CsvField.Number(o.MatchingSamples), CsvField.Number(o.Coverage),
                t.Basis.ToString(), CsvField.Number(t.Absolute), CsvField.Number(t.BaselineLimit),
                b is { Reliable: true } ? CsvField.Number(b.Median) : "", b is { Reliable: true } ? CsvField.Number(b.Sigma) : "", b is null ? "" : CsvField.Number(b.Samples), b is null ? "" : b.Reliable ? "true" : "false",
                T(string.Join("; ", o.Metrics.Skip(1).Select(m => $"{m.ChannelId} ({m.Role}, {m.Unit})"))), "", "", "", "", "",
                reduced, state, T(o.Explanation), T(string.Join(" | ", o.Limitations))
            ])).Append("\r\n");
        }
        foreach (var c in analysis.Correlations)
        {
            text.Append(Row(
            [
                T(common[0]), T(common[1]), common[2], "correlation", T(c.Id), c.Pattern.ToString(), "", "", c.Severity.ToString(), "", "",
                Utc(c.Start), Utc(c.End), CsvField.Number(Math.Round((c.End - c.Start).TotalSeconds, 3)), "", "",
                "", "", "", "", "", "", "", "", "", "", "",
                "", "", "", "", "", "", "", "", c.Timing.ToString(),
                c.CommonOverlapStart is { } cs ? Utc(cs) : "", c.CommonOverlapEnd is { } ce ? Utc(ce) : "", T(string.Join("; ", c.ComponentIds)), T(string.Join("; ", c.Channels)),
                reduced, state, T(c.Explanation), T(string.Join(" | ", c.Limitations))
            ])).Append("\r\n");
        }
        return text.ToString();
    }

    // ---------- JSON ----------

    public static string Json(AnomalyAnalysis analysis, HistoryInfo info)
    {
        ArgumentNullException.ThrowIfNull(analysis); ArgumentNullException.ThrowIfNull(info);
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            Str(w, "schema", Schema);
            WriteRecording(w, analysis, info);
            w.WriteStartObject("analysis");
            w.WriteNumber("engineVersion", analysis.EngineVersion);
            Time(w, "fromUtc", analysis.From); Time(w, "toUtc", analysis.To);
            w.WriteNumber("samplesAnalyzed", analysis.SamplesAnalyzed);
            w.WriteBoolean("reducedConfidence", analysis.ReducedConfidence);
            w.WriteNumber("observationCount", analysis.Observations.Count);
            w.WriteNumber("correlationCount", analysis.Correlations.Count);
            w.WriteNumber("omittedObservations", analysis.OmittedObservations);
            w.WriteBoolean("truncated", analysis.IsTruncated);
            w.WriteEndObject();

            w.WriteStartArray("observations");
            foreach (var o in analysis.Observations) WriteObservation(w, o);
            w.WriteEndArray();
            w.WriteStartArray("correlations");
            foreach (var c in analysis.Correlations) WriteCorrelation(w, c);
            w.WriteEndArray();
            w.WriteStartArray("coverage");
            foreach (var c in analysis.Coverage) WriteCoverage(w, c);
            w.WriteEndArray();
            w.WriteStartArray("ruleEvaluations");
            foreach (var r in analysis.Rules) WriteRule(w, r);
            w.WriteEndArray();
            Strings(w, "limitations", analysis.Limitations);
            w.WriteEndObject();
        }
        return new UTF8Encoding(false).GetString(stream.ToArray());
    }

    /// <summary>Strings come from recordings and are untrusted: an unpaired surrogate would make the writer throw, so it is replaced.</summary>
    private static void Str(Utf8JsonWriter w, string name, string value) => w.WriteString(name, CsvField.ToValidUtf16(value));

    private static void Time(Utf8JsonWriter w, string name, DateTimeOffset? at)
    {
        if (at is { } value) w.WriteString(name, Utc(value)); else w.WriteNull(name);
    }

    /// <summary>A finite number, or null: JSON has no NaN or Infinity, and a missing value is never written as zero.</summary>
    private static void Num(Utf8JsonWriter w, string name, double? value)
    {
        if (value is { } v && double.IsFinite(v)) w.WriteNumber(name, v); else w.WriteNull(name);
    }

    private static void Strings(Utf8JsonWriter w, string name, IEnumerable<string> values)
    {
        w.WriteStartArray(name);
        foreach (var v in values) w.WriteStringValue(CsvField.ToValidUtf16(v));
        w.WriteEndArray();
    }

    private static void WriteRecording(Utf8JsonWriter w, AnomalyAnalysis analysis, HistoryInfo info)
    {
        w.WriteStartObject("recording");
        Str(w, "key", analysis.RecordingKey);
        Str(w, "shortId", info.ShortId);
        if (info.RecordingId is { } id) Str(w, "recordingId", id.ToString("D")); else w.WriteNull("recordingId");
        Str(w, "startedUtc", Utc(info.StartedUtc));
        Str(w, "endedUtc", Utc(info.EndedUtc));
        w.WriteBoolean("hasEnded", info.HasEnded);
        Str(w, "state", info.State.ToString());
        Str(w, "endReason", info.EndReason);
        Str(w, "source", info.Source);
        Str(w, "appVersion", info.AppVersion);
        Strings(w, "protocols", info.Protocols);
        w.WriteNumber("samples", info.Samples);
        w.WriteNumber("lostSamples", info.LostSamples);
        w.WriteNumber("parts", info.Parts);
        Num(w, "dataCoverage", info.DataCoverage);
        w.WriteNumber("gaps", info.Gaps);
        Num(w, "gapSeconds", Math.Round(info.GapTime.TotalSeconds, 3));
        w.WriteBoolean("partial", info.IsPartial);
        if (info.LimitReason.Length > 0) Str(w, "partialReason", info.LimitReason); else w.WriteNull("partialReason");
        w.WriteEndObject();
    }

    private static void WriteMetric(Utf8JsonWriter w, MetricEvidence m)
    {
        var available = m.ValidSamples > 0;
        w.WriteStartObject();
        Str(w, "channelId", m.ChannelId);
        Str(w, "title", m.Title);
        Str(w, "unit", m.Unit);
        Str(w, "role", m.Role);
        w.WriteBoolean("available", available);
        w.WriteNumber("validSamples", m.ValidSamples);
        Num(w, "minimum", available ? m.Minimum : null); Num(w, "p50", available ? m.P50 : null); Num(w, "p95", available ? m.P95 : null); Num(w, "maximum", available ? m.Maximum : null);
        Time(w, "worstAtUtc", available ? m.WorstAt : null);
        w.WriteEndObject();
    }

    private static void WriteObservation(Utf8JsonWriter w, AnomalyObservation o)
    {
        w.WriteStartObject();
        Str(w, "id", o.Id);
        Str(w, "ruleId", o.RuleId); w.WriteNumber("ruleVersion", o.RuleVersion);
        Str(w, "kind", o.Kind.ToString()); Str(w, "domain", o.Domain.ToString());
        Str(w, "severity", o.Severity.ToString()); Str(w, "status", o.Status.ToString());
        Str(w, "title", o.Title);
        Str(w, "startUtc", Utc(o.Start)); Str(w, "endUtc", Utc(o.End));
        Num(w, "durationSeconds", Math.Round(o.Duration.TotalSeconds, 3));
        Str(w, "ending", o.Ending.ToString());
        Time(w, "recoveredAtUtc", o.RecoveredAt);
        w.WriteStartArray("metrics");
        foreach (var m in o.Metrics) WriteMetric(w, m);
        w.WriteEndArray();
        w.WriteNumber("sampleCount", o.SampleCount); w.WriteNumber("matchingSamples", o.MatchingSamples); Num(w, "coverage", o.Coverage);
        var t = o.Threshold;
        w.WriteStartObject("threshold");
        Str(w, "basis", t.Basis.ToString());
        Num(w, "absolute", t.Absolute); Num(w, "baselineLimit", t.BaselineLimit);
        Str(w, "comparison", t.Comparison);
        if (t.Baseline is { } b)
        {
            w.WriteStartObject("baseline");
            Str(w, "channelId", b.ChannelId); Str(w, "unit", b.Unit); w.WriteBoolean("reliable", b.Reliable);
            Num(w, "median", b.Reliable ? b.Median : null); Num(w, "mad", b.Reliable ? b.Mad : null); Num(w, "sigma", b.Reliable ? b.Sigma : null);
            Num(w, "p05", b.Reliable ? b.P05 : null); Num(w, "p95", b.Reliable ? b.P95 : null);
            w.WriteNumber("samples", b.Samples); Num(w, "spanSeconds", Math.Round(b.Span.TotalSeconds, 3));
            if (b.Reason is { } reason) Str(w, "reason", reason); else w.WriteNull("reason");
            w.WriteEndObject();
        }
        else w.WriteNull("baseline");
        w.WriteEndObject();
        w.WriteStartArray("associatedEvents");
        foreach (var e in o.AssociatedEvents) { w.WriteStartObject(); Str(w, "kind", e.Kind.ToString()); Str(w, "atUtc", Utc(e.At)); Str(w, "title", e.Title); w.WriteEndObject(); }
        w.WriteEndArray();
        w.WriteNumber("associatedEventCount", o.AssociatedEventCount);
        Str(w, "explanation", o.Explanation);
        Strings(w, "limitations", o.Limitations);
        w.WriteEndObject();
    }

    private static void WriteCorrelation(Utf8JsonWriter w, AnomalyCorrelation c)
    {
        w.WriteStartObject();
        Str(w, "id", c.Id); Str(w, "pattern", c.Pattern.ToString()); Str(w, "severity", c.Severity.ToString());
        Str(w, "title", c.Title);
        Str(w, "startUtc", Utc(c.Start)); Str(w, "endUtc", Utc(c.End));
        Str(w, "timing", c.Timing.ToString());
        // Present only when every component was present in it; null for chained, tolerance-linked and single-observation correlations.
        if (c.CommonOverlapStart is { } s && c.CommonOverlapEnd is { } e) { w.WriteStartObject("commonOverlap"); Str(w, "startUtc", Utc(s)); Str(w, "endUtc", Utc(e)); w.WriteEndObject(); }
        else w.WriteNull("commonOverlap");
        Strings(w, "componentIds", c.ComponentIds);
        w.WriteStartArray("links");
        foreach (var l in c.Links)
        {
            w.WriteStartObject();
            Str(w, "firstId", l.FirstId); Str(w, "secondId", l.SecondId);
            Time(w, "overlapStartUtc", l.OverlapStart); Time(w, "overlapEndUtc", l.OverlapEnd);
            Num(w, "gapSeconds", Math.Round(l.Gap.TotalSeconds, 3));
            w.WriteEndObject();
        }
        w.WriteEndArray();
        Strings(w, "channels", c.Channels);
        Str(w, "explanation", c.Explanation);
        Strings(w, "limitations", c.Limitations);
        w.WriteEndObject();
    }

    private static void WriteCoverage(Utf8JsonWriter w, MetricCoverage c)
    {
        w.WriteStartObject();
        Str(w, "channelId", c.ChannelId); Str(w, "title", c.Title); Str(w, "unit", c.Unit);
        w.WriteBoolean("usable", c.Usable);
        w.WriteNumber("samples", c.Samples); w.WriteNumber("validSamples", c.ValidSamples); w.WriteNumber("outOfRangeSamples", c.OutOfRangeSamples);
        Num(w, "coverage", c.Coverage);
        Time(w, "firstValidUtc", c.FirstValid); Time(w, "lastValidUtc", c.LastValid);
        if (c.Reason is { } reason) Str(w, "reason", reason); else w.WriteNull("reason");
        w.WriteEndObject();
    }

    private static void WriteRule(Utf8JsonWriter w, RuleEvaluation r)
    {
        w.WriteStartObject();
        Str(w, "ruleId", r.RuleId); w.WriteNumber("ruleVersion", r.RuleVersion); Str(w, "channelId", r.ChannelId);
        Str(w, "state", r.State.ToString()); Str(w, "status", r.Status.ToString());
        if (r.Reason is { } reason) Str(w, "reason", reason); else w.WriteNull("reason");
        w.WriteNumber("observations", r.Observations); w.WriteNumber("eligibleSamples", r.EligibleSamples); w.WriteNumber("excludedSamples", r.ExcludedSamples);
        w.WriteEndObject();
    }

    // ---------- files ----------

    public static Task WriteCsvAsync(string path, AnomalyAnalysis analysis, HistoryInfo info, CancellationToken cancellationToken = default) =>
        WriteAsync(path, () => new UTF8Encoding(true).GetPreamble().Concat(Encoding.UTF8.GetBytes(Csv(analysis, info))).ToArray(), cancellationToken);

    public static Task WriteJsonAsync(string path, AnomalyAnalysis analysis, HistoryInfo info, CancellationToken cancellationToken = default) =>
        WriteAsync(path, () => new UTF8Encoding(false).GetBytes(Json(analysis, info)), cancellationToken);

    /// <summary>
    /// Builds the content off the calling thread, writes it to a temporary file beside the target, flushes it to disk, and only then replaces the target: a failure, a cancellation or
    /// a full disk leaves the previous file (or none) exactly as it was and no temporary file behind. An existing recording is never opened for writing.
    /// </summary>
    private static Task WriteAsync(string path, Func<byte[]> content, CancellationToken cancellationToken) => Task.Run(async () =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        var bytes = content();
        var full = Path.GetFullPath(path);
        var temporary = full + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous))
            {
                await stream.WriteAsync(bytes, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, full, overwrite: true);
        }
        finally { try { if (File.Exists(temporary)) File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }, cancellationToken);
}
