namespace Perch.App;

/// <summary>
/// The "single reused top-level window" idiom the overlay context applies to its settings, history and
/// stats windows: if the instance is still alive, un-minimise and focus it; otherwise create one, null
/// the owner's handle to it on close, and show it. Centralised so every reused window behaves the same
/// (see CLAUDE.md: "Single reused window instances … Wire any new top-level window into all three").
/// </summary>
internal static class WindowHost
{
    /// <summary>
    /// Returns the live-or-newly-created window. When <paramref name="existing"/> is still alive it is
    /// un-minimised, <paramref name="refresh"/>'d, and brought to the front. Otherwise
    /// <paramref name="factory"/> builds one, <paramref name="onClosed"/> is wired to its
    /// <see cref="Form.FormClosed"/> (to null the owner's field), <paramref name="beforeShow"/> runs the
    /// one-time setup that must precede <see cref="Form.Show()"/> (e.g. event wiring), the window is
    /// shown, then <paramref name="refresh"/> runs. <paramref name="refresh"/> runs on both paths, so
    /// "point the window at the current data" lives in one place.
    /// </summary>
    public static T ShowOrFocus<T>(T? existing, Func<T> factory, Action onClosed,
        Action<T>? beforeShow = null, Action<T>? refresh = null) where T : Form
    {
        if (existing is { IsDisposed: false })
        {
            if (existing.WindowState == FormWindowState.Minimized)
                existing.WindowState = FormWindowState.Normal;
            refresh?.Invoke(existing);
            existing.Activate();
            existing.BringToFront();
            return existing;
        }

        var form = factory();
        form.FormClosed += (_, _) => onClosed();
        beforeShow?.Invoke(form);
        form.Show();
        refresh?.Invoke(form);
        form.Activate();
        return form;
    }
}
