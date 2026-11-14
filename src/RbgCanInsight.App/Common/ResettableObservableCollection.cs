using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace RbgCanInsight.App.Common;

/// <summary>
/// Replaces a large virtualized table in one WPF collection-reset notification.
/// ObservableCollection.Clear()+Add() per row creates thousands of individual
/// dispatcher/UI Automation events and can make filtered DBC/trace lists jank.
/// This class preserves item references and returns control to the DataGrid once.
/// </summary>
public sealed class ResettableObservableCollection<T> : ObservableCollection<T>
{
    public void ReplaceAll(IEnumerable<T> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        // Materialize BEFORE clearing, even if the caller accidentally supplies
        // this collection as input.
        var snapshot = items as T[] ?? items.ToArray();
        CheckReentrancy();
        Items.Clear();
        foreach (var item in snapshot)
            Items.Add(item);

        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
