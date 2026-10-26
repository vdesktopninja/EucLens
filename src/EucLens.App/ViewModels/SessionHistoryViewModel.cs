using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text;
using EucLens.App.Presentation;
using EucLens.Core;
using Microsoft.Extensions.Logging;

namespace EucLens.App.ViewModels;

public enum HistoryRangePreset { EntireSession, Last60Minutes, Last15Minutes, Custom }

/// <summary>A marker on the timeline strip and the chart overlay.</summary>
public sealed record TimelineMarker(DateTimeOffset At, HistoryEventKind Kind, string IconTag, string Label);

/// <summary>One row of the incident timeline list.</summary>
public sealed class HistoryEventItem(HistoryEvent source, string time, string note) : ObservableObject
{
    private string annotation = note;
    public HistoryEvent Event { get; } = source;
    public string Time { get; } = time;
    public string IconTag { get; } = HistoryEventKinds.IconTag(source);
    public string KindLabel { get; } = HistoryEventKinds.Label(source.Kind);
    public string Title => Event.Title;
    public string Detail => Event.Detail;
    public bool HasDetail => Event.Detail.Length > 0;
    public bool IsIncident => Event.Kind == HistoryEventKind.UserIncident;
    /// <summary>The administrator's category and description, shown under the incident.</summary>
    public string Annotation { get => annotation; set { if (Set(ref annotation, value)) { Raise(nameof(HasAnnotation)); Raise(nameof(AutomationName)); } } }
    public bool HasAnnotation => annotation.Length > 0;
    public string AutomationName => $"{Time}, {KindLabel}, {Title}" + (HasAnnotation ? $", {Annotation}" : "");
}

/// <summary>One metric shown with the value that was recorded, or explicitly as not available.</summary>
public sealed record HistoryEvidenceRow(string Sample, string Metric, string Value, string Unit, string Status, string Source);

/// <summary>One chart of the history: a plain <see cref="ChartData"/> (the chart view builds its own LiveCharts objects) with its statistics and unit note.</summary>
public sealed class HistoryChartRow : ObservableObject
{
    private string stats = "", readout = "";
    public HistoryChartRow(HistoryChannelData channel, bool showGroup, TimeZoneInfo zone)
    {
        Id = channel.Channel.Id; Title = channel.Channel.Title; Group = HistoryChannels.GroupTitle(channel.Channel.Group); ShowGroup = showGroup;
        Unit = channel.Unit.Length == 0 ? "no unit recorded" : channel.Unit;
        UnitNote = channel.UnitVerified ? "" : HistoryChannels.UnverifiedWarning;
        Description = channel.Channel.Description;
        Data = new ChartData(Title, channel.Unit, capacity: 4000, axisName: null, yMin: channel.UnitVerified ? 0 : null, yMax: null, yMinStep: 1, xStepSeconds: 5, labelSize: 11)
        {
            TimeLabel = utc => HistoryTime.AxisLabel(new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).UtcTicks, zone),
            TooltipTimeLabel = utc => HistoryTime.Format(new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)), zone, milliseconds: true),
        };
    }
    public string Id { get; }
    public string Title { get; }
    public string Group { get; }
    public bool ShowGroup { get; }
    public string Unit { get; }
    public string UnitNote { get; }
    public bool HasUnitNote => UnitNote.Length > 0;
    public string Description { get; }
    public ChartData Data { get; }
    /// <summary>Statistics of the visible range: P50, P95, maximum, valid and missing samples.</summary>
    public string Stats { get => stats; set => Set(ref stats, value); }
    /// <summary>The recorded value nearest to the pointer, or N/A.</summary>
    public string Readout { get => readout; set => Set(ref readout, value); }
    public bool ShowNoData => Data.Points.Count == 0;
    public string AutomationName => $"{Title} in {Unit}. {Stats}" + (Emphasis == ChartEmphasis.Primary ? " Evidence for the selected finding." : Emphasis == ChartEmphasis.Supporting ? " Supporting measurement for the selected finding." : "");
    private ChartEmphasis emphasis;
    /// <summary>The selected finding's relation to this chart. Presentation only: the chart's data and range are untouched.</summary>
    public ChartEmphasis Emphasis
    {
        get => emphasis;
        internal set
        {
            if (!Set(ref emphasis, value)) return;
            Raise(nameof(IsPrimary)); Raise(nameof(IsSupporting)); Raise(nameof(IsEmphasized)); Raise(nameof(EmphasisLabel)); Raise(nameof(AutomationName));
        }
    }
    public bool IsEmphasized => emphasis != ChartEmphasis.None;
    public bool IsPrimary => emphasis == ChartEmphasis.Primary;
    public bool IsSupporting => emphasis == ChartEmphasis.Supporting;
    public string EmphasisLabel => emphasis == ChartEmphasis.Primary ? "Evidence for the selected finding" : emphasis == ChartEmphasis.Supporting ? "Supporting measurement" : "";
    internal void Raised() { Raise(nameof(ShowNoData)); Raise(nameof(AutomationName)); }
}

/// <summary>The selected user incident: administrator annotation, the recorded diagnostic state, observed changes and the evidence.</summary>
public sealed class HistoryIncidentViewModel : ObservableObject
{
    private Choice<IncidentCategory> category;
    private string description = "", saveMessage = "", summary = "Analyzing the recording around this marker…", state = "";
    private bool dirty, evidenceExpanded;
    private readonly Func<HistoryIncidentViewModel, Task> save;

    public HistoryIncidentViewModel(HistoryIncident incident, string time, IncidentAnnotation? annotation, Func<HistoryIncidentViewModel, Task> save)
    {
        Incident = incident; Time = time; this.save = save;
        category = Categories.First(c => c.Value == (annotation?.Category ?? IncidentCategory.Unspecified));
        description = annotation?.Description ?? "";
        SaveCommand = new(() => this.save(this), () => dirty);
    }

    public static IReadOnlyList<Choice<IncidentCategory>> Categories { get; } = Enum.GetValues<IncidentCategory>().Select(c => new Choice<IncidentCategory>(c, RecordingAnnotationStore.DisplayName(c))).ToArray();

    public HistoryIncident Incident { get; }
    public string Time { get; }
    public string Heading => $"Performance issue no. {Incident.Sequence} marked at {Time}";
    public Choice<IncidentCategory> Category { get => category; set { if (Set(ref category, value)) { dirty = true; SaveCommand.Refresh(); SaveMessage = ""; } } }
    public string Description { get => description; set { if (Set(ref description, value)) { dirty = true; SaveCommand.Refresh(); SaveMessage = ""; Raise(nameof(DescriptionCount)); } } }
    public string DescriptionCount => $"{description.Length} / {RecordingAnnotationStore.MaximumDescription}";
    public string SaveMessage { get => saveMessage; set => Set(ref saveMessage, value); }
    public AsyncCommand SaveCommand { get; }
    /// <summary>Deterministic description of the recording around the marker (see <see cref="IncidentAnalyzer"/>).</summary>
    public string Summary { get => summary; set => Set(ref summary, value); }
    /// <summary>Recorded state of the diagnostics as it was when the marker was written; never re-evaluated.</summary>
    public string RecordedState { get => state; set => Set(ref state, value); }
    public ObservableCollection<string> Changes { get; } = [];
    public ObservableCollection<string> Missing { get; } = [];
    public ObservableCollection<HistoryEvidenceRow> Evidence { get; } = [];
    public bool HasChanges => Changes.Count > 0;
    public bool IsEvidenceExpanded { get => evidenceExpanded; set => Set(ref evidenceExpanded, value); }
    /// <summary>True while the text or category differs from what was saved.</summary>
    public bool IsDirty => dirty;
    /// <summary>The page was left or another incident selected with unsaved edits: they are put back when the incident is selected again.</summary>
    internal void ApplyDraft(Choice<IncidentCategory> draftCategory, string draftDescription)
    {
        category = draftCategory; description = draftDescription; dirty = true;
        Raise(nameof(Category)); Raise(nameof(Description)); Raise(nameof(DescriptionCount)); SaveCommand.Refresh();
    }
    /// <summary>A save finished. The editor becomes clean only if it still shows exactly what was saved; anything typed meanwhile stays, unsaved and untouched.</summary>
    internal bool CompleteSave(IncidentCategory savedCategory, string savedText)
    {
        bool unchanged;
        try { unchanged = category.Value == savedCategory && RecordingAnnotationStore.Normalize(description) == savedText; }
        catch (ArgumentException) { unchanged = false; }
        if (unchanged) { description = savedText; dirty = false; Raise(nameof(Description)); Raise(nameof(DescriptionCount)); SaveCommand.Refresh(); }
        return unchanged;
    }
    internal void RaiseLists() { Raise(nameof(HasChanges)); }
}

/// <summary>
/// Session History: opens a saved recording, shows its overview, an interactive incident timeline and synchronized charts over one time range. Loading and aggregation
/// run off the UI thread and are cancelled when another recording is opened or the page is left; a loaded history is released when the page is left, so nothing
/// outlives it. The live charts and the sampling loop are never touched.
/// </summary>
public sealed class SessionHistoryViewModel : ObservableObject
{
    public const int MaximumChartPoints = 1200;
    private static readonly TimeSpan MinimumRange = TimeSpan.FromSeconds(2);

    private readonly BackgroundViewModel background;
    private readonly IDesktopServices desktop;
    private readonly ISessionRecorder recorder;
    private readonly IRecordingStorage storage;
    private TimeZoneInfo zone;
    private readonly ILogger? logger;

    private LoadedHistory? history;
    private RecordingAnnotations annotations = RecordingAnnotations.Empty;
    private RecordingSummary? opened;
    private CancellationTokenSource? loadCts, refreshCts, analysisCts;
    private int openVersion;
    private bool isLoading;
    private string message = "Choose a saved recording and select “Analyze recording”.", error = "", loadProgress = "";
    private DateTimeOffset viewStart, viewEnd;
    private Choice<HistoryRangePreset> preset;
    private Choice<TimeSpan> context;
    private Choice<HistoryEventFilter> filter;
    private RecordingItem? selectedRecording;
    private HistoryEventItem? selectedEvent;
    private HistoryIncidentViewModel? selectedIncident;
    // Unsaved annotation edits survive selecting another incident and leaving the page, for as long as the same recording is the one that is open.
    private readonly Dictionary<Guid, (Choice<IncidentCategory> Category, string Description)> drafts = [];
    private string draftsOwner = "";
    private readonly SemaphoreSlim saveGate = new(1, 1);
    private string customFrom = "", customTo = "", rangeText = "", rangeMessage = "", hoverText = "";
    private DateTimeOffset? hoverAt;
    private bool suppressSelection;
    private IReadOnlyList<HistoryEventItem> allEvents = [];
    private Task refreshTask = Task.CompletedTask;
    private Task analysisTask = Task.CompletedTask;
    private Task findingsTask = Task.CompletedTask;
    private CancellationTokenSource? findingsCts, exportCts;
    private bool exportingFindings;

    public SessionHistoryViewModel(BackgroundViewModel background, IDesktopServices desktop, ISessionRecorder recorder, IRecordingStorage? storage = null, TimeZoneInfo? zone = null, ILogger? logger = null)
    {
        this.background = background; this.desktop = desktop; this.recorder = recorder; this.storage = storage ?? new PhysicalRecordingStorage();
        this.zone = zone ?? TimeZoneInfo.Local; this.logger = logger;
        ChartsRuntime.EnsureInitialized();
        Findings = new HistoryFindingsViewModel(this.zone, NavigateToFinding, ClearEmphasis, ShowFullRecording);
        RangePresets = [new(HistoryRangePreset.EntireSession, "Entire session"), new(HistoryRangePreset.Last60Minutes, "Last 60 minutes"), new(HistoryRangePreset.Last15Minutes, "Last 15 minutes"), new(HistoryRangePreset.Custom, "Custom range")];
        ContextChoices = [new(TimeSpan.FromMinutes(1), "± 1 min"), new(TimeSpan.FromMinutes(2), "± 2 min"), new(TimeSpan.FromMinutes(5), "± 5 min"), new(TimeSpan.FromMinutes(10), "± 10 min")];
        Filters = [new(HistoryEventFilter.All, "All events"), new(HistoryEventFilter.Incidents, "User incidents"), new(HistoryEventFilter.Findings, "Diagnostic findings"), new(HistoryEventFilter.Other, "Recording and session")];
        preset = RangePresets[0]; context = ContextChoices[1]; filter = Filters[0];
        ZoomInCommand = new(() => ZoomBy(0.5), () => HasHistory);
        ZoomOutCommand = new(() => ZoomBy(2), () => HasHistory);
        ResetZoomCommand = new(ResetZoom, () => HasHistory);
        PreviousIncidentCommand = new(() => StepIncident(-1), () => CanStep(-1));
        NextIncidentCommand = new(() => StepIncident(1), () => CanStep(1));
        ApplyCustomRangeCommand = new(ApplyCustomRange, () => HasHistory);
        CloseCommand = new(Release);
        ExportIncidentsCommand = new(() => ExportAsync(incidentsOnly: true), () => HasHistory);
        ExportTimelineCommand = new(() => ExportAsync(incidentsOnly: false), () => HasHistory);
        ExportFindingsCsvCommand = new(() => ExportFindingsAsync(json: false), CanExportFindings);
        ExportFindingsJsonCommand = new(() => ExportFindingsAsync(json: true), CanExportFindings);
        OpenFolderCommand = new(() => { if (opened is not null) desktop.OpenPath(opened.Directory); });
        // The list is rebuilt whenever it is refreshed; keep the recording that is open selected in it.
        var uiContext = SynchronizationContext.Current;
        // After the list's own handlers have run: selecting an item the list has not processed yet could be undone by it.
        background.Recordings.CollectionChanged += (_, _) => { if (uiContext is null) ReselectOpened(); else uiContext.Post(_ => ReselectOpened(), null); };
    }

    // ---------- recordings ----------

    public ObservableCollection<RecordingItem> Recordings => background.Recordings;
    public RecordingItem? SelectedRecording
    {
        get => selectedRecording;
        set
        {
            if (!Set(ref selectedRecording, value) || value is null || suppressSelection) return;
            _ = OpenAsync(value.Summary);
        }
    }
    private void ReselectOpened()
    {
        if (opened is null) return;
        var match = Recordings.FirstOrDefault(r => r.Summary.ShortId == opened.ShortId);
        if (match is null || ReferenceEquals(match, selectedRecording)) return;
        suppressSelection = true; SelectedRecording = match; suppressSelection = false;
    }
    public AsyncCommand RefreshRecordingsCommand => background.RefreshRecordingsCommand;
    public RelayCommand OpenFolderCommand { get; }
    public RelayCommand CloseCommand { get; }

    // ---------- state ----------

    public bool HasHistory => history is not null;
    public bool IsLoading { get => isLoading; private set { if (Set(ref isLoading, value)) { Raise(nameof(ShowPlaceholder)); } } }
    public string Message { get => message; private set => Set(ref message, value); }
    /// <summary>A recording that could not be read, in words the user can act on.</summary>
    public string Error { get => error; private set { if (Set(ref error, value)) { Raise(nameof(HasError)); Raise(nameof(ShowPlaceholder)); } } }
    public bool HasError => error.Length > 0;
    public string LoadProgress { get => loadProgress; private set => Set(ref loadProgress, value); }
    public bool ShowPlaceholder => !HasHistory && !HasError && !IsLoading;
    public LoadedHistory? Loaded => history;
    public HistoryFindingsViewModel Findings { get; }
    /// <summary>
    /// Shows a finding: every chart goes to its real interval (recorded UTC times) with context on both sides, the chart(s) that carry its evidence are emphasized, and the
    /// readouts show the recorded values at the time of the measured extreme. The charts, their data and their shared range are the ones that were already there.
    /// </summary>
    private void NavigateToFinding(FindingTarget target)
    {
        if (history is null) return;
        var duration = target.End - target.Start;
        // Context before and after: half the finding's own length, at least 15 seconds, at most the context the user chose for incidents.
        var padding = TimeSpan.FromTicks(Math.Clamp(duration.Ticks / 2, TimeSpan.FromSeconds(15).Ticks, Math.Max(Context.Ticks, TimeSpan.FromSeconds(15).Ticks)));
        SetRange(target.Start - padding, target.End + padding);
        foreach (var row in Charts)
            row.Emphasis = target.PrimaryChannels.Contains(row.Id, StringComparer.Ordinal) ? ChartEmphasis.Primary
                : target.SupportingChannels.Contains(row.Id, StringComparer.Ordinal) ? ChartEmphasis.Supporting : ChartEmphasis.None;
        if (target.Peak is { } at) SetHover(at);
        Raise(nameof(HasEmphasis));
    }

    private void ClearEmphasis()
    {
        foreach (var row in Charts) row.Emphasis = ChartEmphasis.None;
        Raise(nameof(HasEmphasis));
    }

    private void ShowFullRecording()
    {
        ClearEmphasis();
        ResetZoom();
    }

    /// <summary>True while a finding's chart is emphasized.</summary>
    public bool HasEmphasis => Charts.Any(c => c.Emphasis != ChartEmphasis.None);
    public TimeZoneInfo Zone => zone;
    /// <summary>Validation seam: shows a recording in another time zone (for example one that crosses a clock change). Applies to the next recording opened.</summary>
    internal void UseTimeZone(TimeZoneInfo value) { zone = value; Findings.SetTimeZone(value); Raise(nameof(Zone)); Raise(nameof(ZoneNote)); }
    /// <summary>The most samples loaded; the limit is explained on the page when it is reached. Lowered only by the validation.</summary>
    internal int SampleLimit { get; set; } = HistoryLoader.MaximumSamples;
    /// <summary>True when only the beginning of the recording was analyzed.</summary>
    public bool IsPartial => history?.Info.IsPartial == true;
    public string PartialNote { get; private set; } = "";
    public string ZoneNote => $"Times are shown in {zone.DisplayName.Split(')').Last().Trim()} (UTC{HistoryTime.FormatOffset(zone.GetUtcOffset(DateTimeOffset.UtcNow))}); recordings store UTC.";

    // ---------- overview ----------

    public ObservableCollection<MetricCard> Facts { get; } = [];
    /// <summary>One line for the overview: state, duration, samples, protocol and data coverage. The full facts are one expander away.</summary>
    public string CompactSummary { get; private set; } = "";
    public string StateLabel { get; private set; } = "";
    public string StateIcon { get; private set; } = "NoData";
    public string StateNote { get; private set; } = "";
    public ObservableCollection<string> IssueLines { get; } = [];
    public bool HasIssues => IssueLines.Count > 0;
    public string UnavailableNote { get; private set; } = "";
    public bool HasUnavailableNote => UnavailableNote.Length > 0;

    // ---------- range ----------

    public IReadOnlyList<Choice<HistoryRangePreset>> RangePresets { get; }
    public IReadOnlyList<Choice<TimeSpan>> ContextChoices { get; }
    public IReadOnlyList<Choice<HistoryEventFilter>> Filters { get; }
    public DateTimeOffset ViewStart { get => viewStart; private set => Set(ref viewStart, value); }
    public DateTimeOffset ViewEnd { get => viewEnd; private set => Set(ref viewEnd, value); }
    public DateTimeOffset SessionStart => history?.Span.Start ?? default;
    public DateTimeOffset SessionEnd => history?.Span.End ?? default;
    public string RangeText { get => rangeText; private set => Set(ref rangeText, value); }
    public string RangeMessage { get => rangeMessage; private set => Set(ref rangeMessage, value); }
    public string CustomFrom { get => customFrom; set => Set(ref customFrom, value); }
    public string CustomTo { get => customTo; set => Set(ref customTo, value); }
    public Choice<HistoryRangePreset> SelectedPreset
    {
        get => preset;
        set
        {
            if (!Set(ref preset, value) || history is null) return;
            var (start, end) = history.Span;
            switch (value.Value)
            {
                case HistoryRangePreset.EntireSession: SetRange(start, end); break;
                case HistoryRangePreset.Last60Minutes: SetRange(end - TimeSpan.FromMinutes(60), end); break;
                case HistoryRangePreset.Last15Minutes: SetRange(end - TimeSpan.FromMinutes(15), end); break;
            }
        }
    }
    /// <summary>Time around a selected incident that the view shows.</summary>
    public Choice<TimeSpan> SelectedContext
    {
        get => context;
        set { if (Set(ref context, value) && selectedIncident is not null) SelectIncident(selectedIncident.Incident); }
    }
    public TimeSpan Context => context.Value;
    public RelayCommand ZoomInCommand { get; }
    public RelayCommand ZoomOutCommand { get; }
    public RelayCommand ResetZoomCommand { get; }
    public RelayCommand PreviousIncidentCommand { get; }
    public RelayCommand NextIncidentCommand { get; }
    public RelayCommand ApplyCustomRangeCommand { get; }

    // ---------- timeline and charts ----------

    public BulkObservableCollection<HistoryEventItem> Events { get; } = [];
    public IReadOnlyList<TimelineMarker> Markers { get; private set; } = [];
    public IReadOnlyList<HistoryGap> Gaps { get; private set; } = [];
    public ObservableCollection<HistoryChartRow> Charts { get; } = [];
    public Choice<HistoryEventFilter> SelectedFilter
    {
        get => filter;
        set { if (Set(ref filter, value)) RebuildEvents(); }
    }
    public HistoryEventItem? SelectedEvent
    {
        get => selectedEvent;
        set
        {
            if (!Set(ref selectedEvent, value) || value is null || suppressSelection) return;
            SelectEvent(value.Event);
        }
    }
    public HistoryIncidentViewModel? SelectedIncident { get => selectedIncident; private set { if (Set(ref selectedIncident, value)) { Raise(nameof(HasSelectedIncident)); RaiseSteps(); } } }
    public bool HasSelectedIncident => selectedIncident is not null;
    public string EventsHeading => history is null ? "" : $"{Events.Count} of {allEvents.Count} timeline entries";
    public string HoverText { get => hoverText; private set => Set(ref hoverText, value); }
    public DateTimeOffset? HoverAt => hoverAt;
    public AsyncCommand ExportIncidentsCommand { get; }
    public AsyncCommand ExportTimelineCommand { get; }
    public AsyncCommand ExportFindingsCsvCommand { get; }
    public AsyncCommand ExportFindingsJsonCommand { get; }
    private bool CanExportFindings() => history is not null && Findings.HasAnalysis && !exportingFindings;

    /// <summary>For tests and shutdown: the work of the latest refresh and analysis.</summary>
    internal Task Settled => Task.WhenAll(refreshTask, analysisTask, findingsTask);

    // ---------- page lifetime ----------

    /// <summary>The page was left: tasks are cancelled and the loaded recording and its chart points are released.</summary>
    public void OnPageHidden() => Release();

    /// <summary>The page was shown: the recordings are listed, and the recording that was open is opened again.</summary>
    public void OnPageShown()
    {
        _ = background.RefreshRecordingsAsync();
        if (opened is not null && history is null && !isLoading) _ = OpenAsync(opened);
    }

    /// <summary>Cancels loading, aggregation and analysis and drops the loaded recording; the selection is remembered so the page can open it again.</summary>
    public void Release()
    {
        KeepDraft(selectedIncident);
        openVersion++;
        Cancel(ref loadCts); Cancel(ref refreshCts); Cancel(ref analysisCts); Cancel(ref findingsCts); Cancel(ref exportCts);
        Findings.Clear();
        history = null; annotations = RecordingAnnotations.Empty;
        foreach (var row in Charts) row.Data.Clear();
        Charts.Clear(); Events.Clear(); allEvents = []; Facts.Clear(); IssueLines.Clear();
        Markers = []; Gaps = []; SelectedIncident = null; selectedEvent = null; Raise(nameof(SelectedEvent));
        IsLoading = false; LoadProgress = ""; HoverText = ""; hoverAt = null;
        RaiseAll();
    }

    private static void Cancel(ref CancellationTokenSource? source)
    {
        var current = source; source = null;
        if (current is null) return;
        try { current.Cancel(); } catch (ObjectDisposedException) { }
        current.Dispose();
    }

    // ---------- opening ----------

    public async Task OpenAsync(RecordingSummary summary)
    {
        Release();
        opened = summary;
        suppressSelection = true; SelectedRecording = Recordings.FirstOrDefault(r => r.Summary.ShortId == summary.ShortId); suppressSelection = false;
        var version = openVersion;
        var cts = new CancellationTokenSource(); loadCts = cts;
        // Read once, before the first await: leaving the page disposes the source, and its Token can no longer be read after that.
        var token = cts.Token;
        if (draftsOwner != summary.ShortId) { drafts.Clear(); draftsOwner = summary.ShortId; }
        Error = ""; IsLoading = true; Message = $"Loading {summary.Display}…"; LoadProgress = "";
        var progress = new Progress<HistoryLoadProgress>(p => { if (version == openVersion) LoadProgress = $"{p.Samples:N0} samples · part {p.PartsRead} of {p.Parts}"; });
        try
        {
            var activeId = recorder.IsRecording && recorder.SinkStatus?.RecordingId is { } id ? RecordingFormat.ShortId(id) : null;
            var loaded = await HistoryLoader.LoadAsync(storage, summary.Directory, summary.ShortId, activeId, progress, token, SampleLimit);
            var issues = loaded.Issues.ToList();
            var notes = await Task.Run(() => RecordingAnnotationStore.Load(storage, summary.Directory, RecordingFormat.Stamp(loaded.Info.StartedUtc), summary.ShortId, issues), token);
            if (version != openVersion || token.IsCancellationRequested) return;
            Show(loaded, notes, issues);
        }
        catch (OperationCanceledException) { /* another recording was opened or the page was left */ }
        catch (RecordingFormatException ex) { if (version == openVersion) { Error = ex.Message; Message = "This recording cannot be opened."; } }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger?.LogWarning(ex, "A recording could not be read.");
            if (version == openVersion) { Error = $"The recording could not be read: {ex.Message}"; Message = "This recording cannot be opened."; }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Whatever else a damaged or hostile file provokes: the page shows an error instead of an unobserved failure and an endless "Loading…".
            logger?.LogWarning(ex, "A recording could not be opened.");
            if (version == openVersion) { Error = $"The recording could not be opened: {ex.Message}"; Message = "This recording cannot be opened."; }
        }
        finally { if (version == openVersion) { IsLoading = false; LoadProgress = ""; } }
    }

    private void Show(LoadedHistory loaded, RecordingAnnotations notes, List<HistoryIssue> issues)
    {
        history = loaded; annotations = notes;
        var info = loaded.Info;
        Message = $"{info.ShortId} · {StateWords(info.State)}";
        BuildFacts(loaded, issues);
        BuildEvents(loaded);
        Markers = loaded.Events.Select(e => new TimelineMarker(e.At, e.Kind, HistoryEventKinds.IconTag(e), HistoryEventKinds.Label(e.Kind))).ToArray();
        Gaps = loaded.Gaps;
        var previousGroup = (HistoryGroup?)null;
        foreach (var channel in loaded.Channels.OrderBy(c => c.Channel.Group).ThenBy(c => HistoryChannels.All.ToList().IndexOf(c.Channel)))
        {
            Charts.Add(new HistoryChartRow(channel, previousGroup != channel.Channel.Group, zone));
            previousGroup = channel.Channel.Group;
        }
        UnavailableNote = loaded.UnavailableChannels.Count == 0 ? "" :
            "Not in this recording: " + string.Join("; ", loaded.UnavailableChannels.Select(c => $"{c.Channel.Title} ({Trim(c.UnavailableReason)})")) + ". Missing measurements are never shown as zero.";
        var (start, end) = loaded.Span;
        ViewStart = start; ViewEnd = end;
        RaiseAll();
        SetRange(start, end);
        StartFindings(loaded);
    }

    private void StartFindings(LoadedHistory loaded)
    {
        Cancel(ref findingsCts);
        var cts = new CancellationTokenSource(); findingsCts = cts;
        var token = cts.Token;
        Findings.Loading();
        RaiseSteps();                                              // nothing to export while the analysis runs
        findingsTask = AnalyzeFindingsAsync(loaded, token);
    }

    private async Task AnalyzeFindingsAsync(LoadedHistory loaded, CancellationToken token)
    {
        try
        {
            var result = await AnomalyDetectionEngine.AnalyzeAsync(loaded, cancellationToken: token);
            if (!token.IsCancellationRequested && ReferenceEquals(loaded, history)) { Findings.Show(result, loaded.Info); RaiseSteps(); }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "History anomaly analysis failed.");
            if (!token.IsCancellationRequested && ReferenceEquals(loaded, history)) Findings.Failed(ex.Message);
        }
    }

    private static string Trim(string? reason) => string.IsNullOrWhiteSpace(reason) ? "no valid values" : reason.TrimEnd('.');

    /// <summary>
    /// The protocols of a recording. "Unknown protocol" is not a protocol: the Horizon provider records it for any moment in which no Blast counter instance was found for the
    /// session (not yet detected, session inactive or disconnected). It is shown as a detection state next to the protocol that was detected, never as a second protocol.
    /// </summary>
    internal static string ProtocolSummary(HistoryInfo info)
    {
        if (info.Protocols.Count == 0) return "Not recorded";
        var known = info.Protocols.Where(p => p != UnknownProtocol).ToArray();
        if (known.Length == 0) return "Not detected";
        return info.Protocols.Contains(UnknownProtocol) ? string.Join(", ", known) + " (not detected during part of the recording)" : string.Join(", ", known);
    }

    internal static string ProtocolNote(HistoryInfo info)
    {
        if (!info.Protocols.Contains(UnknownProtocol)) return "";
        return info.Protocols.Count == 1 ? "No Blast counters were found for this session: nothing is claimed about the protocol"
            : "At times no Blast counters were found (not yet detected, session inactive or disconnected)";
    }

    private static readonly string UnknownProtocol = HistoryLoader.ProtocolName(RemoteProtocol.Unknown);

    private static string StateWords(RecordingCompleteness state) => state switch
    {
        RecordingCompleteness.Complete => "Complete", RecordingCompleteness.InProgress => "Recording in progress", RecordingCompleteness.Partial => "Partial analysis", _ => "Interrupted"
    };

    private void BuildFacts(LoadedHistory loaded, List<HistoryIssue> issues)
    {
        var info = loaded.Info;
        Facts.Clear();
        string T(DateTimeOffset at) => HistoryTime.Format(at, zone, withDate: true);
        StateLabel = StateWords(info.State);
        StateIcon = info.State == RecordingCompleteness.Complete ? "Healthy" : info.State == RecordingCompleteness.InProgress ? "Info" : "Warning";
        StateNote = info.State switch
        {
            RecordingCompleteness.Complete => $"Complete: the recording ended properly ({info.EndReason.ToLowerInvariant()}).",
            RecordingCompleteness.InProgress => "Recording in progress: this recording is still being written. What is on disk now is shown; reopen it to see more.",
            RecordingCompleteness.Partial => $"Partial analysis: {(info.SampleLimit > 0 ? $"this recording holds more than the {info.SampleLimit:N0} samples EUC Lens analyzes" : info.LimitReason)}, so only its beginning is shown. Everything on this page (charts, statistics, incidents, counts) covers only that beginning, not the whole session.",
            _ => "Interrupted: no end of recording was written (a crash, a power loss or a forced exit). The data up to the last complete line is shown; the real end time is not known."
        };
        PartialNote = info.IsPartial
            ? $"Partial analysis: only the first {info.Samples:N0} samples ({HistoryTime.FormatSpan(info.Duration)}) are shown{(opened is { Samples: > 0 } o ? $" of {o.Samples:N0} recorded" : "")}. Events and incidents after {T(info.EndedUtc)} are not listed. Nothing below describes the whole session."
            : "";
        Raise(nameof(PartialNote)); Raise(nameof(IsPartial));
        Facts.Add(new("Start", T(info.StartedUtc), ""));
        Facts.Add(new(info.HasEnded ? "End" : info.IsPartial ? "Last sample analyzed" : "Last data", T(info.EndedUtc), info.HasEnded ? "" : info.IsPartial ? "Not the end of the recording" : "Not a recorded end"));
        Facts.Add(new(info.IsPartial ? "Duration analyzed" : "Duration", HistoryTime.FormatSpan(info.Duration), info.IsPartial ? "Beginning of the recording only" : ""));
        Facts.Add(new("Data source", info.IsDemo ? "Demo data" : "Real session", info.AppVersion.Length > 0 ? $"EUC Lens {info.AppVersion}" : ""));
        Facts.Add(new("Protocol", ProtocolSummary(info), ProtocolNote(info)));
        Facts.Add(new("Samples", info.Samples.ToString("N0", CultureInfo.CurrentCulture), $"{info.Parts} recording part(s)"));
        Facts.Add(new("Diagnostic warnings", info.Warnings.ToString(CultureInfo.CurrentCulture), "Recorded by the diagnostic engine"));
        Facts.Add(new("Critical findings", info.Criticals.ToString(CultureInfo.CurrentCulture), "Recorded by the diagnostic engine"));
        Facts.Add(new("Marked incidents", info.Incidents.ToString(CultureInfo.CurrentCulture), "Marked by the user"));
        Facts.Add(new("Lost samples", info.LostSamples.ToString("N0", CultureInfo.CurrentCulture), info.LostSamples > 0 ? "Counted, never silently dropped" : "None recorded"));
        Facts.Add(new("Data coverage", info.DataCoverage.ToString("P1", CultureInfo.CurrentCulture),
            info.Gaps == 0 ? "No gaps between samples" : $"{info.Gaps} gap(s), {HistoryTime.FormatSpan(info.GapTime)} without samples"));
        Facts.Add(new("Recording state", StateLabel, info.State == RecordingCompleteness.Complete ? info.EndReason : info.IsPartial ? "Not the whole recording" : "Not complete"));
        IssueLines.Clear();
        foreach (var issue in issues) IssueLines.Add((issue.IsProblem ? "Problem: " : "Note: ") + issue.Message);
        var protocol = info.Protocols.Where(p => p != UnknownProtocol).ToArray();
        CompactSummary = $"{StateLabel} · {HistoryTime.FormatSpan(info.Duration)} · {info.Samples.ToString("N0", CultureInfo.InvariantCulture)} samples · " +
            (protocol.Length == 0 ? "protocol not detected" : string.Join(", ", protocol)) + $" · {(info.DataCoverage * 100).ToString("0.0", CultureInfo.InvariantCulture)}% data coverage";
        Raise(nameof(HasIssues)); Raise(nameof(StateLabel)); Raise(nameof(StateIcon)); Raise(nameof(StateNote)); Raise(nameof(CompactSummary));
    }

    private void BuildEvents(LoadedHistory loaded)
    {
        var multiDay = loaded.Info.Duration > TimeSpan.FromHours(20) || HistoryTime.ToLocal(loaded.Span.Start, zone).Date != HistoryTime.ToLocal(loaded.Span.End, zone).Date;
        allEvents = loaded.Events.Select(e =>
        {
            var note = e.IncidentId is { } id && annotations.For(id) is { } a ? AnnotationText(a) : "";
            return new HistoryEventItem(e, HistoryTime.Format(e.At, zone, withDate: multiDay), note);
        }).ToArray();
        RebuildEvents();
    }

    private static string AnnotationText(IncidentAnnotation a) =>
        a.Category == IncidentCategory.Unspecified && a.Description.Length == 0 ? "" : $"{RecordingAnnotationStore.DisplayName(a.Category)}{(a.Description.Length > 0 ? " — " + a.Description : "")}";

    private void RebuildEvents()
    {
        var keep = selectedEvent?.Event;
        suppressSelection = true;
        // One Reset for the whole list: thousands of single additions would each be handled by the list control.
        Events.ReplaceAll(allEvents.Where(i => HistoryEventFilters.Matches(filter.Value, i.Event)));
        selectedEvent = keep is null ? null : Events.FirstOrDefault(i => ReferenceEquals(i.Event, keep));
        suppressSelection = false;
        Raise(nameof(SelectedEvent)); Raise(nameof(EventsHeading));
    }

    // ---------- range ----------

    /// <summary>Sets the visible range for every chart at once. The range is kept inside the session, at least two seconds wide, and in UTC.</summary>
    public void SetRange(DateTimeOffset start, DateTimeOffset end)
    {
        if (history is null) return;
        var (first, last) = history.Span;
        if (last <= first) last = first + MinimumRange;
        start = start.ToUniversalTime(); end = end.ToUniversalTime();
        if (end < start) (start, end) = (end, start);
        if (end - start < MinimumRange)
        {
            var center = start + (end - start) / 2;
            start = center - MinimumRange / 2; end = center + MinimumRange / 2;
        }
        var width = end - start;
        if (width >= last - first) { start = first; end = last; }
        else
        {
            if (start < first) { start = first; end = start + width; }
            if (end > last) { end = last; start = end - width; }
        }
        ViewStart = start; ViewEnd = end;
        UpdateRangeTexts();
        UpdatePreset();
        RaiseSteps();
        RequestRefresh();
    }

    private void UpdateRangeTexts()
    {
        RangeText = $"Showing {HistoryTime.Format(viewStart, zone, withDate: true)} to {HistoryTime.Format(viewEnd, zone, withDate: true)} ({HistoryTime.FormatSpan(viewEnd - viewStart)})";
        CustomFrom = HistoryTime.ToLocal(viewStart, zone).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        CustomTo = HistoryTime.ToLocal(viewEnd, zone).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        RangeMessage = "";
    }

    private void UpdatePreset()
    {
        if (history is null) return;
        var (first, last) = history.Span;
        var match = viewStart == first && viewEnd == last ? HistoryRangePreset.EntireSession
            : viewEnd == last && viewEnd - viewStart == TimeSpan.FromMinutes(60) ? HistoryRangePreset.Last60Minutes
            : viewEnd == last && viewEnd - viewStart == TimeSpan.FromMinutes(15) ? HistoryRangePreset.Last15Minutes
            : HistoryRangePreset.Custom;
        var next = RangePresets.First(p => p.Value == match);
        if (!ReferenceEquals(next, preset)) { preset = next; Raise(nameof(SelectedPreset)); }
    }

    public void ZoomBy(double factor, DateTimeOffset? anchor = null)
    {
        if (history is null) return;
        var width = viewEnd - viewStart;
        var at = anchor ?? viewStart + width / 2;
        var ratio = width.Ticks == 0 ? 0.5 : Math.Clamp((at - viewStart).Ticks / (double)width.Ticks, 0, 1);
        var next = TimeSpan.FromTicks(Math.Max(MinimumRange.Ticks, (long)(width.Ticks * factor)));
        var start = at - TimeSpan.FromTicks((long)(next.Ticks * ratio));
        SetRange(start, start + next);
    }

    public void PanBy(double fraction)
    {
        if (history is null) return;
        var delta = TimeSpan.FromTicks((long)((viewEnd - viewStart).Ticks * fraction));
        SetRange(viewStart + delta, viewEnd + delta);
    }

    public void ResetZoom() { if (history is not null) SetRange(history.Span.Start, history.Span.End); }

    private void ApplyCustomRange()
    {
        if (history is null) return;
        var fromOk = TryParseLocal(customFrom, viewStart, out var from, out var fromError);
        var toOk = TryParseLocal(customTo, viewEnd, out var to, out var toError);
        if (!fromOk || !toOk)
        {
            RangeMessage = fromError ?? toError ?? "";
            return;
        }
        if (to <= from) { RangeMessage = "The end of the range must be after its start."; return; }
        SetRange(from, to);
    }

    private bool TryParseLocal(string text, DateTimeOffset reference, out DateTimeOffset utc, out string? error)
    {
        utc = default; error = null;
        var trimmed = text.Trim();
        var formats = new[] { "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm", "HH:mm:ss", "HH:mm" };
        if (!DateTime.TryParseExact(trimmed, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            error = $"“{trimmed}” is not a time. Use yyyy-MM-dd HH:mm:ss or HH:mm:ss (local time).";
            return false;
        }
        if (trimmed.Length <= 8) parsed = HistoryTime.ToLocal(reference, zone).Date + parsed.TimeOfDay;
        try { utc = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(parsed, DateTimeKind.Unspecified), zone), TimeSpan.Zero); return true; }
        catch (ArgumentException) { error = $"{trimmed} does not exist in this time zone (the clock was set forward)."; return false; }
    }

    // ---------- events and incidents ----------

    /// <summary>A marker on the timeline strip was clicked: selects the matching timeline entry (a user incident first) and centres the view on it.</summary>
    public void ActivateMarker(DateTimeOffset at)
    {
        var item = allEvents.Where(i => i.Event.At == at).OrderByDescending(i => i.IsIncident).FirstOrDefault();
        if (item is null) return;
        if (!Events.Contains(item)) { filter = Filters[0]; Raise(nameof(SelectedFilter)); RebuildEvents(); }
        suppressSelection = true; selectedEvent = item; suppressSelection = false; Raise(nameof(SelectedEvent));
        SelectEvent(item.Event);
    }

    private void SelectEvent(HistoryEvent e)
    {
        if (history is null) return;
        if (e.Kind == HistoryEventKind.UserIncident && history.Incidents.FirstOrDefault(i => i.Id == e.IncidentId) is { } incident) { SelectIncident(incident); return; }
        KeepDraft(selectedIncident);
        SelectedIncident = null;
        CenterOn(e.At);
    }

    /// <summary>Remembers unsaved annotation edits of an incident that is about to be replaced; a clean editor leaves nothing behind.</summary>
    private void KeepDraft(HistoryIncidentViewModel? vm)
    {
        if (vm is null) return;
        if (vm.IsDirty) drafts[vm.Incident.Id] = (vm.Category, vm.Description); else drafts.Remove(vm.Incident.Id);
    }

    private void CenterOn(DateTimeOffset at) => SetRange(at - Context, at + Context);

    /// <summary>Centres the view on a marker with the configured context and starts the analysis of what was recorded around it.</summary>
    public void SelectIncident(HistoryIncident incident)
    {
        if (history is null) return;
        Cancel(ref analysisCts);
        var cts = new CancellationTokenSource(); analysisCts = cts;
        KeepDraft(selectedIncident);
        var vm = new HistoryIncidentViewModel(incident, HistoryTime.Format(incident.At, zone, withDate: true, milliseconds: true), annotations.For(incident.Id), SaveAnnotationAsync);
        if (drafts.TryGetValue(incident.Id, out var draft)) vm.ApplyDraft(draft.Category, draft.Description);
        var titles = history.Events.Where(e => e.RuleId is not null).GroupBy(e => e.RuleId!).ToDictionary(g => g.Key, g => g.Last().Title);
        vm.RecordedState = DescribeState(incident, titles);
        foreach (var row in BuildEvidence(incident)) vm.Evidence.Add(row);
        SelectedIncident = vm;
        var item = allEvents.FirstOrDefault(i => i.Event.IncidentId == incident.Id);
        // The incident's row may be hidden by the filter: the list then shows no selection rather than a stale one.
        suppressSelection = true; selectedEvent = item is not null && Events.Contains(item) ? item : null; suppressSelection = false; Raise(nameof(SelectedEvent));
        CenterOn(incident.At);
        var h = history; var window = Context;
        analysisTask = AnalyzeAsync(h, incident, window, vm, cts.Token);
    }

    private async Task AnalyzeAsync(LoadedHistory h, HistoryIncident incident, TimeSpan window, HistoryIncidentViewModel vm, CancellationToken cancellationToken)
    {
        try
        {
            var analysis = await Task.Run(() => IncidentAnalyzer.Analyze(h, incident, window, zone), cancellationToken);
            if (cancellationToken.IsCancellationRequested || !ReferenceEquals(h, history) || !ReferenceEquals(selectedIncident, vm)) return;
            vm.Summary = analysis.Summary;
            vm.Changes.Clear(); foreach (var change in analysis.Changes) vm.Changes.Add(change.Statement);
            vm.Missing.Clear(); foreach (var m in analysis.Missing) vm.Missing.Add(m);
            vm.RaiseLists();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "The recording around an incident could not be analyzed.");
            if (!cancellationToken.IsCancellationRequested && ReferenceEquals(h, history) && ReferenceEquals(selectedIncident, vm)) vm.Summary = $"The recording around this marker could not be analyzed: {ex.Message}";
        }
    }

    private static string DescribeState(HistoryIncident incident, Dictionary<string, string> titles)
    {
        var state = incident.RecordedState;
        if (state is null) return "No diagnostic state was recorded with the samples around this marker.";
        var lines = state.Assessments.Select(a => $"{a.Domain}: {a.StateLabel} ({a.Coverage.ToString().ToLowerInvariant()} coverage)").ToList();
        lines.Add(state.ActiveFindings.Count == 0 ? "Active findings: none were recorded."
            : "Active findings: " + string.Join("; ", state.ActiveFindings.Select(id => titles.TryGetValue(id, out var title) ? $"{TitleOnly(title)} ({id})" : id)) + ".");
        lines.Add("This is the state as recorded at the time; it is not re-evaluated with later thresholds.");
        return string.Join(Environment.NewLine, lines);
    }

    private static string TitleOnly(string title) { var i = title.IndexOf(": ", StringComparison.Ordinal); return i >= 0 ? title[(i + 2)..] : title; }

    private IEnumerable<HistoryEvidenceRow> BuildEvidence(HistoryIncident incident)
    {
        foreach (var (label, sample) in new[] { ("Before", incident.Before), ("After", incident.After) })
        {
            if (sample is null) { yield return new(label, "Sample", "N/A", "", "No sample was recorded on this side of the marker", ""); continue; }
            var tag = $"{label} · {HistoryTime.Format(sample.Timestamp, zone, milliseconds: true)}";
            foreach (var (name, metric) in EvidenceMetrics(sample)) yield return Row(tag, name, metric);
        }
    }

    private static IEnumerable<(string Name, Metric Metric)> EvidenceMetrics(SessionSnapshot s)
    {
        yield return ("Round-trip time", s.Connection.RoundTripTime); yield return ("Network latency", s.Connection.NetworkLatency);
        yield return ("Available bandwidth", s.Connection.AvailableBandwidth); yield return ("Bandwidth consumption", s.Connection.BandwidthConsumption);
        yield return ("Packet loss", s.Connection.PacketLoss); yield return ("Frames per second", s.Graphics.FramesPerSecond); yield return ("Dropped frames", s.Graphics.DroppedFramesPercent);
        yield return ("CPU utilization", s.System.CpuUtilization); yield return ("Memory utilization", s.System.RamUtilization); yield return ("Disk activity", s.System.DiskActivity);
        yield return ("GPU utilization", s.Gpu.Utilization); yield return ("GPU memory utilization", s.Gpu.VramUtilization);
        foreach (var (key, metric) in (s.BlastMetrics ?? new Dictionary<string, Metric>()).OrderBy(k => k.Key, StringComparer.Ordinal)) yield return ("Blast: " + key, metric);
    }

    private static HistoryEvidenceRow Row(string sample, string name, Metric m) => new(sample, name,
        HistoryChannels.TryGetValid(m, out var v) ? v.ToString("0.##", CultureInfo.InvariantCulture) : "N/A", m.Unit,
        HistoryChannels.TryGetValid(m, out _) ? (HistoryChannels.IsUnverified(m.Unit) ? "Raw counter, unit unverified" : "Available") : (m.UnavailableReason ?? m.Error ?? m.Status.ToString()), m.Source ?? "");

    private async Task SaveAnnotationAsync(HistoryIncidentViewModel vm)
    {
        if (history is not { } h || opened is null) return;
        try
        {
            // What is saved is what the editor showed when Save was pressed; typing continues while the file is written.
            var savedCategory = vm.Category.Value;
            var text = RecordingAnnotationStore.Normalize(vm.Description);
            var note = savedCategory == IncidentCategory.Unspecified && text.Length == 0 ? null : new IncidentAnnotation(savedCategory, text, DateTimeOffset.UtcNow);
            var stamp = RecordingFormat.Stamp(h.Info.StartedUtc);
            RecordingAnnotations updated;
            await saveGate.WaitAsync().ConfigureAwait(true);
            try { updated = await Task.Run(() => RecordingAnnotationStore.Update(storage, h.Info.Directory, stamp, h.Info.ShortId, h.Info.RecordingId, vm.Incident.Id, note)); }
            finally { saveGate.Release(); }
            if (ReferenceEquals(h, history)) annotations = updated;   // another recording may have been opened meanwhile
            var item = allEvents.FirstOrDefault(i => i.Event.IncidentId == vm.Incident.Id);
            if (item is not null) item.Annotation = note is null ? "" : AnnotationText(note);
            var clean = vm.CompleteSave(savedCategory, text);
            if (clean) drafts.Remove(vm.Incident.Id);
            vm.SaveMessage = !clean ? "Saved, but you changed the text meanwhile: the newer changes are not saved yet."
                : note is null ? "Annotation removed." : $"Saved beside the recording ({RecordingAnnotationStore.DisplayName(note.Category)}).";
        }
        catch (ArgumentException ex) { vm.SaveMessage = ex.Message; }
        catch (RecordingFormatException)
        {
            vm.SaveMessage = "These annotations were written by a newer version of EUC Lens (or the file is not an annotation file). They were left unchanged and cannot be edited with this version.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger?.LogWarning(ex, "An incident annotation could not be saved.");
            vm.SaveMessage = $"The annotation could not be saved: {ex.Message}";
        }
    }

    private bool CanStep(int direction)
    {
        if (history is not { Incidents.Count: > 0 } h) return false;
        var reference = selectedIncident?.Incident.At ?? viewStart + (viewEnd - viewStart) / 2;
        return direction < 0 ? h.Incidents.Any(i => i.At < reference - TimeSpan.FromMilliseconds(1)) : h.Incidents.Any(i => i.At > reference + TimeSpan.FromMilliseconds(1));
    }

    private void StepIncident(int direction)
    {
        if (history is not { } h) return;
        var reference = selectedIncident?.Incident.At ?? viewStart + (viewEnd - viewStart) / 2;
        var next = direction < 0 ? h.Incidents.LastOrDefault(i => i.At < reference - TimeSpan.FromMilliseconds(1)) : h.Incidents.FirstOrDefault(i => i.At > reference + TimeSpan.FromMilliseconds(1));
        if (next is not null) SelectIncident(next);
    }

    private void RaiseSteps()
    {
        PreviousIncidentCommand.Refresh(); NextIncidentCommand.Refresh();
        ZoomInCommand.Refresh(); ZoomOutCommand.Refresh(); ResetZoomCommand.Refresh(); ApplyCustomRangeCommand.Refresh();
        ExportIncidentsCommand.Refresh(); ExportTimelineCommand.Refresh(); ExportFindingsCsvCommand.Refresh(); ExportFindingsJsonCommand.Refresh();
    }

    // ---------- charts ----------

    private void RequestRefresh()
    {
        if (history is not { } h) return;
        Cancel(ref refreshCts);
        var cts = new CancellationTokenSource(); refreshCts = cts;
        refreshTask = RefreshAsync(h, viewStart, viewEnd, cts.Token);
    }

    private async Task RefreshAsync(LoadedHistory h, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(30, cancellationToken);
            var windows = await Task.Run(() => h.View(from, to, MaximumChartPoints, cancellationToken), cancellationToken);
            if (cancellationToken.IsCancellationRequested || !ReferenceEquals(h, history)) return;
            foreach (var row in Charts)
            {
                var window = windows.FirstOrDefault(w => w.Channel.Id == row.Id);
                if (window is null) continue;
                row.Data.Clear();
                foreach (var p in window.Points) row.Data.Add(new DateTime(p.Ticks, DateTimeKind.Utc), p.Value);
                row.Data.SetXRange(from.UtcDateTime, to.UtcDateTime);
                row.Stats = FormatStats(window);
                row.Raised();
            }
            if (hoverAt is { } at) SetHover(at);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            // Started without anyone awaiting it: a failure is logged and shown, never left in an unobserved task.
            logger?.LogWarning(ex, "The history charts could not be refreshed.");
            if (!cancellationToken.IsCancellationRequested && ReferenceEquals(h, history)) RangeMessage = $"The charts could not be refreshed: {ex.Message}";
        }
    }

    private string FormatStats(SeriesWindow w)
    {
        var s = w.Statistics;
        if (s.Valid == 0) return $"No valid values in this range · {s.Missing:N0} sample(s) without a value (N/A)";
        string V(double? value) => (value ?? double.NaN).ToString("0.##", CultureInfo.InvariantCulture) + (w.Unit.Length > 0 ? " " + w.Unit : "");
        return $"P50 {V(s.P50)} · P95 {V(s.P95)} · Max {V(s.Maximum)}" + (s.MaximumAt is { } at ? $" at {HistoryTime.Format(at, zone)}" : "") +
            $" · {s.Valid:N0} valid · {s.Missing:N0} missing" + (w.Decimated ? " · reduced for drawing, peaks kept" : "");
    }

    /// <summary>The pointer is over the charts: shows the recorded value of every chart at that instant, or N/A where there is none.</summary>
    public void SetHover(DateTimeOffset? at)
    {
        hoverAt = at;
        if (history is not { } h || at is null)
        {
            HoverText = "";
            foreach (var row in Charts) row.Readout = "";
            return;
        }
        HoverText = HistoryTime.Format(at.Value, zone, withDate: true, milliseconds: true);
        foreach (var row in Charts)
        {
            row.Readout = h.Nearest(row.Id, at.Value) is { } nearest
                ? $"{nearest.Value.ToString("0.##", CultureInfo.InvariantCulture)} {(h.Channel(row.Id)?.Unit ?? "")} at {HistoryTime.Format(nearest.At, zone, milliseconds: true)}".Replace("  ", " ", StringComparison.Ordinal)
                : "N/A (no sample here)";
        }
    }

    // ---------- export ----------

    private async Task ExportAsync(bool incidentsOnly)
    {
        if (history is not { } h) return;
        var path = desktop.SelectExportPath(json: false);
        if (path is null) return;
        try
        {
            var csv = HistoryExport.TimelineCsv(h, annotations, zone, incidentsOnly);
            await File.WriteAllTextAsync(path, csv, new UTF8Encoding(false));
            Message = $"{(incidentsOnly ? "Incidents" : "Timeline")} exported to {path}. The session CSV and JSON exports are unchanged.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Message = $"The export failed: {ex.Message}"; }
    }

    /// <summary>
    /// Exports the findings of the open recording (CSV or JSON, see <see cref="FindingsExport"/>) to a file the user chose. The file is built and written off the UI thread, replaced
    /// atomically, and cancelled with the page; a failure says why and leaves no partial file. Nothing about the selection, the charts or the recording changes.
    /// </summary>
    private async Task ExportFindingsAsync(bool json)
    {
        if (history is not { } h || Findings.CurrentAnalysis is not { } analysis) return;
        var info = h.Info;
        var path = desktop.SelectExportPath(json, "Export findings", $"EucLens-findings-{info.ShortId}-{DateTime.Now:yyyyMMdd-HHmmss}");
        if (path is null) return;
        Cancel(ref exportCts);
        var cts = new CancellationTokenSource(); exportCts = cts;
        var token = cts.Token;
        exportingFindings = true; RaiseSteps();
        Findings.ExportMessage = "Exporting findings…";
        var label = json ? "JSON" : "CSV";
        try
        {
            await (json ? FindingsExport.WriteJsonAsync(path, analysis, info, token) : FindingsExport.WriteCsvAsync(path, analysis, info, token));
            if (ReferenceEquals(h, history)) Findings.ExportMessage = $"Findings exported ({label}) to {path}. The recording was not changed." + (analysis.ReducedConfidence ? " The analysis has reduced confidence; the file says so." : "");
        }
        catch (OperationCanceledException) { if (ReferenceEquals(h, history)) Findings.ExportMessage = "The export was cancelled. No file was changed."; }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A command runs as an async void: whatever else goes wrong (a security policy, a bad path, a bug) is reported here and never reaches the dispatcher.
            logger?.LogWarning(ex, "The findings could not be exported.");
            if (ReferenceEquals(h, history)) Findings.ExportMessage = $"The findings could not be exported: {ex.Message} No partial file was left.";
        }
        finally
        {
            exportingFindings = false;
            if (ReferenceEquals(exportCts, cts)) Cancel(ref exportCts);
            RaiseSteps();
        }
    }

    private void RaiseAll()
    {
        Raise(nameof(HasHistory)); Raise(nameof(ShowPlaceholder)); Raise(nameof(SessionStart)); Raise(nameof(SessionEnd)); Raise(nameof(Markers)); Raise(nameof(Gaps));
        Raise(nameof(UnavailableNote)); Raise(nameof(HasUnavailableNote)); Raise(nameof(EventsHeading)); Raise(nameof(HasIssues)); Raise(nameof(StateLabel)); Raise(nameof(StateIcon)); Raise(nameof(StateNote));
        Raise(nameof(ZoneNote)); Raise(nameof(HasSelectedIncident)); Raise(nameof(IsPartial));
        RaiseSteps();
    }
}
