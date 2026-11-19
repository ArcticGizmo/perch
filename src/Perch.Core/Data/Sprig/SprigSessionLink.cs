namespace Perch.Data.Sprig;

/// <summary>
/// Which sprig workspace a Claude Code session is working in, so its overlay row can show that workspace's state.
/// </summary>
/// <remarks>
/// Three signals, strongest first:
/// <list type="number">
/// <item>The workspace's <see cref="SprigWorkspace.MadeForAgent"/> is the session's id: sprig's worktree hook made
/// that slot for this very session.</item>
/// <item>The worktree the session is working in (Perch's own <see cref="SessionWorktreeInfo"/>, which follows edits
/// into a worktree the session made) is inside one of the workspace's <see cref="SprigWorkspace.Dirs"/>.</item>
/// <item>The session's folder is inside one of them.</item>
/// </list>
/// Among folder matches the deepest directory wins. Free slots never match: their worktrees sit on a detached
/// HEAD waiting for the next branch, so a session there isn't working on the slot's workspace.
/// </remarks>
internal static class SprigSessionLink
{
    public static SprigWorkspace? Find(SprigStatus status, string sessionId, string? cwd, string? worktreeDir)
    {
        if (!status.Seen || status.Workspaces.Count == 0) return null;

        foreach (var w in status.Workspaces)
            if (!w.IsFree && w.MadeForAgent is { Length: > 0 } agent && string.Equals(agent, sessionId, StringComparison.OrdinalIgnoreCase))
                return w;

        return Deepest(status, worktreeDir) ?? Deepest(status, cwd);
    }

    // The workspace whose directory holding `path` is the longest, or null.
    private static SprigWorkspace? Deepest(SprigStatus status, string? path)
    {
        if (Normalize(path) is not { } p) return null;
        SprigWorkspace? best = null;
        int bestLength = -1;
        foreach (var w in status.Workspaces)
        {
            if (w.IsFree) continue;
            foreach (var raw in w.Dirs)
            {
                if (Normalize(raw) is not { } dir || dir.Length <= bestLength || !IsUnder(p, dir)) continue;
                best = w;
                bestLength = dir.Length;
            }
        }
        return best;
    }

    /// <summary>Whether <paramref name="path"/> is <paramref name="dir"/> or inside it, on a separator boundary (so
    /// <c>shop--10</c> isn't inside <c>shop--1</c>). Both are already normalized.</summary>
    internal static bool IsUnder(string path, string dir)
    {
        var cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!path.StartsWith(dir, cmp)) return false;
        return path.Length == dir.Length || path[dir.Length] == Path.DirectorySeparatorChar;
    }

    internal static string? Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            var full = Path.GetFullPath(path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar));
            // A drive or filesystem root keeps its separator; anything else loses a trailing one.
            return full.Length > (Path.GetPathRoot(full)?.Length ?? 0) ? full.TrimEnd(Path.DirectorySeparatorChar) : full;
        }
        catch
        {
            return null;
        }
    }
}
