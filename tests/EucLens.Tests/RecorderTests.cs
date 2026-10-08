using System.Globalization;
using System.Text.Json;
using EucLens.Core;
using Xunit;

namespace EucLens.Tests;

public sealed class RecorderTests
{
    [Fact]
    public async Task RecordingLifecycleOnlyCapturesActiveSamplesAndNewRecordingClears()
    {
        var recorder = new SessionRecorder();
        var sample = await DiagnosticTests.Sample();
        recorder.Add(sample); Assert.Equal(0, recorder.SampleCount);
        recorder.Start(); recorder.Add(sample);
        Assert.Throws<InvalidOperationException>(() => recorder.Start());
        recorder.Stop(); recorder.Add(sample);
        Assert.Equal(1, recorder.SampleCount);
        Assert.False(recorder.IsRecording);
        var stoppedDuration = recorder.Duration;
        Assert.Equal(stoppedDuration, recorder.Duration);
        recorder.Start(); Assert.Equal(0, recorder.SampleCount);
    }
    [Fact]
    public async Task RecorderStopsAtBoundedSampleLimit()
    {
        var recorder = new SessionRecorder();
        var sample = await DiagnosticTests.Sample();
        recorder.Start();
        for (var i = 0; i < SessionRecorder.MaximumSamples + 5; i++) recorder.Add(sample);
        Assert.False(recorder.IsRecording);
        Assert.Equal(SessionRecorder.MaximumSamples, recorder.SampleCount);
    }
    [Fact]
    public async Task ExportsPreserveTimestampsDemoMetadataMissingMetricsAndInvariantCsv()
    {
        var directory = Path.Combine(Path.GetTempPath(), "EucLens-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var oldCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("pl-PL");
            var s = await DiagnosticTests.Sample();
            s = s with { Gpu = s.Gpu with { Model = "GPU, \"demo\"" }, Connection = s.Connection with { RoundTripTime = Metric.Available(12.5, "ms"), PacketLoss = Metric.Unavailable("%", "Not provided") } };
            var recorder = new SessionRecorder(); recorder.Start(); recorder.Add(s); recorder.Stop();
            var jsonPath = Path.Combine(directory, "recording.json");
            var csvPath = Path.Combine(directory, "recording.csv");
            await recorder.ExportJsonAsync(jsonPath); await recorder.ExportCsvAsync(csvPath);
            using var json = JsonDocument.Parse(await File.ReadAllTextAsync(jsonPath));
            var row = json.RootElement[0];
            Assert.Equal(s.Timestamp, row.GetProperty("Timestamp").GetDateTimeOffset());
            Assert.True(row.GetProperty("Session").GetProperty("IsDemo").GetBoolean());
            Assert.Equal("Unavailable", row.GetProperty("Connection").GetProperty("PacketLoss").GetProperty("Status").GetString());
            var csv = await File.ReadAllTextAsync(csvPath);
            Assert.Contains("12.5", csv); Assert.Contains("Unavailable", csv); Assert.Contains("\"GPU, \"\"demo\"\"\"", csv);
            Assert.Contains("demo_scenario", csv);
            Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
        }
        finally { CultureInfo.CurrentCulture = oldCulture; Directory.Delete(directory, true); }
    }
    [Fact]
    public async Task CancelledExportDoesNotReplaceExistingFileOrLeaveTemporaryFile()
    {
        var directory = Path.Combine(Path.GetTempPath(), "EucLens-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "metrics.json");
            await File.WriteAllTextAsync(path, "original");
            var recorder = new SessionRecorder(); recorder.Start(); recorder.Add(await DiagnosticTests.Sample()); recorder.Stop();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => recorder.ExportJsonAsync(path, new CancellationToken(true)));
            Assert.Equal("original", await File.ReadAllTextAsync(path));
            Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
        }
        finally { Directory.Delete(directory, true); }
    }
}
