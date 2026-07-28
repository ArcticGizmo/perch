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
    /// <para>
    /// Returns <c>true</c> when a host window was resolved and brought forward (including one that had to
    /// be un-hidden first), <c>false</c> when the session has no host window to focus at all. A live
    /// session can genuinely have none — its terminal window may have been hidden or torn down while the
    /// process kept running — and silently doing nothing reads as a broken click, so the caller is
    /// expected to tell the user rather than discard this.
    /// </para>
    /// </summary>
    bool FocusTerminalForProcess(int pid, string? projectHint = null);

    /// <summary>Brings the main window of the process identified by <paramref name="pid"/> to the
    /// foreground (used to re-focus an already-running quick-link app). Best-effort; never throws.</summary>
    void FocusProcessMainWindow(int pid);
}
