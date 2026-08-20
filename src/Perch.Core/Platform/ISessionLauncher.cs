using Perch.Data;

namespace Perch.Platform;

/// <summary>
/// Launches a fresh terminal that resumes a past Claude Code session (<c>claude --resume &lt;id&gt;</c>) in
/// its working directory. Inherently OS-specific — Windows Terminal / cmd on Windows, Terminal.app on
/// macOS — so it lives behind this seam and is resolved through the app's composition root. The caller
/// falls back to copying the resume command to the clipboard when <see cref="Reopen"/> reports it couldn't
/// launch one.
/// </summary>
public interface ISessionLauncher
{
    /// <summary>
    /// Opens a new terminal in <paramref name="cwd"/> running <c>claude --resume &lt;sessionId&gt;</c>, using
    /// the user's preferred <paramref name="terminal"/> (falling back to a plain console if that specific
    /// one can't be launched). Returns true if a terminal was launched; false when none could be found (or
    /// the platform doesn't implement this yet), so the caller can degrade to copying the command instead.
    /// Best-effort; never throws.
    /// </summary>
    bool Reopen(string cwd, string sessionId, TerminalApp terminal);

    /// <summary>
    /// Launches (or re-activates) the Claude Desktop app — the host of a <c>claude-desktop</c> session that
    /// isn't showing a window. Activating an already-running instance simply brings its window forward, so
    /// this doubles as "un-hide the app closed to the tray". Returns true when the launch was issued, false
    /// when Claude Desktop couldn't be found (not installed) or the platform doesn't implement this yet, so
    /// the caller can tell the user rather than leave a click that does nothing. Best-effort; never throws.
    /// </summary>
    bool OpenClaudeDesktop();
}
