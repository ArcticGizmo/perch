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
}
