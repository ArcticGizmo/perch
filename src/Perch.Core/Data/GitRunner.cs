namespace Perch.Data;

using System.Collections.Concurrent;
using System.Diagnostics;

/// <summary>
/// The one place Perch starts <c>git</c> (review fixes CP11). Perch runs git by itself in repos it didn't choose:
/// the git-stats glyph every few seconds, the changed-files panel, the review window, the @-mention file lists. A
/// repo's own config can name commands that ordinary read-only git runs, so a repo that arrives with a prepared
/// <c>.git</c> (a zip, a shared drive, or a config an injected session wrote) would otherwise run its command on
/// the next poll. Verified against git 2.55:
/// <list type="bullet">
///   <item><c>core.fsmonitor</c> runs on <c>status</c>, <c>ls-files</c> and <c>diff</c>;</item>
///   <item>a <c>filter.&lt;driver&gt;.clean</c>/<c>process</c> named by <c>.gitattributes</c> runs on <c>diff</c> and
///     <c>diff --numstat</c> (and on <c>status</c> for a racily-clean file);</item>
///   <item><c>diff.external</c> and <c>diff.&lt;driver&gt;.command</c> run on <c>diff</c>;</item>
///   <item><c>diff.&lt;driver&gt;.textconv</c> runs on <c>diff</c> and <c>show</c>.</item>
/// </list>
/// So every <see cref="Trust.Automatic"/> run turns fsmonitor off, passes <c>--no-ext-diff --no-textconv</c> to
/// <c>diff</c>/<c>show</c>/<c>log</c>, and blanks every filter driver the repo's <b>own</b> config defines (local or
/// worktree scope). Drivers from the user's global or system config, such as git-lfs, are theirs and keep
/// working. The results are unchanged for an ordinary repo.
///
/// Also: git is always the absolute PATH copy (CP7), every run has a timeout that kills the process tree, and at
/// most <see cref="MaxConcurrent"/> git processes run at once across the whole app. Never throws: a failure to
/// launch, a timeout or a busy gate is exit <c>-1</c>.
/// </summary>
internal static class GitRunner
{
    /// <summary>How much of the repo's own config a run may act on.</summary>
    internal enum Trust
    {
        /// <summary>Anything Perch runs by itself, including every read the UI triggers: repo-supplied commands
        /// are neutralised as above.</summary>
        Automatic,

        /// <summary>A write the user asked for (stage, unstage, discard, apply a hunk, commit): git behaves as it
        /// would in their terminal, so their hooks and filters run, like <c>git commit</c> there would. Only
        /// fsmonitor stays off; it's a speed-up, and harmless to skip.</summary>
        UserAction,
    }

    internal readonly record struct Result(int Exit, string Stdout, string Stderr);

    /// <summary>The most git processes Perch runs at once. The git-stats poll, a review window and the file
    /// pickers can otherwise pile up a burst of processes across many sessions.</summary>
    internal const int MaxConcurrent = 4;

    private static readonly SemaphoreSlim Gate = new(MaxConcurrent, MaxConcurrent);
    private static readonly Result Failed = new(-1, "", "");

    // The repo-local filter probe, cached per working directory until one of its config files changes.
    private static readonly ConcurrentDictionary<string, Probe> Probes = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan ProbeMaxAge = TimeSpan.FromMinutes(1);

    // The subcommands that can render a diff, and so take --no-ext-diff / --no-textconv.
    private static readonly HashSet<string> DiffingCommands = new(StringComparer.Ordinal) { "diff", "show", "log" };

    /// <summary>Runs <c>git &lt;args&gt;</c> in <paramref name="cwd"/> as an <see cref="Trust.Automatic"/> run.</summary>
    public static Result Run(string cwd, int timeoutMs, params string[] args) =>
        Run(cwd, timeoutMs, Trust.Automatic, stdin: null, args);

    /// <summary>Runs <c>git &lt;args&gt;</c> in <paramref name="cwd"/>, writing <paramref name="stdin"/> (when
    /// non-null) to the child's input. Waits up to <paramref name="timeoutMs"/> for a free slot, then up to
    /// <paramref name="timeoutMs"/> for git itself.</summary>
    public static Result Run(string cwd, int timeoutMs, Trust trust, string? stdin, params string[] args)
    {
        if (string.IsNullOrEmpty(cwd) || !Directory.Exists(cwd))
            return Failed;
        if (!Gate.Wait(timeoutMs))
            return Failed;
        try
        {
            var filters = trust == Trust.Automatic ? LocalFilterDrivers(cwd, timeoutMs) : [];
            return Exec(cwd, HardenedArgs(args, trust, filters), stdin, timeoutMs);
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>
    /// The full argument list for a run: the config overrides first, then <paramref name="args"/> with
    /// <c>--no-ext-diff --no-textconv</c> inserted after a diffing subcommand. Pure; unit-tested.
    /// </summary>
    internal static List<string> HardenedArgs(IReadOnlyList<string> args, Trust trust, IReadOnlyCollection<string> localFilters)
    {
        var full = new List<string>(args.Count + 8 + localFilters.Count * 8) { "-c", "core.fsmonitor=false" };
        if (trust == Trust.Automatic)
        {
            full.Add("-c"); full.Add("log.showSignature=false");   // log would otherwise run gpg.program
            foreach (var name in localFilters)
            {
                // An empty command means "no filter"; required=false stops git refusing to run without it.
                full.Add("-c"); full.Add($"filter.{name}.clean=");
                full.Add("-c"); full.Add($"filter.{name}.smudge=");
                full.Add("-c"); full.Add($"filter.{name}.process=");
                full.Add("-c"); full.Add($"filter.{name}.required=false");
            }
        }

        int sub = SubcommandIndex(args);
        for (int i = 0; i < args.Count; i++)
        {
            full.Add(args[i]);
            if (i == sub && trust == Trust.Automatic && DiffingCommands.Contains(args[i]))
            {
                full.Add("--no-ext-diff");
                full.Add("--no-textconv");
            }
        }
        return full;
    }

    // The index of the subcommand: the first argument that isn't a global option (or the value of -c / -C).
    private static int SubcommandIndex(IReadOnlyList<string> args)
    {
        for (int i = 0; i < args.Count; i++)
        {
            if (args[i] is "-c" or "-C") { i++; continue; }
            if (!args[i].StartsWith('-')) return i;
        }
        return -1;
    }

    // ---- the repo-local filter probe ------------------------------------------------------------------

    // The filter drivers the repo's own config defines. `git config --list` reads config and runs nothing.
    private static IReadOnlyCollection<string> LocalFilterDrivers(string cwd, int timeoutMs)
    {
        if (Probes.TryGetValue(cwd, out var cached) && cached.StillValid())
            return cached.Filters;

        var r = Exec(cwd, ["config", "--show-scope", "--show-origin", "-z", "--list"], null, timeoutMs);
        if (r.Exit != 0)
        {
            // Not a repo (or git failed): the real command will fail the same way, so there's nothing to blank.
            Probes.TryRemove(cwd, out _);
            return [];
        }
        var probe = ParseConfigList(r.Stdout, cwd);
        if (probe.Cacheable) Probes[cwd] = probe; else Probes.TryRemove(cwd, out _);
        return probe.Filters;
    }

    /// <summary>
    /// Reads <c>git config --show-scope --show-origin -z --list</c> (triples of <c>scope\0origin\0key\nvalue\0</c>)
    /// into the repo-controlled filter driver names plus what's needed to know when to look again. The result
    /// is only cacheable when the repo-scope config is plain files Perch can watch: an <c>include</c>, an
    /// <c>includeIf</c> or per-worktree config could pull in a file that doesn't exist yet, so then the probe
    /// runs every time. Pure; unit-tested.
    /// </summary>
    internal static Probe ParseConfigList(string output, string cwd)
    {
        var filters = new SortedSet<string>(StringComparer.Ordinal);
        var origins = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool cacheable = true;

        var parts = output.Split('\0');
        for (int i = 0; i + 2 < parts.Length; i += 3)
        {
            string scope = parts[i], origin = parts[i + 1], entry = parts[i + 2];
            if (scope is not ("local" or "worktree"))
                continue;

            int nl = entry.IndexOf('\n');
            string key = (nl < 0 ? entry : entry[..nl]).ToLowerInvariant();

            if (scope == "worktree" || key.StartsWith("include.", StringComparison.Ordinal)
                || key.StartsWith("includeif.", StringComparison.Ordinal) || key == "extensions.worktreeconfig")
                cacheable = false;
            if (origin.StartsWith("file:", StringComparison.Ordinal))
                origins.Add(origin[5..]);
            else
                cacheable = false;

            // filter.<name>.<var>: the name is everything between the first and last dot, and may itself hold dots.
            if (key.StartsWith("filter.", StringComparison.Ordinal))
            {
                int last = key.LastIndexOf('.');
                if (last > "filter.".Length)
                    filters.Add((nl < 0 ? entry : entry[..nl])["filter.".Length..last]);
            }
        }

        var stamps = new List<(string Path, long Length, long Ticks)>();
        foreach (var o in origins)
        {
            try
            {
                var fi = new FileInfo(Path.IsPathRooted(o) ? o : Path.GetFullPath(Path.Combine(cwd, o)));
                if (!fi.Exists) { cacheable = false; continue; }
                stamps.Add((fi.FullName, fi.Length, fi.LastWriteTimeUtc.Ticks));
            }
            catch { cacheable = false; }
        }
        if (stamps.Count == 0) cacheable = false;   // a repo always has .git/config; seeing none means we can't watch it

        return new Probe(filters.ToArray(), stamps, cacheable, DateTime.UtcNow);
    }

    internal sealed record Probe(
        IReadOnlyCollection<string> Filters,
        IReadOnlyList<(string Path, long Length, long Ticks)> Stamps,
        bool Cacheable,
        DateTime At)
    {
        public bool StillValid()
        {
            if (!Cacheable || DateTime.UtcNow - At > ProbeMaxAge) return false;
            foreach (var (path, length, ticks) in Stamps)
            {
                try
                {
                    var fi = new FileInfo(path);
                    if (!fi.Exists || fi.Length != length || fi.LastWriteTimeUtc.Ticks != ticks) return false;
                }
                catch { return false; }
            }
            return true;
        }
    }

    // ---- process plumbing -----------------------------------------------------------------------------

    private static Result Exec(string cwd, IReadOnlyList<string> args, string? stdin, int timeoutMs)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = ExecutableResolver.Resolve("git"),   // absolute: never a git.exe planted in the repo (CP7)
                WorkingDirectory = cwd,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = stdin is not null,
                UseShellExecute = false,
                CreateNoWindow = true,
                // Git emits UTF-8 (file bytes, commit messages, paths); without this .NET would decode the child's
                // output with the console/ANSI code page (CP1252 on Windows), mangling non-ASCII and rendering a
                // file's UTF-8 BOM as "ï»¿".
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                StandardErrorEncoding = System.Text.Encoding.UTF8,
                StandardInputEncoding = stdin is not null ? new System.Text.UTF8Encoding(false) : null,
            };
            foreach (var a in args)
                psi.ArgumentList.Add(a);

            using var proc = Process.Start(psi);
            if (proc == null)
                return Failed;

            // Drain both pipes async so a large diff (or a chatty stderr) can't deadlock the child.
            var stdout = proc.StandardOutput.ReadToEndAsync();
            var stderr = proc.StandardError.ReadToEndAsync();

            if (stdin is not null)
            {
                try { proc.StandardInput.Write(stdin); proc.StandardInput.Close(); } catch { }
            }

            if (!proc.WaitForExit(timeoutMs))
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                return Failed;
            }
            return new Result(proc.ExitCode, stdout.GetAwaiter().GetResult(), stderr.GetAwaiter().GetResult());
        }
        catch
        {
            return Failed;
        }
    }
}
