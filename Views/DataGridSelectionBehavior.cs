using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace FLAC_Tag_Editor.Views;

/// <summary>
/// DataGrid の選択行変更時に、対象行が表示領域へ収まるよう自動スクロールする添付ビヘイビア。
/// </summary>
public static class DataGridSelectionBehavior
{
    public static readonly AttachedProperty<bool> ScrollSelectedItemIntoViewProperty =
        AvaloniaProperty.RegisterAttached<DataGrid, bool>(
            "ScrollSelectedItemIntoView",
            typeof(DataGridSelectionBehavior),
            defaultValue: false);

    static DataGridSelectionBehavior()
    {
        ScrollSelectedItemIntoViewProperty.Changed.AddClassHandler<DataGrid>(OnScrollSelectedItemIntoViewChanged);
    }

    public static bool GetScrollSelectedItemIntoView(DataGrid element) =>
        element.GetValue(ScrollSelectedItemIntoViewProperty);

    public static void SetScrollSelectedItemIntoView(DataGrid element, bool value) =>
        element.SetValue(ScrollSelectedItemIntoViewProperty, value);

    private static void OnScrollSelectedItemIntoViewChanged(DataGrid dataGrid, AvaloniaPropertyChangedEventArgs e)
    {
        var wasEnabled = e.OldValue is true;
        var isEnabled = e.NewValue is true;

        if (!wasEnabled && isEnabled)
        {
            dataGrid.SelectionChanged += OnSelectionChanged;
        }
        else if (wasEnabled && !isEnabled)
        {
            dataGrid.SelectionChanged -= OnSelectionChanged;
        }
    }

    private static void OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is not DataGrid dataGrid || dataGrid.SelectedItem is not { } selectedItem)
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            if (ReferenceEquals(dataGrid.SelectedItem, selectedItem))
            {
                dataGrid.ScrollIntoView(selectedItem, null);
            }
        }, DispatcherPriority.Background);
    }
}
