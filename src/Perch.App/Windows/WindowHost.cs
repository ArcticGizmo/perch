using Avalonia.Controls;

namespace Perch.Avalonia.Windows;

/// <summary>
/// The "single reused top-level window" idiom — the Avalonia counterpart of the WinForms app's
/// <c>WindowHost</c>. If the instance is still open, un-minimise, refresh, and focus it; otherwise
/// build one, wire <see cref="Window.Closed"/> to null the owner's field, show it, then refresh.
/// Centralised so every reused window (history, stats, flight path, …) behaves the same
/// (CLAUDE.md: "Single reused window instances … wire any new top-level window into all three").
/// </summary>
internal static class WindowHost
{
    /// <summary>
    /// Returns the live-or-newly-created window. When <paramref name="existing"/> is still open it is
    /// un-minimised, <paramref name="refresh"/>'d, and activated. Otherwise <paramref name="factory"/>
    /// builds one, <paramref name="onClosed"/> is wired to its <see cref="Window.Closed"/> (to null the
    /// owner's field), it is shown, then <paramref name="refresh"/> runs. <paramref name="refresh"/> runs
    /// on both paths, so "point the window at the current data" lives in one place.
    /// </summary>
    public static T ShowOrFocus<T>(T? existing, Func<T> factory, Action onClosed,
        Action<T>? refresh = null) where T : Window
    {
        // The owner's field is nulled by onClosed, so a non-null reference means the window is still open.
        if (existing is { } alive)
        {
            if (alive.WindowState == WindowState.Minimized) alive.WindowState = WindowState.Normal;
            refresh?.Invoke(alive);
            alive.Activate();
            return alive;
        }

        var window = factory();
        window.Closed += (_, _) => onClosed();
        window.Show();
        refresh?.Invoke(window);
        window.Activate();
        return window;
    }
}
