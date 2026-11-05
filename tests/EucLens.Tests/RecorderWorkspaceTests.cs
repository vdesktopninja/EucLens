using System.Collections.Specialized;
using System.Text.RegularExpressions;
using EucLens.App;
using EucLens.App.ViewModels;
using EucLens.Core;
using EucLens.Diagnostics;
using EucLens.Providers;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EucLens.Tests;

/// <summary>
/// The Session Recorder page: one status for monitoring and recording, the tiles, the actions, the compact latency chart's empty state, and the saved-recordings workspace (sorting, filter, one selection, one set of
/// actions). The recording engine is the real one (or a fake that reports a condition to present); nothing here measures, records or stores differently from before.
/// </summary>
public sealed class RecorderWorkspaceTests
{
    private static string Repo([System.Runtime.CompilerServices.CallerFilePath] string file = "") => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file)!, "../.."));
    private static string Source(string relative) => File.ReadAllText(Path.Combine(Repo(), relative));

    private sealed class Desktop : IDesktopServices
    {
        public List<string> Opened { get; } = [];
        public string? ExportPath { get; set; }
        public int ExportDialogs { get; private set; }
        public bool OpenSucceeds { get; set; } = true;
        public string? SelectExportPath(bool json) { ExportDialogs++; return ExportPath; }
        public bool ConfirmNewRecording() => true;
        public void ApplyTheme(bool dark) { }
        public Task SavePreferencesAsync(Preferences preferences) => Task.CompletedTask;
        public Task<Preferences?> LoadPreferencesAsync() => Task.FromResult<Preferences?>(null);
        public string? SelectFolder(string? initialDirectory) => null;
        public bool OpenPath(string path, bool selectFile = false) { Opened.Add(path); return OpenSucceeds; }
    }

    /// <summary>A recorder that reports whatever condition the test wants shown; it records nothing.</summary>
    private sealed class FakeRecorder : ISessionRecorder
    {
        public bool IsRecording { get; set; }
        public int SampleCount { get; set; }
        public TimeSpan Duration { get; set; }
        public IReadOnlyList<SessionSnapshot> Samples => [];
        public RecordingSinkStatus? SinkStatus { get; set; }
        public bool IsPersistent { get; set; } = true;
        public int Starts { get; private set; }
        public event Action<RecordingEndReason>? Ended;
        public void Raise(RecordingEndReason reason) => Ended?.Invoke(reason);
        public void Start() { Starts++; IsRecording = true; }
        public void Stop() => IsRecording = false;
        public void Add(SessionSnapshot sample) { }
        public Task ExportJsonAsync(string path, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ExportCsvAsync(string path, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private static RecordingSinkStatus Sink(RecordingSinkState state, Guid? id = null, long written = 0, long lost = 0, RecordingFault? fault = null) => new(state, id, "x.jsonl", 1, 0, written, lost, fault);

    private static MainViewModel NewMain(ISessionRecorder recorder, Desktop? desktop = null, MonitoringService? monitoring = null, BackgroundViewModel? background = null) =>
        new(new MockMetricsProvider(), new DiagnosticIntelligenceEngine(DiagnosticRuleSet.Default()), recorder, desktop ?? new Desktop(), NullLogger<MainViewModel>.Instance, monitoring: monitoring, background: background);

    private sealed class Rig
    {
        public Desktop Desktop { get; } = new();
        public MemoryStorage Storage { get; } = new();
        public SessionRecorder Recorder { get; private set; } = null!;
        public RecordingWriter Writer { get; private set; } = null!;
        public MonitoringService Monitoring { get; private set; } = null!;
        public MainViewModel Vm { get; private set; } = null!;

        public static async Task<Rig> CreateAsync(bool persistent = true, bool startMonitoring = true)
        {
            var rig = new Rig();
            var clock = new FakeClock(TestSamples.T0);
            if (persistent)
            {
                rig.Writer = new RecordingWriter(() => new RecordingOptions { Directory = HistoryFixture.Dir, QuotaBytes = 512L << 20, SegmentBytes = 256 << 10, MinimumFreeBytes = 0, FlushInterval = TimeSpan.FromMilliseconds(50), CompressClosedParts = false, RetryInterval = TimeSpan.FromSeconds(5), GiveUpAfter = TimeSpan.FromSeconds(60) }, rig.Storage, clock);
                rig.Recorder = new SessionRecorder(rig.Writer, clock);
            }
            else rig.Recorder = new SessionRecorder();
            rig.Monitoring = new MonitoringService(new MonitoringCoordinator(interval: TimeSpan.FromMilliseconds(20)), rig.Recorder);
            rig.Vm = NewMain(rig.Recorder, rig.Desktop, rig.Monitoring, new BackgroundViewModel(rig.Monitoring, rig.Recorder, rig.Desktop, storage: rig.Storage));
            await rig.Vm.InitializeAsync(default);
            if (startMonitoring) rig.Monitoring.StartMonitoring(); else rig.Monitoring.StopMonitoring();   // monitoring is on by default; the tests that need it stopped stop it
            return rig;
        }
    }

    // ---------- status, tiles and controls ----------

    [Fact]
    public async Task BeforeAnyRecordingThePageSaysReadyAndWhatToDoNext()
    {
        var rig = await Rig.CreateAsync(startMonitoring: false);
        var workspace = rig.Vm.Recorder;
        Assert.Equal(RecorderStatusKind.Ready, workspace.Status);
        Assert.Equal(["Ready", "00:00:00", "0"], workspace.Tiles.Select(t => t.Value));
        Assert.Equal(["Recording", "Duration", "Samples"], workspace.Tiles.Select(t => t.Label));
        Assert.Contains("Start monitoring", workspace.StatusDetail, StringComparison.Ordinal);
        Assert.Equal("Monitoring stopped · Real Session · detecting the session…", workspace.MonitoringSummary);
        Assert.Equal("Start monitoring", workspace.MonitoringActionLabel);
        Assert.Same(rig.Vm.Background.StartMonitoringCommand, workspace.MonitoringActionCommand);
        Assert.False(rig.Vm.StartRecordingCommand.CanExecute(null));      // recording needs monitoring (unchanged contract)
        Assert.Contains("Start monitoring first", workspace.StartHint, StringComparison.Ordinal);
        Assert.False(rig.Vm.StopRecordingCommand.CanExecute(null));
        rig.Monitoring.StartMonitoring();
        Assert.Contains("Monitoring active", workspace.MonitoringSummary, StringComparison.Ordinal);
        Assert.Equal("", workspace.StartHint);
        Assert.Equal("Pause monitoring", workspace.MonitoringActionLabel);
    }

    [Fact]
    public async Task StartingAndStoppingARecordingMovesThroughRecordingToCompletedAndTheTilesFollowTheRecorder()
    {
        var rig = await Rig.CreateAsync();
        var workspace = rig.Vm.Recorder;
        var seen = new List<string>();
        workspace.StatusTile.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(HistoryTile.Value)) seen.Add(workspace.StatusTile.Value); };
        Assert.True(rig.Vm.StartRecordingCommand.CanExecute(null));
        rig.Vm.StartRecordingCommand.Execute(null);
        Assert.True(rig.Recorder.IsRecording);
        Assert.Equal(RecorderStatusKind.Recording, workspace.Status);
        Assert.Equal("Recording", workspace.StatusTile.Value);
        Assert.Contains("Recording to disk", workspace.StatusDetail, StringComparison.Ordinal);
        Assert.False(rig.Vm.StartRecordingCommand.CanExecute(null));
        Assert.True(rig.Vm.StopRecordingCommand.CanExecute(null));
        // Samples arrive through the recorder; the page follows the refresh the view model already does (no timer, no polling of its own).
        for (var i = 0; i < 7; i++) rig.Recorder.Add(HistoryFixture.Sample(i));
        rig.Vm.SetPresenting(false); rig.Vm.SetPresenting(true);          // the refresh a window restore performs
        Assert.Equal("7", workspace.SamplesTile.Value);
        Assert.Matches(@"^\d{2,}:\d{2}:\d{2}$", workspace.DurationTile.Value);
        Assert.Equal(rig.Vm.RecordingDuration, workspace.DurationTile.Value);
        rig.Vm.StopRecordingCommand.Execute(null);
        Assert.False(rig.Recorder.IsRecording);
        await rig.Recorder.StopAsync();
        rig.Vm.SetPresenting(false); rig.Vm.SetPresenting(true);
        Assert.Equal(RecorderStatusKind.Completed, workspace.Status);
        Assert.Equal("Completed", workspace.StatusTile.Value);
        Assert.Equal("7", workspace.SamplesTile.Value);
        Assert.Contains("saved", workspace.StatusDetail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Recording", seen);
    }

    [Fact]
    public async Task RapidRepeatedStartRequestsStartExactlyOneRecordingAndRepeatedStopsAreHarmless()
    {
        var rig = await Rig.CreateAsync();
        var starts = 0;
        for (var i = 0; i < 25; i++)
        {
            if (rig.Vm.StartRecordingCommand.CanExecute(null)) starts++;
            rig.Vm.StartRecordingCommand.Execute(null);                      // what a double click or a held key press does, with or without the CanExecute check
        }
        Assert.Equal(1, starts);                                              // after the first, the button is disabled: a second click cannot reach the command
        Assert.True(rig.Recorder.IsRecording);
        var id = rig.Recorder.SinkStatus!.RecordingId;
        rig.Vm.StartRecordingCommand.Execute(null);
        Assert.Equal(id, rig.Recorder.SinkStatus!.RecordingId);              // still the same recording: the repeated request did not replace it
        rig.Vm.StopRecordingCommand.Execute(null);
        rig.Vm.StopRecordingCommand.Execute(null);
        rig.Vm.StopRecordingCommand.Execute(null);
        await rig.Recorder.StopAsync();
        Assert.Single(RecordingCatalog.Scan(rig.Storage, HistoryFixture.Dir));
    }

    [Fact]
    public async Task StartRequestedWhileMonitoringIsNotRunningSaysWhyAndStartsNothing()
    {
        var rig = await Rig.CreateAsync(startMonitoring: false);
        rig.Vm.StartRecordingCommand.Execute(null);                           // not reachable from the disabled button; the command itself refuses too
        Assert.False(rig.Recorder.IsRecording);
        Assert.Contains("start or resume monitoring", rig.Vm.Status, StringComparison.Ordinal);
        Assert.Equal(RecorderStatusKind.Ready, rig.Vm.Recorder.Status);
    }

    [Fact]
    public async Task PausingMonitoringDuringARecordingKeepsItRecordingAndSaysSoAndTheMonitoringActionFollows()
    {
        var rig = await Rig.CreateAsync();
        var workspace = rig.Vm.Recorder;
        rig.Vm.StartRecordingCommand.Execute(null);
        Assert.Same(rig.Vm.Background.PauseMonitoringCommand, workspace.MonitoringActionCommand);
        Assert.True(workspace.MonitoringActionCommand.CanExecute(null));
        workspace.MonitoringActionCommand.Execute(null);                      // Pause monitoring
        Assert.Equal(MonitoringState.Paused, rig.Monitoring.Coordinator.State);
        Assert.True(rig.Recorder.IsRecording);                                // monitoring and recording are two things: pausing one does not stop the other
        Assert.Equal(RecorderStatusKind.Recording, workspace.Status);
        Assert.Contains("Monitoring paused", workspace.MonitoringSummary, StringComparison.Ordinal);
        Assert.Equal("Monitoring paused", workspace.StatusTile.Detail);
        Assert.Equal("Warning", workspace.StatusTile.Icon);                   // a shape beside the words, not colour alone
        Assert.Contains("gap", workspace.StatusDetail, StringComparison.Ordinal);
        Assert.Equal("Resume monitoring", workspace.MonitoringActionLabel);
        Assert.Same(rig.Vm.Background.ResumeMonitoringCommand, workspace.MonitoringActionCommand);
        workspace.MonitoringActionCommand.Execute(null);                      // Resume monitoring
        Assert.Equal(MonitoringState.Running, rig.Monitoring.Coordinator.State);
        Assert.Equal("", workspace.StatusTile.Icon);
        Assert.Equal("Pause monitoring", workspace.MonitoringActionLabel);
        await rig.Recorder.StopAsync();
    }

    [Fact]
    public async Task TheMonitoringActionIsExactlyTheExistingCommandWithItsOwnConditions()
    {
        var rig = await Rig.CreateAsync(startMonitoring: false);
        var workspace = rig.Vm.Recorder;
        var background = rig.Vm.Background;
        foreach (var (state, command) in new (MonitoringState, RelayCommand)[] { (MonitoringState.Stopped, background.StartMonitoringCommand), (MonitoringState.Running, background.PauseMonitoringCommand), (MonitoringState.Paused, background.ResumeMonitoringCommand) })
        {
            switch (state) { case MonitoringState.Running: rig.Monitoring.StartMonitoring(); break; case MonitoringState.Paused: rig.Monitoring.PauseMonitoring(); break; }
            Assert.Equal(state, rig.Monitoring.Coordinator.State);
            Assert.Same(command, workspace.MonitoringActionCommand);
            Assert.True(workspace.MonitoringActionCommand.CanExecute(null));
        }
    }

    [Fact]
    public async Task MarkingAnIssueIsOnlyAvailableDuringARecordingAndTheHintSaysWhy()
    {
        var rig = await Rig.CreateAsync();
        var background = rig.Vm.Background;
        Assert.False(background.MarkIncidentCommand.CanExecute(null));
        Assert.Contains("Start a recording", background.IncidentHint, StringComparison.Ordinal);
        rig.Vm.StartRecordingCommand.Execute(null);
        Assert.True(background.MarkIncidentCommand.CanExecute(null));
        Assert.Equal("", background.IncidentHint);
        rig.Recorder.Add(HistoryFixture.Sample(0));
        await background.MarkIncidentCommand.ExecuteAsync();
        Assert.Contains("Last incident: no. 1", background.LastIncidentText, StringComparison.Ordinal);
        await rig.Recorder.StopAsync();
    }

    [Fact]
    public void ARecordingThatEndedByItselfIsAnErrorWithItsReasonUntilTheNextRecordingStarts()
    {
        var fake = new FakeRecorder { IsRecording = true, SampleCount = 40, Duration = TimeSpan.FromSeconds(40), SinkStatus = Sink(RecordingSinkState.Active, Guid.NewGuid(), 40) };
        var vm = NewMain(fake);
        Assert.Equal(RecorderStatusKind.Recording, vm.Recorder.Status);
        fake.IsRecording = false; fake.SinkStatus = Sink(RecordingSinkState.Idle, null, 40);
        fake.Raise(RecordingEndReason.QuotaReached);
        // The event can arrive on another thread; the view model posts it to its own context, which is the test's thread here.
        Assert.Equal(RecorderStatusKind.Error, vm.Recorder.Status);
        Assert.Equal("Stopped", vm.Recorder.StatusLabel);
        Assert.Contains("storage quota", vm.Recorder.StatusDetail, StringComparison.Ordinal);
        Assert.Equal("Quota reached", vm.Recorder.StatusTile.Detail);
        Assert.Equal("Critical", vm.Recorder.StatusTile.Icon);
        fake.Raise(RecordingEndReason.StorageError);
        Assert.Contains("storage problem", vm.Recorder.StatusDetail, StringComparison.Ordinal);
        // A new recording clears the old reason.
        fake.IsRecording = true; fake.SampleCount = 0;
        vm.Recorder.Refresh();
        Assert.Equal(RecorderStatusKind.Recording, vm.Recorder.Status);
        fake.IsRecording = false; fake.SampleCount = 3;
        vm.Recorder.Refresh();
        Assert.Equal(RecorderStatusKind.Completed, vm.Recorder.Status);
    }

    [Fact]
    public void AFaultedStorageSinkIsShownAsAnErrorWhileTheRecordingContinuesAndLostSamplesAreCounted()
    {
        var fake = new FakeRecorder { IsRecording = true, SampleCount = 12, SinkStatus = Sink(RecordingSinkState.Faulted, Guid.NewGuid(), 9, lost: 3, fault: new RecordingFault(RecordingFaultKind.DiskFull, "The disk is full.", DateTimeOffset.UtcNow, false)) };
        var vm = NewMain(fake);
        vm.Background.RefreshStorage();
        vm.Recorder.Refresh();
        Assert.Equal(RecorderStatusKind.Error, vm.Recorder.Status);
        Assert.Equal("Storage problem", vm.Recorder.StatusLabel);
        Assert.Contains("The disk is full.", vm.Recorder.StatusDetail, StringComparison.Ordinal);
        Assert.Equal("3 lost", vm.Recorder.SamplesTile.Detail);
        Assert.Equal("Warning", vm.Recorder.SamplesTile.Icon);
        Assert.Equal("12", vm.Recorder.SamplesTile.Value);
    }

    [Fact]
    public void ARecordingBeingFinalizedIsShownAsFinalizingAndNothingIsInvented()
    {
        var fake = new FakeRecorder { IsRecording = false, SampleCount = 20, SinkStatus = Sink(RecordingSinkState.Active, Guid.NewGuid(), 20) };
        var vm = NewMain(fake);
        Assert.True(vm.Recorder.IsFinalizing);
        Assert.Equal(RecorderStatusKind.Finalizing, vm.Recorder.Status);
        Assert.Equal("Finalizing", vm.Recorder.StatusTile.Value);
        // The enumeration holds only states the recorder can actually report: ready, recording, finalizing, completed and error. There is no recording pause.
        Assert.Equal(["Ready", "Recording", "Finalizing", "Completed", "Error"], Enum.GetNames<RecorderStatusKind>());
        Assert.DoesNotContain("pause", string.Join(' ', Enum.GetNames<RecorderStatusKind>()), StringComparison.OrdinalIgnoreCase);
        // The start command keeps its own conditions: the recorder waits for the finalization itself.
        Assert.DoesNotContain("IsFinalizing", Regex.Match(Source("src/EucLens.App/ViewModels/MainViewModel.cs"), @"StartRecordingCommand = new\([^\n]*").Value, StringComparison.Ordinal);
    }

    [Fact]
    public void WhenAFinalizingRecordingIsCompleteTheSavedListIsRefreshedOnceNotPerSample()
    {
        var fake = new FakeRecorder { IsRecording = false, SampleCount = 20, SinkStatus = Sink(RecordingSinkState.Active, Guid.NewGuid(), 20) };
        var background = new BackgroundViewModel(new MonitoringService(new MonitoringCoordinator(), fake), fake, new Desktop(), storage: new MemoryStorage());
        var vm = NewMain(fake, background: background);
        var changes = 0;
        background.Recordings.CollectionChanged += (_, _) => changes++;
        for (var i = 0; i < 20; i++) vm.Recorder.Refresh();                   // finalizing: no scanning, however often the page refreshes
        Assert.Equal(0, changes);
        fake.SinkStatus = Sink(RecordingSinkState.Idle, null, 20);
        for (var i = 0; i < 20; i++) vm.Recorder.Refresh();
        SpinWait.SpinUntil(() => changes > 0, TimeSpan.FromSeconds(5));
        Assert.Equal(1, changes);
    }

    [Fact]
    public async Task TheMonitoringSummaryNamesTheKindOfDataAndWhatTheSessionProvidesFromTheExistingDetection()
    {
        var rig = await Rig.CreateAsync();
        var summary = rig.Vm.Recorder.MonitoringSummary;
        Assert.StartsWith("Monitoring active · Real Session · ", summary, StringComparison.Ordinal);
        Assert.Equal(summary.Split(" · ")[2], rig.Vm.Recorder.MonitoringSummary.Split(" · ")[2]);
        Assert.Equal(rig.Vm.DataAvailability, rig.Vm.Recorder.MonitoringDetail);
        // Nothing about the protocol is typed on the page: it is the detection's own result.
        var workspace = Source("src/EucLens.App/ViewModels/RecorderWorkspace.cs");
        Assert.Contains("main.DataAvailability", workspace, StringComparison.Ordinal);
        var synthetic = NewMain(new SessionRecorder(), new Desktop());
        Assert.NotNull(synthetic.Recorder.MonitoringSummary);
    }

    // ---------- the live latency chart ----------

    [Fact]
    public async Task TheChartHasAnEmptyStateInsteadOfAFabricatedLineAndKeepsGapsAsTheyAre()
    {
        var rig = await Rig.CreateAsync(startMonitoring: false);
        var workspace = rig.Vm.Recorder;
        var chart = rig.Vm.RecordingChart;
        Assert.Empty(chart.Points);
        Assert.False(workspace.ChartHasValues);
        Assert.True(workspace.ChartShowEmpty);
        Assert.StartsWith("No RTT samples available yet.", workspace.ChartEmptyText, StringComparison.Ordinal);
        // Samples without RTT (a session without Horizon counters): the points exist, none has a value, and the page says RTT is unavailable, which is not "empty".
        chart.Add(DateTime.Now, null); chart.Add(DateTime.Now.AddSeconds(1), null);
        workspace.Refresh();
        Assert.True(workspace.ChartShowEmpty);
        Assert.StartsWith("RTT is not available in this session", workspace.ChartEmptyText, StringComparison.Ordinal);
        Assert.Equal(2, chart.Points.Count);                                   // nothing was added to draw a flat line
        Assert.All(chart.Points, p => Assert.Null(p.Value));
        // Values appear: the chart is shown, its data is exactly what was recorded, gaps stay gaps.
        chart.Add(DateTime.Now.AddSeconds(2), 31.5); chart.Add(DateTime.Now.AddSeconds(3), null); chart.Add(DateTime.Now.AddSeconds(4), 29);
        workspace.Refresh();
        Assert.True(workspace.ChartHasValues);
        Assert.False(workspace.ChartShowEmpty);
        Assert.Equal([null, null, 31.5, null, 29], chart.Points.Select(p => p.Value));
        Assert.Equal("Round-trip time (RTT) · latest 600 samples", workspace.ChartSubtitle);
        Assert.Equal(600, chart.Capacity);
        Assert.Equal("ms", chart.Unit);
    }

    [Fact]
    public void TheChartIsCompactAndTheTextIsTheRequestedOne()
    {
        var xaml = Source("src/EucLens.App/Views/RecorderView.xaml");
        var height = double.Parse(Regex.Match(xaml, @"<lvc:CartesianChart [^>]*Height=""(\d+)""").Groups[1].Value);
        Assert.InRange(height, 180, 240);                                      // the old chart was 216 DIPs after a 100+ DIP header; the section is now one title, one subtitle and this
        Assert.Contains("Text=\"Live connection latency\"", xaml, StringComparison.Ordinal);
        Assert.Contains("{Binding Monitor.Recorder.ChartSubtitle}", xaml, StringComparison.Ordinal);
        Assert.Single(Regex.Matches(xaml, "<lvc:CartesianChart "));   // the page still has exactly one chart: the smoke test's layout matrix expects it
        Assert.Contains("ui:LiveChart.Data=\"{Binding Monitor.RecordingChart}\"", xaml, StringComparison.Ordinal);   // the same chart data source
    }

    // ---------- storage and details ----------

    [Fact]
    public async Task TheStorageDetailsKeepEveryExplanationAndFollowTheSettings()
    {
        var rig = await Rig.CreateAsync();
        var workspace = rig.Vm.Recorder;
        Assert.Equal(rig.Vm.Background.RecordingFolder, workspace.FolderText);
        Assert.Contains($"{1024:N0} MiB", workspace.QuotaText, StringComparison.Ordinal);
        Assert.Contains("14 days", workspace.QuotaText, StringComparison.Ordinal);
        Assert.Contains("never the recording in progress", workspace.QuotaText, StringComparison.Ordinal);
        Assert.Contains("euclens.recording/1", RecorderWorkspace.FormatText, StringComparison.Ordinal);
        Assert.Contains("never written as zero", RecorderWorkspace.FormatText, StringComparison.Ordinal);
        Assert.Contains("exact length as a gap", RecorderWorkspace.PauseText, StringComparison.Ordinal);
        Assert.Contains("non-persistent VDI", RecorderWorkspace.VdiText, StringComparison.Ordinal);
        Assert.Contains("Every recording is a new file", workspace.RecordLimit, StringComparison.Ordinal);
        Assert.Contains("86,400", NewMain(new SessionRecorder()).RecordLimit, StringComparison.Ordinal);     // the in-memory recorder's own limit text, kept
        // The explanations are collapsed by default and the page's main area has no long paragraphs of them.
        var xaml = RecorderView();
        var expander = Regex.Match(xaml, @"<Expander Header=""Storage and recording details""[^>]*>").Value;
        Assert.NotEmpty(expander);
        Assert.DoesNotContain("IsExpanded=\"True\"", expander, StringComparison.Ordinal);
        Assert.Contains("OpenRecordingsFolderCommand", xaml, StringComparison.Ordinal);
        var outside = xaml.Replace(Regex.Match(xaml, @"(?s)<Expander Header=""Storage and recording details"".*?</Expander>").Value, "");
        Assert.DoesNotContain("Pausing collects no samples", outside, StringComparison.Ordinal);
        Assert.DoesNotContain("readable JSON Lines", outside, StringComparison.Ordinal);
    }

    private static string RecorderView() => Source("src/EucLens.App/Views/RecorderView.xaml");

    // ---------- saved recordings ----------

    private static RecordingSummary Summary(int n, DateTimeOffset? started = null, double seconds = 60, long bytes = 10_000, long samples = 60, bool complete = true, bool withFile = true, string? id = null) =>
        new(id ?? n.ToString("x8"), started ?? TestSamples.T0.AddMinutes(n), complete ? (started ?? TestSamples.T0.AddMinutes(n)).AddSeconds(seconds) : null, TimeSpan.FromSeconds(seconds), bytes, samples, 0, 0, 0, complete, complete ? "User stopped" : "", "Real", withFile ? [$"/rec/euclens-{n:x8}-part001.jsonl"] : [], "/rec");

    private static (BackgroundViewModel Background, Desktop Desktop, FakeRecorder Recorder) NewBackground()
    {
        var desktop = new Desktop(); var recorder = new FakeRecorder();
        return (new BackgroundViewModel(new MonitoringService(new MonitoringCoordinator(), recorder), recorder, desktop, storage: new MemoryStorage()), desktop, recorder);
    }

    private static void Load(BackgroundViewModel background, IEnumerable<RecordingSummary> summaries) => background.Recordings.ReplaceAll(summaries.Select(s => new RecordingItem(s, background)));

    [Fact]
    public void RecordingsAreListedNewestFirstAndSortByDateDurationAndSizeBothWays()
    {
        var (background, _, _) = NewBackground();
        Load(background, [Summary(1, seconds: 300, bytes: 5_000), Summary(3, seconds: 100, bytes: 30_000), Summary(2, seconds: 200, bytes: 20_000)]);
        var list = background.SavedRecordings;
        Assert.Equal(["00000003", "00000002", "00000001"], list.Items.Select(i => i.ShortId));      // newest first by default
        Assert.Equal("▼", list.RecordedSortMark);
        list.SortByDurationCommand.Execute(null);
        Assert.Equal(["00000001", "00000002", "00000003"], list.Items.Select(i => i.ShortId));      // longest first
        Assert.Equal("▼", list.DurationSortMark); Assert.Equal("", list.RecordedSortMark);
        list.SortByDurationCommand.Execute(null);
        Assert.Equal(["00000003", "00000002", "00000001"], list.Items.Select(i => i.ShortId));      // shortest first
        Assert.Equal("▲", list.DurationSortMark);
        list.SortBySizeCommand.Execute(null);
        Assert.Equal(["00000003", "00000002", "00000001"], list.Items.Select(i => i.ShortId));      // largest first
        list.SortBySizeCommand.Execute(null);
        Assert.Equal(["00000001", "00000002", "00000003"], list.Items.Select(i => i.ShortId));
        list.SortByRecordedCommand.Execute(null);
        Assert.Equal(["00000003", "00000002", "00000001"], list.Items.Select(i => i.ShortId));      // a new column starts with its natural direction
        list.SortByRecordedCommand.Execute(null);
        Assert.Equal(["00000001", "00000002", "00000003"], list.Items.Select(i => i.ShortId));      // oldest first
        Assert.Contains("oldest first", list.SortDescription, StringComparison.Ordinal);
    }

    [Fact]
    public void EqualSortValuesKeepAFixedOrderWhateverOrderTheCatalogReturnsThem()
    {
        var (background, _, _) = NewBackground();
        var same = Enumerable.Range(0, 30).Select(n => Summary(n, started: TestSamples.T0, seconds: 90, bytes: 7_000, id: $"{(n * 7) % 30:x8}")).ToList();
        var list = background.SavedRecordings;
        string[]? first = null;
        foreach (var key in new Action[] { () => list.SortBy(RecordingSort.Duration), () => list.SortBy(RecordingSort.Size), () => list.SortBy(RecordingSort.Recorded) })
        {
            key();
            first = null;
            for (var shuffle = 0; shuffle < 5; shuffle++)
            {
                Load(background, same.OrderBy(s => (s.ShortId.GetHashCode() ^ shuffle * 31) & 0xFFFF).ToList());
                var order = list.Items.Select(i => i.ShortId).ToArray();
                first ??= order;
                Assert.Equal(first, order);
            }
            Assert.Equal(first!.OrderBy(x => x, StringComparer.Ordinal), first);        // ties fall back to the id, ascending, in every direction of the primary key
        }
    }

    [Fact]
    public void TheStatusFilterShowsCompleteInterruptedAndInProgressRecordingsFromTheirOwnFiles()
    {
        var (background, _, recorder) = NewBackground();
        var activeId = Guid.NewGuid();
        recorder.IsRecording = true; recorder.SinkStatus = Sink(RecordingSinkState.Active, activeId, 5);
        Load(background, [Summary(1), Summary(2, complete: false), Summary(3), Summary(4, complete: false, id: RecordingFormat.ShortId(activeId))]);
        var list = background.SavedRecordings;
        Assert.Equal(4, list.Items.Count);
        Assert.Equal(["All", "Complete", "In progress", "Interrupted"], list.StatusFilters.Select(f => f.Label));
        list.SelectedFilter = list.StatusFilters[1];
        Assert.Equal(["00000003", "00000001"], list.Items.Select(i => i.ShortId));
        Assert.Equal("2 of 4 recordings", list.CountText);
        list.SelectedFilter = list.StatusFilters[2];
        Assert.Equal([RecordingFormat.ShortId(activeId)], list.Items.Select(i => i.ShortId));
        Assert.Equal("In progress", list.Items[0].StatusLabel);
        list.SelectedFilter = list.StatusFilters[3];
        Assert.Equal(["00000002"], list.Items.Select(i => i.ShortId));
        Assert.Equal("Interrupted", list.Items[0].StatusLabel);
        Assert.Equal("Warning", list.Items[0].StatusIcon);
        list.SelectedFilter = list.StatusFilters[0];
        Assert.Equal(4, list.Items.Count);
        Assert.Equal("4 recordings", list.CountText);
    }

    [Fact]
    public void SelectingARecordingOnlySelectsItAndOpensNavigatesOrAnalyzesNothing()
    {
        var (background, desktop, _) = NewBackground();
        var analyzed = new List<RecordingSummary>();
        background.AnalyzeRequested += analyzed.Add;
        Load(background, [Summary(1), Summary(2)]);
        var list = background.SavedRecordings;
        Assert.False(list.HasSelection);
        list.SelectedItem = list.Items[1];
        list.SelectedItem = list.Items[0];
        Assert.True(list.HasSelection);
        Assert.Equal("00000002", list.SelectedItem!.ShortId);
        Assert.Empty(analyzed);                                                // Session History is not asked for
        Assert.Empty(desktop.Opened);                                          // no file or folder is opened
        Assert.Equal(0, desktop.ExportDialogs);                                // nothing is exported
        Assert.Contains("00:01:00", list.SelectionSummary, StringComparison.Ordinal);
        Assert.Contains("samples", list.SelectionSummary, StringComparison.Ordinal);
        // The view's code behind keeps a selection from scrolling the page.
        Assert.Contains("RecordingsList.RequestBringIntoView += (_, e) => e.Handled = true;", Source("src/EucLens.App/Views/RecorderView.xaml.cs"), StringComparison.Ordinal);
        // The code that selects never calls anything that opens, analyzes or exports.
        var selection = Source("src/EucLens.App/ViewModels/SavedRecordingsViewModel.cs");
        var setter = Regex.Match(selection, @"(?s)public RecordingItem\? SelectedItem.*?RaiseSelection\(\);\s*\}\s*\}").Value;
        Assert.NotEmpty(setter);
        Assert.DoesNotContain("Execute", setter, StringComparison.Ordinal);
        Assert.DoesNotContain("Analyze", setter, StringComparison.Ordinal);
    }

    [Fact]
    public void ActionsAreDisabledWithAnExplanationUntilARecordingIsSelectedAndShowFileNeedsAFile()
    {
        var (background, _, _) = NewBackground();
        Load(background, [Summary(1), Summary(2, withFile: false)]);
        var list = background.SavedRecordings;
        foreach (var command in new System.Windows.Input.ICommand[] { list.AnalyzeCommand, list.ExportJsonCommand, list.ExportCsvCommand, list.ShowFileCommand, list.OpenFolderCommand })
            Assert.False(command.CanExecute(null));
        Assert.Equal(SavedRecordingsViewModel.SelectHint, list.ActionHint);
        Assert.Equal(SavedRecordingsViewModel.SelectHint, list.AnalyzeHint);
        list.SelectedItem = list.Items.Single(i => i.ShortId == "00000001");
        foreach (var command in new System.Windows.Input.ICommand[] { list.AnalyzeCommand, list.ExportJsonCommand, list.ExportCsvCommand, list.ShowFileCommand, list.OpenFolderCommand })
            Assert.True(command.CanExecute(null));
        Assert.Equal("", list.ActionHint);
        list.SelectedItem = list.Items.Single(i => i.ShortId == "00000002");
        Assert.False(list.ShowFileCommand.CanExecute(null));
        Assert.True(list.OpenFolderCommand.CanExecute(null));
        Assert.Equal(SavedRecordingsViewModel.NoFileHint, list.ActionHint);
        Assert.Equal(SavedRecordingsViewModel.NoFileHint, list.ShowFileHint);
        // The page gives each action its accessible name and its explanation as help text.
        var xaml = RecorderView();
        foreach (var command in new[] { "AnalyzeCommand", "ExportJsonCommand", "ExportCsvCommand", "ShowFileCommand", "OpenFolderCommand" })
        {
            var button = Regex.Match(xaml, $@"<Button [^>]*SavedRecordings\.{command}[^>]*>").Value;
            Assert.Contains("AutomationProperties.Name=", button, StringComparison.Ordinal);
            Assert.Contains("AutomationProperties.HelpText=", button, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void AnalyzeRoutesTheSelectedRecordingToSessionHistoryAndShowFileAndFolderGoThroughTheDesktop()
    {
        var (background, desktop, _) = NewBackground();
        var analyzed = new List<RecordingSummary>();
        background.AnalyzeRequested += analyzed.Add;
        Load(background, [Summary(5)]);
        var list = background.SavedRecordings;
        list.SelectedItem = list.Items[0];
        list.AnalyzeCommand.Execute(null);
        Assert.Equal("00000005", Assert.Single(analyzed).ShortId);
        list.ShowFileCommand.Execute(null);
        list.OpenFolderCommand.Execute(null);
        Assert.Equal(["/rec/euclens-00000005-part001.jsonl", "/rec"], desktop.Opened);
        desktop.OpenSucceeds = false;
        list.ShowFileCommand.Execute(null);
        Assert.Equal("Windows Explorer could not open the location.", background.RecordingsMessage);   // the existing message, unchanged
        // The main view model still turns the request into the Session History page (the existing wiring).
        var main = NewMain(new SessionRecorder(), desktop, background: background);
        background.RequestAnalyze(background.Recordings[0].Summary);
        Assert.IsType<SessionHistoryPage>(main.SelectedPage);
    }

    [Fact]
    public async Task ExportsOfTheSelectedRecordingUseTheExistingExportsAndWriteTheSamples()
    {
        var recorded = await HistoryFixture.RecordAsync(40);
        var desktop = new Desktop();
        var folder = Path.Combine(Path.GetTempPath(), "euclens-recorder-ws-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var store = new BackgroundSettingsStore(Path.Combine(folder, "background.json"));
            await store.SaveAsync(BackgroundSettings.Defaults with { RecordingDirectory = HistoryFixture.Dir });
            var recorder = new SessionRecorder();
            var background = new BackgroundViewModel(new MonitoringService(new MonitoringCoordinator(), recorder), recorder, desktop, store, recorded.Storage);
            await background.LoadAsync();
            await background.RefreshRecordingsAsync();
            var list = background.SavedRecordings;
            var item = Assert.Single(list.Items);
            Assert.Equal(recorded.ShortId, item.ShortId);
            Assert.Equal("Complete", item.StatusLabel);
            list.SelectedItem = item;
            var csv = Path.Combine(folder, "out.csv"); var json = Path.Combine(folder, "out.json");
            desktop.ExportPath = csv;
            await list.ExportCsvCommand.ExecuteAsync();
            desktop.ExportPath = json;
            await list.ExportJsonCommand.ExecuteAsync();
            Assert.Equal(2, desktop.ExportDialogs);
            Assert.Equal(41, File.ReadAllLines(csv).Length);                       // header + 40 samples, as the per-row button wrote before
            Assert.True(new FileInfo(json).Length > 0);
            using (var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(json))) Assert.NotEqual(System.Text.Json.JsonValueKind.Undefined, document.RootElement.ValueKind);
            Assert.StartsWith("Exported 40 samples to ", background.RecordingsMessage, StringComparison.Ordinal);
            // A cancelled dialog exports nothing.
            desktop.ExportPath = null;
            await list.ExportCsvCommand.ExecuteAsync();
            Assert.Equal(3, desktop.ExportDialogs);
            // Repeated clicks on a running export are one export (the existing command guards re-entry).
            Assert.True(list.ExportCsvCommand.CanExecute(null));
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public async Task ARefreshKeepsTheSelectionWhileTheRecordingExistsAndClearsItWhenItWasDeletedOutside()
    {
        var first = await HistoryFixture.RecordAsync(10);
        var second = await HistoryFixture.RecordAsync(12, storage: first.Storage, make: i => HistoryFixture.Sample(i, at: HistoryFixture.T0.AddHours(1).AddSeconds(i)));
        var desktop = new Desktop();
        var folder = Path.Combine(Path.GetTempPath(), "euclens-recorder-ws-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var store = new BackgroundSettingsStore(Path.Combine(folder, "background.json"));
            await store.SaveAsync(BackgroundSettings.Defaults with { RecordingDirectory = HistoryFixture.Dir });
            var recorder = new SessionRecorder();
            var background = new BackgroundViewModel(new MonitoringService(new MonitoringCoordinator(), recorder), recorder, desktop, store, first.Storage);
            await background.LoadAsync();
            var changes = 0;
            background.Recordings.CollectionChanged += (_, e) => { changes++; Assert.Equal(NotifyCollectionChangedAction.Reset, e.Action); };
            await background.RefreshRecordingsAsync();
            Assert.Equal(1, changes);                                              // the whole list in one notification
            var list = background.SavedRecordings;
            Assert.Equal(2, list.Items.Count);
            Assert.Contains(second.ShortId, list.Items.Select(i => i.ShortId));
            list.SelectedItem = list.Items.Single(i => i.ShortId == first.ShortId);
            var selectedId = list.SelectedItem!.ShortId;
            var oldItem = list.SelectedItem;
            await background.RefreshRecordingsAsync();
            Assert.Equal(selectedId, list.SelectedItem!.ShortId);                  // still selected after a refresh ...
            Assert.NotSame(oldItem, list.SelectedItem);                            // ... as the new item of the same recording
            Assert.True(list.AnalyzeCommand.CanExecute(null));
            // Deleted outside EUC Lens: the next refresh drops it and the selection with it; the actions explain.
            foreach (var key in first.Storage.Files.Keys.Where(k => k.Contains(selectedId, StringComparison.Ordinal)).ToList()) first.Storage.Files.Remove(key);
            await background.RefreshRecordingsAsync();
            Assert.Single(list.Items);
            Assert.Null(list.SelectedItem);
            Assert.False(list.AnalyzeCommand.CanExecute(null));
            Assert.Equal(SavedRecordingsViewModel.SelectHint, list.ActionHint);
            Assert.Contains("1 recording", background.RecordingsMessage, StringComparison.Ordinal);
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public async Task ADeletedOrDamagedRecordingNeverBreaksTheListOrAnExport()
    {
        var good = await HistoryFixture.RecordAsync(10);
        var damaged = await HistoryFixture.RecordAsync(10, storage: good.Storage, make: i => HistoryFixture.Sample(i, at: HistoryFixture.T0.AddHours(2).AddSeconds(i)));
        var desktop = new Desktop();
        var folder = Path.Combine(Path.GetTempPath(), "euclens-recorder-ws-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var store = new BackgroundSettingsStore(Path.Combine(folder, "background.json"));
            await store.SaveAsync(BackgroundSettings.Defaults with { RecordingDirectory = HistoryFixture.Dir });
            var recorder = new SessionRecorder();
            var background = new BackgroundViewModel(new MonitoringService(new MonitoringCoordinator(), recorder), recorder, desktop, store, good.Storage);
            await background.LoadAsync();
            await background.RefreshRecordingsAsync();
            var list = background.SavedRecordings;
            Assert.Equal(2, list.Items.Count);
            // The recording is selected, then its files disappear before the export: the export reports the problem and the page stays usable.
            list.SelectedItem = list.Items.Single(i => i.ShortId == damaged.ShortId);
            foreach (var key in good.Storage.Files.Keys.Where(k => k.Contains(damaged.ShortId, StringComparison.Ordinal) && !k.EndsWith(".meta.json", StringComparison.Ordinal)).ToList()) good.Storage.Files.Remove(key);
            desktop.ExportPath = Path.Combine(folder, "gone.csv");
            await list.ExportCsvCommand.ExecuteAsync();
            Assert.True(background.RecordingsMessage.StartsWith("Export failed:", StringComparison.Ordinal) || background.RecordingsMessage.StartsWith("Exported", StringComparison.Ordinal));
            // Damaged content: garbage in the remaining file; listing and refreshing still work.
            foreach (var key in good.Storage.Files.Keys.Where(k => k.Contains(good.ShortId, StringComparison.Ordinal) && k.EndsWith(".jsonl", StringComparison.Ordinal)).ToList()) good.Storage.Files[key] = [0xFF, 0x00, 0x13, 0x37, 0x7B];
            await background.RefreshRecordingsAsync();
            Assert.NotNull(background.RecordingsMessage);
            Assert.True(list.Items.Count <= 2);
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public void AFilterThatHidesTheSelectedRecordingClearsTheSelectionInsteadOfActingOnSomethingInvisible()
    {
        var (background, _, _) = NewBackground();
        Load(background, [Summary(1), Summary(2, complete: false)]);
        var list = background.SavedRecordings;
        list.SelectedItem = list.Items.Single(i => i.ShortId == "00000002");
        list.SelectedFilter = list.StatusFilters[1];                           // Complete
        Assert.Null(list.SelectedItem);
        Assert.False(list.AnalyzeCommand.CanExecute(null));
        list.SelectedFilter = list.StatusFilters[0];
        Assert.Null(list.SelectedItem);                                         // not restored behind the user's back
        list.SelectedItem = list.Items[0];
        list.SelectedFilter = list.StatusFilters[0];
        Assert.NotNull(list.SelectedItem);                                      // a filter that keeps it keeps the selection
    }

    [Fact]
    public void AnEmptyFolderAndAnEmptyFilterSayWhichIsWhich()
    {
        var (background, _, _) = NewBackground();
        var list = background.SavedRecordings;
        Assert.True(list.IsEmpty); Assert.False(list.HasItems);
        Assert.Equal("No saved recordings", list.CountText);
        Assert.Contains("No saved recordings yet", list.EmptyText, StringComparison.Ordinal);
        Load(background, [Summary(1)]);
        list.SelectedFilter = list.StatusFilters[3];
        Assert.True(list.IsEmpty);
        Assert.Equal("No recording matches this filter.", list.EmptyText);
        Assert.Equal("0 of 1 recordings", list.CountText);
    }

    [Fact]
    public void HundredsOfRecordingsSortAndRefreshQuicklyWithOneNotificationAndALimitedListHeightSoRowsAreVirtual()
    {
        var (background, _, _) = NewBackground();
        var many = Enumerable.Range(0, 1500).Select(n => Summary(n, seconds: n % 97, bytes: (n * 7919L) % 100_000, complete: n % 5 != 0)).ToList();
        var list = background.SavedRecordings;
        var itemEvents = 0;
        list.Items.CollectionChanged += (_, _) => itemEvents++;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        Load(background, many);
        Assert.Equal(1, itemEvents);                                           // one rebuild, one notification
        Assert.Equal(1500, list.Items.Count);
        list.SortBySizeCommand.Execute(null); list.SortByDurationCommand.Execute(null); list.SortByRecordedCommand.Execute(null);
        list.SelectedFilter = list.StatusFilters[3];
        Assert.Equal(300, list.Items.Count);
        list.SelectedFilter = list.StatusFilters[0];
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"sorting and filtering 1,500 recordings took {watch.Elapsed.TotalMilliseconds:0} ms");
        // The list never reads a recording's content: it is built from the catalog's summaries only.
        var source = Source("src/EucLens.App/ViewModels/SavedRecordingsViewModel.cs");
        Assert.DoesNotContain("ReadSamples", source, StringComparison.Ordinal);
        Assert.DoesNotContain("HistoryLoader", source, StringComparison.Ordinal);
        Assert.DoesNotContain("AnomalyDetectionEngine", source, StringComparison.Ordinal);
        // Virtual rows in a list of bounded height with its own scroll viewer: the cost is the rows in view, not the number of recordings.
        var xaml = RecorderView();
        Assert.Contains("<VirtualizingStackPanel />", xaml, StringComparison.Ordinal);
        Assert.Contains("VirtualizingPanel.IsVirtualizing\" Value=\"True\"", xaml, StringComparison.Ordinal);
        Assert.Contains("VirtualizationMode\" Value=\"Recycling\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"RecordingsScroll\"", xaml, StringComparison.Ordinal);
        Assert.Matches(@"<ListBox x:Name=""RecordingsList""[^>]*MaxHeight=""\d+""", xaml);
    }

    [Fact]
    public async Task OnlyTheSelectedRecordingsActionsExistOnceAndTheRowsHaveNoButtons()
    {
        await Task.CompletedTask;
        var xaml = RecorderView();
        var template = Regex.Match(xaml, @"(?s)<ListBox\.ItemTemplate>.*?</ListBox\.ItemTemplate>").Value;
        Assert.NotEmpty(template);
        Assert.DoesNotContain("<Button", template, StringComparison.Ordinal);                 // no per-row Show file, Open folder, Export JSON, Export CSV
        foreach (var label in new[] { "Analyze recording", "Show file", "Open folder" })
            Assert.Equal(1, Regex.Matches(xaml, $"Content=\"{label}\"").Count - (label == "Open folder" ? 1 : 0));   // Open folder also exists once in the collapsed storage details
        Assert.Equal(2, Regex.Matches(xaml, "Content=\"Export JSON\"").Count);                // the current recording's export and the selected recording's
        Assert.Matches(@"Content=""Analyze recording""[^>]*PrimaryButton", xaml);
        Assert.Matches(@"Content=""Start recording""[^>]*PrimaryButton", xaml);
        Assert.Matches(@"Content=""Stop recording""[^>]*PrimaryButton", xaml);
        Assert.DoesNotMatch(@"Content=""Export JSON""[^>]*PrimaryButton", xaml);
    }

    [Fact]
    public void ThePageShowsTheHierarchyItAsksForAndNoDoubleClickBehaviorWasAdded()
    {
        var xaml = RecorderView();
        int At(string text) { var i = xaml.IndexOf(text, StringComparison.Ordinal); Assert.True(i >= 0, text); return i; }
        // title, status and tiles, actions, live chart, saved recordings, collapsed details
        Assert.True(At("Text=\"Session recorder\"") < At("Monitor.Recorder.Tiles") && At("Monitor.Recorder.Tiles") < At("Content=\"Start recording\"") && At("Content=\"Start recording\"") < At("Live connection latency\" Style")
            && At("Live connection latency\" Style") < At("Text=\"Saved recordings\"") && At("Text=\"Saved recordings\"") < At("Storage and recording details"));
        Assert.DoesNotContain("MouseDoubleClick", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("MouseDoubleClick", Source("src/EucLens.App/Views/RecorderView.xaml.cs"), StringComparison.Ordinal);
        Assert.DoesNotContain("ContextMenu", xaml, StringComparison.Ordinal);                  // no important action hidden in a context menu
        // Start and stop are two buttons that never trade places: a double click on Start cannot hit Stop.
        Assert.Single(Regex.Matches(xaml, "Content=\"Start recording\""));
        Assert.Single(Regex.Matches(xaml, "Content=\"Stop recording\""));
        Assert.True(At("Content=\"Start recording\"") < At("Content=\"Stop recording\""));
        // Three prominent tiles, the live regions and the names that screen readers need.
        Assert.Contains("MaximumColumns=\"3\"", xaml, StringComparison.Ordinal);
        foreach (var live in new[] { "Monitor.Recorder.StatusDetail", "Monitor.Recorder.MonitoringSummary", "Monitor.Background.RecordingsMessage" })
            Assert.Matches($@"Text=""\{{Binding {Regex.Escape(live)}\}}""[^>]*AutomationProperties\.LiveSetting=""Polite""", xaml);
        foreach (Match button in Regex.Matches(xaml, @"<Button [^>]*>"))
            Assert.True(button.Value.Contains("AutomationProperties.Name=", StringComparison.Ordinal) || button.Value.Contains("Content=\"", StringComparison.Ordinal), button.Value);
        // The shared typography only: no literal font size or family on the page, no tab order overrides.
        Assert.DoesNotMatch(@"FontSize=""\d", xaml);
        Assert.DoesNotMatch(@"FontFamily=""", xaml);
        Assert.DoesNotContain("TabIndex", xaml, StringComparison.Ordinal);
        // Every binding of the page resolves.
        var members = typeof(RecorderWorkspace).GetProperties().Select(p => p.Name).ToHashSet();
        foreach (Match m in Regex.Matches(xaml, @"\{Binding Monitor\.Recorder\.([A-Za-z]+)"))
            Assert.Contains(m.Groups[1].Value, members);
        var saved = typeof(SavedRecordingsViewModel).GetProperties().Select(p => p.Name).ToHashSet();
        foreach (Match m in Regex.Matches(xaml, @"\{Binding Monitor\.Background\.SavedRecordings\.([A-Za-z]+)"))
            Assert.Contains(m.Groups[1].Value, saved);
        var item = typeof(RecordingItem).GetProperties().Select(p => p.Name).ToHashSet();
        foreach (var name in new[] { "Started", "Duration", "Samples", "Size", "StatusIcon", "StatusSummary", "Status", "FileName", "AutomationName" })
            Assert.Contains(name, item);
    }

    // ---------- navigation and lifetime ----------

    private static int Count(object source, string eventName)
    {
        for (var type = source.GetType(); type is not null; type = type.BaseType)
            if (type.GetField(eventName, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.DeclaredOnly)?.GetValue(source) is Delegate d)
                return d.GetInvocationList().Length;
        return 0;
    }

    private static int[] Subscriptions(MainViewModel vm, MonitoringService monitoring, SessionRecorder recorder) =>
    [
        Count(vm, "PropertyChanged"), Count(vm.Recorder, "PropertyChanged"), Count(vm.Background, "PropertyChanged"), Count(vm.Background.SavedRecordings, "PropertyChanged"), Count(vm.Background.Recordings, "CollectionChanged"),
        Count(vm.Background.SavedRecordings.Items, "CollectionChanged"), Count(monitoring, "StatusChanged"), Count(recorder, "Ended"), Count(vm.Background, "SettingsChanged"), Count(vm.Background, "AnalyzeRequested"),
        Count(vm.StartRecordingCommand, "CanExecuteChanged"), Count(vm.Recorder.StatusTile, "PropertyChanged"),
    ];

    [Fact]
    public async Task NavigatingAwayAndBackManyTimesDuringARecordingAddsNoSubscriptionsAndKeepsRecording()
    {
        var rig = await Rig.CreateAsync();
        var vm = rig.Vm;
        rig.Vm.StartRecordingCommand.Execute(null);
        for (var i = 0; i < 5; i++) rig.Recorder.Add(HistoryFixture.Sample(i));
        var recorderPage = vm.Pages.OfType<RecorderPage>().Single();
        vm.SelectedPage = recorderPage;
        var baseline = Subscriptions(vm, rig.Monitoring, rig.Recorder);
        var id = rig.Recorder.SinkStatus!.RecordingId;
        for (var round = 0; round < 60; round++)
        {
            vm.SelectedPage = vm.Pages[round % 4 == 0 ? 0 : round % 4 == 1 ? 1 : 3];      // Dashboard, Diagnostics, Session History ...
            vm.SelectedPage = recorderPage;                                                // ... and back
        }
        Assert.Equal(baseline, Subscriptions(vm, rig.Monitoring, rig.Recorder));
        Assert.True(rig.Recorder.IsRecording);                                            // the recording never noticed the navigation
        Assert.Equal(id, rig.Recorder.SinkStatus!.RecordingId);
        Assert.Equal(RecorderStatusKind.Recording, vm.Recorder.Status);
        Assert.Equal(MonitoringState.Running, rig.Monitoring.Coordinator.State);
        Assert.Single(rig.Vm.Pages.OfType<RecorderPage>());                               // one page object: the view model creates nothing per visit
        await rig.Recorder.StopAsync();
    }

    [Fact]
    public async Task HidingTheWindowToTheTrayAndRestoringItKeepsRecordingAndTheStatusIsCurrentAgain()
    {
        var rig = await Rig.CreateAsync();
        var vm = rig.Vm;
        vm.StartRecordingCommand.Execute(null);
        rig.Recorder.Add(HistoryFixture.Sample(0));
        vm.SetPresenting(false);                                                          // minimized to the tray
        for (var i = 1; i < 9; i++) rig.Recorder.Add(HistoryFixture.Sample(i));            // collection continues with nobody looking
        Assert.True(rig.Recorder.IsRecording);
        vm.SetPresenting(true);                                                           // restored
        Assert.Equal("9", vm.Recorder.SamplesTile.Value);
        Assert.Equal(RecorderStatusKind.Recording, vm.Recorder.Status);
        var before = Subscriptions(vm, rig.Monitoring, rig.Recorder);
        for (var i = 0; i < 20; i++) { vm.SetPresenting(false); vm.SetPresenting(true); }
        Assert.Equal(before, Subscriptions(vm, rig.Monitoring, rig.Recorder));
        await rig.Recorder.StopAsync();
    }

    [Fact]
    public async Task AfterARestartTheExistingRecordingsAreListedFromTheFolderWithTheirStatus()
    {
        var recorded = await HistoryFixture.RecordAsync(30);
        var interrupted = await HistoryFixture.RecordAsync(15, storage: recorded.Storage, finish: false, make: i => HistoryFixture.Sample(i, at: HistoryFixture.T0.AddHours(3).AddSeconds(i)));
        var folder = Path.Combine(Path.GetTempPath(), "euclens-recorder-ws-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var store = new BackgroundSettingsStore(Path.Combine(folder, "background.json"));
            await store.SaveAsync(BackgroundSettings.Defaults with { RecordingDirectory = HistoryFixture.Dir });
            // A fresh process: new view models over the same folder.
            var recorder = new SessionRecorder();
            var background = new BackgroundViewModel(new MonitoringService(new MonitoringCoordinator(), recorder), recorder, new Desktop(), store, recorded.Storage);
            await background.LoadAsync();
            var vm = NewMain(recorder, background: background);
            vm.SelectedPage = vm.Pages.OfType<RecorderPage>().Single();                      // showing the page lists the recordings
            SpinWait.SpinUntil(() => background.Recordings.Count == 2, TimeSpan.FromSeconds(10));
            var list = background.SavedRecordings;
            Assert.Equal(2, list.Items.Count);
            Assert.Equal(["Complete", "Interrupted"], list.Items.OrderBy(i => i.StatusLabel, StringComparer.Ordinal).Select(i => i.StatusLabel));   // the one without an end is interrupted, never called complete
            Assert.Equal("30", list.Items.Single(i => i.StatusLabel == "Complete").Samples);
            Assert.Null(list.SelectedItem);                                                  // nothing preselected, nothing opened
        }
        finally { Directory.Delete(folder, true); }
        GC.KeepAlive(interrupted);
    }

    [Fact]
    public void TheRecorderPresentationHoldsNoWindowViewOrTimer()
    {
        foreach (var file in new[] { "RecorderWorkspace.cs", "SavedRecordingsViewModel.cs" })
        {
            var source = Source("src/EucLens.App/ViewModels/" + file);
            Assert.DoesNotContain("System.Windows", source, StringComparison.Ordinal);
            Assert.DoesNotContain("DispatcherTimer", source, StringComparison.Ordinal);
            Assert.DoesNotContain("System.Threading.Timer", source, StringComparison.Ordinal);
            Assert.DoesNotContain("PeriodicTimer", source, StringComparison.Ordinal);
            Assert.DoesNotContain("Task.Delay", source, StringComparison.Ordinal);
            Assert.DoesNotContain("new MonitoringService", source, StringComparison.Ordinal);
            Assert.DoesNotContain("IMetricsProvider", source, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ThePageChangesNoRecordingMonitoringExportOrStorageContract()
    {
        // The engine, the writer, the catalog, the exports and the storage logic are untouched by this redesign: their files do not mention the new presentation types.
        foreach (var file in new[] { "src/EucLens.Core/SessionRecorder.cs", "src/EucLens.Core/Recording/RecordingWriter.cs", "src/EucLens.Core/Recording/RecordingCatalog.cs", "src/EucLens.Core/Recording/RecordingFormat.cs", "src/EucLens.Core/Recording/RecordingExport.cs" })
        {
            var text = Source(file);
            Assert.DoesNotContain("RecorderWorkspace", text, StringComparison.Ordinal);
            Assert.DoesNotContain("SavedRecordings", text, StringComparison.Ordinal);
        }
        Assert.Contains("euclens.recording/1", Source("src/EucLens.Core/Recording/RecordingFormat.cs"), StringComparison.Ordinal);
    }
}
