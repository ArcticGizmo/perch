namespace Perch.Data;

using System.Diagnostics;
using System.Linq;

/// <summary>The Markdown files found in a project directory, as forward-slashed paths relative to the
/// scanned root, sorted and de-duplicated. <see cref="Truncated"/> is true when a hard file cap was hit,
/// so the viewer can note that not everything is listed rather than implying it covered the whole tree.</summary>
public sealed record MarkdownProjectFiles(IReadOnlyList<string> RelativePaths, bool Truncated)
{
    public static readonly MarkdownProjectFiles Empty = new([], false);
}

/// <summary>
/// Enumerates a project's Markdown files (<c>.md</c>/<c>.markdown</c>) for the viewer's project pane. In a
/// git working tree it shells <c>git ls-files --cached --others --exclude-standard -- *.md *.markdown</c>,
/// so <c>.gitignore</c> is honoured for free and the scan is bounded by what git tracks/sees. Outside a
/// repo it falls back to a bounded-depth directory walk that skips the usual noise directories
/// (<c>node_modules</c>, <c>bin</c>, <c>obj</c>, …) and never follows symlinks.
///
/// Best-effort and meant to run off the UI thread: git missing, a timeout, or an unreadable directory all
/// yield <see cref="MarkdownProjectFiles.Empty"/> rather than throwing. A hard file cap keeps a monorepo
/// from flooding the pane; hitting it sets <see cref="MarkdownProjectFiles.Truncated"/>.
/// </summary>
internal static class MarkdownProjectScan
{
    private const int GitTimeoutMs = 4000;
    private const int MaxDepth = 6;      // fallback walk: how deep below the root to descend
    private const int MaxFiles = 2000;   // hard ceiling either way, so a huge tree can't flood the pane

    // Directories the fallback walk never descends into — build output, dependency caches, VCS/editor
    // metadata. (The git path doesn't need these: --exclude-standard already drops whatever .gitignore
    // covers, which is normally exactly this set.)
    private static readonly string[] SkipDirs =
        [".git", "node_modules", "bin", "obj", ".venv", "venv", "dist", "build", ".next", "target", ".idea", ".vs", ".gradle"];

    /// <summary>
    /// The project's Markdown files under <paramref name="cwd"/>. Uses git (honouring <c>.gitignore</c>)
    /// when <paramref name="cwd"/> is a repo, else a bounded directory walk. Never throws.
    /// </summary>
    public static MarkdownProjectFiles Scan(string cwd)
    {
        if (string.IsNullOrEmpty(cwd) || !Directory.Exists(cwd))
            return MarkdownProjectFiles.Empty;

        // In a repo, prefer git — but if the invocation fails outright (git not on PATH, timeout), fall
        // back to the walk rather than showing nothing.
        if (GitRepoService.IsRepo(cwd) && FromGit(cwd) is { } gitFiles)
            return gitFiles;

        return FromWalk(cwd);
    }

    private static MarkdownProjectFiles? FromGit(string cwd)
    {
        // --cached + --others --exclude-standard = tracked files plus untracked-but-not-ignored ones, so
        // .gitignore is applied for us. -z gives NUL-separated paths (robust to spaces/newlines in names).
        // The pathspecs match .md/.markdown at any depth. Paths come back relative to cwd, forward-slashed.
        var (exit, stdout) = RunGit(cwd, GitTimeoutMs,
            "--no-optional-locks", "ls-files", "-z", "--cached", "--others", "--exclude-standard",
            "--", "*.md", "*.markdown");
        if (exit != 0)
            return null;

        var paths = stdout.Split('\0', StringSplitOptions.RemoveEmptyEntries)
                          .Select(p => p.Replace('\\', '/'));
        return Finish(paths, truncated: false);
    }

    private static MarkdownProjectFiles FromWalk(string cwd)
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
                if (!IsMarkdown(f.Name))
                    continue;
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

    private static MarkdownProjectFiles Finish(IEnumerable<string> paths, bool truncated)
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
        return list.Count == 0 && !truncated ? MarkdownProjectFiles.Empty : new MarkdownProjectFiles(list, truncated);
    }

    private static bool IsMarkdown(string name) =>
        name.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith(".markdown", StringComparison.OrdinalIgnoreCase);

    // A minimal, best-effort `git` runner mirroring GitStatsService/GitRepoService: UTF-8 pipes, async
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
