using Perch.Platform;

namespace Perch.Platform.Mac;

/// <summary>
/// macOS <see cref="IVirtualDesktopManager"/>: no-op. macOS has Spaces, but the window server offers no
/// public API to move an arbitrary window to the active Space; the reused-window flow raises the window
/// app-level instead (which the system follows to its Space). Left as a stub so the Windows behaviour has a
/// home to grow into if a public/scriptable path appears.
/// </summary>
public sealed class VirtualDesktopManager : IVirtualDesktopManager
{
    public bool MoveWindowToCurrentDesktop(nint windowHandle) => false;
}
