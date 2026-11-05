using System.Collections.Specialized;
using EucLens.Core;

namespace EucLens.App.ViewModels;

public enum RecordingSort { Recorded, Duration, Size }

/// <summary>
/// The saved-recordings workspace of the Session Recorder page: the list the catalog found, shown sorted and filtered, with one selection and one set of actions for it.
/// <para>
/// Selecting a recording only selects it: nothing is opened, analyzed or exported, no page is shown and no file or folder is touched. Every action is the existing command of the
/// recording (<see cref="RecordingItem"/>), so analysis, exports and the Explorer actions behave exactly as before. The list is built from the catalog's small summaries and
/// reads no recording content; sorting, filtering and re-selection work on those summaries only.
/// </para>
/// </summary>
public sealed class SavedRecordingsViewModel : ObservableObject
{
    private readonly BackgroundViewModel owner;
    private RecordingItem? selected;
    private string? selectedId;
    private bool rebuilding;
    private Choice<RecordingStatusKind?> filter;
    private RecordingSort sortKey = RecordingSort.Recorded;
    private bool descending = true;

    public SavedRecordingsViewModel(BackgroundViewModel owner)
    {
        this.owner = owner;
        StatusFilters =
        [
            new(null, "All"), new(RecordingStatusKind.Complete, "Complete"), new(RecordingStatusKind.InProgress, "In progress"), new(RecordingStatusKind.Interrupted, "Interrupted"),
        ];
        filter = StatusFilters[0];
        AnalyzeCommand = new(() => selected?.AnalyzeCommand.Execute(null), () => selected is not null);
        ExportJsonCommand = new(() => selected?.ExportJsonCommand.ExecuteAsync() ?? Task.CompletedTask, () => selected is not null);
        ExportCsvCommand = new(() => selected?.ExportCsvCommand.ExecuteAsync() ?? Task.CompletedTask, () => selected is not null);
        ShowFileCommand = new(() => selected?.OpenFileCommand.Execute(null), () => selected is { HasFile: true });
        OpenFolderCommand = new(() => selected?.OpenFolderCommand.Execute(null), () => selected is not null);
        SortByRecordedCommand = new(() => SortBy(RecordingSort.Recorded));
        SortByDurationCommand = new(() => SortBy(RecordingSort.Duration));
        SortBySizeCommand = new(() => SortBy(RecordingSort.Size));
        // The list is replaced as a whole by the refresh: one event, one rebuild. The view model lives as long as the owner it listens to.
        owner.Recordings.CollectionChanged += OnSourceChanged;
    }

    /// <summary>What the list shows, in the current order.</summary>
    public BulkObservableCollection<RecordingItem> Items { get; } = [];
    public IReadOnlyList<Choice<RecordingStatusKind?>> StatusFilters { get; }

    public Choice<RecordingStatusKind?> SelectedFilter
    {
        get => filter;
        set { if (value is not null && Set(ref filter, value)) Rebuild(); }
    }

    public RecordingSort SortKey => sortKey;
    public bool SortDescending => descending;
    /// <summary>The direction mark of a column header: empty for the columns the list is not sorted by.</summary>
    public string RecordedSortMark => Mark(RecordingSort.Recorded);
    public string DurationSortMark => Mark(RecordingSort.Duration);
    public string SizeSortMark => Mark(RecordingSort.Size);
    public string SortDescription => $"Sorted by {sortKey switch { RecordingSort.Recorded => "date", RecordingSort.Duration => "duration", _ => "size" }}, {(descending ? (sortKey == RecordingSort.Recorded ? "newest first" : "largest first") : (sortKey == RecordingSort.Recorded ? "oldest first" : "smallest first"))}";
    private string Mark(RecordingSort key) => sortKey != key ? "" : descending ? "▼" : "▲";

    public RelayCommand SortByRecordedCommand { get; }
    public RelayCommand SortByDurationCommand { get; }
    public RelayCommand SortBySizeCommand { get; }

    /// <summary>Choosing the column the list is sorted by again reverses it; choosing another column starts with the natural direction (newest, longest, largest first).</summary>
    public void SortBy(RecordingSort key)
    {
        if (sortKey == key) descending = !descending; else { sortKey = key; descending = true; }
        foreach (var name in new[] { nameof(SortKey), nameof(SortDescending), nameof(RecordedSortMark), nameof(DurationSortMark), nameof(SizeSortMark), nameof(SortDescription) }) Raise(name);
        Rebuild();
    }

    // ---------- selection and actions ----------

    public RecordingItem? SelectedItem
    {
        get => selected;
        set
        {
            if (rebuilding) return;                         // the list control clears its selection while the items are replaced; the refresh restores it by identity
            if (!Set(ref selected, value)) return;
            selectedId = value?.ShortId;
            RaiseSelection();
        }
    }

    public bool HasSelection => selected is not null;
    public string SelectionSummary => selected is null ? "" : $"{selected.Started} · {selected.Duration} · {selected.Samples} samples · {selected.StatusLabel}";
    public string SelectionAutomationName => selected is null ? "No recording selected" : $"Selected recording: {selected.AutomationName}";

    public RelayCommand AnalyzeCommand { get; }
    public AsyncCommand ExportJsonCommand { get; }
    public AsyncCommand ExportCsvCommand { get; }
    public RelayCommand ShowFileCommand { get; }
    public RelayCommand OpenFolderCommand { get; }

    public const string SelectHint = "Select a recording to analyze, export or find it in Explorer.";
    public const string NoFileHint = "This recording has no readable file in the recordings folder, so it cannot be shown.";
    /// <summary>Why an action is unavailable, said once next to the action bar and given to screen readers as the actions' help text; empty when everything is available.</summary>
    public string ActionHint => selected is null ? SelectHint : selected.HasFile ? "" : NoFileHint;
    public string ShowFileHint => selected is null ? SelectHint : selected.HasFile ? "Selects the recording's first file in Windows Explorer." : NoFileHint;
    public string AnalyzeHint => selected is null ? SelectHint : "Opens the recording in Session History. Nothing is analyzed until you choose this.";

    private void RaiseSelection()
    {
        foreach (var name in new[] { nameof(HasSelection), nameof(SelectionSummary), nameof(SelectionAutomationName), nameof(ActionHint), nameof(ShowFileHint), nameof(AnalyzeHint) }) Raise(name);
        AnalyzeCommand.Refresh(); ExportJsonCommand.Refresh(); ExportCsvCommand.Refresh(); ShowFileCommand.Refresh(); OpenFolderCommand.Refresh();
    }

    // ---------- the list ----------

    public int TotalCount => owner.Recordings.Count;
    public bool IsEmpty => Items.Count == 0;
    public bool HasItems => Items.Count > 0;
    public string CountText => TotalCount == 0 ? "No saved recordings" : Items.Count == TotalCount ? $"{TotalCount:N0} recording{(TotalCount == 1 ? "" : "s")}" : $"{Items.Count:N0} of {TotalCount:N0} recordings";
    public string EmptyText => TotalCount == 0
        ? "No saved recordings yet. A recording is saved to disk as it is collected; it is listed here after you start one and refresh."
        : "No recording matches this filter.";

    private void OnSourceChanged(object? sender, NotifyCollectionChangedEventArgs e) => Rebuild();

    /// <summary>
    /// Rebuilds the visible list from the catalog's recordings: filtered, then sorted deterministically (the chosen key, then newest first, then the recording's id, so equal values never swap places
    /// between refreshes). The selected recording stays selected when it is still listed and visible; otherwise the selection is cleared and the actions say why they are unavailable.
    /// </summary>
    public void Rebuild()
    {
        rebuilding = true;
        try
        {
            IEnumerable<RecordingItem> source = owner.Recordings;
            if (filter.Value is { } kind) source = source.Where(item => item.StatusKind == kind);
            Func<RecordingItem, IComparable> key = sortKey switch
            {
                RecordingSort.Duration => item => item.DurationValue,
                RecordingSort.Size => item => item.Bytes,
                _ => item => item.StartedUtc,
            };
            var ordered = descending ? source.OrderByDescending(key) : source.OrderBy(key);
            ordered = ordered.ThenByDescending(item => item.StartedUtc).ThenBy(item => item.ShortId, StringComparer.Ordinal);
            Items.ReplaceAll(ordered.ToList());
            selected = selectedId is null ? null : Items.FirstOrDefault(item => item.ShortId == selectedId);
            if (selected is null) selectedId = null;
        }
        finally { rebuilding = false; }
        Raise(nameof(SelectedItem));
        foreach (var name in new[] { nameof(TotalCount), nameof(IsEmpty), nameof(HasItems), nameof(CountText), nameof(EmptyText) }) Raise(name);
        RaiseSelection();
    }
}
