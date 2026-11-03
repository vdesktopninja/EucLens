using System.Text.Json;
using EucLens.App;
using EucLens.App.ViewModels;
using EucLens.Core;
using Xunit;
using EucLens.Diagnostics;
using EucLens.Providers;
using Microsoft.Extensions.Logging.Abstractions;

namespace EucLens.Tests;

/// <summary>
/// The stable release monitors the real session only: no Demo Mode, no workload selection, and nothing saved, selected or started from the
/// command line can bring synthetic data into monitoring. Recordings and exports of earlier versions keep their historical values.
/// </summary>
public sealed class StableReleaseSourceTests
{
    private static string Repo([System.Runtime.CompilerServices.CallerFilePath] string file = "") => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file)!, "../.."));
    private static string Source(string relative) => File.ReadAllText(Path.Combine(Repo(), relative));

    [Theory]
    [InlineData("""{"Dark":true,"Protocol":1,"Scenario":2,"Workload":3,"Mode":1}""")]
    [InlineData("""{"Dark":true,"Protocol":"OmnissaBlast","Scenario":"LowFps","Workload":"VideoPlayback","Mode":"DemoMode"}""")]
    [InlineData("""{"Dark":true,"Protocol":99,"Scenario":99,"Workload":99,"Mode":99}""")]
    public async Task PreferencesFromEarlierVersionsCannotReEnableDemoModeOrAWorkload(string saved)
    {
        // DesktopServices reads preferences.json with the default serializer options; a 0.5.0 file selected Demo Mode, a protocol, a scenario and a workload.
        var preferences = JsonSerializer.Deserialize<Preferences>(saved)!;
        Assert.Equal(new Preferences(true), preferences);
        Assert.Equal("""{"Dark":true}""", JsonSerializer.Serialize(preferences));

        await using var real = new RealSample();
        var desktop = new Desktop(preferences);
        var vm = new MainViewModel(real, new DiagnosticIntelligenceEngine(DiagnosticRuleSet.Default()), new SessionRecorder(), desktop, NullLogger<MainViewModel>.Instance);
        await vm.InitializeAsync(default);
        using (var run = new CancellationTokenSource(TimeSpan.FromMilliseconds(300))) await vm.RunAsync(run.Token);

        Assert.True(vm.IsDark);
        Assert.Equal("Real Session", vm.ModeLabel);
        Assert.StartsWith("REAL SESSION · ", vm.ModeBanner);
        Assert.All(real.Collected, s => Assert.False(s.Session.IsDemo));
        Assert.NotEmpty(vm.OverviewCards);

        await vm.SavePreferencesCommand.ExecuteAsync();
        Assert.Equal(new Preferences(true), desktop.Saved);
    }

    [Fact]
    public async Task RealMonitoringRejectsSyntheticSamplesSoTheyAreNeitherShownEvaluatedNorRecorded()
    {
        // Even if a synthetic generator were wired in as the application's source, its samples never enter monitoring.
        await using var synthetic = new MockMetricsProvider();
        var recorder = new SessionRecorder();
        var vm = new MainViewModel(synthetic, new DiagnosticIntelligenceEngine(DiagnosticRuleSet.Default()), recorder, new Desktop(null), NullLogger<MainViewModel>.Instance);
        await vm.InitializeAsync(default);
        vm.StartRecordingCommand.Execute(null);
        Assert.True(recorder.IsRecording);
        using (var run = new CancellationTokenSource(TimeSpan.FromMilliseconds(400))) await vm.RunAsync(run.Token);

        Assert.Equal(0, recorder.SampleCount);
        Assert.Empty(vm.OverviewCards);
        Assert.Empty(vm.Diagnostics.Findings);
        Assert.All(vm.RttChart.Points, p => Assert.Null(p.Value));
        Assert.Contains("A synthetic sample reached real monitoring and was discarded.", vm.Status);
        Assert.StartsWith("REAL SESSION", vm.RecordingState.Split(" · ")[1]);
    }

    [Fact]
    public void ProductionPagesOfferNoDataSourceDemoOrWorkloadSelection()
    {
        var views = Directory.GetFiles(Path.Combine(Repo(), "src/EucLens.App/Views"), "*.xaml").Append(Path.Combine(Repo(), "src/EucLens.App/MainWindow.xaml"));
        foreach (var file in views)
        {
            var xaml = File.ReadAllText(file);
            foreach (var forbidden in new[] { "Monitor.Modes", "SelectedMode", "Scenarios", "SelectedScenario", "Workloads", "SelectedWorkload", "Monitor.Protocols", "SelectedProtocol", "IsDemo", "Demo ", "Workload" })
                Assert.False(xaml.Contains(forbidden, StringComparison.Ordinal), $"{Path.GetFileName(file)} still contains '{forbidden}'.");
        }
        foreach (var page in new[] { "DashboardView.xaml", "SettingsView.xaml" })
        {
            var xaml = File.ReadAllText(Path.Combine(Repo(), "src/EucLens.App/Views", page));
            Assert.Contains("{Binding Monitor.ProtocolDisplay}", xaml);
            Assert.Contains("{Binding Monitor.DataAvailability}", xaml);
        }
        // The view model has nothing to select either.
        Assert.DoesNotContain(typeof(MainViewModel).GetProperties(), p => p.Name is "Modes" or "SelectedMode" or "IsDemo" or "IsReal" or "Scenarios" or "SelectedScenario"
            or "Workloads" or "SelectedWorkload" or "Protocols" or "SelectedProtocol");
        Assert.False(typeof(MainViewModel).GetMethod("UseSyntheticSource", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.IsPublic);
        Assert.Null(typeof(MainViewModel).GetMethod("UseSyntheticSource", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public));
    }

    [Fact]
    public void TheApplicationRegistersOnlyTheRealSessionSourceAndOnlyTheSmokeHarnessCreatesSyntheticData()
    {
        var app = Source("src/EucLens.App/App.xaml.cs");
        Assert.Contains("registrations.AddSingleton<IMetricsProvider, RealSessionMetricsProvider>();", app);
        Assert.DoesNotContain("AddSingleton<MockMetricsProvider", app);
        Assert.DoesNotContain("MockMetricsProvider>", app);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(app, @"new MockMetricsProvider\("));
        // Startup accepts --smoke-test (the automated harness) and the single-instance probe; nothing selects a data source.
        Assert.DoesNotContain("--demo", app, StringComparison.OrdinalIgnoreCase);

        var callers = Directory.GetFiles(Path.Combine(Repo(), "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(f => File.ReadAllText(f) is var text && (text.Contains("UseSyntheticSource(", StringComparison.Ordinal) || text.Contains("UseSyntheticDataAsync(", StringComparison.Ordinal)
                || text.Contains("MockMetricsProvider", StringComparison.Ordinal)))
            .Select(f => Path.GetFileName(f)).Order(StringComparer.Ordinal).ToArray();
        // The generator itself, the view model hook, and the smoke-harness files that use it (live sections and recording fixtures).
        Assert.Equal(["App.xaml.cs", "BackgroundValidation.cs", "HistoryValidation.cs", "MainViewModel.cs", "MockMetricsProvider.cs"], callers);
        Assert.Contains("if (smoke) await RunSmokeAsync(", app);
    }

    [Fact]
    public async Task RecordingsWithAHistoricalWorkloadAndDemoDataStillOpenAndExportTheirValues()
    {
        var dir = IncidentTests.Dir;
        // A 0.5.0 Demo Mode recording whose samples carry the workload the user had selected.
        var oldStorage = new MemoryStorage();
        var oldWriter = new RecordingWriter(() => IncidentTests.Options(compress: false), oldStorage, new FakeClock(oldStorage.Now), "0.5.0");
        var oldRecorder = new SessionRecorder(oldWriter, new FakeClock(oldStorage.Now));
        oldRecorder.Start(true);
        for (var i = 0; i < 4; i++) oldRecorder.Add(TestSamples.Make(i) with { Workload = WorkloadContext.VideoPlayback });
        await oldRecorder.StopAsync();
        var oldPart = Path.Combine(dir, oldStorage.Names(dir).Single(n => n.EndsWith(".jsonl", StringComparison.Ordinal)));
        Assert.Contains("\"Workload\":\"VideoPlayback\"", HistoryFixture.ReadText(oldStorage, oldPart));

        var history = HistoryFixture.Load(oldStorage, RecordingFormat.ShortIdOf(Path.GetFileName(oldPart)));
        Assert.Equal("0.5.0", history.Info.AppVersion);
        Assert.True(history.Info.IsDemo);
        Assert.Equal(4, history.SampleCount);
        var replayed = RecordingFormat.ReadSamples(oldStorage, [oldPart]).ToArray();
        Assert.All(replayed, s => Assert.Equal(WorkloadContext.VideoPlayback, s.Workload));

        // A recording made now has no workload; the CSV column stays where it was and says Unavailable, older samples keep their value.
        var storage = new MemoryStorage();
        var writer = new RecordingWriter(() => IncidentTests.Options(compress: false), storage, new FakeClock(storage.Now));
        var recorder = new SessionRecorder(writer, new FakeClock(storage.Now));
        recorder.Start(false);
        for (var i = 0; i < 4; i++) recorder.Add(TestSamples.Make(i) with { Workload = null });
        await recorder.StopAsync();
        var part = Path.Combine(dir, storage.Names(dir).Single(n => n.EndsWith(".jsonl", StringComparison.Ordinal)));
        Assert.DoesNotContain("\"Workload\"", HistoryFixture.ReadText(storage, part));
        Assert.All(RecordingFormat.ReadSamples(storage, [part]), s => Assert.Null(s.Workload));

        var csv = Path.Combine(Path.GetTempPath(), $"euclens-workload-{Guid.NewGuid():N}.csv");
        try
        {
            await RecordingExport.ExportCsvAsync(csv, () => replayed.Take(1).Concat(RecordingFormat.ReadSamples(storage, [part]).Take(1)));
            var lines = await File.ReadAllLinesAsync(csv);
            var column = Array.IndexOf(lines[0].Split(','), "workload");
            Assert.Equal(5, column);
            Assert.Equal("VideoPlayback", lines[1].Split(',')[column]);
            Assert.Equal("Unavailable", lines[2].Split(',')[column]);
        }
        finally { File.Delete(csv); }
    }

    /// <summary>A real-session source: non-synthetic samples as a Windows session without Horizon Blast counters would deliver.</summary>
    private sealed class RealSample : IMetricsProvider
    {
        public List<SessionSnapshot> Collected { get; } = [];
        public string Name => "real (test)";
        public Task<ProviderAvailability> GetAvailabilityAsync(CancellationToken cancellationToken = default) => Task.FromResult(new ProviderAvailability(true, "test"));
        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<SessionSnapshot> CollectAsync(CancellationToken cancellationToken = default)
        {
            var s = DiagnosticsPresentationTests.Snap(DateTimeOffset.UtcNow) with { Workload = null };
            s = s with { Session = s.Session with { Protocol = RemoteProtocol.Unknown } };
            lock (Collected) Collected.Add(s);
            return Task.FromResult(s);
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class Desktop(Preferences? stored) : IDesktopServices
    {
        public Preferences? Saved { get; private set; }
        public string? SelectExportPath(bool json) => null;
        public bool ConfirmNewRecording() => true;
        public void ApplyTheme(bool dark) { }
        public Task SavePreferencesAsync(Preferences preferences) { Saved = preferences; return Task.CompletedTask; }
        public Task<Preferences?> LoadPreferencesAsync() => Task.FromResult(stored);
    }
}
