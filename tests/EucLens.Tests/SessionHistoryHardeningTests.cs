using System.Text;
using EucLens.App;
using EucLens.App.Presentation;
using EucLens.App.ViewModels;
using EucLens.Core;
using Microsoft.Extensions.Logging;
using Xunit;

namespace EucLens.Tests;

/// <summary>Session History under races and damage: leaving the page while a load finishes, selection under a filter, annotation edits while a save runs, newer annotation files.</summary>
public sealed class SessionHistoryHardeningTests
{
    private static readonly string Dir = HistoryFixture.Dir;

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

    private sealed class CollectingLogger : ILogger
    {
        public List<string> Warnings { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        { if (logLevel >= LogLevel.Warning) lock (Warnings) Warnings.Add(formatter(state, exception) + (exception is null ? "" : " | " + exception.GetType().Name)); }
    }

    private static SessionHistoryViewModel CreateHistory(IRecordingStorage storage, ILogger? logger = null)
    {
        var desktop = new Desktop();
        var recorder = new SessionRecorder();
        var monitoring = new MonitoringService(new MonitoringCoordinator(), recorder);
        var background = new BackgroundViewModel(monitoring, recorder, desktop, storage: storage);
        return new SessionHistoryViewModel(background, desktop, recorder, storage, TimeZoneInfo.Utc, logger: logger);
    }

    private static RecordingSummary Summary(IRecordingStorage storage, string shortId) => RecordingCatalog.Scan(storage, Dir).Single(s => s.ShortId == shortId);

    private static async Task<(SessionHistoryViewModel History, HistoryFixture.Recorded Rec)> OpenAsync(int count = 600, Func<int, SessionSnapshot>? make = null, Func<int, HistoryFixture.RecordingContext, Task>? step = null)
    {
        var rec = await HistoryFixture.RecordAsync(count, make, step, compress: false);
        var h = CreateHistory(rec.Storage);
        await h.OpenAsync(Summary(rec.Storage, rec.ShortId));
        await h.Settled;
        return (h, rec);
    }

    // ---------------------------------------------------------------- leaving the page while a load completes

    [Fact]
    public async Task LeavingThePageAfterTheLoadFinishedButBeforeTheUiContinuesIsHarmless()
    {
        // The view model lives on one UI-like thread. The load finishes on the thread pool; its continuation waits for the UI thread, which is busy here.
        await SingleThreadContext.RunAsync(async () =>
        {
            var rec = await HistoryFixture.RecordAsync(300, step: async (i, c) => { if (i == 150) await c.MarkIncidentAsync(); }, compress: false);
            var log = new CollectingLogger();
            var h = CreateHistory(rec.Storage, log);
            var opening = h.OpenAsync(Summary(rec.Storage, rec.ShortId));
            Thread.Sleep(800);                       // the loader is done; its continuation is queued behind this
            h.OnPageHidden();                        // the page is left: the cancellation source of the load is disposed
            var thrown = await Record.ExceptionAsync(() => opening);   // used to be an ObjectDisposedException from reading the token after the await
            Assert.Null(thrown);
            Assert.False(h.HasHistory);
            Assert.False(h.IsLoading);
            Assert.Equal("", h.Error);
            Assert.Empty(h.Events);
            Assert.Empty(log.Warnings);             // nothing failed quietly either: no exception was raised and swallowed on the way
        });
    }

    [Fact]
    public async Task ARepeatedOpenAndLeaveNeverFailsOrLeavesTheLoadingStateOn()
    {
        var rec = await HistoryFixture.RecordAsync(300, compress: false);
        var h = CreateHistory(rec.Storage);
        for (var i = 0; i < 40; i++)
        {
            var opening = h.OpenAsync(Summary(rec.Storage, rec.ShortId));
            await Task.Delay(i % 9);
            h.OnPageHidden();
            Assert.Null(await Record.ExceptionAsync(() => opening));
            Assert.False(h.IsLoading);
        }
    }

    private sealed class FailingStorage(IRecordingStorage inner) : IRecordingStorage
    {
        public void EnsureDirectory(string directory) => inner.EnsureDirectory(directory);
        public Stream Create(string path) => inner.Create(path);
        public Stream OpenRead(string path) => throw new InvalidOperationException("an unexpected failure inside the storage");
        public IReadOnlyList<RecordingFileInfo> List(string directory) => inner.List(directory);
        public void Delete(string path) => inner.Delete(path);
        public void Rename(string from, string to) => inner.Rename(from, to);
        public long? FreeSpace(string directory) => inner.FreeSpace(directory);
    }

    [Fact]
    public async Task AnUnexpectedFailureWhileOpeningIsShownAsAnErrorNotLostInAnUnobservedTask()
    {
        var rec = await HistoryFixture.RecordAsync(100, compress: false);
        var summary = Summary(rec.Storage, rec.ShortId);
        var h = CreateHistory(new FailingStorage(rec.Storage));
        Assert.Null(await Record.ExceptionAsync(() => h.OpenAsync(summary)));
        Assert.True(h.HasError);
        Assert.Contains("could not be opened", h.Error, StringComparison.Ordinal);
        Assert.False(h.IsLoading);
        Assert.False(h.HasHistory);
    }

    // ---------------------------------------------------------------- selection under a filter

    [Fact]
    public async Task PreviousAndNextIncidentNeverLeaveAStaleRowSelectedWhenTheFilterHidesTheIncident()
    {
        var (h, _) = await OpenAsync(600, make: i => TestSamples.Make(i, withEvent: i == 100), step: async (i, c) => { if (i == 400) await c.MarkIncidentAsync(); });
        var finding = h.Events.First(e => e.Event.IsFinding);
        h.SelectedFilter = h.Filters.Single(f => f.Value == HistoryEventFilter.Findings);   // incidents are hidden
        h.SelectedEvent = finding;
        Assert.Same(finding, h.SelectedEvent);
        Assert.DoesNotContain(h.Events, e => e.IsIncident);

        h.NextIncidentCommand.Execute(null);

        Assert.NotNull(h.SelectedIncident);                                                  // the incident is selected and shown
        Assert.Null(h.SelectedEvent);                                                         // the list does not keep highlighting the finding
        Assert.Equal("Diagnostic findings", h.SelectedFilter.Label);                          // the user's filter is left as it was
    }

    // ---------------------------------------------------------------- annotations

    [Fact]
    public async Task TextTypedWhileASaveRunsIsNeverOverwrittenByTheOlderSave()
    {
        var (h, rec) = await OpenAsync(600, step: async (i, c) => { if (i == 300) await c.MarkIncidentAsync(); });
        h.SelectedEvent = h.Events.Single(e => e.IsIncident);
        var vm = h.SelectedIncident!;
        vm.Category = HistoryIncidentViewModel.Categories.Single(x => x.Value == IncidentCategory.Freeze);
        vm.Description = "first version";
        var gate = rec.Storage.WriteGate = new ManualResetEventSlim(false);                    // the disk is slow: the save is in flight

        var saving = vm.SaveCommand.ExecuteAsync();
        await Task.Delay(150);
        vm.Description = "second, newer version";                                              // typed while the first save is still being written
        gate.Set(); rec.Storage.WriteGate = null;
        await saving;

        Assert.Equal("second, newer version", vm.Description);                                 // not replaced by the text that was saved
        Assert.True(vm.IsDirty);                                                               // and still waiting to be saved
        Assert.True(vm.SaveCommand.CanExecute(null));
        Assert.Contains("changed the text meanwhile", vm.SaveMessage, StringComparison.Ordinal);
        Assert.Equal("Freeze — first version", h.Events.Single(e => e.IsIncident).Annotation);  // the list shows what is really on disk
        await vm.SaveCommand.ExecuteAsync();
        Assert.False(vm.IsDirty);
        Assert.Equal("Freeze — second, newer version", h.Events.Single(e => e.IsIncident).Annotation);
        var saved = RecordingAnnotationStore.Load(rec.Storage, Dir, RecordingFormat.Stamp(h.Loaded!.Info.StartedUtc), rec.ShortId);
        Assert.Equal("second, newer version", saved.For(vm.Incident.Id)!.Description);
    }

    [Fact]
    public async Task UnsavedAnnotationEditsSurviveSelectingAnotherIncidentAndLeavingThePage()
    {
        var (h, _) = await OpenAsync(900, step: async (i, c) => { if (i is 200 or 600) await c.MarkIncidentAsync(); });
        var first = h.Loaded!.Incidents[0]; var second = h.Loaded.Incidents[1];
        h.SelectedEvent = h.Events.Single(e => e.Event.IncidentId == first.Id);
        h.SelectedIncident!.Category = HistoryIncidentViewModel.Categories.Single(x => x.Value == IncidentCategory.SlowResponse);
        h.SelectedIncident.Description = "typed, not saved";

        h.SelectedEvent = h.Events.Single(e => e.Event.IncidentId == second.Id);            // another incident
        Assert.Equal("", h.SelectedIncident!.Description);
        h.SelectedEvent = h.Events.Single(e => e.Event.IncidentId == first.Id);             // and back
        Assert.Equal("typed, not saved", h.SelectedIncident!.Description);
        Assert.Equal(IncidentCategory.SlowResponse, h.SelectedIncident.Category.Value);
        Assert.True(h.SelectedIncident.IsDirty);

        h.OnPageHidden();                                                                   // the page is left and shown again
        var summary = h.Recordings.Count > 0 ? h.Recordings[0].Summary : null;
        _ = summary;
        h.OnPageShown();
        await Task.Delay(200); await h.Settled;
        h.SelectedEvent = h.Events.Single(e => e.Event.IncidentId == first.Id);
        Assert.Equal("typed, not saved", h.SelectedIncident!.Description);

        await h.SelectedIncident.SaveCommand.ExecuteAsync();                                // saved: nothing is kept as a draft any more
        h.SelectedEvent = h.Events.Single(e => e.Event.IncidentId == second.Id);
        h.SelectedEvent = h.Events.Single(e => e.Event.IncidentId == first.Id);
        Assert.False(h.SelectedIncident!.IsDirty);
        Assert.Equal("typed, not saved", h.SelectedIncident.Description);
    }

    [Fact]
    public async Task AnAnnotationFileFromANewerVersionIsShownAsUnusableAndNeverOverwrittenOrMovedAside()
    {
        var rec = await HistoryFixture.RecordAsync(300, step: async (i, c) => { if (i == 150) await c.MarkIncidentAsync(); }, compress: false);
        var started = HistoryLoader.Load(rec.Storage, Dir, rec.ShortId).Info.StartedUtc;
        var sidecar = RecordingAnnotationStore.PathFor(Dir, RecordingFormat.Stamp(started), rec.ShortId);
        var newer = Encoding.UTF8.GetBytes("{\"schema\":\"euclens.annotations/2\",\"incidents\":{},\"somethingNew\":[1,2,3]}");
        rec.Storage.Files[sidecar] = newer; rec.Storage.Times[sidecar] = rec.Storage.Now;
        var h = CreateHistory(rec.Storage);
        await h.OpenAsync(Summary(rec.Storage, rec.ShortId)); await h.Settled;
        Assert.Contains(h.IssueLines, line => line.Contains("annotation file of this recording could not be used", StringComparison.Ordinal));

        h.SelectedEvent = h.Events.Single(e => e.IsIncident);
        h.SelectedIncident!.Description = "my note";
        await h.SelectedIncident.SaveCommand.ExecuteAsync();

        Assert.Contains("newer version", h.SelectedIncident.SaveMessage, StringComparison.Ordinal);
        Assert.Equal(newer, rec.Storage.Files[sidecar]);                                    // byte for byte unchanged
        Assert.DoesNotContain(rec.Storage.Names(Dir), n => n.Contains("unreadable", StringComparison.Ordinal));
        Assert.True(h.SelectedIncident.IsDirty);                                            // the edit is kept, not lost
    }

    [Fact]
    public void ConcurrentAnnotationSavesAllEndUpInTheFileWithNoLeftoverTemporaryFiles()
    {
        var storage = new MemoryStorage();
        var ids = Enumerable.Range(0, 40).Select(_ => Guid.NewGuid()).ToArray();
        Parallel.ForEach(ids, new ParallelOptions { MaxDegreeOfParallelism = 8 }, id =>
            RecordingAnnotationStore.Update(storage, Dir, "20261023-080000", "abcdef12", Guid.NewGuid(), id, new IncidentAnnotation(IncidentCategory.Freeze, "note " + id, DateTimeOffset.UtcNow)));
        var loaded = RecordingAnnotationStore.Load(storage, Dir, "20261023-080000", "abcdef12");
        Assert.All(ids, id => Assert.Equal("note " + id, loaded.For(id)!.Description));
        Assert.Equal(["EucLens-20261023-080000-abcdef12.annotations.json"], storage.Names(Dir));    // every temporary file was renamed away
    }

    [Fact]
    public void AnAnnotationFileFarLargerThanAnyRealOneIsNeitherParsedNorOverwritten()
    {
        var storage = new MemoryStorage();
        var path = RecordingAnnotationStore.PathFor(Dir, "20261023-080000", "abcdef12");
        var huge = new byte[(int)RecordingAnnotationStore.MaximumFileBytes + 1024];
        storage.Files[path] = huge; storage.Times[path] = storage.Now;
        var issues = new List<HistoryIssue>();
        var loaded = RecordingAnnotationStore.Load(storage, Dir, "20261023-080000", "abcdef12", issues);
        Assert.Empty(loaded.Incidents);
        Assert.Contains(issues, i => i.Kind == HistoryIssueKind.UnreadableAnnotations);
        Assert.Throws<RecordingFormatException>(() => RecordingAnnotationStore.Update(storage, Dir, "20261023-080000", "abcdef12", null, Guid.NewGuid(), new IncidentAnnotation(IncidentCategory.Other, "x", DateTimeOffset.UtcNow)));
        Assert.Equal(huge.Length, storage.Files[path].Length);
    }
}
