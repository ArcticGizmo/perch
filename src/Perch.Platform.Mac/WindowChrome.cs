using Perch.Platform;

namespace Perch.Platform.Mac;

/// <summary>
/// macOS <see cref="IWindowChrome"/>: applies the overlay windows' NSWindow behaviour that Avalonia
/// doesn't expose directly. The macOS analogue of the Windows "no-activate tool window" is a window that
/// floats above normal windows, joins every Space, stays put during Mission Control, and is skipped by
/// Cmd-` window cycling — set via <c>collectionBehavior</c> and the window <c>level</c>. Click-through
/// (for the ambient glow/confetti overlays) maps to <c>setIgnoresMouseEvents:</c>.
///
/// Avalonia's platform handle for a window on macOS is its top-level <c>NSView</c>; the <c>NSWindow</c> is
/// <c>[view window]</c>. All the sends here are void setters taking a primitive — no struct returns — so
/// they work identically on arm64 and x86_64.
///
/// NOTE (Phase 3): written against the AppKit docs but not yet verified on a Mac. If Avalonia ends up
/// managing the window level itself and fights this, drop the <c>setLevel:</c> line (Topmost already
/// elevates the window) and keep the collection-behaviour / click-through bits.
/// </summary>
public sealed class WindowChrome : IWindowChrome
{
    // NSWindowLevel: float above normal document windows. (NSStatusWindowLevel == 25.)
    private const int NSStatusWindowLevel = 25;

    // NSWindowCollectionBehavior bits.
    private const int CanJoinAllSpaces = 1 << 0; // 1
    private const int Stationary       = 1 << 4; // 16 — don't shuffle in Mission Control
    private const int IgnoresCycle     = 1 << 6; // 64 — skip Cmd-` window cycling
    private const int FullScreenAux    = 1 << 8; // 256 — allowed over a fullscreen app
    private const int OverlayBehavior  = CanJoinAllSpaces | Stationary | IgnoresCycle | FullScreenAux;

    public void MakeToolWindowNoActivate(IntPtr handle) => Configure(handle, clickThrough: false);
    public void MakeClickThroughNoActivate(IntPtr handle) => Configure(handle, clickThrough: true);

    /// <summary>Lifts the window to the front of its level without activating it, so a hint shows above
    /// the overlay. <c>orderFrontRegardless</c> raises a window without making it key/main. Best-effort.</summary>
    public void BringToTopNoActivate(IntPtr nsView)
    {
        if (nsView == IntPtr.Zero) return;
        try
        {
            IntPtr window = ObjC.SendGet(nsView, ObjC.Sel("window"));
            if (window != IntPtr.Zero) ObjC.SendVoid(window, ObjC.Sel("orderFrontRegardless"));
        }
        catch { /* best-effort */ }
    }

    private static void Configure(IntPtr nsView, bool clickThrough)
    {
        if (nsView == IntPtr.Zero) return;
        try
        {
            IntPtr window = ObjC.SendGet(nsView, ObjC.Sel("window"));
            if (window == IntPtr.Zero) return;

            ObjC.SendVoid(window, ObjC.Sel("setLevel:"), (nint)NSStatusWindowLevel);
            ObjC.SendVoid(window, ObjC.Sel("setCollectionBehavior:"), (nuint)OverlayBehavior);
            ObjC.SendVoid(window, ObjC.Sel("setHidesOnDeactivate:"), (byte)0);

            if (clickThrough)
                ObjC.SendVoid(window, ObjC.Sel("setIgnoresMouseEvents:"), (byte)1);
        }
        catch { /* best-effort: leave Avalonia's defaults if the runtime interop misbehaves */ }
    }
}
