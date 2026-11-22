using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using FLAC_Tag_Editor.ViewModels;

namespace FLAC_Tag_Editor.Views;

/// <summary>
/// Window.Closing イベントを ViewModel の IWindowCloseGuard へ非同期委譲する添付ビヘイビア。
/// </summary>
public static class WindowClosingBehavior
{
    private static readonly ConditionalWeakTable<Window, CloseState> States = new();

    public static readonly AttachedProperty<IWindowCloseGuard?> CloseGuardProperty =
        AvaloniaProperty.RegisterAttached<Window, IWindowCloseGuard?>(
            "CloseGuard",
            typeof(WindowClosingBehavior));

    static WindowClosingBehavior()
    {
        CloseGuardProperty.Changed.AddClassHandler<Window>(OnCloseGuardChanged);
    }

    public static IWindowCloseGuard? GetCloseGuard(Window element) =>
        element.GetValue(CloseGuardProperty);

    public static void SetCloseGuard(Window element, IWindowCloseGuard? value) =>
        element.SetValue(CloseGuardProperty, value);

    private static void OnCloseGuardChanged(Window window, AvaloniaPropertyChangedEventArgs e)
    {
        var hadGuard = e.OldValue is IWindowCloseGuard;
        var hasGuard = e.NewValue is IWindowCloseGuard;

        if (!hadGuard && hasGuard)
        {
            window.Closing += OnWindowClosing;
        }
        else if (hadGuard && !hasGuard)
        {
            window.Closing -= OnWindowClosing;
        }
    }

    private static void OnWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (sender is not Window window)
        {
            return;
        }

        var state = States.GetOrCreateValue(window);
        if (state.AllowClose)
        {
            return;
        }

        if (e.CloseReason == WindowCloseReason.OSShutdown)
        {
            return;
        }

        var guard = GetCloseGuard(window);
        if (guard is null || !guard.ShouldInterceptClose)
        {
            return;
        }

        e.Cancel = true;

        if (state.IsConfirming)
        {
            return;
        }

        state.IsConfirming = true;
        _ = HandleCloseConfirmationAsync(window, guard, state);
    }

    private static async Task HandleCloseConfirmationAsync(
        Window window,
        IWindowCloseGuard guard,
        CloseState state)
    {
        try
        {
            var canClose = await guard.ConfirmCloseAsync();
            if (canClose)
            {
                state.AllowClose = true;
                Dispatcher.UIThread.Post(window.Close);
            }
        }
        finally
        {
            state.IsConfirming = false;
        }
    }

    private sealed class CloseState
    {
        public bool AllowClose { get; set; }
        public bool IsConfirming { get; set; }
    }
}
