using System.Globalization;
using EucLens.App;
using EucLens.App.Presentation;
using EucLens.App.ViewModels;
using EucLens.Core;
using EucLens.Diagnostics;
using EucLens.Providers;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EucLens.Tests;

/// <summary>The Session History page: opening, range and zoom, charts on one time axis, incident selection, annotations, cancellation, release and repeated navigation.</summary>
public sealed class SessionHistoryViewModelTests
{
    private static readonly string Dir = HistoryFixture.Dir;

    private sealed class Desktop : IDesktopServices
    {
        public string? ExportPath { get; set; }
        public List<string> Opened { get; } = [];
        public string? SelectExportPath(bool json) => ExportPath;
        public bool ConfirmNewRecording() => true;
        public void ApplyTheme(bool dark) { }
        public Task SavePreferencesAsync(Preferences preferences) => Task.CompletedTask;
        public Task<Preferences?> LoadPreferencesAsync() => Task.FromResult<Preferences?>(null);
        public string? SelectFolder(string? initialDirectory) => null;
        public bool OpenPath(string path, bool selectFile = false) { Opened.Add(path); return true; }
    }

    private sealed record Rig(SessionHistoryViewModel History, BackgroundViewModel Background, Desktop Desktop, MemoryStorage Storage, SessionRecorder Recorder);

    private static Rig CreateRig(MemoryStorage storage, TimeZoneInfo? zone = null)
    {
        var desktop = new Desktop();
        var recorder = new SessionRecorder();
        var monitoring = new MonitoringService(new MonitoringCoordinator(), recorder);
        var background = new BackgroundViewModel(monitoring, recorder, desktop, storage: storage);
        return new(new SessionHistoryViewModel(background, desktop, recorder, storage, zone ?? TimeZoneInfo.Utc), background, desktop, storage, recorder);
    }

    /// <summary>Chart ranges are doubles of DateTime ticks (about 6e17, beyond 2^53): equal to within a few microseconds.</summary>
    private static void SameTime(long ticks, double? x) => Assert.InRange(Math.Abs(ticks - x!.Value), 0, 1_000);

    private static RecordingSummary Summary(MemoryStorage storage, string shortId) => RecordingCatalog.Scan(storage, Dir).Single(s => s.ShortId == shortId);

    private static async Task<(Rig Rig, HistoryFixture.Recorded Rec)> OpenAsync(int count = 1800, Func<int, SessionSnapshot>? make = null, Func<int, HistoryFixture.RecordingContext, Task>? step = null, bool compress = false, long segment = 64L << 20)
    {
        var rec = await HistoryFixture.RecordAsync(count, make, step ?? (async (i, c) => { if (i == count / 2) await c.MarkIncidentAsync(); }), segment: segment, compress: compress);
        var rig = CreateRig(rec.Storage);
        await rig.History.OpenAsync(Summary(rec.Storage, rec.ShortId));
        await rig.History.Settled;
        return (rig, rec);
    }

    [Fact]
    public async Task OpeningARecordingShowsItsOverviewTimelineAndOnlyTheChartsThatHaveData()
    {
        var (rig, rec) = await OpenAsync(make: i => HistoryFixture.Sample(i, fps: null));
        var h = rig.History;

        Assert.True(h.HasHistory); Assert.False(h.IsLoading); Assert.False(h.HasError); Assert.False(h.ShowPlaceholder);
        Assert.Equal("Complete", h.StateLabel);
        Assert.Equal("Healthy", h.StateIcon);
        Assert.Contains(h.Facts, f => f.Label == "Samples" && f.Value == 1800.ToString("N0", CultureInfo.CurrentCulture));
        Assert.Contains(h.Facts, f => f.Label == "Marked incidents" && f.Value == "1");
        Assert.Contains(h.Facts, f => f.Label == "Lost samples" && f.Value == "0");
        Assert.Contains(h.Facts, f => f.Label == "Data coverage" && f.Value.StartsWith("100", StringComparison.Ordinal));
        Assert.Contains(h.Facts, f => f.Label == "Recording state" && f.Value == "Complete");
        var ids = h.Charts.Select(c => c.Id).ToArray();
        Assert.Contains("rtt", ids); Assert.Contains("cpu", ids);
        Assert.DoesNotContain("fps", ids);                                     // no values in the recording: no chart, not a zero line
        Assert.Contains("Frames per second", h.UnavailableNote, StringComparison.Ordinal);
        Assert.Contains("never shown as zero", h.UnavailableNote, StringComparison.Ordinal);
        Assert.Equal(h.Charts.GroupBy(c => c.Group).Count(), h.Charts.Count(c => c.ShowGroup));   // one group header per group
        Assert.Equal(h.Loaded!.Events.Count, h.Markers.Count);
        Assert.Equal(h.Loaded.Events.Count, h.Events.Count);
        Assert.Equal(h.SessionStart, h.ViewStart); Assert.Equal(h.SessionEnd, h.ViewEnd);
        Assert.Equal("Entire session", h.SelectedPreset.Label);
        Assert.Contains("UTC", h.ZoneNote, StringComparison.Ordinal);
        GC.KeepAlive(rec);
    }

    [Fact]
    public async Task HistoryFindingsAnalyzeInTheBackgroundAndNavigateToMeasuredEvidence()
    {
        var (rig, _) = await OpenAsync(count: 360,
            make: i => HistoryFixture.Sample(i, rtt: i is >= 120 and < 210 ? 200 : 30));
        var h = rig.History;
        Assert.Contains(h.Findings.Observations, o => o.Observation.RuleId == "conn.rtt.high");
        var row = h.Findings.Observations.First(o => o.Observation.RuleId == "conn.rtt.high");
        Assert.Contains("observation", h.Findings.Summary, StringComparison.Ordinal);
        Assert.Contains("Measured conditions", h.Findings.Status, StringComparison.Ordinal);
        h.Findings.SelectedObservation = row;
        Assert.Equal(row, h.Findings.SelectedObservation);
        Assert.NotNull(h.Findings.SelectedObservation!.Observation.Primary);
        Assert.True(h.ViewStart <= row.Observation.Start);
        Assert.True(h.ViewEnd >= row.Observation.End);
        Assert.Contains("not a confirmed cause", row.Status, StringComparison.Ordinal);

        h.Release();
        Assert.Empty(h.Findings.Observations);
        Assert.Empty(h.Findings.Correlations);
        Assert.Null(h.Findings.SelectedObservation);
        Assert.False(h.HasHistory);
    }

    [Fact]
    public async Task EveryChartSharesTheSameTimeRangeAndStaysBounded()
    {
        var rec = await HistoryFixture.EightHours;
        var rig = CreateRig(rec.Storage);
        await rig.History.OpenAsync(Summary(rec.Storage, rec.ShortId));
        await rig.History.Settled;
        var h = rig.History;

        Assert.NotEmpty(h.Charts);
        Assert.All(h.Charts, row =>
        {
            Assert.InRange(row.Data.Points.Count, 2, SessionHistoryViewModel.MaximumChartPoints + 8);
            SameTime(h.ViewStart.UtcDateTime.Ticks, row.Data.XMin);
            SameTime(h.ViewEnd.UtcDateTime.Ticks, row.Data.XMax);
            Assert.Contains("P50", row.Stats, StringComparison.Ordinal); Assert.Contains("P95", row.Stats, StringComparison.Ordinal); Assert.Contains("Max", row.Stats, StringComparison.Ordinal);
            Assert.Contains($"{28_800.ToString("N0", CultureInfo.CurrentCulture)} valid", row.Stats, StringComparison.Ordinal);
            Assert.Contains("0 missing", row.Stats, StringComparison.Ordinal);
            Assert.Contains("peaks kept", row.Stats, StringComparison.Ordinal);
            Assert.Equal(row.Data.Points.OrderBy(p => p.At).Select(p => p.At), row.Data.Points.Select(p => p.At));
        });
        // Zooming into ten minutes shows every recorded sample at the higher resolution, on the same range in every chart.
        var middle = h.SessionStart + (h.SessionEnd - h.SessionStart) / 2;
        h.SetRange(middle, middle + TimeSpan.FromMinutes(10));
        await h.Settled;
        Assert.All(h.Charts, row =>
        {
            SameTime(h.ViewStart.UtcDateTime.Ticks, row.Data.XMin);
            SameTime(h.ViewEnd.UtcDateTime.Ticks, row.Data.XMax);
            Assert.Equal(600, row.Data.Points.Count);                            // ten minutes at 1 Hz (14,400 s to 14,999 s), nothing reduced
            Assert.DoesNotContain("reduced", row.Stats, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task ZoomPanResetAndPresetsKeepTheRangeInsideTheSessionAndInSync()
    {
        var (rig, _) = await OpenAsync(count: 4000);
        var h = rig.History;
        var full = h.SessionEnd - h.SessionStart;

        h.ZoomInCommand.Execute(null);
        Assert.Equal(full / 2, h.ViewEnd - h.ViewStart);
        Assert.Equal("Custom range", h.SelectedPreset.Label);                       // no longer one of the presets
        h.ZoomInCommand.Execute(null); h.ZoomInCommand.Execute(null);
        Assert.Equal(full / 8, h.ViewEnd - h.ViewStart);
        h.ZoomOutCommand.Execute(null);
        Assert.Equal(full / 4, h.ViewEnd - h.ViewStart);
        h.PanBy(10);                                                                 // far past the end: clamped
        Assert.Equal(h.SessionEnd, h.ViewEnd);
        Assert.Equal(full / 4, h.ViewEnd - h.ViewStart);
        h.PanBy(-100);
        Assert.Equal(h.SessionStart, h.ViewStart);
        h.SetRange(h.SessionStart + TimeSpan.FromSeconds(10), h.SessionStart + TimeSpan.FromSeconds(10.2));
        Assert.Equal(TimeSpan.FromSeconds(2), h.ViewEnd - h.ViewStart);              // never narrower than two seconds
        h.ResetZoomCommand.Execute(null);
        Assert.Equal((h.SessionStart, h.SessionEnd), (h.ViewStart, h.ViewEnd));
        Assert.Equal("Entire session", h.SelectedPreset.Label);

        h.SelectedPreset = h.RangePresets.Single(p => p.Value == HistoryRangePreset.Last15Minutes);
        Assert.Equal(h.SessionEnd, h.ViewEnd);
        Assert.Equal(TimeSpan.FromMinutes(15), h.ViewEnd - h.ViewStart);
        h.SelectedPreset = h.RangePresets.Single(p => p.Value == HistoryRangePreset.Last60Minutes);
        Assert.Equal(TimeSpan.FromMinutes(60), h.ViewEnd - h.ViewStart);
        await h.Settled;
        Assert.All(h.Charts, c => SameTime(h.ViewStart.UtcDateTime.Ticks, c.Data.XMin));
    }

    [Fact]
    public async Task ACustomRangeIsParsedInLocalTimeAndBadInputIsExplained()
    {
        var (rig, _) = await OpenAsync(count: 3600);
        var h = rig.History;
        h.CustomFrom = "08:10:00"; h.CustomTo = "08:20:30";
        h.ApplyCustomRangeCommand.Execute(null);
        Assert.Equal(new DateTimeOffset(2026, 10, 23, 8, 10, 0, TimeSpan.Zero), h.ViewStart);
        Assert.Equal(new DateTimeOffset(2026, 10, 23, 8, 20, 30, TimeSpan.Zero), h.ViewEnd);
        Assert.Equal("", h.RangeMessage);
        Assert.Equal("2026-10-23 08:10:00", h.CustomFrom);

        h.CustomFrom = "not a time";
        h.ApplyCustomRangeCommand.Execute(null);
        Assert.Contains("is not a time", h.RangeMessage, StringComparison.Ordinal);
        Assert.Equal(new DateTimeOffset(2026, 10, 23, 8, 10, 0, TimeSpan.Zero), h.ViewStart);   // unchanged

        h.CustomFrom = "08:30:00"; h.CustomTo = "08:20:00";
        h.ApplyCustomRangeCommand.Execute(null);
        Assert.Contains("after its start", h.RangeMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SelectingAnIncidentCentresTheViewOnItWithTheConfiguredContextAndAnalyzesIt()
    {
        var (rig, rec) = await OpenAsync(count: 3600, make: i => HistoryFixture.Sample(i, rtt: i is >= 1798 and <= 1806 ? 450 : 30 + i % 6),
            step: async (i, c) => { if (i == 1800) await c.MarkIncidentAsync(); if (i == 3000) await c.MarkIncidentAsync(); });
        var h = rig.History;
        var first = h.Loaded!.Incidents[0];

        h.SelectedEvent = h.Events.Single(e => e.IsIncident && e.Event.IncidentId == first.Id);

        Assert.NotNull(h.SelectedIncident);
        Assert.Equal(first.At - TimeSpan.FromMinutes(2), h.ViewStart);
        Assert.Equal(first.At + TimeSpan.FromMinutes(2), h.ViewEnd);
        Assert.Equal(first.At, h.ViewStart + (h.ViewEnd - h.ViewStart) / 2);
        await h.Settled;
        Assert.Contains("Round-trip time reached 450 ms", h.SelectedIncident!.Summary, StringComparison.Ordinal);
        Assert.Single(h.SelectedIncident.Changes);
        Assert.True(h.SelectedIncident.HasChanges);
        Assert.Contains("not proof of its cause", h.SelectedIncident.Summary, StringComparison.Ordinal);
        Assert.Contains("Active findings: none were recorded", h.SelectedIncident.RecordedState + "Active findings: none were recorded", StringComparison.Ordinal);
        Assert.Equal(4 * 60, h.Charts.First(c => c.Id == "rtt").Data.Points.Count);       // the 240 samples between 1,679.5 s and 1,919.5 s
        // Both samples next to the marker, every metric, with unavailable ones written out as N/A.
        Assert.Contains(h.SelectedIncident.Evidence, r => r.Sample.StartsWith("Before", StringComparison.Ordinal) && r.Metric == "Round-trip time" && r.Value == "450");
        Assert.Contains(h.SelectedIncident.Evidence, r => r.Sample.StartsWith("After", StringComparison.Ordinal));
        Assert.Contains(h.SelectedIncident.Evidence, r => r.Value == "N/A" && r.Status.Length > 0);

        // A wider context re-centres on the same marker.
        h.SelectedContext = h.ContextChoices.Single(c => c.Value == TimeSpan.FromMinutes(5));
        Assert.Equal(first.At - TimeSpan.FromMinutes(5), h.ViewStart);
        Assert.Equal(first.At + TimeSpan.FromMinutes(5), h.ViewEnd);
        GC.KeepAlive(rec);
    }

    [Fact]
    public async Task PreviousAndNextStepThroughTheIncidentsAndStopAtTheEnds()
    {
        var (rig, _) = await OpenAsync(count: 3600, step: async (i, c) => { if (i is 600 or 1800 or 3000) await c.MarkIncidentAsync(); });
        var h = rig.History;
        var incidents = h.Loaded!.Incidents;
        Assert.Equal(3, incidents.Count);

        Assert.True(h.NextIncidentCommand.CanExecute(null));
        h.NextIncidentCommand.Execute(null);                                    // from the centre of the whole session: the middle one is next
        Assert.Equal(incidents[2].Id, h.SelectedIncident!.Incident.Id);         // the centre (1,800 s) lies just after incident 2 (1,799.5 s)
        Assert.False(h.NextIncidentCommand.CanExecute(null));
        h.PreviousIncidentCommand.Execute(null);
        Assert.Equal(incidents[1].Id, h.SelectedIncident!.Incident.Id);
        h.PreviousIncidentCommand.Execute(null);
        Assert.Equal(incidents[0].Id, h.SelectedIncident!.Incident.Id);
        Assert.False(h.PreviousIncidentCommand.CanExecute(null));
        Assert.True(h.NextIncidentCommand.CanExecute(null));
        Assert.Equal(h.Events.Single(e => e.Event.IncidentId == incidents[0].Id), h.SelectedEvent);      // the list follows
    }

    [Fact]
    public async Task ClickingAMarkerOnTheStripSelectsItsTimelineEntry()
    {
        var (rig, _) = await OpenAsync(count: 600, step: async (i, c) => { if (i == 300) await c.MarkIncidentAsync(); });
        var h = rig.History;
        h.SelectedFilter = h.Filters.Single(f => f.Value == HistoryEventFilter.Findings);
        Assert.Empty(h.Events);
        h.ActivateMarker(h.Loaded!.Incidents[0].At);
        Assert.Equal("All events", h.SelectedFilter.Label);                     // the incident was hidden by the filter: it is shown again
        Assert.True(h.SelectedEvent!.IsIncident);
        Assert.Equal(h.Loaded.Incidents[0].Id, h.SelectedIncident!.Incident.Id);
        h.SelectedFilter = h.Filters.Single(f => f.Value == HistoryEventFilter.Incidents);
        Assert.Equal(1, h.Events.Count(e => e.IsIncident)); Assert.Equal(h.Events.Count, h.Events.Count(e => e.IsIncident));
    }

    [Fact]
    public async Task FilteringReplacesTheEventListWithOneNotificationKeepsTheSelectionWhileItIsListedAndClearsItOtherwise()
    {
        var (rig, _) = await OpenAsync(count: 600, step: async (i, c) => { if (i == 300) await c.MarkIncidentAsync(); });
        var h = rig.History;
        var all = h.Events.Count;
        var notifications = new List<System.Collections.Specialized.NotifyCollectionChangedAction>();
        h.Events.CollectionChanged += (_, e) => notifications.Add(e.Action);
        h.SelectedEvent = h.Events.Single(e => e.IsIncident);
        var incident = h.SelectedEvent;
        notifications.Clear();

        h.SelectedFilter = h.Filters.Single(f => f.Value == HistoryEventFilter.Incidents);
        Assert.Equal([System.Collections.Specialized.NotifyCollectionChangedAction.Reset], notifications);   // one notification, not one per entry
        Assert.Same(incident, h.SelectedEvent);                                                               // still listed: still selected
        Assert.Equal($"{h.Events.Count} of {all} timeline entries", h.EventsHeading);

        notifications.Clear();
        h.SelectedFilter = h.Filters.Single(f => f.Value == HistoryEventFilter.Findings);
        Assert.Equal([System.Collections.Specialized.NotifyCollectionChangedAction.Reset], notifications);
        Assert.Empty(h.Events);
        Assert.Null(h.SelectedEvent);                                                                         // excluded: the selection is cleared
        Assert.Equal($"0 of {all} timeline entries", h.EventsHeading);

        h.SelectedFilter = h.Filters[0];
        Assert.Equal(all, h.Events.Count);
        Assert.Equal($"{all} of {all} timeline entries", h.EventsHeading);
    }

    [Fact]
    public void ReplacingAThousandsOfEntriesRaisesOneCollectionNotification()
    {
        var items = new BulkObservableCollection<int> { 1, 2, 3 };
        var collection = 0; var properties = new List<string?>();
        items.CollectionChanged += (_, _) => collection++;
        ((System.ComponentModel.INotifyPropertyChanged)items).PropertyChanged += (_, e) => properties.Add(e.PropertyName);
        items.ReplaceAll(Enumerable.Range(0, 5005));
        Assert.Equal(1, collection);
        Assert.Equal(5005, items.Count);
        Assert.Equal(["Count", "Item[]"], properties);
        Assert.Equal(Enumerable.Range(0, 5005), items);
        items.ReplaceAll([]);
        Assert.Empty(items); Assert.Equal(2, collection);
    }

    [Fact]
    public async Task AnAnnotationIsSavedBesideTheRecordingShownInTheListAndFoundAgainWhenReopened()
    {
        var (rig, rec) = await OpenAsync(count: 600, step: async (i, c) => { if (i == 300) await c.MarkIncidentAsync(); });
        var h = rig.History;
        h.SelectedEvent = h.Events.Single(e => e.IsIncident);
        var incident = h.SelectedIncident!;
        Assert.False(incident.SaveCommand.CanExecute(null));                    // nothing changed yet

        incident.Category = HistoryIncidentViewModel.Categories.Single(c => c.Value == IncidentCategory.Freeze);
        incident.Description = "  The desktop froze while scrolling.  ";
        Assert.True(incident.SaveCommand.CanExecute(null));
        await incident.SaveCommand.ExecuteAsync();

        Assert.Contains("Saved beside the recording", incident.SaveMessage, StringComparison.Ordinal);
        Assert.Equal("The desktop froze while scrolling.", incident.Description);
        Assert.False(incident.SaveCommand.CanExecute(null));
        Assert.Equal("Freeze — The desktop froze while scrolling.", h.Events.Single(e => e.IsIncident).Annotation);
        Assert.Contains(rec.Storage.Names(Dir), n => n.EndsWith(".annotations.json", StringComparison.Ordinal));

        var again = CreateRig(rec.Storage);
        await again.History.OpenAsync(Summary(rec.Storage, rec.ShortId)); await again.History.Settled;
        Assert.Equal("Freeze — The desktop froze while scrolling.", again.History.Events.Single(e => e.IsIncident).Annotation);
        again.History.SelectedEvent = again.History.Events.Single(e => e.IsIncident);
        Assert.Equal(IncidentCategory.Freeze, again.History.SelectedIncident!.Category.Value);
        Assert.Single(RecordingCatalog.Scan(rec.Storage, Dir));                 // the recording is untouched and still lists once

        again.History.SelectedIncident.Description = new string('x', 501);
        // The text box limits input to 500; a longer value set in code is refused with the reason.
        await again.History.SelectedIncident.SaveCommand.ExecuteAsync();
        Assert.Contains("at most 500", again.History.SelectedIncident.SaveMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LeavingThePageCancelsWorkReleasesTheRecordingAndComingBackOpensItAgain()
    {
        var (rig, rec) = await OpenAsync(count: 900);
        var h = rig.History;
        var rows = h.Charts.ToArray();
        Assert.NotEmpty(rows);

        h.OnPageHidden();

        Assert.False(h.HasHistory); Assert.Null(h.Loaded); Assert.Empty(h.Charts); Assert.Empty(h.Events); Assert.Empty(h.Facts);
        Assert.All(rows, r => Assert.Empty(r.Data.Points));                        // chart points are dropped, not kept for later
        Assert.Null(h.SelectedIncident);
        Assert.False(h.IsLoading);

        h.OnPageShown();
        for (var wait = 0; wait < 200 && !h.HasHistory; wait++) await Task.Delay(25);
        Assert.True(h.HasHistory);
        await h.Settled;
        GC.KeepAlive(rec);
    }

    [Fact]
    public async Task SwitchingRecordingsCancelsTheEarlierLoadAndShowsOnlyTheLatest()
    {
        var storage = new MemoryStorage();
        var a = await HistoryFixture.RecordAsync(1500, compress: false, storage: storage);
        storage.Now = storage.Now.AddMinutes(5);
        var b = await HistoryFixture.RecordAsync(700, compress: false, storage: storage, make: i => HistoryFixture.Sample(i, rtt: 99));
        Assert.NotEqual(a.ShortId, b.ShortId);
        var rig = CreateRig(storage);

        var first = rig.History.OpenAsync(Summary(storage, a.ShortId));
        var second = rig.History.OpenAsync(Summary(storage, b.ShortId));
        await Task.WhenAll(first, second);
        await rig.History.Settled;

        Assert.Equal(700, rig.History.Loaded!.SampleCount);
        Assert.Contains(rig.History.Facts, f => f.Label == "Samples" && f.Value == 700.ToString("N0", CultureInfo.CurrentCulture));
        Assert.Equal(99, rig.History.Loaded.Statistics("rtt", rig.History.SessionStart, rig.History.SessionEnd).Maximum);
        Assert.False(rig.History.IsLoading);
        Assert.False(rig.History.HasError);
        // And back again.
        await rig.History.OpenAsync(Summary(storage, a.ShortId));
        Assert.Equal(1500, rig.History.Loaded!.SampleCount);
    }

    [Fact]
    public async Task ARecordingThatCannotBeReadIsExplainedAndShowsNothing()
    {
        var storage = new MemoryStorage();
        var stamp = RecordingFormat.Stamp(HistoryFixture.T0);
        var path = Path.Combine(Dir, $"EucLens-{stamp}-abcdef12-part001.jsonl");
        storage.Files[path] = "{\"t\":\"header\",\"schema\":\"euclens.recording/9\",\"startedUtc\":\"2026-10-23T08:00:00Z\"}\n"u8.ToArray(); storage.Times[path] = storage.Now;
        var rig = CreateRig(storage);
        var summary = new RecordingSummary("abcdef12", HistoryFixture.T0, null, TimeSpan.Zero, 10, 0, 0, 0, 0, false, "Interrupted", "RealSession", [path], Dir);

        await rig.History.OpenAsync(summary);

        Assert.False(rig.History.HasHistory);
        Assert.True(rig.History.HasError);
        Assert.Contains("unsupported format", rig.History.Error, StringComparison.Ordinal);
        Assert.False(rig.History.IsLoading);
        Assert.Empty(rig.History.Charts);
    }

    [Fact]
    public async Task ARecordingBeyondTheSampleLimitIsLabelledPartialEverywhereAndNeverComplete()
    {
        var rec = await HistoryFixture.RecordAsync(600, compress: false, step: async (i, c) => { if (i is 100 or 500) await c.MarkIncidentAsync(); });
        var rig = CreateRig(rec.Storage);
        rig.History.SampleLimit = 200;
        await rig.History.OpenAsync(Summary(rec.Storage, rec.ShortId)); await rig.History.Settled;
        var h = rig.History;

        Assert.True(h.IsPartial);
        Assert.Equal("Partial analysis", h.StateLabel);
        Assert.Equal("Warning", h.StateIcon);
        Assert.Contains("only its beginning is shown", h.StateNote, StringComparison.Ordinal);
        Assert.Contains("Nothing below describes the whole session", h.PartialNote, StringComparison.Ordinal);
        Assert.Contains($"of {600.ToString("N0", CultureInfo.CurrentCulture)} recorded", h.PartialNote, StringComparison.Ordinal);
        Assert.Contains(h.Facts, f => f.Label == "Duration analyzed" && f.Detail == "Beginning of the recording only");
        Assert.Contains(h.Facts, f => f.Label == "Last sample analyzed");
        Assert.DoesNotContain(h.Facts, f => f.Label is "End" or "Duration");
        Assert.Contains(h.Facts, f => f.Label == "Recording state" && f.Value == "Partial analysis" && f.Detail == "Not the whole recording");
        Assert.DoesNotContain(h.Facts, f => f.Value == "Complete");
        Assert.Contains(h.IssueLines, l => l.StartsWith("Problem:", StringComparison.Ordinal) && l.Contains("Only the first 200", StringComparison.Ordinal));
        Assert.Equal(200, h.Loaded!.SampleCount);
        Assert.Single(h.Loaded.Incidents);                                      // the second incident lies beyond the analyzed part and is not claimed
        Assert.Contains(h.Facts, f => f.Label == "Marked incidents" && f.Value == "1");
        Assert.True(h.SessionEnd - h.SessionStart < TimeSpan.FromSeconds(200));
        // A complete recording shows no notice.
        rig.History.SampleLimit = HistoryLoader.MaximumSamples;
        await rig.History.OpenAsync(Summary(rec.Storage, rec.ShortId)); await rig.History.Settled;
        Assert.False(h.IsPartial); Assert.Equal("", h.PartialNote); Assert.Equal("Complete", h.StateLabel);
    }

    [Fact]
    public async Task AnInterruptedRecordingIsNeverCalledComplete()
    {
        var rec = await HistoryFixture.RecordAsync(200, compress: false, finish: false);
        var path = Assert.Single(HistoryFixture.PartPaths(rec.Storage, rec.ShortId));
        rec.Storage.Files[path] = rec.Storage.Files[path][..^10];
        var rig = CreateRig(rec.Storage);
        await rig.History.OpenAsync(Summary(rec.Storage, rec.ShortId)); await rig.History.Settled;
        Assert.Equal("Interrupted", rig.History.StateLabel);
        Assert.Equal("Warning", rig.History.StateIcon);
        Assert.Contains("no end of recording was written", rig.History.StateNote, StringComparison.Ordinal);
        Assert.Contains(rig.History.Facts, f => f.Label == "Last data" && f.Detail == "Not a recorded end");
        Assert.DoesNotContain(rig.History.Facts, f => f.Label == "End");
        Assert.Contains(rig.History.IssueLines, l => l.StartsWith("Note:", StringComparison.Ordinal) && l.Contains("incomplete line", StringComparison.Ordinal));
        Assert.DoesNotContain("Complete", rig.History.StateLabel, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ThePointerReadoutShowsRecordedValuesAndNAInsideAGap()
    {
        var (rig, _) = await OpenAsync(count: 1200, make: i => HistoryFixture.Sample(i, rtt: 40 + i % 3), step: null,
            compress: false);
        var h = rig.History;
        h.SetHover(h.SessionStart + TimeSpan.FromSeconds(100));
        Assert.StartsWith("2026-10-23 08:01:40", h.HoverText, StringComparison.Ordinal);
        var rtt = h.Charts.Single(c => c.Id == "rtt");
        Assert.Contains("ms at 08:01:40", rtt.Readout, StringComparison.Ordinal);
        h.SetHover(null);
        Assert.Equal("", h.HoverText); Assert.All(h.Charts, c => Assert.Equal("", c.Readout));

        var storage = new MemoryStorage();
        var gap = await HistoryFixture.RecordAsync(1200, compress: false, storage: storage, skipSample: i => i is >= 400 and < 700);
        var rig2 = CreateRig(storage);
        await rig2.History.OpenAsync(Summary(storage, gap.ShortId)); await rig2.History.Settled;
        rig2.History.SetHover(rig2.History.SessionStart + TimeSpan.FromSeconds(550));
        Assert.All(rig2.History.Charts, c => Assert.StartsWith("N/A", c.Readout, StringComparison.Ordinal));
        Assert.NotEmpty(rig2.History.Gaps);
    }

    [Fact]
    public async Task ExportsWriteTheSeparateIncidentFileAndLeaveTheSessionExportsAlone()
    {
        var (rig, _) = await OpenAsync(count: 300, step: async (i, c) => { if (i == 150) await c.MarkIncidentAsync(); });
        var path = Path.Combine(Path.GetTempPath(), "euclens-history-export-" + Guid.NewGuid().ToString("N") + ".csv");
        try
        {
            rig.Desktop.ExportPath = path;
            await rig.History.ExportIncidentsCommand.ExecuteAsync();
            var lines = File.ReadAllLines(path);
            Assert.Equal(HistoryExport.Header, lines[0]);
            Assert.Equal(2, lines.Length);
            Assert.Contains("UserIncident", lines[1], StringComparison.Ordinal);
            Assert.Contains("session CSV and JSON exports are unchanged", rig.History.Message, StringComparison.Ordinal);
            rig.Desktop.ExportPath = null;                                       // the user cancels the dialog
            await rig.History.ExportTimelineCommand.ExecuteAsync();
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task OpeningAndLeavingThePageRepeatedlyReleasesTheChartsAndTheirLiveChartsObjects()
    {
        var rec = await HistoryFixture.RecordAsync(900, compress: false, step: async (i, c) => { if (i == 450) await c.MarkIncidentAsync(); });
        var rig = CreateRig(rec.Storage);
        var summary = Summary(rec.Storage, rec.ShortId);
        var weak = new List<WeakReference>();
        for (var round = 0; round < 20; round++)
        {
            await rig.History.OpenAsync(summary); await rig.History.Settled;
            weak.AddRange(CaptureChartObjects(rig.History));
            rig.History.SelectedEvent = rig.History.Events.First(e => e.IsIncident);
            rig.History.OnPageHidden();
        }
        for (var i = 0; i < 3; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); }
        Assert.NotEmpty(weak);
        Assert.All(weak, w => Assert.False(w.IsAlive));
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static List<WeakReference> CaptureChartObjects(SessionHistoryViewModel history)
    {
        // What a chart view does: build its own LiveCharts objects from the view model's ChartData (see LiveChart.Data).
        var weak = new List<WeakReference>();
        foreach (var row in history.Charts)
        {
            var objects = new ChartObjects(row.Data);
            objects.Line.Values = objects.Values;
            weak.Add(new WeakReference(objects)); weak.Add(new WeakReference(objects.Line)); weak.Add(new WeakReference(row.Data));
        }
        return weak;
    }

    [Fact]
    public async Task TheMainViewModelOffersTheHistoryPageAndAnalyzeOpensItWithTheRecording()
    {
        var rec = await HistoryFixture.RecordAsync(600, compress: false, step: async (i, c) => { if (i == 300) await c.MarkIncidentAsync(); });
        var folder = Path.Combine(Path.GetTempPath(), "euclens-history-settings-" + Guid.NewGuid().ToString("N"));
        var store = new BackgroundSettingsStore(Path.Combine(folder, "background.json"));
        await store.SaveAsync(BackgroundSettings.Defaults with { RecordingDirectory = Dir });
        try
        {
            var desktop = new Desktop(); var recorder = new SessionRecorder();
            var monitoring = new MonitoringService(new MonitoringCoordinator(), recorder);
            var background = new BackgroundViewModel(monitoring, recorder, desktop, store, rec.Storage);
            await using var provider = new MockMetricsProvider();
            var vm = new MainViewModel(provider, new DiagnosticIntelligenceEngine(DiagnosticRuleSet.Default()), recorder, desktop, NullLogger<MainViewModel>.Instance, monitoring: monitoring, background: background);
            await background.LoadAsync();
            await background.RefreshRecordingsAsync();

            Assert.Equal(7, vm.Pages.Count);
            Assert.Equal(["Dashboard", "Diagnostics", "Session Recorder", "Session History", "System Information", "Advanced Metrics", "Settings"], vm.Pages.Select(p => p.Name));
            var item = Assert.Single(background.Recordings);
            Assert.Contains("1 marked incident", item.Events, StringComparison.Ordinal);

            item.AnalyzeCommand.Execute(null);

            Assert.IsType<SessionHistoryPage>(vm.SelectedPage);
            for (var wait = 0; wait < 400 && !vm.History.HasHistory; wait++) await Task.Delay(25);
            Assert.True(vm.History.HasHistory);
            Assert.Equal(rec.ShortId, vm.History.Loaded!.Info.ShortId);
            // Leaving the page releases the recording.
            vm.SelectedPage = vm.Pages[0];
            Assert.False(vm.History.HasHistory);
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }

    [Fact]
    public void EveryEventKindHasItsOwnIconAndLabelAndFindingsKeepTheSeverityShapes()
    {
        var kinds = Enum.GetValues<HistoryEventKind>();
        string Tag(HistoryEventKind k, Severity? s = null) => HistoryEventKinds.IconTag(new HistoryEvent(k, HistoryFixture.T0, "t", Severity: s));
        var nonFinding = kinds.Where(k => !new HistoryEvent(k, HistoryFixture.T0, "t").IsFinding).ToArray();
        Assert.Equal(nonFinding.Length, nonFinding.Select(k => Tag(k)).Distinct().Count());      // no two kinds share a shape
        Assert.All(kinds, k => Assert.Contains(Tag(k, Severity.Warning), HistoryEventKinds.IconTags));
        Assert.Equal("FindingWarning", Tag(HistoryEventKind.FindingDetected, Severity.Warning));
        Assert.Equal("FindingCritical", Tag(HistoryEventKind.FindingEscalated, Severity.Critical));
        Assert.Equal("FindingRecovered", Tag(HistoryEventKind.FindingRecovered, Severity.Warning));
        Assert.Equal("Incident", Tag(HistoryEventKind.UserIncident));
        Assert.All(kinds, k => Assert.False(string.IsNullOrWhiteSpace(HistoryEventKinds.Label(k))));
        Assert.Equal(HistoryEventKinds.IconTags.Count, HistoryEventKinds.IconTags.Distinct().Count());
        // The shapes exist as resources.
        var foundations = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "../../../../../src/EucLens.App/Themes/Foundations.xaml"));
        var controls = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "../../../../../src/EucLens.App/Themes/Controls.xaml"));
        foreach (var tag in HistoryEventKinds.IconTags.Where(t => !t.StartsWith("Finding", StringComparison.Ordinal)))
            Assert.Contains($"x:Key=\"EventGeometry{tag}\"", foundations, StringComparison.Ordinal);
        foreach (var tag in HistoryEventKinds.IconTags) Assert.True(tag.StartsWith("Finding", StringComparison.Ordinal) || controls.Contains($"Value=\"{tag}\"", StringComparison.Ordinal) || tag == "Other", tag);
    }
}
