namespace Perch.Platform;

/// <summary>
/// Brings a session's terminal (or an arbitrary process's main window) to the foreground. Inherently
/// OS-specific — on Windows it walks the process ancestry to find the hosting terminal/IDE window; on
/// other platforms it degrades to a best-effort activation or a no-op. Implemented per platform and
/// resolved by the app's composition root, so neither UI toolkit hard-codes the interop.
/// </summary>
public interface IWindowActivator
{
    /// <summary>
    /// Focuses the host window of the Claude Code session running under <paramref name="pid"/>.
    /// <paramref name="projectHint"/> (the session's project name) disambiguates a host — e.g. VS Code
    /// or Rider — that owns several project windows in one process. Best-effort; never throws.
    /// </summary>
    void FocusTerminalForProcess(int pid, string? projectHint = null);

    /// <summary>Brings the main window of the process identified by <paramref name="pid"/> to the
    /// foreground (used to re-focus an already-running quick-link app). Best-effort; never throws.</summary>
    void FocusProcessMainWindow(int pid);
}
