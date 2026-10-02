using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace Perch.Avalonia.Views;

/// <summary>The click and hover wiring the Roost's code-built buttons and rows share.</summary>
internal static class PointerWiring
{
    /// <summary>A left-button release runs <paramref name="onClick"/>. With <paramref name="handle"/> it also marks the
    /// release handled, so a click on a row inside something clickable doesn't fire that too.</summary>
    public static T OnLeftClick<T>(this T control, Action onClick, bool handle = false) where T : InputElement
    {
        control.PointerReleased += (_, e) =>
        {
            if (e.InitialPressMouseButton != MouseButton.Left) return;
            if (handle) e.Handled = true;
            onClick();
        };
        return control;
    }

    /// <summary>Hovering paints <paramref name="wash"/> behind the border and leaving clears it — except while
    /// <paramref name="keep"/> says the background is spoken for (the active tab, a lit row).</summary>
    public static Border HoverWash(this Border border, IBrush wash, Func<bool>? keep = null)
    {
        border.PointerEntered += (_, _) => { if (keep?.Invoke() != true) border.Background = wash; };
        border.PointerExited += (_, _) => { if (keep?.Invoke() != true) border.Background = Brushes.Transparent; };
        return border;
    }
}
