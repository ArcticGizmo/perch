namespace Perch.Platform;

/// <summary>
/// Applies the native window styles Avalonia doesn't expose directly, on the OS window handle Avalonia
/// hands back from <c>TryGetPlatformHandle()</c>: a tool window that never takes activation (so showing
/// an overlay never steals focus from the terminal the user is typing in), and a fully click-through
/// variant for the ambient overlays (glow, confetti, dense drop-zone) that must never intercept the mouse.
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
}
