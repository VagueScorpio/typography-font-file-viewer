using System.Collections.ObjectModel;
using System.Collections.Specialized;

namespace TypographyFontFileViewer.Core;

/// <summary>
/// An observable collection that can be refilled in one notification.
/// </summary>
/// <remarks>
/// <para>
/// Adding items to an ordinary <see cref="ObservableCollection{T}"/> one at a time
/// raises one change notification each, and every bound list re-measures and re-lays out
/// in response. For a handful of items that is invisible. For a folder of a thousand
/// fonts it is seconds of the window sitting there saying "Scanning…", with the work
/// going into layout rather than into reading fonts.
/// </para>
/// <para>
/// <see cref="ReplaceAll"/> swaps the contents and raises a single reset instead, which
/// a virtualizing list handles in one pass.
/// </para>
/// </remarks>
public sealed class ObservableRangeCollection<T> : ObservableCollection<T>
{
    public ObservableRangeCollection() { }

    public ObservableRangeCollection(IEnumerable<T> items) : base(items) { }

    /// <summary>Replaces everything in the collection, notifying once.</summary>
    public void ReplaceAll(IEnumerable<T> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        CheckReentrancy();

        Items.Clear();
        foreach (T item in items) Items.Add(item);

        OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
