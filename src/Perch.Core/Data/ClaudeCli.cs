namespace Perch.Data;

/// <summary>
/// Small helpers for constructing Claude Code CLI invocations, shared so the terminal launcher
/// (<see cref="Perch.Platform.ISessionLauncher"/>) and the "copy resume command" clipboard path can never
/// drift apart.
/// </summary>
public static class ClaudeCli
{
    /// <summary>The command that resumes an existing session by id: <c>claude --resume &lt;sessionId&gt;</c>.</summary>
    public static string ResumeCommand(string sessionId) => $"claude --resume {sessionId}";
}
