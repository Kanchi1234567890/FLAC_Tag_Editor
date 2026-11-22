using System;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;

namespace FLAC_Tag_Editor.Views;

/// <summary>
/// <see cref="Grid"/> の指定行を折りたたみ（高さ 0）／展開（可変高さ・前回高さ復元）で切り替える添付ビヘイビア。
/// 水平 <see cref="GridSplitter"/> と組み合わせて、非表示時に余白を残さず表示時のリサイズ高さを保持する。
/// </summary>
public static class CollapsibleRowBehavior
{
    private static readonly ConditionalWeakTable<Grid, RowState> States = new();

    public static readonly AttachedProperty<int> RowIndexProperty =
        AvaloniaProperty.RegisterAttached<Grid, int>(
            "RowIndex",
            typeof(CollapsibleRowBehavior),
            defaultValue: -1);

    public static readonly AttachedProperty<bool> IsExpandedProperty =
        AvaloniaProperty.RegisterAttached<Grid, bool>(
            "IsExpanded",
            typeof(CollapsibleRowBehavior),
            defaultValue: false);

    public static readonly AttachedProperty<double> DefaultHeightProperty =
        AvaloniaProperty.RegisterAttached<Grid, double>(
            "DefaultHeight",
            typeof(CollapsibleRowBehavior),
            defaultValue: 240.0);

    public static readonly AttachedProperty<double> ExpandedMinHeightProperty =
        AvaloniaProperty.RegisterAttached<Grid, double>(
            "ExpandedMinHeight",
            typeof(CollapsibleRowBehavior),
            defaultValue: 80.0);

    static CollapsibleRowBehavior()
    {
        RowIndexProperty.Changed.AddClassHandler<Grid>(OnBehaviorPropertyChanged);
        IsExpandedProperty.Changed.AddClassHandler<Grid>(OnBehaviorPropertyChanged);
        DefaultHeightProperty.Changed.AddClassHandler<Grid>(OnBehaviorPropertyChanged);
        ExpandedMinHeightProperty.Changed.AddClassHandler<Grid>(OnBehaviorPropertyChanged);
    }

    public static int GetRowIndex(Grid element) =>
        element.GetValue(RowIndexProperty);

    public static void SetRowIndex(Grid element, int value) =>
        element.SetValue(RowIndexProperty, value);

    public static bool GetIsExpanded(Grid element) =>
        element.GetValue(IsExpandedProperty);

    public static void SetIsExpanded(Grid element, bool value) =>
        element.SetValue(IsExpandedProperty, value);

    public static double GetDefaultHeight(Grid element) =>
        element.GetValue(DefaultHeightProperty);

    public static void SetDefaultHeight(Grid element, double value) =>
        element.SetValue(DefaultHeightProperty, value);

    public static double GetExpandedMinHeight(Grid element) =>
        element.GetValue(ExpandedMinHeightProperty);

    public static void SetExpandedMinHeight(Grid element, double value) =>
        element.SetValue(ExpandedMinHeightProperty, value);

    private static void OnBehaviorPropertyChanged(Grid grid, AvaloniaPropertyChangedEventArgs e)
    {
        var state = States.GetValue(grid, static _ => new RowState());
        EnsureAttachedHandler(grid, state);
        ApplyRowState(grid, state);
    }

    private static void EnsureAttachedHandler(Grid grid, RowState state)
    {
        if (state.IsHandlerAttached)
        {
            return;
        }

        grid.AttachedToVisualTree += OnGridAttachedToVisualTree;
        state.IsHandlerAttached = true;
    }

    private static void OnGridAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is not Grid grid || !States.TryGetValue(grid, out var state))
        {
            return;
        }

        ApplyRowState(grid, state);
    }

    private static void ApplyRowState(Grid grid, RowState state)
    {
        var rowIndex = GetRowIndex(grid);
        if (rowIndex < 0 || rowIndex >= grid.RowDefinitions.Count)
        {
            return;
        }

        var rowDef = grid.RowDefinitions[rowIndex];
        var isExpanded = GetIsExpanded(grid);
        var minHeight = NormalizeNonNegative(GetExpandedMinHeight(grid), fallback: 0.0);
        var defaultHeight = Math.Max(minHeight, NormalizeNonNegative(GetDefaultHeight(grid), fallback: minHeight));

        if (!isExpanded)
        {
            // 展開中から折りたたむ直前の高さ（スプリッター調整結果）を記憶
            if (state.HasApplied && state.LastIsExpanded)
            {
                var currentHeight = ResolveCurrentExpandedHeight(rowDef);
                if (currentHeight > 0)
                {
                    state.LastExpandedHeight = currentHeight;
                }
            }

            rowDef.MinHeight = 0.0;
            rowDef.Height = new GridLength(0.0, GridUnitType.Pixel);
            state.HasApplied = true;
            state.LastIsExpanded = false;
            return;
        }

        // 既に展開済みならユーザーのリサイズ結果を維持し、MinHeight のみ保証する
        if (state.HasApplied && state.LastIsExpanded)
        {
            rowDef.MinHeight = minHeight;
            if (!rowDef.Height.IsAbsolute || rowDef.Height.Value < minHeight)
            {
                var restored = ClampToAvailableHeight(grid, rowIndex, state.LastExpandedHeight ?? defaultHeight, minHeight);
                rowDef.Height = new GridLength(restored, GridUnitType.Pixel);
            }

            return;
        }

        var desiredHeight = state.LastExpandedHeight is > 0
            ? Math.Max(minHeight, state.LastExpandedHeight.Value)
            : defaultHeight;

        var targetHeight = ClampToAvailableHeight(grid, rowIndex, desiredHeight, minHeight);
        rowDef.MinHeight = minHeight;
        rowDef.Height = new GridLength(targetHeight, GridUnitType.Pixel);
        state.HasApplied = true;
        state.LastIsExpanded = true;
    }

    private static double ResolveCurrentExpandedHeight(RowDefinition rowDef)
    {
        if (rowDef.Height.IsAbsolute && double.IsFinite(rowDef.Height.Value) && rowDef.Height.Value > 0)
        {
            return rowDef.Height.Value;
        }

        if (double.IsFinite(rowDef.ActualHeight) && rowDef.ActualHeight > 0)
        {
            return rowDef.ActualHeight;
        }

        return 0.0;
    }

    private static double ClampToAvailableHeight(Grid grid, int targetRowIndex, double desiredHeight, double minHeight)
    {
        var clamped = Math.Max(minHeight, desiredHeight);
        if (!grid.IsAttachedToVisualTree() || !double.IsFinite(grid.Bounds.Height) || grid.Bounds.Height <= 0)
        {
            return clamped;
        }

        var reservedHeight = 0.0;
        for (var i = 0; i < grid.RowDefinitions.Count; i++)
        {
            if (i == targetRowIndex)
            {
                continue;
            }

            var otherRow = grid.RowDefinitions[i];
            if (otherRow.Height.IsStar)
            {
                reservedHeight += NormalizeNonNegative(otherRow.MinHeight, fallback: 0.0);
            }
            else if (double.IsFinite(otherRow.ActualHeight) && otherRow.ActualHeight > 0)
            {
                reservedHeight += otherRow.ActualHeight;
            }
            else if (otherRow.Height.IsAbsolute && double.IsFinite(otherRow.Height.Value) && otherRow.Height.Value > 0)
            {
                reservedHeight += otherRow.Height.Value;
            }
        }

        var maxAvailable = grid.Bounds.Height - reservedHeight;
        if (maxAvailable >= minHeight)
        {
            clamped = Math.Min(clamped, maxAvailable);
        }

        return Math.Round(clamped);
    }

    private static double NormalizeNonNegative(double value, double fallback)
    {
        return double.IsFinite(value) && value >= 0 ? value : fallback;
    }

    private sealed class RowState
    {
        public bool IsHandlerAttached { get; set; }
        public bool HasApplied { get; set; }
        public bool LastIsExpanded { get; set; }
        public double? LastExpandedHeight { get; set; }
    }
}
