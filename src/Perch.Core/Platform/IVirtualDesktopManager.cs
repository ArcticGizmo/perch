namespace Perch.Platform;

/// <summary>
/// Pulls one of the app's <em>own</em> top-level windows onto the virtual desktop the user is currently
/// viewing. Windows (and, in principle, any multi-desktop shell) lets a window live on a virtual desktop
/// other than the one on screen; re-focusing such a window would ordinarily drag the user across to it —
/// the behaviour <see cref="IWindowActivator.FocusAppWindowForProcess"/> deliberately wants for "jump back
/// to the call I'm in". For a reused single-instance window (Settings, History, the Markdown viewer) the
/// wanted behaviour is the reverse — bring the window to <em>here</em>. Inherently OS-specific and resolved
/// through the app's composition root, so no UI code touches the interop.
/// </summary>
public interface IVirtualDesktopManager
{
    /// <summary>
    /// If the window identified by <paramref name="windowHandle"/> (a native top-level handle owned by this
    /// process) sits on a virtual desktop other than the one currently displayed, moves it onto the current
    /// desktop. A no-op when the window is already on the current desktop, when the OS exposes no virtual
    /// desktops, when the handle is zero/unknown, or on any interop failure. Best-effort; never throws.
    /// Returns <c>true</c> only when a move was actually performed.
    /// </summary>
    bool MoveWindowToCurrentDesktop(nint windowHandle);
}
