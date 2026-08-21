namespace Perch.Platform;

/// <summary>
/// Applies the native window styles Avalonia doesn't expose directly, on the OS window handle Avalonia
/// hands back from <c>TryGetPlatformHandle()</c>: a tool window that never takes activation (so showing
/// an overlay never steals focus from the terminal the user is typing in), and a fully click-through
/// variant for the ambient overlays (glow, dense drop-zone) that must never intercept the mouse.
///
/// On Windows these map to <c>WS_EX_*</c> extended styles; other platforms use their own window flags
/// (macOS: <c>NSWindow</c> level / <c>collectionBehavior</c> / <c>ignoresMouseEvents</c>). Every method is
/// best-effort and never throws; the off-platform / no-op implementation ignores the call, so UI code
/// goes through the seam without any per-OS branching of its own. Resolved by the app's composition root.
/// </summary>
public interface IWindowChrome
{
    /// <summary>Marks the window as a no-activate tool window (no Alt+Tab / app-switcher entry, never
    /// takes focus). Best-effort; a zero handle is ignored.</summary>
    void MakeToolWindowNoActivate(IntPtr handle);

    /// <summary>Marks the window click-through (transparent to the mouse) as well as no-activate
    /// tool-window — for the ambient overlays that must never intercept input or take focus.
    /// Best-effort; a zero handle is ignored.</summary>
    void MakeClickThroughNoActivate(IntPtr handle);

    /// <summary>Raises the window to the top of the topmost z-order band <em>without</em> activating it,
    /// so a non-activating tooltip/hint shows above another always-on-top window (e.g. the overlay)
    /// instead of behind it. Best-effort; a zero handle is ignored.</summary>
    void BringToTopNoActivate(IntPtr handle);

    /// <summary>Sets whether the OS window manager rounds the window's outer corners. Windows 11 rounds
    /// borderless top-level windows by default, which no amount of owner-drawn squaring can undo — the
    /// docked full-height column wants square corners flush to the screen edge, so it turns this off (and
    /// back on when it floats again). On Windows this is <c>DWMWA_WINDOW_CORNER_PREFERENCE</c>; elsewhere a
    /// no-op. Best-effort; a zero handle is ignored.</summary>
    void SetWindowCornerPreference(IntPtr handle, bool rounded);

    /// <summary>Reads, live from the OS, the geometry of the monitor containing the point <paramref name="x"/>,
    /// <paramref name="y"/> (physical pixels) — nearest monitor if the point is off every screen: its full
    /// bounds and work area (taskbar excluded) in physical pixels, and the display scale. Bypasses the UI
    /// framework's cached screen list, which can serve a stale work area after a resolution change (leaving
    /// the docked column too tall, drooping under the taskbar). Null when unavailable (off-platform stub).</summary>
    MonitorGeometry? GetMonitorGeometryAt(int x, int y);

    /// <summary>Forces the window to the foreground and gives it keyboard focus, working around the OS
    /// foreground-lock that otherwise stops a background tray process from stealing focus (needed by the
    /// session switcher, which a global hotkey summons and which must accept typing immediately). Unlike
    /// the no-activate helpers above, this one deliberately activates. Best-effort; a zero handle is
    /// ignored.</summary>
    void ForceForeground(IntPtr handle);
}
