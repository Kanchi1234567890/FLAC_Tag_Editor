using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace FLAC_Tag_Editor.Views;

/// <summary>
/// ViewModel からのフォーカス要求に応じて対象コントロールへフォーカスとスクロールを行う添付ビヘイビア。
/// </summary>
public static class FocusBehavior
{
    public static readonly AttachedProperty<bool> IsFocusRequestedProperty =
        AvaloniaProperty.RegisterAttached<Control, bool>(
            "IsFocusRequested",
            typeof(FocusBehavior),
            defaultValue: false,
            defaultBindingMode: BindingMode.TwoWay);

    static FocusBehavior()
    {
        IsFocusRequestedProperty.Changed.AddClassHandler<Control>(OnIsFocusRequestedChanged);
    }

    public static bool GetIsFocusRequested(Control element) =>
        element.GetValue(IsFocusRequestedProperty);

    public static void SetIsFocusRequested(Control element, bool value) =>
        element.SetValue(IsFocusRequestedProperty, value);

    private static void OnIsFocusRequestedChanged(Control control, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.NewValue is not true)
        {
            return;
        }

        if (control.IsAttachedToVisualTree())
        {
            ScheduleFocus(control);
            return;
        }

        void OnAttached(object? sender, VisualTreeAttachmentEventArgs args)
        {
            control.AttachedToVisualTree -= OnAttached;
            if (GetIsFocusRequested(control))
            {
                ScheduleFocus(control);
            }
        }

        control.AttachedToVisualTree += OnAttached;
    }

    private static void ScheduleFocus(Control control)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (!control.IsAttachedToVisualTree() || !GetIsFocusRequested(control))
            {
                return;
            }

            control.SetCurrentValue(IsFocusRequestedProperty, false);
            control.BringIntoView();
            control.Focus();
        }, DispatcherPriority.Background);
    }
}
