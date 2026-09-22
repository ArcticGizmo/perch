namespace Perch.Statusline;

using System.IO;

/// <summary>
/// Reads the current git branch straight off disk — no <c>git</c> subprocess — so it's cheap enough to
/// run on every statusline refresh (which can fire every ~300ms). Walks up from a directory to the
/// nearest <c>.git</c>, reads <c>HEAD</c>, and returns the short branch name. Detached HEAD, a bare
/// short hash, worktree <c>.git</c> files, and any IO error all return null (the template's
/// <c>{{#if git.branch}}</c> then just renders nothing).
/// </summary>
internal static class GitHead
{
    public static string? ReadBranch(string? startDir)
    {
        try
        {
            var gitDir = FindGitDir(startDir);
            if (gitDir is null) return null;

            var head = Path.Combine(gitDir, "HEAD");
            if (!File.Exists(head)) return null;

            var text = File.ReadAllText(head).Trim();
            // "ref: refs/heads/<branch>" — the common case. A raw 40-char hash is a detached HEAD.
            const string prefix = "ref: refs/heads/";
            if (text.StartsWith(prefix, System.StringComparison.Ordinal))
            {
                var name = text[prefix.Length..].Trim();
                return name.Length > 0 ? name : null;
            }
            return null;
        }
        catch
        {
            return null;
        }
    }

    // The nearest ancestor directory's `.git`. Handles both a real `.git` directory and a `.git` *file*
    // (submodules / linked worktrees) whose "gitdir: <path>" points elsewhere.
    private static string? FindGitDir(string? startDir)
    {
        var dir = startDir;
        while (!string.IsNullOrEmpty(dir))
        {
            var candidate = Path.Combine(dir, ".git");
            if (Directory.Exists(candidate)) return candidate;
            if (File.Exists(candidate))
            {
                var line = File.ReadAllText(candidate).Trim();
                const string p = "gitdir:";
                if (line.StartsWith(p, System.StringComparison.Ordinal))
                {
                    var target = line[p.Length..].Trim();
                    var resolved = Path.IsPathRooted(target) ? target : Path.GetFullPath(Path.Combine(dir, target));
                    return Directory.Exists(resolved) ? resolved : null;
                }
                return null;
            }
            dir = Path.GetDirectoryName(dir);
        }
        return null;
    }
}
