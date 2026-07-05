using Perch.Platform;

namespace Perch.Platform.Mac;

/// <summary>
/// macOS <see cref="IWindowChrome"/>. Phase 3 will set the <c>NSWindow</c> level /
/// <c>collectionBehavior</c> (keep the overlays out of Mission Control / Spaces cycling) and
/// <c>ignoresMouseEvents</c> (click-through) via AppKit P/Invoke on the window handle.
/// Stub for now: no-op, so the mac head builds and runs.
/// </summary>
public sealed class WindowChrome : IWindowChrome
{
    public void MakeToolWindowNoActivate(IntPtr handle) { }
    public void MakeClickThroughNoActivate(IntPtr handle) { }
}
