using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace EucLens.App.ViewModels;

/// <summary>
/// An observable collection that can be replaced as a whole with one notification. Clearing and adding thousands of entries one by one raises one
/// CollectionChanged per entry, and every listener (the list control, its automation peers) reacts to each.
/// </summary>
public sealed class BulkObservableCollection<T> : ObservableCollection<T>
{
    /// <summary>Replaces the content and raises a single Reset (plus the Count and indexer property changes).</summary>
    public void ReplaceAll(IEnumerable<T> items)
    {
        CheckReentrancy();
        Items.Clear();
        foreach (var item in items) Items.Add(item);
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
