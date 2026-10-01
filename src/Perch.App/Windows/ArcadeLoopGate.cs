using Avalonia.Controls;

namespace Perch.Avalonia.Windows;

/// <summary>
/// Runs an arcade window's ~60fps loop only while the window is in front: paused when it's deactivated or
/// minimised, resumed when it's active again. The games count time in ticks, so a pause simply freezes play.
/// The window still starts its loop in <c>OnOpened</c> and stops it in <c>OnClosed</c>; this only adds the
/// pause and resume in between. Review fixes CP24.
/// </summary>
internal static class ArcadeLoopGate
{
    public static void Attach(Window window, Action begin, Action stop)
    {
        bool closed = false;
        window.Closed += (_, _) => closed = true;
        window.Activated += (_, _) =>
        {
            if (!closed && window.WindowState != WindowState.Minimized) begin();
        };
        window.Deactivated += (_, _) => stop();
        window.PropertyChanged += (_, e) =>
        {
            if (e.Property != Window.WindowStateProperty || closed) return;
            if (window.WindowState == WindowState.Minimized) stop();
            else if (window.IsActive) begin();
        };
    }
}
