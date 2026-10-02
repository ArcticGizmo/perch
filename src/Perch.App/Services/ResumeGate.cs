using Avalonia.Controls;
using Perch.Avalonia.Windows;
using Perch.Data;
using Perch.Data.Control;

namespace Perch.Avalonia.Services;

/// <summary>
/// The checks every resume of an existing session runs before Perch spawns <c>claude --resume</c> for it — from a
/// session window's start or wake, and from a dormant Roost pane's first send (docs/session-recovery-plan.md, R6). One
/// copy, so the two surfaces can't drift apart on a safety gate.
/// </summary>
internal static class ResumeGate
{
    /// <summary>Why Perch mustn't drive <paramref name="sessionId"/> right now (docs/session-ui-plan.md §Phase 4 (a)):
    /// it's running in a real terminal, or under another Perch instance. Null when it's safe. The lock is looked for in
    /// the session's own config dir, where the controller writes it (review fixes CP13).</summary>
    public static string? Refusal(string sessionId, string? configDir, Func<string, ClaudeSession?>? liveLookup)
    {
        if (liveLookup?.Invoke(sessionId) is { IsPerchControlled: false } live)
            return $"{live.DisplayName} is live in a terminal (PID {live.Pid}) — Perch can't take it over while " +
                   "it's running. Close it there, or use “Elevate to Perch” on its overlay row.";
        if (SessionLock.HeldByOther(sessionId, SessionLock.SessionsDirFor(configDir)) is { } other)
            return $"session {Shorten(sessionId)} is already controlled by {other.Profile} (PID {other.Pid}).";
        return null;
    }

    /// <summary>Claude Code's folder-trust question. Perch launches Claude headlessly (<c>-p</c>), where Claude skips
    /// its own dialog, so Perch asks before spawning in a folder that isn't trusted yet.</summary>
    public static Task<bool> ConfirmTrustAsync(Window owner, string cwd) =>
        ConfirmDialog.ShowAsync(owner,
            "Do you trust the files in this folder?",
            $"{cwd}\n\nQuick safety check: is this a project you created or one you trust — like your own " +
            "code, a well-known open-source project, or work from your team? Claude Code will be able to " +
            "read, edit, and execute files in this folder. If you're not sure, review what's in it first.",
            "Yes, proceed", "No, cancel");

    private static string Shorten(string id) => id.Length > 8 ? id[..8] : id;
}
