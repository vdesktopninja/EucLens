using EucLens.App;
using EucLens.App.ViewModels;
using EucLens.Core;
using EucLens.Diagnostics;
using EucLens.Providers;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EucLens.Tests;

/// <summary>The one-click action behind the tray menu and the Session Recorder page: availability, confirmation, failure reporting, and no interruption.</summary>
public sealed class IncidentActionTests
{
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
        public CancellationTokenSource Cancel = new();
        public Task Loop = Task.CompletedTask;

        public static async Task<Rig> CreateAsync(bool persistent = true)
        {
            var rig = new Rig();
            if (persistent)
            {
                rig.Writer = new RecordingWriter(() => new RecordingOptions { Directory = IncidentTests.Dir, SegmentBytes = 256 << 10, MinimumFreeBytes = 0, FlushInterval = TimeSpan.FromMilliseconds(50), CompressClosedParts = false }, rig.Storage, rig.Clock);
                rig.Recorder = new SessionRecorder(rig.Writer, rig.Clock);
            }
            else rig.Recorder = new SessionRecorder();
            rig.Monitoring = new MonitoringService(new MonitoringCoordinator(interval: TimeSpan.FromMilliseconds(20)), rig.Recorder);
            rig.Vm = new MainViewModel(rig.Provider, new DiagnosticIntelligenceEngine(DiagnosticRuleSet.Default()), rig.Recorder, new Desktop(), NullLogger<MainViewModel>.Instance,
                monitoring: rig.Monitoring, background: new BackgroundViewModel(rig.Monitoring, rig.Recorder, new Desktop(), storage: rig.Storage));
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
    public async Task TheActionIsAvailableOnlyWhileARecordingIsSavedToDiskAndSaysWhyOtherwise()
    {
        await using var rig = await Rig.CreateAsync();
        var background = rig.Vm.Background;
        Assert.False(rig.Monitoring.CanMarkIncident);
        Assert.False(background.MarkIncidentCommand.CanExecute(null));
        Assert.Contains("Start a recording", background.IncidentHint, StringComparison.Ordinal);

        rig.Vm.StartRecordingCommand.Execute(null);                // no manual refresh: the real commands update everything
        Assert.True(rig.Monitoring.CanMarkIncident);
        Assert.True(background.MarkIncidentCommand.CanExecute(null));
        Assert.True(background.CanMarkIncident);
        Assert.Equal("", background.IncidentHint);

        rig.Vm.StopRecordingCommand.Execute(null);
        Assert.False(rig.Monitoring.CanMarkIncident);
        Assert.False(background.CanMarkIncident);
        Assert.NotEqual("", background.IncidentHint);

        await using var memory = await Rig.CreateAsync(persistent: false);
        memory.Vm.StartRecordingCommand.Execute(null);
        Assert.False(memory.Monitoring.CanMarkIncident);
        Assert.Contains("saved to disk", memory.Vm.Background.IncidentHint, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MarkingSavesConfirmsAndNeverInterruptsMonitoringOrRecording()
    {
        await using var rig = await Rig.CreateAsync();
        rig.Run();
        await WaitUntil(() => rig.Vm.NetworkReadout != "Unavailable");
        rig.Vm.StartRecordingCommand.Execute(null);
        await WaitUntil(() => rig.Recorder.SampleCount >= 3);
        var results = new List<IncidentResult>(); rig.Vm.Background.IncidentCompleted += results.Add;
        var completed = new List<IncidentResult>(); rig.Monitoring.IncidentCompleted += completed.Add;
        var ticksBefore = rig.Monitoring.Coordinator.Ticks; var samplesBefore = rig.Recorder.SampleCount;

        var result = await rig.Vm.Background.MarkIncidentAsync();

        Assert.True(result.Saved);
        Assert.Equal(result.Marker, rig.Monitoring.LastIncident);
        Assert.Single(completed);
        await WaitUntil(() => results.Count == 1);                  // the view model reports on its UI context
        Assert.Single(results);
        Assert.Contains("saved to the recording", rig.Vm.Background.LastIncidentText, StringComparison.Ordinal);
        Assert.Contains($"no. {result.Marker!.Sequence}", rig.Vm.Background.LastIncidentText, StringComparison.Ordinal);
        Assert.False(rig.Vm.Background.LastIncidentFailed);
        Assert.Equal(result.Message, rig.Vm.Status);                // the status bar confirms it too
        // Monitoring and recording carry on exactly as before.
        await WaitUntil(() => rig.Recorder.SampleCount >= samplesBefore + 3);
        Assert.True(rig.Recorder.IsRecording);
        Assert.Equal(MonitoringState.Running, rig.Monitoring.Coordinator.State);
        Assert.True(rig.Monitoring.Coordinator.Ticks > ticksBefore);
        Assert.Equal(TrayState.Recording, TrayStateResolver.Resolve(rig.Monitoring.Status).State);
        await rig.Recorder.StopAsync();
        var mark = IncidentTests.Lines(rig.Storage).Single(l => l.Type == "mark" && l.Root.GetProperty("kind").GetString() == "UserIncident").Root;
        Assert.Equal(result.Marker.Id.ToString("D"), mark.GetProperty("id").GetString());
    }

    [Fact]
    public async Task AFailureIsShownAsAFailureAndLeavesNoLastIncident()
    {
        await using var rig = await Rig.CreateAsync();
        rig.Vm.StartRecordingCommand.Execute(null);
        rig.Recorder.Add(TestSamples.Make(0));
        await rig.Writer!.FlushAsync();
        rig.Storage.WriteFault = new UnauthorizedAccessException("Access denied");

        var result = await rig.Vm.Background.MarkIncidentAsync();
        await WaitUntil(() => rig.Vm.Status == result.Message);

        Assert.Equal(IncidentOutcome.StorageFailed, result.Outcome);
        Assert.Null(rig.Monitoring.LastIncident);
        Assert.True(rig.Vm.Background.LastIncidentFailed);
        Assert.StartsWith("Incident not saved:", rig.Vm.Background.LastIncidentText, StringComparison.Ordinal);
        Assert.DoesNotContain("saved to the recording", rig.Vm.Background.LastIncidentText, StringComparison.Ordinal);
        Assert.Equal(result.Message, rig.Vm.Status);
        // The data-loss state is surfaced as well: the recording reports its storage fault.
        Assert.Equal(RecordingSinkState.Faulted, rig.Recorder.SinkStatus!.State);
        rig.Monitoring.Refresh();
        Assert.NotNull(rig.Monitoring.Status.StorageProblem);
    }

    [Fact]
    public async Task MarkingWithoutARecordingExplainsAndWritesNothing()
    {
        await using var rig = await Rig.CreateAsync();
        var result = await rig.Vm.Background.MarkIncidentAsync();
        await WaitUntil(() => rig.Vm.Status == result.Message);
        Assert.Equal(IncidentOutcome.NoRecording, result.Outcome);
        Assert.True(rig.Vm.Background.LastIncidentFailed);
        Assert.Contains("Start a recording", rig.Vm.Status, StringComparison.Ordinal);
        Assert.Empty(rig.Storage.Names(IncidentTests.Dir));
        Assert.Null(rig.Monitoring.LastIncident);
    }

    [Fact]
    public async Task ThePausedStateAllowsMarkingAndTheMarkerIsStillWritten()
    {
        await using var rig = await Rig.CreateAsync();
        rig.Vm.StartRecordingCommand.Execute(null);
        rig.Monitoring.PauseMonitoring();
        Assert.True(rig.Monitoring.CanMarkIncident);                // the user's report is valid while collection is paused
        Assert.True((await rig.Vm.Background.MarkIncidentAsync()).Saved);
        await rig.Recorder.StopAsync();
        var kinds = IncidentTests.Lines(rig.Storage).Where(l => l.Type == "mark").Select(l => l.Root.GetProperty("kind").GetString() ?? "").ToArray();
        Assert.Equal(new[] { "Paused", "UserIncident" }, kinds);
    }
}
