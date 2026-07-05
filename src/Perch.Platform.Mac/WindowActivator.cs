using Perch.Platform;

namespace Perch.Platform.Mac;

/// <summary>
/// macOS <see cref="IWindowActivator"/>. Phase 3: walk the parent-pid chain via <c>libproc</c>, then
/// raise the owning terminal/IDE app through the Accessibility API (<c>AXUIElement</c>) or scoped
/// AppleScript — requires the Accessibility permission. Best-effort per terminal, like the Windows
/// ConPTY caveat. Stub for now: no-op.
/// </summary>
public sealed class WindowActivator : IWindowActivator
{
    public void FocusTerminalForProcess(int pid, string? projectHint = null) { }
    public void FocusProcessMainWindow(int pid) { }
}
