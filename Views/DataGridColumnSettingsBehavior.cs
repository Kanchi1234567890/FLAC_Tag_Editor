using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FLAC_Tag_Editor.Models;
using FLAC_Tag_Editor.ViewModels;

namespace FLAC_Tag_Editor.Views;

/// <summary>
/// DataGrid の列表示状態・並び順（DisplayIndex）・列幅（Width）を
/// <see cref="TrackGridColumnSettingsViewModel"/> と双方向同期する添付ビヘイビア。
/// </summary>
public static class DataGridColumnSettingsBehavior
{
    private static readonly ConditionalWeakTable<DataGrid, SyncState> States = new();

    public static readonly AttachedProperty<TrackGridColumnSettingsViewModel?> ColumnSettingsProperty =
        AvaloniaProperty.RegisterAttached<DataGrid, TrackGridColumnSettingsViewModel?>(
            "ColumnSettings",
            typeof(DataGridColumnSettingsBehavior));

    static DataGridColumnSettingsBehavior()
    {
        ColumnSettingsProperty.Changed.AddClassHandler<DataGrid>(OnColumnSettingsChanged);
    }

    public static TrackGridColumnSettingsViewModel? GetColumnSettings(DataGrid element) =>
        element.GetValue(ColumnSettingsProperty);

    public static void SetColumnSettings(DataGrid element, TrackGridColumnSettingsViewModel? value) =>
        element.SetValue(ColumnSettingsProperty, value);

    private static SyncState GetOrCreateState(DataGrid dataGrid)
    {
        return States.GetValue(dataGrid, static grid => new SyncState(grid));
    }

    private static void OnColumnSettingsChanged(DataGrid dataGrid, AvaloniaPropertyChangedEventArgs e)
    {
        var state = GetOrCreateState(dataGrid);
        DetachFromGridAndViewModel(dataGrid, state);

        state.ViewModel = e.NewValue as TrackGridColumnSettingsViewModel;
        if (state.ViewModel is null)
        {
            return;
        }

        AttachToGridAndViewModel(dataGrid, state);

        if (dataGrid.IsAttachedToVisualTree())
        {
            ApplyViewModelToGrid(dataGrid, state);
        }
    }

    private static void AttachToGridAndViewModel(DataGrid dataGrid, SyncState state)
    {
        if (!state.IsGridAttached)
        {
            dataGrid.AttachedToVisualTree += OnAttachedToVisualTree;
            dataGrid.DetachedFromVisualTree += OnDetachedFromVisualTree;
            dataGrid.ColumnReordered += OnColumnReordered;
            dataGrid.AddHandler(
                InputElement.PointerReleasedEvent,
                OnPointerReleased,
                RoutingStrategies.Tunnel | RoutingStrategies.Bubble,
                handledEventsToo: true);

            foreach (var column in dataGrid.Columns)
            {
                if (state.TrackedColumns.Add(column))
                {
                    column.PropertyChanged += state.ColumnPropertyChangedHandler;
                }
            }

            state.IsGridAttached = true;
        }

        if (state.ViewModel is not null && !state.IsViewModelAttached)
        {
            state.ViewModel.LayoutRequested += state.LayoutRequestedHandler;
            state.IsViewModelAttached = true;
        }
    }

    private static void DetachFromGridAndViewModel(DataGrid dataGrid, SyncState state)
    {
        if (state.ViewModel is not null && state.IsViewModelAttached)
        {
            state.ViewModel.LayoutRequested -= state.LayoutRequestedHandler;
            state.IsViewModelAttached = false;
        }

        if (state.IsGridAttached)
        {
            dataGrid.AttachedToVisualTree -= OnAttachedToVisualTree;
            dataGrid.DetachedFromVisualTree -= OnDetachedFromVisualTree;
            dataGrid.ColumnReordered -= OnColumnReordered;
            dataGrid.RemoveHandler(InputElement.PointerReleasedEvent, OnPointerReleased);

            foreach (var column in state.TrackedColumns)
            {
                column.PropertyChanged -= state.ColumnPropertyChangedHandler;
            }

            state.TrackedColumns.Clear();
            state.IsGridAttached = false;
        }
    }

    private static void OnAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is not DataGrid dataGrid || !States.TryGetValue(dataGrid, out var state))
        {
            return;
        }

        if (state.ViewModel is not null && !state.IsViewModelAttached)
        {
            state.ViewModel.LayoutRequested += state.LayoutRequestedHandler;
            state.IsViewModelAttached = true;
        }

        foreach (var column in dataGrid.Columns)
        {
            if (state.TrackedColumns.Add(column))
            {
                column.PropertyChanged += state.ColumnPropertyChangedHandler;
            }
        }

        ApplyViewModelToGrid(dataGrid, state);

        // Avalonia DataGrid の初期レイアウト確定後にも DisplayIndex / Width を再保証
        Dispatcher.UIThread.Post(
            () =>
            {
                if (dataGrid.IsAttachedToVisualTree() && state.ViewModel is not null)
                {
                    ApplyViewModelToGrid(dataGrid, state);
                }
            },
            DispatcherPriority.Loaded);
    }

    private static void OnDetachedFromVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is not DataGrid dataGrid || !States.TryGetValue(dataGrid, out var state))
        {
            return;
        }

        if (!state.IsApplyingFromViewModel && state.ViewModel is not null)
        {
            PushGridLayoutToViewModel(dataGrid, state.ViewModel);
        }

        if (state.ViewModel is not null && state.IsViewModelAttached)
        {
            state.ViewModel.LayoutRequested -= state.LayoutRequestedHandler;
            state.IsViewModelAttached = false;
        }
    }

    private static void OnLayoutRequested(DataGrid dataGrid, SyncState state)
    {
        if (dataGrid.IsAttachedToVisualTree())
        {
            ApplyViewModelToGrid(dataGrid, state);
        }
    }

    private static void OnColumnReordered(object? sender, DataGridColumnEventArgs e)
    {
        if (sender is not DataGrid dataGrid || !States.TryGetValue(dataGrid, out var state))
        {
            return;
        }

        if (state.IsApplyingFromViewModel || state.ViewModel is null)
        {
            return;
        }

        PushGridLayoutToViewModel(dataGrid, state.ViewModel);
    }

    private static void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (sender is not DataGrid dataGrid || !States.TryGetValue(dataGrid, out var state))
        {
            return;
        }

        if (state.IsApplyingFromViewModel || state.ViewModel is null || state.IsPostSyncQueued)
        {
            return;
        }

        // 列ヘッダーのドラッグリサイズやダブルクリック自動幅調整の完了後に差分を同期する
        state.IsPostSyncQueued = true;
        Dispatcher.UIThread.Post(
            () =>
            {
                state.IsPostSyncQueued = false;
                if (!state.IsApplyingFromViewModel && dataGrid.IsAttachedToVisualTree() && state.ViewModel is not null)
                {
                    PushGridLayoutToViewModel(dataGrid, state.ViewModel);
                }
            },
            DispatcherPriority.Background);
    }

    private static void OnColumnPropertyChanged(DataGrid dataGrid, SyncState state, object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (state.IsApplyingFromViewModel || state.ViewModel is null)
        {
            return;
        }

        if (e.Property != DataGridColumn.WidthProperty)
        {
            return;
        }

        if (sender is not DataGridColumn column || !column.IsVisible)
        {
            return;
        }

        var kind = DataGridPasteBehavior.GetColumnKind(column);
        if (kind == TrackGridColumnKind.None)
        {
            return;
        }

        PushGridLayoutToViewModel(dataGrid, state.ViewModel);
    }

    private static void ApplyViewModelToGrid(DataGrid dataGrid, SyncState state)
    {
        var viewModel = state.ViewModel;
        if (viewModel is null || dataGrid.Columns.Count == 0)
        {
            return;
        }

        state.IsApplyingFromViewModel = true;
        try
        {
            var mappedColumns = new List<(DataGridColumn Column, TrackGridColumnOptionViewModel Option)>(dataGrid.Columns.Count);
            foreach (var column in dataGrid.Columns)
            {
                var kind = DataGridPasteBehavior.GetColumnKind(column);
                if (kind == TrackGridColumnKind.None || !viewModel.TryGetColumn(kind, out var option))
                {
                    continue;
                }

                mappedColumns.Add((column, option));

                if (column.IsVisible != option.IsVisible)
                {
                    column.IsVisible = option.IsVisible;
                }

                if (option.CanResize)
                {
                    var targetWidth = option.Definition.ClampWidth(option.Width);
                    if (!column.Width.IsAbsolute || Math.Abs(column.Width.Value - targetWidth) >= 0.5)
                    {
                        column.Width = new DataGridLength(targetWidth, DataGridLengthUnitType.Pixel);
                    }

                    if (Math.Abs(column.MinWidth - option.MinWidth) >= 0.5)
                    {
                        column.MinWidth = option.MinWidth;
                    }
                }
            }

            // DisplayIndex は 0..Columns.Count-1 の範囲へ昇順に適用すると衝突・例外を防げる
            var ordered = mappedColumns
                .OrderBy(item => item.Option.DisplayIndex)
                .ThenBy(item => item.Option.Definition.DefaultDisplayIndex)
                .ToList();

            var maxValidIndex = dataGrid.Columns.Count - 1;
            for (var i = 0; i < ordered.Count && i <= maxValidIndex; i++)
            {
                var column = ordered[i].Column;
                if (column.DisplayIndex != i)
                {
                    column.DisplayIndex = i;
                }
            }
        }
        finally
        {
            state.IsApplyingFromViewModel = false;
        }
    }

    private static void PushGridLayoutToViewModel(DataGrid dataGrid, TrackGridColumnSettingsViewModel viewModel)
    {
        var snapshots = new List<TrackGridColumnSettings>(dataGrid.Columns.Count);

        foreach (var column in dataGrid.Columns)
        {
            var kind = DataGridPasteBehavior.GetColumnKind(column);
            if (kind == TrackGridColumnKind.None || !viewModel.TryGetColumn(kind, out var option))
            {
                continue;
            }

            snapshots.Add(new TrackGridColumnSettings
            {
                Key = option.Key,
                IsVisible = column.IsVisible,
                DisplayIndex = column.DisplayIndex,
                Width = ResolveColumnWidth(column, option)
            });
        }

        if (snapshots.Count > 0)
        {
            viewModel.SyncFromGridLayout(snapshots);
        }
    }

    private static double ResolveColumnWidth(DataGridColumn column, TrackGridColumnOptionViewModel option)
    {
        if (!option.CanResize)
        {
            return option.DefaultWidth;
        }

        if (column.IsVisible && double.IsFinite(column.ActualWidth) && column.ActualWidth > 0)
        {
            return column.ActualWidth;
        }

        if (column.Width.IsAbsolute && double.IsFinite(column.Width.Value) && column.Width.Value > 0)
        {
            return column.Width.Value;
        }

        return option.Width;
    }

    private sealed class SyncState
    {
        private readonly WeakReference<DataGrid> _gridRef;

        public SyncState(DataGrid dataGrid)
        {
            _gridRef = new WeakReference<DataGrid>(dataGrid);

            LayoutRequestedHandler = (_, _) =>
            {
                if (_gridRef.TryGetTarget(out var grid))
                {
                    OnLayoutRequested(grid, this);
                }
            };

            ColumnPropertyChangedHandler = (sender, args) =>
            {
                if (_gridRef.TryGetTarget(out var grid))
                {
                    OnColumnPropertyChanged(grid, this, sender, args);
                }
            };
        }

        public TrackGridColumnSettingsViewModel? ViewModel { get; set; }
        public bool IsGridAttached { get; set; }
        public bool IsViewModelAttached { get; set; }
        public bool IsApplyingFromViewModel { get; set; }
        public bool IsPostSyncQueued { get; set; }
        public HashSet<DataGridColumn> TrackedColumns { get; } = [];
        public EventHandler LayoutRequestedHandler { get; }
        public EventHandler<AvaloniaPropertyChangedEventArgs> ColumnPropertyChangedHandler { get; }
    }
}
