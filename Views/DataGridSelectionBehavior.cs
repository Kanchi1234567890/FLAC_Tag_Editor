using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using FLAC_Tag_Editor.ViewModels;

namespace FLAC_Tag_Editor.Views;

/// <summary>
/// DataGrid の選択行自動スクロールおよび複数選択（SelectedItems）と ViewModel コレクションの双方向同期を提供する添付ビヘイビア。
/// </summary>
public static class DataGridSelectionBehavior
{
    private static readonly ConditionalWeakTable<DataGrid, SelectionSyncState> SyncStates = new();

    public static readonly AttachedProperty<bool> ScrollSelectedItemIntoViewProperty =
        AvaloniaProperty.RegisterAttached<DataGrid, bool>(
            "ScrollSelectedItemIntoView",
            typeof(DataGridSelectionBehavior),
            defaultValue: false);

    public static readonly AttachedProperty<IList?> SelectedItemsProperty =
        AvaloniaProperty.RegisterAttached<DataGrid, IList?>(
            "SelectedItems",
            typeof(DataGridSelectionBehavior));

    static DataGridSelectionBehavior()
    {
        ScrollSelectedItemIntoViewProperty.Changed.AddClassHandler<DataGrid>(OnScrollSelectedItemIntoViewChanged);
        SelectedItemsProperty.Changed.AddClassHandler<DataGrid>(OnSelectedItemsChanged);
    }

    public static bool GetScrollSelectedItemIntoView(DataGrid element) =>
        element.GetValue(ScrollSelectedItemIntoViewProperty);

    public static void SetScrollSelectedItemIntoView(DataGrid element, bool value) =>
        element.SetValue(ScrollSelectedItemIntoViewProperty, value);

    public static IList? GetSelectedItems(DataGrid element) =>
        element.GetValue(SelectedItemsProperty);

    public static void SetSelectedItems(DataGrid element, IList? value) =>
        element.SetValue(SelectedItemsProperty, value);

    private static void OnScrollSelectedItemIntoViewChanged(DataGrid dataGrid, AvaloniaPropertyChangedEventArgs e)
    {
        UpdateSelectionChangedSubscription(dataGrid);
    }

    private static void OnSelectedItemsChanged(DataGrid dataGrid, AvaloniaPropertyChangedEventArgs e)
    {
        var state = SyncStates.GetOrCreateValue(dataGrid);
        DetachCollectionChanged(state);

        var newList = e.NewValue as IList;
        state.BoundList = newList;

        if (newList is INotifyCollectionChanged observable)
        {
            state.ObservableList = observable;
            state.CollectionChangedHandler = (_, _) => SyncFromViewModelToDataGrid(dataGrid, state);
            observable.CollectionChanged += state.CollectionChangedHandler;
        }

        UpdateSelectionChangedSubscription(dataGrid);

        if (newList is null)
        {
            return;
        }

        if (newList.Count > 0)
        {
            SyncFromViewModelToDataGrid(dataGrid, state);
        }
        else if (dataGrid.SelectedItems is { Count: > 0 })
        {
            SyncFromDataGridToViewModel(dataGrid, state);
        }
    }

    private static void UpdateSelectionChangedSubscription(DataGrid dataGrid)
    {
        var state = SyncStates.GetOrCreateValue(dataGrid);
        var needsSubscription = GetScrollSelectedItemIntoView(dataGrid) || state.BoundList is not null;

        if (needsSubscription && !state.IsSelectionChangedSubscribed)
        {
            dataGrid.SelectionChanged += OnSelectionChanged;
            dataGrid.DetachedFromVisualTree += OnDetachedFromVisualTree;
            dataGrid.AttachedToVisualTree += OnAttachedToVisualTree;
            state.IsSelectionChangedSubscribed = true;
        }
        else if (!needsSubscription && state.IsSelectionChangedSubscribed)
        {
            dataGrid.SelectionChanged -= OnSelectionChanged;
            dataGrid.DetachedFromVisualTree -= OnDetachedFromVisualTree;
            dataGrid.AttachedToVisualTree -= OnAttachedToVisualTree;
            state.IsSelectionChangedSubscribed = false;
            state.LastScrolledItem = null;
        }
    }

    private static void OnDetachedFromVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is DataGrid dataGrid && SyncStates.TryGetValue(dataGrid, out var state))
        {
            DetachCollectionChanged(state);
        }
    }

    private static void OnAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is not DataGrid dataGrid || !SyncStates.TryGetValue(dataGrid, out var state))
        {
            return;
        }

        if (state.BoundList is INotifyCollectionChanged observable && state.ObservableList is null)
        {
            state.ObservableList = observable;
            state.CollectionChangedHandler = (_, _) => SyncFromViewModelToDataGrid(dataGrid, state);
            observable.CollectionChanged += state.CollectionChangedHandler;
        }

        if (state.BoundList is { Count: > 0 })
        {
            SyncFromViewModelToDataGrid(dataGrid, state);
        }
    }

    private static void OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is not DataGrid dataGrid)
        {
            return;
        }

        var state = SyncStates.GetOrCreateValue(dataGrid);

        if (state.BoundList is not null)
        {
            SyncFromDataGridToViewModel(dataGrid, state);
        }

        if (!GetScrollSelectedItemIntoView(dataGrid))
        {
            return;
        }

        var selectedItem = dataGrid.SelectedItem;
        if (selectedItem is null || ReferenceEquals(selectedItem, state.LastScrolledItem))
        {
            if (selectedItem is null)
            {
                state.LastScrolledItem = null;
            }

            return;
        }

        state.LastScrolledItem = selectedItem;
        Dispatcher.UIThread.Post(() =>
        {
            if (ReferenceEquals(dataGrid.SelectedItem, selectedItem))
            {
                dataGrid.ScrollIntoView(selectedItem, null);
            }
        }, DispatcherPriority.Background);
    }

    private static void SyncFromDataGridToViewModel(DataGrid dataGrid, SelectionSyncState state)
    {
        if (state.IsSyncing || state.BoundList is not { } boundList)
        {
            return;
        }

        var gridItems = CollectDistinctItems(dataGrid.SelectedItems);
        if (HasSameItemSet(boundList, gridItems))
        {
            return;
        }

        state.IsSyncing = true;
        try
        {
            if (boundList is IBulkList bulkList)
            {
                bulkList.ReplaceAll(gridItems);
            }
            else
            {
                var gridSet = new HashSet<object>(gridItems, ReferenceEqualityComparer.Instance);
                for (var i = boundList.Count - 1; i >= 0; i--)
                {
                    var existing = boundList[i];
                    if (existing is null || !gridSet.Contains(existing))
                    {
                        boundList.RemoveAt(i);
                    }
                }

                var boundSet = CollectReferenceSet(boundList);
                foreach (var item in gridItems)
                {
                    if (boundSet.Add(item))
                    {
                        boundList.Add(item);
                    }
                }
            }
        }
        finally
        {
            state.IsSyncing = false;
        }
    }

    private static void SyncFromViewModelToDataGrid(DataGrid dataGrid, SelectionSyncState state)
    {
        if (state.IsSyncing || state.BoundList is not { } boundList || dataGrid.SelectedItems is null)
        {
            return;
        }

        var availableSet = CollectAvailableItemSet(dataGrid);
        var desiredItems = new List<object>(boundList.Count);
        var desiredSet = new HashSet<object>(ReferenceEqualityComparer.Instance);
        foreach (var item in boundList)
        {
            if (item is not null
                && availableSet.Contains(item)
                && desiredSet.Add(item))
            {
                desiredItems.Add(item);
            }
        }

        if (HasSameItemSet(dataGrid.SelectedItems, desiredItems))
        {
            return;
        }

        state.IsSyncing = true;
        try
        {
            var gridSelected = dataGrid.SelectedItems;
            if (desiredItems.Count == 0)
            {
                if (gridSelected.Count > 0)
                {
                    gridSelected.Clear();
                }

                return;
            }

            var toRemove = new List<object>();
            foreach (var existing in gridSelected)
            {
                if (existing is not null && !desiredSet.Contains(existing))
                {
                    toRemove.Add(existing);
                }
            }

            foreach (var item in toRemove)
            {
                gridSelected.Remove(item);
            }

            var remainingGridSet = CollectReferenceSet(gridSelected);
            foreach (var item in desiredItems)
            {
                if (remainingGridSet.Add(item))
                {
                    gridSelected.Add(item);
                }
            }
        }
        finally
        {
            state.IsSyncing = false;
        }
    }

    private static void DetachCollectionChanged(SelectionSyncState state)
    {
        if (state.ObservableList is not null && state.CollectionChangedHandler is not null)
        {
            state.ObservableList.CollectionChanged -= state.CollectionChangedHandler;
        }

        state.ObservableList = null;
        state.CollectionChangedHandler = null;
    }

    private static HashSet<object> CollectAvailableItemSet(DataGrid dataGrid)
    {
        var result = new HashSet<object>(ReferenceEqualityComparer.Instance);
        IEnumerable? source = dataGrid.CollectionView ?? dataGrid.ItemsSource;
        if (source is null)
        {
            return result;
        }

        foreach (var item in source)
        {
            if (item is not null)
            {
                result.Add(item);
            }
        }

        return result;
    }

    private static List<object> CollectDistinctItems(IList? source)
    {
        var result = new List<object>();
        if (source is null)
        {
            return result;
        }

        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        foreach (var item in source)
        {
            if (item is not null && seen.Add(item))
            {
                result.Add(item);
            }
        }

        return result;
    }

    private static bool HasSameItemSet(IList left, List<object> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        var rightSet = new HashSet<object>(right, ReferenceEqualityComparer.Instance);
        foreach (var item in left)
        {
            if (item is null || !rightSet.Contains(item))
            {
                return false;
            }
        }

        return true;
    }

    private static HashSet<object> CollectReferenceSet(IList list)
    {
        var set = new HashSet<object>(ReferenceEqualityComparer.Instance);
        for (var i = 0; i < list.Count; i++)
        {
            if (list[i] is { } item)
            {
                set.Add(item);
            }
        }

        return set;
    }

    private sealed class SelectionSyncState
    {
        public IList? BoundList { get; set; }
        public INotifyCollectionChanged? ObservableList { get; set; }
        public NotifyCollectionChangedEventHandler? CollectionChangedHandler { get; set; }
        public bool IsSyncing { get; set; }
        public bool IsSelectionChangedSubscribed { get; set; }
        public object? LastScrolledItem { get; set; }
    }
}
