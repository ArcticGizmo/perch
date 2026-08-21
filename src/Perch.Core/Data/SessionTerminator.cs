using System.Diagnostics;
using System.Text.Json.Nodes;

namespace Perch.Data;

/// <summary>The outcome of a <see cref="SessionTerminator.Terminate"/> attempt, so the caller can tell the
/// user what actually happened rather than guessing.</summary>
public enum TerminateResult
{
    /// <summary>The session process (and its descendants) were killed.</summary>
    Terminated = 0,
    /// <summary>The process had already exited — nothing to do. The next scan drops the row.</summary>
    AlreadyGone = 1,
    /// <summary>Refused: the PID no longer belongs to the session Perch listed (see the identity check in
    /// <see cref="SessionTerminator.Terminate"/>). Nothing was killed.</summary>
    NotTheSession = 2,
    /// <summary>The kill was attempted and failed — typically access denied (a session running elevated
    /// while Perch is not).</summary>
    Failed = 3,
}

/// <summary>
/// Terminates a Claude Code session by killing its process tree. Deliberately plain <see cref="Process"/>
/// work rather than a platform seam: <see cref="Process.Kill(bool)"/> is cross-platform, and Core already
/// kills child processes this way (see <c>ClaudeCodePluginManager</c>, <c>GitStatsService</c>), so there is no
/// OS-specific behaviour to hide behind an interface.
/// </summary>
public static class SessionTerminator
{
    // How far a live process's start time may sit from the session file's recorded startedAt and still be
    // accepted as the same process. Claude Code writes startedAt a beat *after* the process starts (~2s in
    // practice), so this can't be exact — but a recycled PID is a different process launched minutes or
    // hours later, which this comfortably separates.
    private static readonly TimeSpan StartTimeTolerance = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Kills the session running under <paramref name="pid"/> along with its descendants (MCP servers,
    /// spawned shells), so nothing is left orphaned behind it.
    ///
    /// <para>Before killing anything it re-checks that the PID still belongs to the session Perch listed,
    /// by comparing the live process's start time against the <c>startedAt</c> in that session's
    /// <c>{pid}.json</c>. This matters because the session file is keyed by PID and outlives an unclean
    /// exit: an unrelated process that inherited a recycled PID would otherwise be killed on the user's
    /// behalf. On Windows the hazard is concrete — the Claude <em>desktop app</em> is also
    /// <c>claude.exe</c>, so a name check alone would not save it. When identity can't be established the
    /// kill is refused (<see cref="TerminateResult.NotTheSession"/>) rather than risked.</para>
    ///
    /// Never throws.
    /// </summary>
    public static TerminateResult Terminate(string pid)
    {
        if (!int.TryParse(pid, out int id) || id <= 0)
            return TerminateResult.NotTheSession;

        Process process;
        try
        {
            process = Process.GetProcessById(id);
        }
        catch
        {
            // No such process — it exited between the scan and this click.
            return TerminateResult.AlreadyGone;
        }

        using (process)
        {
            if (!IsRecordedSession(process, id))
                return TerminateResult.NotTheSession;

            try
            {
                if (process.HasExited)
                    return TerminateResult.AlreadyGone;
                process.Kill(entireProcessTree: true);
                return TerminateResult.Terminated;
            }
            catch
            {
                // Raced to exit on its own, or access denied (an elevated session under a non-elevated
                // Perch). Re-reading HasExited tells the two apart so the caller reports the right thing.
                try { if (process.HasExited) return TerminateResult.AlreadyGone; } catch { }
                return TerminateResult.Failed;
            }
        }
    }

    // True when `process` is the same process the session file for `id` describes, judged by start time.
    // A missing/unreadable session file or an unreadable start time means we can't establish identity, so
    // this returns false and the caller refuses the kill.
    private static bool IsRecordedSession(Process process, int id)
    {
        if (ReadStartedAt(id) is not { } startedAt)
            return false;

        try
        {
            return (process.StartTime - startedAt).Duration() <= StartTimeTolerance;
        }
        catch
        {
            // StartTime throws for a process we can't open far enough to read it.
            return false;
        }
    }

    // The startedAt from ~/.claude/sessions/{pid}.json as a local time, or null when the file is absent,
    // unparseable, or carries no usable startedAt. Read with FileShare.ReadWrite like every other reader —
    // Claude Code writes these live.
    private static DateTime? ReadStartedAt(int id)
    {
        try
        {
            var path = Path.Combine(ClaudePaths.SessionsDir, $"{id}.json");
            if (!File.Exists(path))
                return null;

            string json;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var reader = new StreamReader(fs))
                json = reader.ReadToEnd();

            var ms = JsonNode.Parse(json)?["startedAt"]?.GetValue<long>() ?? 0;
            return ms > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(ms).LocalDateTime : null;
        }
        catch
        {
            return null;
        }
    }
}
