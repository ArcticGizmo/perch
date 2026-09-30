namespace Perch.Data;

/// <summary>
/// Which file-system events under a repo's working tree should refresh the Change Review window (review fixes
/// CP24). Its watcher is recursive, so without a filter a dependency install, or git's own object writes during a
/// fetch or gc, would keep re-running <c>git status</c>. Ignored: anything under <c>node_modules</c>, and
/// <c>.git</c> internals other than the ones that change what the window shows: <c>HEAD</c>, <c>index</c>,
/// <c>packed-refs</c> and <c>refs/</c>. Build output (<c>bin</c>, <c>obj</c>) is deliberately <em>not</em>
/// ignored: plenty of repos track a <c>bin/</c> of scripts, and a build storm costs a single refresh once the
/// watcher's events are coalesced.
/// </summary>
internal static class RepoWatchFilter
{
    /// <summary>True when an event at <paramref name="relativePath"/> (relative to the watched folder, either
    /// separator) should trigger a refresh. An unknown path does, to be safe.</summary>
    public static bool IsRelevant(string? relativePath)
    {
        if (string.IsNullOrEmpty(relativePath)) return true;
        var parts = relativePath.Split('/', '\\');
        for (int i = 0; i < parts.Length; i++)
        {
            if (parts[i].Equals("node_modules", StringComparison.OrdinalIgnoreCase)) return false;
            if (!parts[i].Equals(".git", StringComparison.OrdinalIgnoreCase)) continue;

            // Inside .git: .git itself (created, deleted, or a worktree's .git file), and the refs/HEAD/index.
            int rest = parts.Length - (i + 1);
            if (rest == 0) return true;
            var inner = parts[i + 1];
            if (inner == "refs") return true;
            return rest == 1 && inner is "HEAD" or "index" or "packed-refs";
        }
        return true;
    }
}
