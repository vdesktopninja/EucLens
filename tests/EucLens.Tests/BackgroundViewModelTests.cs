using EucLens.App;
using EucLens.App.ViewModels;
using EucLens.Core;
using EucLens.Diagnostics;
using EucLens.Providers;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EucLens.Tests;

/// <summary>Monitoring and recording keep working with the window hidden; settings, recordings list and failure reporting.</summary>
public sealed class BackgroundViewModelTests
{
    private static readonly string Dir = Path.Combine(Path.GetTempPath(), "euclens-vm-recordings");

    private sealed class TestDesktop : IDesktopServices
    {
        public List<string> Opened { get; } = [];
        public string? ExportPath { get; set; }
        public string? Folder { get; set; }
        public string? SelectExportPath(bool json) => ExportPath;
        public bool ConfirmNewRecording() => true;
        public void ApplyTheme(bool dark) { }
        public Task SavePreferencesAsync(Preferences preferences) => Task.CompletedTask;
        public Task<Preferences?> LoadPreferencesAsync() => Task.FromResult<Preferences?>(null);
        public string? SelectFolder(string? initialDirectory) => Folder;
        public bool OpenPath(string path, bool selectFile = false) { Opened.Add(path); return true; }
    }

    private sealed class Rig : IAsyncDisposable
    {
        public MockMetricsProvider Provider = new();
        public MemoryStorage Storage = new();
        public SessionRecorder Recorder = null!;
        public RecordingWriter Writer = null!;
        public MonitoringService Monitoring = null!;
        public MonitoringCoordinator Coordinator = null!;
        public MainViewModel Vm = null!;
        public TestDesktop Desktop = new();
        public CancellationTokenSource Cancel = new();
        public Task Loop = Task.CompletedTask;

        public static async Task<Rig> CreateAsync(bool persistent = true, long quota = 512L << 20, MemoryStorage? storage = null)
        {
            var rig = new Rig();
            if (storage is not null) rig.Storage = storage;
            if (persistent)
            {
                rig.Writer = new RecordingWriter(() => new RecordingOptions { Directory = Dir, QuotaBytes = quota, SegmentBytes = 256 << 10, MinimumFreeBytes = 0, FlushInterval = TimeSpan.FromMilliseconds(50), CompressClosedParts = false },
                    rig.Storage);
                rig.Recorder = new SessionRecorder(rig.Writer);
            }
            else rig.Recorder = new SessionRecorder();
            rig.Coordinator = new MonitoringCoordinator(interval: TimeSpan.FromMilliseconds(20));
            rig.Monitoring = new MonitoringService(rig.Coordinator, rig.Recorder);
            rig.Vm = new MainViewModel(rig.Provider, new DiagnosticIntelligenceEngine(DiagnosticRuleSet.Default()), rig.Recorder, rig.Desktop, NullLogger<MainViewModel>.Instance,
                monitoring: rig.Monitoring, background: new BackgroundViewModel(rig.Monitoring, rig.Recorder, rig.Desktop, storage: rig.Storage));
            await rig.Vm.InitializeAsync(default);
            return rig;
        }

        public void Run() => Loop = Vm.RunAsync(Cancel.Token);
        public async ValueTask DisposeAsync()
        {
            Cancel.Cancel();
            try { await Loop; } catch (OperationCanceledException) { }
            await Recorder.StopAsync();
            await Provider.DisposeAsync();
        }
    }

    private static async Task WaitUntil(Func<bool> condition, int milliseconds = 10_000)
    {
        var until = Environment.TickCount64 + milliseconds;
        while (!condition()) { if (Environment.TickCount64 > until) throw new TimeoutException("Condition not reached."); await Task.Delay(10); }
    }

    [Fact]
    public async Task AHiddenWindowStillCollectsEvaluatesAndRecordsButDoesNotRefreshThePresentation()
    {
        await using var rig = await Rig.CreateAsync(persistent: false);
        rig.Run();
        await WaitUntil(() => rig.Vm.NetworkReadout != "Unavailable");
        rig.Vm.StartRecordingCommand.Execute(null);
        rig.Vm.SetPresenting(false);                       // the window is hidden to the tray
        var shownBefore = rig.Vm.UpdatedAt;
        var chartBefore = rig.Vm.RttChart.Points[^1].At;
        var cardsBefore = rig.Vm.OverviewCards.ToArray();
        var countBefore = rig.Recorder.SampleCount;
        var ticksBefore = rig.Coordinator.Ticks;
        await WaitUntil(() => rig.Recorder.SampleCount >= countBefore + 10);
        Assert.True(rig.Coordinator.Ticks >= ticksBefore + 10);                                  // collection continues
        Assert.True(rig.Recorder.IsRecording);                                                   // so does recording
        Assert.Equal(shownBefore, rig.Vm.UpdatedAt);                                             // nothing is rendered
        Assert.Equal(chartBefore, rig.Vm.RttChart.Points[^1].At);                                // charts are not fed either
        Assert.Equal(cardsBefore, rig.Vm.OverviewCards.ToArray());
        Assert.Equal(rig.Monitoring.LastSampleAt, rig.Recorder.Samples[^1].Timestamp);           // the latest sample is known to the services

        rig.Vm.SetPresenting(true);                        // restored: the current state is presented at once
        Assert.True(rig.Vm.RttChart.Points[^1].At > chartBefore);
        Assert.Contains(rig.Vm.RttChart.Points, p => p.Value is null); // the hidden interval is a gap, not a line across it
    }

    [Fact]
    public async Task RepeatedHideAndRestoreNeverStartsASecondSamplerOrDuplicatesSamples()
    {
        await using var rig = await Rig.CreateAsync(persistent: false);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        rig.Run();
        rig.Vm.StartRecordingCommand.Execute(null);
        for (var i = 0; i < 40; i++) { rig.Vm.SetPresenting(false); await Task.Delay(8); rig.Vm.SetPresenting(true); await Task.Delay(8); }
        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Vm.RunAsync(CancellationToken.None)); // exactly one loop exists
        await WaitUntil(() => rig.Recorder.SampleCount >= 30);
        var stamps = rig.Recorder.Samples.Select(s => s.Timestamp).ToArray();
        Assert.Equal(stamps.Length, stamps.Distinct().Count());
        Assert.Equal(stamps.OrderBy(t => t), stamps);                                           // strictly in order, none twice
        // One loop at a 20 ms cadence cannot exceed one tick per 20 ms.
        Assert.True(rig.Coordinator.Ticks <= clock.Elapsed.TotalMilliseconds / 20 + 5, $"{rig.Coordinator.Ticks} ticks in {clock.Elapsed.TotalMilliseconds:0} ms");
    }

    [Fact]
    public async Task APersistedRecordingContinuesWhileHiddenAndHoldsEverySample()
    {
        await using var rig = await Rig.CreateAsync();
        rig.Run();
        await WaitUntil(() => rig.Vm.NetworkReadout != "Unavailable");
        rig.Vm.StartRecordingCommand.Execute(null);
        Assert.True(rig.Recorder.IsRecording);
        Assert.True(rig.Recorder.IsPersistent);
        rig.Vm.SetPresenting(false);
        var hiddenFrom = rig.Recorder.SampleCount;
        await WaitUntil(() => rig.Recorder.SampleCount >= hiddenFrom + 25);
        rig.Vm.SetPresenting(true);
        rig.Vm.StopRecordingCommand.Execute(null);
        await rig.Recorder.StopAsync();
        var summary = Assert.Single(RecordingCatalog.Scan(rig.Storage, Dir));
        Assert.True(summary.IsComplete);
        Assert.Equal(rig.Recorder.SampleCount, (int)summary.Samples);
        Assert.True(summary.Samples >= 25);
        Assert.Equal((int)summary.Samples, rig.Writer.ReadSamples().Count());                    // nothing is missing, including the hidden interval
        Assert.False(rig.Vm.HasUnexportedSamples);                                              // it is saved: exiting loses nothing
        Assert.Contains("Demo data", Assert.Single(await ListAsync(rig)).Source);
    }

    private static async Task<List<RecordingItem>> ListAsync(Rig rig)
    {
        var folder = Path.Combine(Path.GetTempPath(), "euclens-vm-settings-" + Guid.NewGuid().ToString("N"));
        var store = new BackgroundSettingsStore(Path.Combine(folder, "background.json"));
        await store.SaveAsync(BackgroundSettings.Defaults with { RecordingDirectory = Dir });
        var background = new BackgroundViewModel(rig.Monitoring, rig.Recorder, rig.Desktop, store, rig.Storage);
        await background.LoadAsync();
        await background.RefreshRecordingsAsync();
        Directory.Delete(folder, true);
        return background.Recordings.ToList();
    }

    [Fact]
    public async Task PausingStopsCollectionAndMarksTheRecordingAndResumingContinues()
    {
        await using var rig = await Rig.CreateAsync();
        rig.Run();
        await WaitUntil(() => rig.Vm.NetworkReadout != "Unavailable");
        rig.Vm.StartRecordingCommand.Execute(null);
        await WaitUntil(() => rig.Recorder.SampleCount >= 5);
        Assert.True(rig.Vm.Background.PauseMonitoringCommand.CanExecute(null));
        rig.Vm.Background.PauseMonitoringCommand.Execute(null);
        await Task.Delay(100);
        var atPause = rig.Recorder.SampleCount;
        await Task.Delay(250);
        Assert.Equal(atPause, rig.Recorder.SampleCount);                                         // paused means no samples
        Assert.True(rig.Recorder.IsRecording);                                                   // the recording stays open
        Assert.Equal(TrayState.Paused, TrayStateResolver.Resolve(rig.Monitoring.Status).State);
        Assert.False(rig.Vm.StartRecordingCommand.CanExecute(null));
        Assert.True(rig.Vm.Background.ResumeMonitoringCommand.CanExecute(null));
        rig.Vm.Background.ResumeMonitoringCommand.Execute(null);
        await WaitUntil(() => rig.Recorder.SampleCount >= atPause + 5);
        await rig.Recorder.StopAsync();
        var kinds = new List<string>();
        foreach (var name in rig.Storage.Names(Dir).Where(n => n.Contains("-part")))
            foreach (var (type, root) in RecordingFormat.ReadLines(rig.Storage, Path.Combine(Dir, name)))
                if (type == "mark") kinds.Add(root.GetProperty("kind").GetString()!);
        Assert.Equal(new[] { "Paused", "Resumed" }, kinds);
    }

    [Fact]
    public async Task StartingARecordingNeedsMonitoringAndAnUnusableFolderIsReportedWithoutRecording()
    {
        var storage = new MemoryStorage { CreateFault = _ => new UnauthorizedAccessException("Access to the path is denied.") };
        await using var rig = await Rig.CreateAsync(storage: storage);
        rig.Run();
        await WaitUntil(() => rig.Vm.NetworkReadout != "Unavailable");
        rig.Vm.StartRecordingCommand.Execute(null);
        Assert.False(rig.Recorder.IsRecording);
        Assert.Contains("could not be started", rig.Vm.Status);
        Assert.Contains("denied", rig.Vm.Status);
        Assert.Equal(TrayState.Monitoring, TrayStateResolver.Resolve(rig.Monitoring.Status).State); // never claims to record
        storage.CreateFault = null;
        rig.Coordinator.Pause();
        Assert.False(rig.Vm.StartRecordingCommand.CanExecute(null));
    }

    [Fact]
    public async Task ARecordingThatHitsTheQuotaEndsExplicitlyAndTheStatusSaysSo()
    {
        await using var rig = await Rig.CreateAsync(quota: 120_000);
        rig.Monitoring.Notifications.Enabled = true;
        var notes = new List<NotificationRequest>(); rig.Monitoring.NotificationRaised += notes.Add;
        rig.Run();
        await WaitUntil(() => rig.Vm.NetworkReadout != "Unavailable");
        rig.Vm.StartRecordingCommand.Execute(null);
        await WaitUntil(() => !rig.Recorder.IsRecording, 30_000);
        await WaitUntil(() => rig.Vm.Status.Contains("quota", StringComparison.Ordinal));
        Assert.Contains("storage quota", rig.Vm.Status);
        Assert.Equal(TrayState.Warning, TrayStateResolver.Resolve(rig.Monitoring.Status).State);
        Assert.Contains(notes, n => n.Kind == NotificationKind.RecordingStopped);
        Assert.True(Assert.Single(RecordingCatalog.Scan(rig.Storage, Dir)).IsComplete);
    }

    [Fact]
    public async Task ListedRecordingsExportWithTheExistingCsvAndJsonFormats()
    {
        await using var rig = await Rig.CreateAsync();
        rig.Run();
        await WaitUntil(() => rig.Vm.NetworkReadout != "Unavailable");
        rig.Vm.StartRecordingCommand.Execute(null);
        await WaitUntil(() => rig.Recorder.SampleCount >= 6);
        rig.Vm.StopRecordingCommand.Execute(null);
        await rig.Recorder.StopAsync();
        var (items, background) = await ListAsyncKeepingBackground(rig);
        var item = Assert.Single(items);
        Assert.Equal(item.Summary.Samples.ToString("N0", System.Globalization.CultureInfo.CurrentCulture), item.Samples);
        var folder = Path.Combine(Path.GetTempPath(), "euclens-vm-export-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            rig.Desktop.ExportPath = Path.Combine(folder, "listed.csv");
            await item.ExportCsvCommand.ExecuteAsync();
            var lines = File.ReadAllLines(rig.Desktop.ExportPath);
            Assert.StartsWith("timestamp,data_source,is_demo", lines[0]);
            Assert.Equal(item.Summary.Samples, lines.Length - 1);
            rig.Desktop.ExportPath = Path.Combine(folder, "listed.json");
            await item.ExportJsonCommand.ExecuteAsync();
            using var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(rig.Desktop.ExportPath));
            Assert.Equal((int)item.Summary.Samples, json.RootElement.GetArrayLength());
            item.OpenFolderCommand.Execute(null); item.OpenFileCommand.Execute(null);
            Assert.Equal(2, rig.Desktop.Opened.Count);
            Assert.Contains("Exit from the tray icon menu", background.TrayExplanation);
        }
        finally { Directory.Delete(folder, true); }
    }

    private static async Task<(List<RecordingItem> Items, BackgroundViewModel Background)> ListAsyncKeepingBackground(Rig rig)
    {
        var folder = Path.Combine(Path.GetTempPath(), "euclens-vm-settings-" + Guid.NewGuid().ToString("N"));
        var store = new BackgroundSettingsStore(Path.Combine(folder, "background.json"));
        await store.SaveAsync(BackgroundSettings.Defaults with { RecordingDirectory = Dir });
        var background = new BackgroundViewModel(rig.Monitoring, rig.Recorder, rig.Desktop, store, rig.Storage);
        await background.LoadAsync();
        await background.RefreshRecordingsAsync();
        Directory.Delete(folder, true);
        return (background.Recordings.ToList(), background);
    }

    // ---------- settings ----------

    [Fact]
    public async Task SettingsAreValidatedProbedSavedAndAppliedToNotifications()
    {
        var folder = Path.Combine(Path.GetTempPath(), "euclens-vm-bg-" + Guid.NewGuid().ToString("N"));
        try
        {
            var recorder = new SessionRecorder();
            var monitoring = new MonitoringService(new MonitoringCoordinator(), recorder);
            var store = new BackgroundSettingsStore(Path.Combine(folder, "background.json"));
            var vm = new BackgroundViewModel(monitoring, recorder, new TestDesktop(), store);
            var changed = new List<BackgroundSettings>(); vm.SettingsChanged += changed.Add;
            Assert.False(vm.MinimizeToTray); Assert.False(vm.CloseToTray); Assert.False(vm.Notifications); Assert.True(vm.MonitorOnStart); Assert.False(vm.RecordOnStart);
            Assert.False(monitoring.Notifications.Enabled);

            vm.RetentionQuotaMiB = "lots"; await vm.SaveCommand.ExecuteAsync();
            Assert.True(vm.SettingsInvalid); Assert.Contains("whole number", vm.SettingsMessage);
            vm.RetentionQuotaMiB = "8"; await vm.SaveCommand.ExecuteAsync();
            Assert.True(vm.SettingsInvalid); Assert.Contains("between 64", vm.SettingsMessage);
            vm.RetentionQuotaMiB = "2048";
            var notAFolder = Path.Combine(folder, "file.txt"); Directory.CreateDirectory(folder); await File.WriteAllTextAsync(notAFolder, "x");
            vm.RecordingDirectory = Path.Combine(notAFolder, "recordings");               // a path below a file: cannot be created
            await vm.SaveCommand.ExecuteAsync();
            Assert.True(vm.SettingsInvalid); Assert.Contains("cannot be used", vm.SettingsMessage);
            Assert.Empty(changed); Assert.False(File.Exists(Path.Combine(folder, "background.json")));

            vm.RecordingDirectory = Path.Combine(folder, "recordings");
            vm.MinimizeToTray = true; vm.CloseToTray = true; vm.Notifications = true;
            await vm.SaveCommand.ExecuteAsync();
            Assert.False(vm.SettingsInvalid, vm.SettingsMessage);
            Assert.Contains("tray icon menu to exit", vm.SettingsMessage);
            Assert.True(Directory.Exists(Path.Combine(folder, "recordings")));
            Assert.True(monitoring.Notifications.Enabled);
            var saved = Assert.Single(changed);
            Assert.True(saved.MinimizeToTray && saved.CloseToTray && saved.Notifications && saved.RetentionQuotaMiB == 2048);

            // A restart: a new view model loads the same settings.
            var second = new BackgroundViewModel(new MonitoringService(new MonitoringCoordinator(), new SessionRecorder()), new SessionRecorder(), new TestDesktop(), store);
            await second.LoadAsync();
            Assert.Equal(saved, second.Current);
            Assert.True(second.CloseToTray);
            await second.MarkTrayHintShownAsync();
            var third = new BackgroundViewModel(new MonitoringService(new MonitoringCoordinator(), new SessionRecorder()), new SessionRecorder(), new TestDesktop(), store);
            await third.LoadAsync();
            Assert.True(third.Current.TrayHintShown);
            Assert.True(third.Current.MinimizeToTray);
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }

    [Fact]
    public async Task ADamagedSettingsFileKeepsDefaultsAndExplainsWhy()
    {
        var folder = Path.Combine(Path.GetTempPath(), "euclens-vm-bad-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(folder, "background.json"), "{ not json");
            var recorder = new SessionRecorder();
            var vm = new BackgroundViewModel(new MonitoringService(new MonitoringCoordinator(), recorder), recorder, new TestDesktop(), new BackgroundSettingsStore(Path.Combine(folder, "background.json")));
            await vm.LoadAsync();
            Assert.Equal(BackgroundSettings.Defaults, vm.Current);
            Assert.True(vm.SettingsInvalid);
            Assert.Contains("defaults are in effect", vm.SettingsMessage);
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public void RecordingsFolderCanBeOpenedAndTheDurabilityNoteIsShown()
    {
        var recorder = new SessionRecorder();
        var desktop = new TestDesktop();
        var vm = new BackgroundViewModel(new MonitoringService(new MonitoringCoordinator(), recorder), recorder, desktop);
        Assert.Contains("non-persistent", vm.DurabilityNote);
        vm.OpenRecordingsFolderCommand.Execute(null);
        Assert.Equal(BackgroundSettings.Defaults.EffectiveDirectory, Assert.Single(desktop.Opened));
        desktop.Folder = Path.Combine(Path.GetTempPath(), "chosen");
        vm.BrowseFolderCommand.Execute(null);
        Assert.Equal(desktop.Folder, vm.RecordingDirectory);
        vm.UseDefaultFolderCommand.Execute(null);
        Assert.Equal("", vm.RecordingDirectory);
        Assert.Empty(vm.Recordings);
    }
}
