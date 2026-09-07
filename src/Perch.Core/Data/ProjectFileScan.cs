namespace Perch.Data;

using System.Diagnostics;
using System.Linq;

/// <summary>All of a project's files, as forward-slashed paths relative to the scanned root, sorted and
/// de-duplicated. <see cref="Truncated"/> is true when the hard file cap was hit, so a caller can note the
/// list isn't exhaustive.</summary>
public sealed record ProjectFiles(IReadOnlyList<string> RelativePaths, bool Truncated)
{
    public static readonly ProjectFiles Empty = new([], false);
}

/// <summary>
/// Enumerates every file in a project directory for the composer's <c>@</c>-mention picker. In a git working
/// tree it shells <c>git ls-files --cached --others --exclude-standard</c>, so <c>.gitignore</c> is honoured
/// for free and the set is bounded by what git tracks/sees. Outside a repo it falls back to a bounded-depth
/// walk that skips the usual noise directories (<c>node_modules</c>, <c>bin</c>, <c>obj</c>, …) and never
/// follows symlinks.
///
/// Best-effort and meant to run off the UI thread: git missing, a timeout, or an unreadable directory all
/// yield <see cref="ProjectFiles.Empty"/> rather than throwing. A hard file cap keeps a monorepo from
/// flooding the picker; hitting it sets <see cref="ProjectFiles.Truncated"/>. This is the all-files sibling
/// of <see cref="MarkdownProjectScan"/> (which restricts to Markdown for the viewer's project pane).
/// </summary>
internal static class ProjectFileScan
{
    private const int GitTimeoutMs = 4000;
    private const int MaxDepth = 8;       // fallback walk: how deep below the root to descend
    private const int MaxFiles = 5000;    // hard ceiling either way, so a huge tree can't flood the picker

    private static readonly string[] SkipDirs =
        [".git", "node_modules", "bin", "obj", ".venv", "venv", "dist", "build", ".next", "target", ".idea", ".vs", ".gradle"];

    /// <summary>Every file under <paramref name="cwd"/>. Uses git (honouring <c>.gitignore</c>) when
    /// <paramref name="cwd"/> is a repo, else a bounded directory walk. Never throws.</summary>
    public static ProjectFiles Scan(string cwd)
    {
        if (string.IsNullOrEmpty(cwd) || !Directory.Exists(cwd))
            return ProjectFiles.Empty;

        if (GitRepoService.IsRepo(cwd) && FromGit(cwd) is { } gitFiles)
            return gitFiles;

        return FromWalk(cwd);
    }

    private static ProjectFiles? FromGit(string cwd)
    {
        // --cached + --others --exclude-standard = tracked files plus untracked-but-not-ignored ones, so
        // .gitignore is applied for us. -z gives NUL-separated paths (robust to spaces/newlines in names).
        var (exit, stdout) = RunGit(cwd, GitTimeoutMs,
            "--no-optional-locks", "ls-files", "-z", "--cached", "--others", "--exclude-standard");
        if (exit != 0)
            return null;

        var paths = stdout.Split('\0', StringSplitOptions.RemoveEmptyEntries)
                          .Select(p => p.Replace('\\', '/'));
        return Finish(paths, truncated: false);
    }

    private static ProjectFiles FromWalk(string cwd)
    {
        var results = new List<string>();
        bool truncated = false;
        var stack = new Stack<(DirectoryInfo Dir, int Depth)>();
        stack.Push((new DirectoryInfo(cwd), 0));

        while (stack.Count > 0 && !truncated)
        {
            var (dir, depth) = stack.Pop();

            FileInfo[] files;
            try { files = dir.GetFiles(); }
            catch { continue; }   // access denied / gone — skip this directory
            foreach (var f in files)
            {
                results.Add(Path.GetRelativePath(cwd, f.FullName).Replace('\\', '/'));
                if (results.Count >= MaxFiles) { truncated = true; break; }
            }
            if (truncated || depth >= MaxDepth)
                continue;

            DirectoryInfo[] subs;
            try { subs = dir.GetDirectories(); }
            catch { continue; }
            foreach (var sub in subs)
            {
                if (SkipDirs.Contains(sub.Name, StringComparer.OrdinalIgnoreCase))
                    continue;
                if ((sub.Attributes & FileAttributes.ReparsePoint) != 0)
                    continue;   // don't follow symlinks/junctions — they can loop or escape the tree
                stack.Push((sub, depth + 1));
            }
        }

        return Finish(results, truncated);
    }

    private static ProjectFiles Finish(IEnumerable<string> paths, bool truncated)
    {
        var list = paths
            .Where(p => !string.IsNullOrEmpty(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (list.Count > MaxFiles)
        {
            list = list.Take(MaxFiles).ToList();
            truncated = true;
        }
        return list.Count == 0 && !truncated ? ProjectFiles.Empty : new ProjectFiles(list, truncated);
    }

    // A minimal, best-effort `git` runner mirroring MarkdownProjectScan/GitRepoService: UTF-8 pipes, async
    // draining so a large listing can't deadlock the child, a hard timeout with tree-kill, and (-1, "") on
    // any failure. Duplicated rather than shared because those runners are private to their own services.
    private static (int Exit, string Stdout) RunGit(string cwd, int timeoutMs, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = cwd,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                StandardErrorEncoding = System.Text.Encoding.UTF8,
            };
            foreach (var a in args)
                psi.ArgumentList.Add(a);

            using var proc = Process.Start(psi);
            if (proc == null)
                return (-1, "");

            var stdout = proc.StandardOutput.ReadToEndAsync();
            _ = proc.StandardError.ReadToEndAsync();

            if (!proc.WaitForExit(timeoutMs))
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                return (-1, "");
            }
            return (proc.ExitCode, stdout.GetAwaiter().GetResult());
        }
        catch
        {
            return (-1, "");
        }
    }
}
