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
            return $"session {PerchSession.Shorten(sessionId)} is already controlled by {other.Profile} (PID {other.Pid}).";
        return null;
    }

    /// <summary>The first half of the folder-trust gate: the config dir the spawn will pin (<paramref name="configDir"/>
    /// — a resume's transcript owner, or a fresh session's chosen account), and whether <paramref name="cwd"/> is trusted
    /// in it — including via an ancestor, or an earlier yes in Claude Code itself. Runs off the UI thread (the store can
    /// be large, and finding a transcript can mean a search). Throws when either can't be read; the caller decides.</summary>
    public static Task<(string? ConfigDir, bool Trusted)> ReadTrustAsync(string cwd, Func<string?> configDir) =>
        Task.Run(() =>
        {
            var dir = configDir();
            return (dir, DirectoryTrust.Evaluate(dir, cwd));
        });

    /// <summary>The second half: Claude Code's folder-trust question, for a folder that isn't trusted yet (Perch launches
    /// Claude headlessly, <c>-p</c>, where Claude skips its own dialog). A yes is recorded in <paramref name="configDir"/>
    /// — the same store Claude Code reads — so this folder and its subfolders don't ask again.</summary>
    public static async Task<bool> ConfirmTrustAsync(Window owner, string? configDir, string cwd)
    {
        bool ok = await ConfirmDialog.ShowAsync(owner,
            "Do you trust the files in this folder?",
            $"{cwd}\n\nQuick safety check: is this a project you created or one you trust — like your own " +
            "code, a well-known open-source project, or work from your team? Claude Code will be able to " +
            "read, edit, and execute files in this folder. If you're not sure, review what's in it first.",
            "Yes, proceed", "No, cancel");
        if (ok) _ = Task.Run(() => DirectoryTrust.Grant(configDir, cwd));
        return ok;
    }
}
