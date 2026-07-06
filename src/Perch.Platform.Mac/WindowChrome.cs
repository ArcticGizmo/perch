using Perch.Platform;

namespace Perch.Platform.Mac;

/// <summary>
/// macOS <see cref="IWindowChrome"/>: applies the overlay windows' NSWindow behaviour that Avalonia
/// doesn't expose directly. The macOS analogue of the Windows "no-activate tool window" is a window that
/// floats above normal windows, joins every Space, stays put during Mission Control, and is skipped by
/// Cmd-` window cycling — set via <c>collectionBehavior</c> and the window <c>level</c>. Click-through
/// (for the ambient glow/confetti overlays) maps to <c>setIgnoresMouseEvents:</c>.
///
/// Avalonia's platform handle for a window on macOS is the <c>NSWindow</c> itself (its native window class
/// is <c>AvnWindow</c>, an <c>NSWindow</c> subclass) — <em>not</em> the top-level <c>NSView</c> as the AppKit
/// docs' usual "[view window]" idiom would suggest. <see cref="WindowOf"/> tolerates either: an NSView
/// responds to <c>-window</c> and an NSWindow does not, so we branch on that. Getting this wrong is fatal,
/// not best-effort: an <c>unrecognized selector</c> raises an Objective-C <c>NSException</c> that unwinds
/// through the P/Invoke boundary and terminates the process — a managed <c>try/catch</c> does not stop it.
/// All the sends here are void setters taking a primitive — no struct returns — so they work identically on
/// arm64 and x86_64.
///
/// NOTE (verified on macOS arm64): if Avalonia ends up managing the window level itself and fights this,
/// drop the <c>setLevel:</c> line (Topmost already elevates the window) and keep the collection-behaviour /
/// click-through bits.
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
    public void BringToTopNoActivate(IntPtr handle)
    {
        try
        {
            IntPtr window = WindowOf(handle);
            if (window != IntPtr.Zero) ObjC.SendVoid(window, ObjC.Sel("orderFrontRegardless"));
        }
        catch { /* best-effort */ }
    }

    private static void Configure(IntPtr handle, bool clickThrough)
    {
        try
        {
            IntPtr window = WindowOf(handle);
            if (window == IntPtr.Zero) return;

            ObjC.SendVoid(window, ObjC.Sel("setLevel:"), (nint)NSStatusWindowLevel);
            ObjC.SendVoid(window, ObjC.Sel("setCollectionBehavior:"), (nuint)OverlayBehavior);
            ObjC.SendVoid(window, ObjC.Sel("setHidesOnDeactivate:"), (byte)0);

            if (clickThrough)
                ObjC.SendVoid(window, ObjC.Sel("setIgnoresMouseEvents:"), (byte)1);
        }
        catch { /* best-effort: leave Avalonia's defaults if the runtime interop misbehaves */ }
    }

    // Resolve the NSWindow from Avalonia's platform handle. Avalonia 12 hands back the NSWindow (AvnWindow)
    // directly, but older paths (and the generic AppKit idiom) hand back the top-level NSView. Only an
    // NSView responds to -window, so probe for it: view → [view window]; otherwise the handle is the window.
    private static IntPtr WindowOf(IntPtr handle)
    {
        if (handle == IntPtr.Zero) return IntPtr.Zero;
        IntPtr windowSel = ObjC.Sel("window");
        bool isView = ObjC.SendBool(handle, ObjC.Sel("respondsToSelector:"), windowSel) != 0;
        return isView ? ObjC.SendGet(handle, windowSel) : handle;
    }
}
