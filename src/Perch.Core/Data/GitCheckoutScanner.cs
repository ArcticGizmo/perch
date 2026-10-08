namespace Perch.Data;

/// <summary>
/// Finds the git checkouts behind a set of folders (the folders Claude sessions ran in) and reads their remotes, for
/// <see cref="RepoCheckoutResolver"/>. It reads <c>.git/config</c> directly rather than running <c>git</c> per
/// folder: a user can have dozens of project folders, and a process per folder (twice) is slow and noisy for no
/// gain. Linked worktrees and submodules (<c>.git</c> as a <c>gitdir:</c> file) are followed to the shared config.
///
/// <para>Blocking file IO: call it off the UI thread. Best-effort: an unreadable folder is skipped, never thrown.</para>
/// </summary>
public static class GitCheckoutScanner
{
    /// <summary>The distinct checkout roots under <paramref name="folders"/>, each with its remotes. Folders outside
    /// any checkout, and checkouts with no remotes, are left out.</summary>
    public static IReadOnlyList<CheckoutCandidate> Scan(IEnumerable<string> folders)
    {
        var roots = new Dictionary<string, CheckoutCandidate>(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in folders)
        {
            try
            {
                if (FindRoot(folder) is not { } root || roots.ContainsKey(root)) continue;
                var remotes = ReadRemotes(root);
                if (remotes.Count > 0) roots[root] = new CheckoutCandidate(root, remotes);
            }
            catch { }
        }
        return roots.Values.ToList();
    }

    /// <summary>The nearest folder at or above <paramref name="dir"/> holding a <c>.git</c> directory or file.</summary>
    public static string? FindRoot(string dir)
    {
        if (string.IsNullOrWhiteSpace(dir)) return null;
        for (var d = new DirectoryInfo(Path.GetFullPath(dir)); d is not null; d = d.Parent)
        {
            var git = Path.Combine(d.FullName, ".git");
            if (Directory.Exists(git) || File.Exists(git)) return d.FullName.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        return null;
    }

    /// <summary>The remotes configured for the checkout at <paramref name="root"/>, read from its git config.</summary>
    public static IReadOnlyList<GitRemoteEntry> ReadRemotes(string root) =>
        ConfigPath(root) is { } path && File.Exists(path) ? ParseRemotes(File.ReadAllText(path)) : [];

    // .git/config for an ordinary checkout. For a ".git" file ("gitdir: <path>", a linked worktree or submodule),
    // the config lives in that gitdir — or, for a linked worktree, in the shared dir its "commondir" file names.
    internal static string? ConfigPath(string root)
    {
        var git = Path.Combine(root, ".git");
        if (Directory.Exists(git)) return Path.Combine(git, "config");
        if (!File.Exists(git)) return null;

        var line = File.ReadLines(git).FirstOrDefault(l => l.StartsWith("gitdir:", StringComparison.OrdinalIgnoreCase));
        if (line is null) return null;
        var gitDir = Path.GetFullPath(Path.Combine(root, line["gitdir:".Length..].Trim()));
        var common = Path.Combine(gitDir, "commondir");
        if (File.Exists(common))
        {
            var rel = File.ReadAllText(common).Trim();
            if (rel.Length > 0) gitDir = Path.GetFullPath(Path.Combine(gitDir, rel));
        }
        return Path.Combine(gitDir, "config");
    }

    /// <summary>
    /// The <c>[remote "name"] url = …</c> entries of a git config file, in file order (a remote with several urls
    /// yields several entries). Section and key names are case-insensitive; <c>#</c>/<c>;</c> comments and quoted
    /// values are handled. Pure.
    /// </summary>
    internal static IReadOnlyList<GitRemoteEntry> ParseRemotes(string config)
    {
        var list = new List<GitRemoteEntry>();
        string? remote = null;
        foreach (var raw in config.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] is '#' or ';') continue;
            if (line[0] == '[')
            {
                remote = null;
                var close = line.IndexOf(']');
                if (close < 0) continue;
                var header = line[1..close].Trim();
                if (header.StartsWith("remote", StringComparison.OrdinalIgnoreCase)
                    && header[6..].Trim() is { Length: >= 2 } name && name[0] == '"' && name[^1] == '"')
                    remote = name[1..^1];
                continue;
            }
            if (remote is null) continue;
            var eq = line.IndexOf('=');
            if (eq < 0 || !line[..eq].Trim().Equals("url", StringComparison.OrdinalIgnoreCase)) continue;
            var value = StripComment(line[(eq + 1)..].Trim());
            if (value.Length >= 2 && value[0] == '"' && value[^1] == '"') value = value[1..^1];
            if (value.Length > 0) list.Add(new GitRemoteEntry(remote, value));
        }
        return list;
    }

    // An unquoted value ends at an inline comment.
    private static string StripComment(string value)
    {
        if (value.StartsWith('"')) return value;
        int cut = value.IndexOfAny(['#', ';']);
        return (cut >= 0 ? value[..cut] : value).Trim();
    }
}
