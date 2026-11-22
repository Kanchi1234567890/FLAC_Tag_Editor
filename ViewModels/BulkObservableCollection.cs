using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace FLAC_Tag_Editor.ViewModels;

/// <summary>
/// 複数要素の一括置換を単一の変更通知で実行できるリストインターフェース。
/// </summary>
public interface IBulkList : IList
{
    void ReplaceAll(IEnumerable items);
}

/// <summary>
/// 一括置換時に単一の Reset 通知のみを発行する ObservableCollection 拡張。
/// </summary>
public sealed class BulkObservableCollection<T> : ObservableCollection<T>, IBulkList
{
    private const string IndexerPropertyName = "Item[]";

    public void ReplaceAll(IEnumerable<T> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        CheckReentrancy();
        Items.Clear();
        foreach (var item in items)
        {
            Items.Add(item);
        }

        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs(IndexerPropertyName));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    void IBulkList.ReplaceAll(IEnumerable items)
    {
        ArgumentNullException.ThrowIfNull(items);

        CheckReentrancy();
        Items.Clear();
        foreach (var item in items)
        {
            if (item is T typed)
            {
                Items.Add(typed);
            }
        }

        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs(IndexerPropertyName));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
