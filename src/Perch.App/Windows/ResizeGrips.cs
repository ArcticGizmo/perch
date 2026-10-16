using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace Perch.Avalonia.Windows;

/// <summary>
/// Resize handles for a frameless window (<c>WindowDecorations.None</c>), which has no system resize border: invisible
/// strips along every edge and squares at every corner that start the OS resize loop. Lay them over the window's
/// frame in a <see cref="Grid"/> (after the frame, so they're on top); they cover only its outer few pixels. The
/// window still needs <c>CanResize = true</c>.
/// </summary>
internal static class ResizeGrips
{
    public const double Thickness = 6;

    /// <param name="window">The window the grips resize.</param>
    /// <param name="onResizeStart">Called as a drag starts (e.g. to remember the user chose a size).</param>
    public static IEnumerable<Control> For(Window window, Action? onResizeStart = null)
    {
        Control Grip(WindowEdge edge, HorizontalAlignment h, VerticalAlignment v, double w, double ht, StandardCursorType cursor)
        {
            var grip = new Border
            {
                Background = Brushes.Transparent, HorizontalAlignment = h, VerticalAlignment = v,
                Cursor = new Cursor(cursor),
            };
            if (!double.IsNaN(w)) grip.Width = w;
            if (!double.IsNaN(ht)) grip.Height = ht;
            grip.PointerPressed += (_, e) =>
            {
                if (!e.GetCurrentPoint(window).Properties.IsLeftButtonPressed) return;
                onResizeStart?.Invoke();
                window.BeginResizeDrag(edge, e);
                e.Handled = true;   // never a move-drag as well
            };
            return grip;
        }

        const double t = Thickness, c = Thickness * 2;
        yield return Grip(WindowEdge.West, HorizontalAlignment.Left, VerticalAlignment.Stretch, t, double.NaN, StandardCursorType.LeftSide);
        yield return Grip(WindowEdge.East, HorizontalAlignment.Right, VerticalAlignment.Stretch, t, double.NaN, StandardCursorType.RightSide);
        yield return Grip(WindowEdge.North, HorizontalAlignment.Stretch, VerticalAlignment.Top, double.NaN, t, StandardCursorType.TopSide);
        yield return Grip(WindowEdge.South, HorizontalAlignment.Stretch, VerticalAlignment.Bottom, double.NaN, t, StandardCursorType.BottomSide);
        yield return Grip(WindowEdge.NorthWest, HorizontalAlignment.Left, VerticalAlignment.Top, c, c, StandardCursorType.TopLeftCorner);
        yield return Grip(WindowEdge.NorthEast, HorizontalAlignment.Right, VerticalAlignment.Top, c, c, StandardCursorType.TopRightCorner);
        yield return Grip(WindowEdge.SouthWest, HorizontalAlignment.Left, VerticalAlignment.Bottom, c, c, StandardCursorType.BottomLeftCorner);
        yield return Grip(WindowEdge.SouthEast, HorizontalAlignment.Right, VerticalAlignment.Bottom, c, c, StandardCursorType.BottomRightCorner);
    }
}
