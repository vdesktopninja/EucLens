using System.Text.Json;
using EucLens.App;
using EucLens.App.ViewModels;
using EucLens.Core;
using EucLens.Diagnostics;
using EucLens.Providers;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EucLens.Tests;

/// <summary>Mocked counter readers; no Horizon Agent or Windows performance counters are required.</summary>
public sealed class BlastProviderTests
{
    private const string Session = BlastCounterCatalog.SessionCategory;
    private const string Imaging = BlastCounterCatalog.ImagingCategory;
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("Session ID: 3; (main)", 3, true)]
    [InlineData("Session ID: 3; Channel: Imaging; (main)", 3, true)]
    [InlineData("Session ID: 13; (main)", 3, false)]
    [InlineData("Session ID: 31; Channel: Imaging; (main)", 3, false)]
    [InlineData("Session ID: 3", 3, false)]
    [InlineData("Session ID: 7; (main)", 7, true)]
    [InlineData("_Total", 3, false)]
    public void MatchesOnlyTheExactSessionId(string instance, int session, bool expected) =>
        Assert.Equal(expected, BlastCounterCatalog.MatchesSession(instance, session));

    [Fact]
    public async Task ReadsOnlyCurrentSessionInstancesWhenOtherUsersAreConnected()
    {
        var reader = new FakeReader();
        reader.Add(Session, "Session ID: 3; (main)", "RTT", 42);
        reader.Add(Session, "Session ID: 5; (main)", "RTT", 999);
        reader.Add(Imaging, "Session ID: 3; Channel: Imaging; (main)", "Frames per second", 24);
        reader.Add(Imaging, "Session ID: 5; Channel: Imaging; (main)", "Frames per second", 60);
        await using var provider = new OmnissaBlastProvider(reader, new FakeContext(5));

        var sample = await provider.CollectAsync();

        Assert.Equal(999, sample.Connection.RoundTripTime.Value);
        Assert.Equal(60, sample.Graphics.FramesPerSecond.Value);
        Assert.Equal(5, sample.Session.SessionId);
        Assert.DoesNotContain(reader.ReadInstances, i => i.StartsWith("Session ID: 3;", StringComparison.Ordinal));
        Assert.False(sample.Session.IsDemo);
    }

    [Fact]
    public async Task PreservesMetadataAndConvertsDocumentedKbpsToMbps()
    {
        var reader = new FakeReader();
        reader.Add(Imaging, "Session ID: 3; Channel: Imaging; (main)", "Outbound Bandwidth (Kbps)", 2500, raw: 2500);
        await using var provider = new OmnissaBlastProvider(reader, new FakeContext(3));

        var metric = (await provider.CollectAsync()).BlastMetrics!["imagingOutboundMbps"];

        Assert.Equal(2.5, metric.Value);
        Assert.Equal("Mbps", metric.Unit);
        Assert.Equal(2500, metric.RawValue);
        Assert.Equal(Now, metric.Timestamp);
        Assert.Equal(MetricStatus.Available, metric.Status);
        Assert.Equal(@"\Horizon Blast Imaging Counters(Session ID: 3; Channel: Imaging; (main))\Outbound Bandwidth (Kbps)", metric.Source);
    }

    [Fact]
    public async Task ZeroIsAMeasurementNotAnUnavailableValue()
    {
        var reader = new FakeReader();
        reader.Add(Imaging, "Session ID: 3; Channel: Imaging; (main)", "Frames per second", 0);
        await using var provider = new OmnissaBlastProvider(reader, new FakeContext(3));

        var sample = await provider.CollectAsync();

        Assert.Equal(MetricStatus.Available, sample.Graphics.FramesPerSecond.Status);
        Assert.Equal(0, sample.Graphics.FramesPerSecond.Value);
        Assert.NotEqual("N/A", sample.Graphics.FramesPerSecond.Display);
    }

    [Fact]
    public async Task DistinguishesUnavailableStaleAndFailedSamples()
    {
        var reader = new FakeReader();
        const string instance = "Session ID: 3; (main)";
        reader.Add(Session, instance, "RTT", 0, status: MetricStatus.Stale, error: "Native counter timestamp has not advanced.");
        reader.Add(Session, instance, "Packet Loss (Uplink)", 0, status: MetricStatus.Failed, error: "Win32Exception: access denied");
        reader.Throw(Session, instance, "Automatic Reconnect Count", new InvalidOperationException("Instance vanished"));
        reader.Add(Session, instance, "Jitter (Uplink)", double.NaN);
        await using var provider = new OmnissaBlastProvider(reader, new FakeContext(3));

        var metrics = (await provider.CollectAsync()).BlastMetrics!;

        Assert.Equal(MetricStatus.Stale, metrics["rtt"].Status);
        Assert.Null(metrics["rtt"].Value);
        Assert.Equal(MetricStatus.Failed, metrics["packetLoss"].Status);
        Assert.Equal(MetricStatus.Failed, metrics["reconnects"].Status);
        Assert.Equal("Instance vanished", metrics["reconnects"].Error);
        Assert.Equal(MetricStatus.Failed, metrics["jitter"].Status);
        Assert.Equal(MetricStatus.Unavailable, metrics["estimatedBandwidth"].Status);
        Assert.All(metrics.Values.Where(m => m.Status != MetricStatus.Available), m => Assert.Equal("N/A", m.Display));
    }

    [Fact]
    public async Task MissingAgentReportsNotApplicableWithoutReadingCounters()
    {
        var reader = new FakeReader();
        reader.Add(Session, "Session ID: 3; (main)", "RTT", 10);
        await using var provider = new OmnissaBlastProvider(reader, new FakeContext(3, installed: false));

        var sample = await provider.CollectAsync();
        var availability = await provider.GetAvailabilityAsync();

        Assert.All(sample.BlastMetrics!.Values, m => Assert.Equal(MetricStatus.Unavailable, m.Status));
        Assert.Contains("not detected", sample.Connection.RoundTripTime.UnavailableReason);
        Assert.Empty(reader.ReadInstances);
        Assert.False(availability.IsAvailable);
    }

    [Fact]
    public async Task DisconnectedSessionIsUnavailableAndRecoversOnReconnect()
    {
        var reader = new FakeReader();
        reader.Add(Session, "Session ID: 3; (main)", "RTT", 18);
        var context = new FakeContext(3, active: false);
        await using var provider = new OmnissaBlastProvider(reader, context);

        Assert.Equal(MetricStatus.Unavailable, (await provider.CollectAsync()).Connection.RoundTripTime.Status);
        context.Active = true;
        var recovered = await provider.CollectAsync();

        Assert.Equal(18, recovered.Connection.RoundTripTime.Value);
        Assert.True(reader.Resets >= 2, "Discovery changes must release cached native counter handles.");
    }

    [Fact]
    public async Task AmbiguousInstancesAreRefused()
    {
        var reader = new FakeReader();
        reader.Add(Session, "Session ID: 3; (main)", "RTT", 10);
        reader.Add(Session, "Session ID: 3; (secondary)", "RTT", 90);
        await using var provider = new OmnissaBlastProvider(reader, new FakeContext(3));

        var rtt = (await provider.CollectAsync()).Connection.RoundTripTime;

        Assert.Equal(MetricStatus.Unavailable, rtt.Status);
        Assert.Contains("ambiguous", rtt.UnavailableReason);
    }

    [Fact]
    public async Task DiscoveryAndContextFailuresDoNotThrow()
    {
        var reader = new FakeReader { InstanceFailure = new UnauthorizedAccessException("Performance counter access denied") };
        await using var failingReader = new OmnissaBlastProvider(reader, new FakeContext(3));
        await using var failingContext = new OmnissaBlastProvider(new FakeReader(), new ThrowingContext());

        var a = await failingReader.CollectAsync();
        var b = await failingContext.CollectAsync();

        Assert.All(a.BlastMetrics!.Values, m => Assert.Equal(MetricStatus.Failed, m.Status));
        Assert.All(b.BlastMetrics!.Values, m => Assert.Equal(MetricStatus.Failed, m.Status));
    }

    [Fact]
    public async Task EncoderTypeIsReportedAsRawCodeOnly()
    {
        var reader = new FakeReader();
        reader.Add(Imaging, "Session ID: 3; Channel: Imaging; (main)", "Encoder Type", 6);
        await using var provider = new OmnissaBlastProvider(reader, new FakeContext(3));

        var graphics = (await provider.CollectAsync()).Graphics;

        Assert.Equal("Raw code 6", graphics.Encoder);
        Assert.Contains("unverified", graphics.Codec);
        Assert.Null(graphics.HardwareEncoding);
    }

    [Fact]
    public void UndocumentedUnitsAreFlaggedAndNotScaled()
    {
        foreach (var key in new[] { "jitter", "estimatedBandwidth", "pollRate", "fbcRate" })
        {
            var definition = BlastCounterCatalog.All.Single(d => d.Key == key);
            Assert.False(definition.UnitVerified);
            Assert.Equal(1, definition.Scale);
            Assert.Contains("unverified", definition.Unit);
        }
        Assert.Equal(BlastCounterCatalog.All.Count, BlastCounterCatalog.All.Select(d => d.Key).Distinct().Count());
    }

    [Fact]
    public async Task DisposeReleasesReaderAndRejectsFurtherCollection()
    {
        var reader = new FakeReader();
        var provider = new OmnissaBlastProvider(reader, new FakeContext(3));
        await provider.DisposeAsync();
        await provider.DisposeAsync();

        Assert.Equal(1, reader.Disposals);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => provider.CollectAsync());
    }

    [Fact]
    public async Task CancellationIsHonoured()
    {
        await using var provider = new OmnissaBlastProvider(new FakeReader(), new FakeContext(3));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.CollectAsync(cancelled.Token));
    }

    [Fact]
    public async Task StaleRttNeverProducesAHealthyAssessment()
    {
        var reader = new FakeReader();
        reader.Add(Session, "Session ID: 3; (main)", "RTT", 0, status: MetricStatus.Stale, error: "Not advanced");
        await using var provider = new OmnissaBlastProvider(reader, new FakeContext(3));

        var report = new DiagnosticEngine([new LatencyRule()]).Evaluate(await provider.CollectAsync());

        Assert.Equal(HealthStatus.Unavailable, report.Health);
        Assert.Equal(MetricStatus.Stale, report.Results[0].Status);
    }

    [Fact]
    public async Task IdleDesktopWithLowFpsRaisesNoWarning()
    {
        var reader = new FakeReader();
        reader.Add(Imaging, "Session ID: 3; Channel: Imaging; (main)", "Frames per second", 1);
        await using var provider = new OmnissaBlastProvider(reader, new FakeContext(3));
        var sample = await provider.CollectAsync();

        var report = new DiagnosticEngine([new FrameRateRule()]).Evaluate(sample with { Workload = WorkloadContext.Idle });

        Assert.DoesNotContain(report.Results, r => r.Severity != Severity.Info);
    }

    [Fact]
    public async Task RealRecordingExportsCounterMetadata()
    {
        var reader = new FakeReader();
        reader.Add(Session, "Session ID: 3; (main)", "RTT", 31, raw: 31);
        reader.Add(Session, "Session ID: 3; (main)", "Packet Loss (Uplink)", 0, status: MetricStatus.Stale, error: "Not advanced");
        await using var provider = new OmnissaBlastProvider(reader, new FakeContext(3));
        var recorder = new SessionRecorder();
        recorder.Start();
        recorder.Add(await provider.CollectAsync());
        recorder.Stop();
        var directory = Directory.CreateTempSubdirectory("euclens-blast-");
        try
        {
            var csvPath = Path.Combine(directory.FullName, "real.csv");
            var jsonPath = Path.Combine(directory.FullName, "real.json");
            await recorder.ExportCsvAsync(csvPath);
            await recorder.ExportJsonAsync(jsonPath);
            var lines = await File.ReadAllLinesAsync(csvPath);
            var header = lines[0].Split(',');
            var row = lines[1].Split(',');
            string Cell(string column) => row[Array.IndexOf(header, column)];

            Assert.Equal("RealSession", Cell("data_source"));
            Assert.Equal("31", Cell("blast_rtt"));
            Assert.Equal("31", Cell("blast_rtt_raw"));
            Assert.Equal("Available", Cell("blast_rtt_status"));
            Assert.Equal("Stale", Cell("blast_packetLoss"));
            Assert.Equal("Stale", Cell("blast_packetLoss_status"));
            Assert.Equal("Unavailable", Cell("gpu_percent"));

            using var json = JsonDocument.Parse(await File.ReadAllTextAsync(jsonPath));
            var rtt = json.RootElement[0].GetProperty("BlastMetrics").GetProperty("rtt");
            Assert.Equal(31, rtt.GetProperty("RawValue").GetInt64());
            Assert.StartsWith(@"\Horizon Blast Session Counters", rtt.GetProperty("Source").GetString());
            Assert.False(json.RootElement[0].GetProperty("Session").GetProperty("IsDemo").GetBoolean());
            // The provider declares no workload: none is selected anywhere, and frame-rate rules use the measured screen activity.
            Assert.Equal("Unavailable", Cell("workload"));
            Assert.True(!json.RootElement[0].TryGetProperty("Workload", out var workload) || workload.ValueKind == JsonValueKind.Null);
        }
        finally { directory.Delete(true); }
    }

    [Fact]
    public async Task RealSessionShowsNotApplicableAndChangingTheSourceStopsRecording()
    {
        await using var demo = new MockMetricsProvider();
        await using var real = new OmnissaBlastProvider(new FakeReader(), new FakeContext(3, installed: false));
        var recorder = new SessionRecorder();
        var vm = new MainViewModel(real, new DiagnosticEngine([new LatencyRule()]), recorder, new FakeDesktop(),
            NullLogger<MainViewModel>.Instance);
        await vm.InitializeAsync(default);
        Assert.Equal("Real Session", vm.ModeLabel);

        using (var run = new CancellationTokenSource(TimeSpan.FromMilliseconds(400))) await vm.RunAsync(run.Token);

        Assert.NotEmpty(vm.OverviewCards);
        Assert.All(vm.OverviewCards, c => Assert.Equal("N/A", c.Value));
        Assert.StartsWith("REAL SESSION", vm.ModeBanner);
        vm.StartRecordingCommand.Execute(null);
        Assert.Contains("REAL SESSION", vm.RecordingState);

        // Only automated validation can change the source; a recording never continues across the change.
        await demo.InitializeAsync();
        vm.UseSyntheticSource(demo);

        Assert.False(recorder.IsRecording);
        Assert.Contains("Recording stopped", vm.Status);
        Assert.Empty(vm.OverviewCards);
        Assert.StartsWith("SYNTHETIC TEST DATA", vm.ModeBanner);
    }

    private sealed class FakeContext(int session, bool installed = true, bool active = true) : IBlastSessionContext
    {
        public bool Active { get; set; } = active;
        public BlastSessionContext GetCurrent() => !installed
            ? new(session, false, false, "Horizon Agent installation was not detected (test).")
            : new(session, true, Active, Active ? "active" : $"Windows session {session} is disconnected.");
    }

    private sealed class ThrowingContext : IBlastSessionContext
    {
        public BlastSessionContext GetCurrent() => throw new System.ComponentModel.Win32Exception(5, "WTS query denied");
    }

    private sealed class FakeReader : IBlastCounterReader
    {
        private readonly Dictionary<(string, string, string), Func<CounterReading>> counters = [];
        public Exception? InstanceFailure { get; init; }
        public List<string> ReadInstances { get; } = [];
        public int Resets { get; private set; }
        public int Disposals { get; private set; }

        public void Add(string category, string instance, string counter, double value, long? raw = null,
            MetricStatus status = MetricStatus.Available, string? error = null) =>
            counters[(category, instance, counter)] = () => status == MetricStatus.Available
                ? new(value, raw, Now) : new(null, raw, Now, status, error);
        public void Throw(string category, string instance, string counter, Exception exception) =>
            counters[(category, instance, counter)] = () => throw exception;

        public IReadOnlyList<string> GetInstances(string category)
        {
            if (InstanceFailure is not null) throw InstanceFailure;
            return counters.Keys.Where(k => k.Item1 == category).Select(k => k.Item2).Distinct().ToArray();
        }
        public CounterReading Read(string category, string instance, string counter)
        {
            ReadInstances.Add(instance);
            return counters.TryGetValue((category, instance, counter), out var read) ? read()
                : new(null, null, Now, MetricStatus.Unavailable, $"Counter is not installed: {counter}.");
        }
        public void Reset() => Resets++;
        public void Dispose() => Disposals++;
    }

    private sealed class FakeDesktop : IDesktopServices
    {
        public string? SelectExportPath(bool json) => null;
        public bool ConfirmNewRecording() => true;
        public void ApplyTheme(bool dark) { }
        public Task SavePreferencesAsync(Preferences preferences) => Task.CompletedTask;
        public Task<Preferences?> LoadPreferencesAsync() => Task.FromResult<Preferences?>(null);
    }
}
