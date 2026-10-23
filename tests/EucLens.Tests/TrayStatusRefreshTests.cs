using EucLens.App;
using EucLens.App.ViewModels;
using EucLens.Core;
using EucLens.Diagnostics;
using EucLens.Providers;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EucLens.Tests;

/// <summary>
/// The monitoring status behind the tray icon and the tray menu follows recording transitions at once. None of these tests runs the sampling loop
/// (no tick can refresh the status) and none calls <see cref="MonitoringService.Refresh"/> itself: the real view-model commands must do it.
/// The tray menu text is a pure function of <c>Status.IsRecording</c> and the coordinator state (TrayIconService.UpdateMenu); the Windows smoke test
/// checks the real menu items.
/// </summary>
public sealed class TrayStatusRefreshTests
{
    private static readonly string Dir = Path.Combine(Path.GetTempPath(), "euclens-tray-refresh");

    private sealed class Desktop : IDesktopServices
    {
        public string? SelectExportPath(bool json) => null;
        public bool ConfirmNewRecording() => true;
        public void ApplyTheme(bool dark) { }
        public Task SavePreferencesAsync(Preferences preferences) => Task.CompletedTask;
        public Task<Preferences?> LoadPreferencesAsync() => Task.FromResult<Preferences?>(null);
        public string? SelectFolder(string? initialDirectory) => null;
        public bool OpenPath(string path, bool selectFile = false) => true;
    }

    private sealed class Rig : IAsyncDisposable
    {
        public MockMetricsProvider Provider = new();
        public MemoryStorage Storage = new();
        public FakeClock Clock = new(TestSamples.T0);
        public SessionRecorder Recorder = null!;
        public RecordingWriter? Writer;
        public MonitoringService Monitoring = null!;
        public MainViewModel Vm = null!;
        public List<MonitoringStatus> Changes { get; } = [];
        public List<NotificationRequest> Notes { get; } = [];

        public static async Task<Rig> CreateAsync(bool persistent = true, long quota = 512L << 20)
        {
            var rig = new Rig();
            if (persistent)
            {
                rig.Writer = new RecordingWriter(() => new RecordingOptions
                {
                    Directory = Dir, QuotaBytes = quota, SegmentBytes = 256 << 10, MinimumFreeBytes = 0, FlushInterval = TimeSpan.FromMilliseconds(50), CompressClosedParts = false,
                    RetryInterval = TimeSpan.FromSeconds(5), GiveUpAfter = TimeSpan.FromSeconds(60)
                }, rig.Storage, rig.Clock);
                rig.Recorder = new SessionRecorder(rig.Writer, rig.Clock);
            }
            else rig.Recorder = new SessionRecorder();
            rig.Monitoring = new MonitoringService(new MonitoringCoordinator(interval: TimeSpan.FromMilliseconds(20)), rig.Recorder);
            rig.Vm = new MainViewModel(rig.Provider, new DiagnosticIntelligenceEngine(DiagnosticRuleSet.Default()), rig.Recorder, new Desktop(), NullLogger<MainViewModel>.Instance,
                monitoring: rig.Monitoring, background: new BackgroundViewModel(rig.Monitoring, rig.Recorder, new Desktop(), storage: rig.Storage));
            await rig.Vm.InitializeAsync(default);
            rig.Monitoring.Notifications.Enabled = true; // after initialization, which applies the stored (default: off) settings
            rig.Monitoring.StatusChanged += s => { lock (rig.Changes) rig.Changes.Add(s); };
            rig.Monitoring.NotificationRaised += rig.Notes.Add;
            Assert.Equal(0, rig.Monitoring.Coordinator.Ticks); // the sampling loop is not running: only commands can change the status
            return rig;
        }

        public TrayAppearance Tray => TrayStateResolver.Resolve(Monitoring.Status);
        public async ValueTask DisposeAsync() { await Recorder.StopAsync(); await Provider.DisposeAsync(); }
    }

    [Fact]
    public async Task StartRecordingChangesTheStatusAndTheTrayStateAtOnce()
    {
        await using var rig = await Rig.CreateAsync();
        Assert.Equal(TrayState.Monitoring, rig.Tray.State);
        Assert.False(rig.Monitoring.Status.IsRecording);

        rig.Vm.StartRecordingCommand.Execute(null);

        Assert.True(rig.Recorder.IsRecording);
        Assert.True(rig.Monitoring.Status.IsRecording);
        Assert.Equal(TrayState.Recording, rig.Tray.State);
        Assert.Equal("recording", rig.Tray.IconKey);
        // The inputs of the tray menu: it offers Stop Recording, and Pause Monitoring stays available.
        Assert.True(rig.Monitoring.Status.IsRecording && rig.Monitoring.Coordinator.State == MonitoringState.Running);
        Assert.Single(rig.Changes);
        Assert.Equal(0, rig.Monitoring.Coordinator.Ticks);
    }

    [Fact]
    public async Task StopRecordingRestoresTheMonitoringStateAtOnceForPersistedAndInMemoryRecordings()
    {
        foreach (var persistent in new[] { true, false })
        {
            await using var rig = await Rig.CreateAsync(persistent);
            rig.Vm.StartRecordingCommand.Execute(null);
            Assert.Equal(TrayState.Recording, rig.Tray.State);

            rig.Vm.StopRecordingCommand.Execute(null);

            Assert.False(rig.Recorder.IsRecording);
            Assert.False(rig.Monitoring.Status.IsRecording);
            Assert.Equal(TrayState.Monitoring, rig.Tray.State);
            Assert.Equal(2, rig.Changes.Count);                         // start, stop
            await rig.Recorder.StopAsync();                             // the sink's own end notification arrives afterwards ...
            Assert.Equal(2, rig.Changes.Count);                         // ... and changes nothing: no duplicate status
            Assert.Empty(rig.Notes);
        }
    }

    [Fact]
    public async Task ThePausedStateStillWinsAndStartIsRefusedWhilePaused()
    {
        await using var rig = await Rig.CreateAsync();
        rig.Vm.StartRecordingCommand.Execute(null);
        rig.Monitoring.PauseMonitoring();
        Assert.Equal(TrayState.Paused, rig.Tray.State);                 // an open recording never shows as Recording while paused
        rig.Vm.StopRecordingCommand.Execute(null);
        Assert.Equal(TrayState.Paused, rig.Tray.State);
        rig.Vm.StartRecordingCommand.Execute(null);                     // refused: recording needs running monitoring
        Assert.False(rig.Recorder.IsRecording);
        Assert.False(rig.Monitoring.Status.IsRecording);
        rig.Monitoring.ResumeMonitoring();
        Assert.Equal(TrayState.Monitoring, rig.Tray.State);
    }

    [Fact]
    public async Task AQuotaStopUpdatesTheTrayAtTheMomentTheRecordingEnds()
    {
        await using var rig = await Rig.CreateAsync(quota: 120_000);
        var atEnd = new TaskCompletionSource<MonitoringStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        // Registered after the monitoring service's own handler, so it sees the status as of the moment the recording ended.
        rig.Recorder.Ended += _ => atEnd.TrySetResult(rig.Monitoring.Status);
        rig.Vm.StartRecordingCommand.Execute(null);
        for (var i = 0; i < 400 && rig.Recorder.IsRecording; i++) { rig.Recorder.Add(TestSamples.Make(i)); if (i % 20 == 19) await rig.Writer!.FlushAsyncSafe(); }

        var status = await atEnd.Task.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.False(status.IsRecording);
        Assert.Contains("quota", status.StorageProblem, StringComparison.Ordinal);
        Assert.Equal(TrayState.Warning, TrayStateResolver.Resolve(status).State);
        Assert.Equal(0, rig.Monitoring.Coordinator.Ticks);              // not the sampling loop: the end notification did it
        Assert.Contains(rig.Notes, n => n.Kind == NotificationKind.RecordingStopped);
    }

    [Fact]
    public async Task AStorageFailureThatEndsTheRecordingUpdatesTheTrayAtTheMomentItEnds()
    {
        await using var rig = await Rig.CreateAsync();
        var atEnd = new TaskCompletionSource<MonitoringStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Recorder.Ended += _ => atEnd.TrySetResult(rig.Monitoring.Status);
        rig.Vm.StartRecordingCommand.Execute(null);
        rig.Recorder.Add(TestSamples.Make(0));
        await rig.Writer!.FlushAsync();
        rig.Storage.WriteFault = new UnauthorizedAccessException("Access denied");
        for (var i = 1; i <= 90 && rig.Recorder.IsRecording; i++) { rig.Clock.Advance(TimeSpan.FromSeconds(1)); rig.Recorder.Add(TestSamples.Make(i)); await rig.Writer.FlushAsyncSafe(); }

        var status = await atEnd.Task.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.False(status.IsRecording);
        Assert.StartsWith("storage", status.StorageProblem, StringComparison.Ordinal); // the live storage fault text (or the final reason) is shown
        Assert.Equal(TrayState.Warning, TrayStateResolver.Resolve(status).State);
        Assert.Equal(0, rig.Monitoring.Coordinator.Ticks);
        Assert.Contains(rig.Notes, n => n.Kind == NotificationKind.RecordingStopped);
        Assert.Equal(1, rig.Notes.Count(n => n.Kind == NotificationKind.RecordingStopped)); // announced once
    }

    [Fact]
    public async Task RepeatedTransitionsRaiseOneStatusChangePerTransitionWithoutRecursionOrDuplicates()
    {
        await using var rig = await Rig.CreateAsync();
        var depth = 0; var maxDepth = 0; var delivered = 0;
        rig.Monitoring.StatusChanged += _ => { maxDepth = Math.Max(maxDepth, Interlocked.Increment(ref depth)); delivered++; Interlocked.Decrement(ref depth); };
        var recordingStateRaises = new List<int>();
        var count = 0;
        rig.Vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(MainViewModel.RecordingState)) count++; };

        for (var cycle = 0; cycle < 50; cycle++)
        {
            count = 0;
            rig.Vm.StartRecordingCommand.Execute(null);
            Assert.True(rig.Monitoring.Status.IsRecording);
            recordingStateRaises.Add(count);
            count = 0;
            rig.Vm.StopRecordingCommand.Execute(null);
            Assert.False(rig.Monitoring.Status.IsRecording);
            recordingStateRaises.Add(count);
        }
        await rig.Recorder.StopAsync();

        Assert.Equal(100, delivered);                                   // exactly one per transition
        Assert.Equal(100, rig.Changes.Count);
        Assert.Equal(1, maxDepth);                                      // no handler ran inside another one
        Assert.All(recordingStateRaises, n => Assert.InRange(n, 1, 2)); // the view model refreshes a bounded number of times per transition ...
        Assert.Single(recordingStateRaises.Distinct());                 // ... and the same number every time: no growth
        Assert.Empty(rig.Notes);
    }

    [Fact]
    public async Task ConcurrentRefreshesAndReadsNeverShowAnImpossibleStatusAndTheFinalStatusIsTheRealOne()
    {
        await using var rig = await Rig.CreateAsync();
        using var stop = new CancellationTokenSource();
        var impossible = 0; long reads = 0;
        MonitoringStatus? last = null; var outOfOrder = 0;
        rig.Monitoring.StatusChanged += s => { Volatile.Write(ref last, s); };
        // Two workers only, and they back off (SpinWait sleeps now and then): a busy loop per core starves the writer thread and the other
        // tests that run in parallel on a two-core CI runner.
        var workers = Enumerable.Range(0, 2).Select(w => Task.Run(() =>
        {
            var spin = new SpinWait();
            while (!stop.IsCancellationRequested)
            {
                if (w == 0) rig.Monitoring.Refresh();
                var s = rig.Monitoring.Status;
                Interlocked.Increment(ref reads);
                if (s.IsRecording && s.Monitoring != MonitoringState.Running) Interlocked.Increment(ref impossible);
                if (s.ActiveProblems < 0 || s.CriticalProblems > s.ActiveProblems) Interlocked.Increment(ref outOfOrder);
                spin.SpinOnce();
            }
        })).ToArray();

        for (var i = 0; i < 100; i++)
        {
            rig.Vm.StartRecordingCommand.Execute(null);
            rig.Vm.StopRecordingCommand.Execute(null);
        }
        rig.Vm.StartRecordingCommand.Execute(null);
        await Task.Delay(50);
        stop.Cancel();
        await Task.WhenAll(workers);

        Assert.Equal(0, impossible);
        Assert.Equal(0, outOfOrder);
        Assert.True(reads > 0);
        // After the last command the cached status is exactly the real state, whatever the concurrent refreshes did in between.
        Assert.True(rig.Recorder.IsRecording);
        Assert.True(rig.Monitoring.Status.IsRecording);
        Assert.Equal(TrayState.Recording, rig.Tray.State);
        rig.Vm.StopRecordingCommand.Execute(null);
        Assert.False(rig.Monitoring.Status.IsRecording);
        Assert.Equal(TrayState.Monitoring, rig.Tray.State);
    }
}
