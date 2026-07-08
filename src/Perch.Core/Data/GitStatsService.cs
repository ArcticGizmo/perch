namespace Perch.Data;

using System.Collections.Concurrent;
using System.Diagnostics;

/// <summary>
/// Unstaged line churn for a working tree: lines added / deleted per <c>git diff --numstat</c>
/// (tracked files, working tree vs. index). Untracked files contribute nothing, matching git's own
/// "+/- lines" view. A clean tree is <see cref="IsEmpty"/>.
/// </summary>
public readonly record struct GitLineStats(int Added, int Deleted)
{
    public bool IsEmpty => Added == 0 && Deleted == 0;
}

/// <summary>
/// Computes per-directory unstaged git line stats by shelling out to <c>git diff --numstat</c>, cached
/// with a short TTL and refreshed on a background thread so the hot session scan never blocks on a
/// process spawn. Reads are non-blocking: a missing/stale entry schedules a refresh and returns whatever
/// is cached (possibly null), then <see cref="StatsUpdated"/> nudges a repaint once the fresh value lands.
///
/// Entirely opt-in. While <see cref="Enabled"/> is false nothing is cached and — crucially — no git
/// process is ever launched, so the feature costs zero cycles when off. Turning it off also drops the
/// cache so a later re-enable starts clean.
/// </summary>
internal sealed class GitStatsService : IDisposable
{
    // A cached result is served for this long before a scan schedules a background refresh. The working
    // tree only changes when a session edits files, so a few seconds keeps the overlay glanceably fresh
    // without spawning git on every scan.
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(3);

    // Hard ceiling on a single git invocation so a huge or pathological repo can't wedge a worker.
    private const int GitTimeoutMs = 4000;

    private readonly ConcurrentDictionary<string, Entry> _cache = new();
    // Directories with a refresh in flight, so concurrent scans don't pile up duplicate git processes.
    private readonly ConcurrentDictionary<string, byte> _fetching = new();

    private volatile bool _enabled;
    private volatile bool _disposed;

    /// <summary>Raised (on a thread-pool thread) when a background refresh changed a directory's stats,
    /// so the owner can re-scan and repaint. Never raised while disabled or disposed.</summary>
    public event Action? StatsUpdated;

    /// <summary>Master switch. Turning it off clears the cache and guarantees no further git process is
    /// launched until it's turned back on. Driven by the experimental setting.</summary>
    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (_enabled == value)
                return;
            _enabled = value;
            if (!value)
                _cache.Clear();
        }
    }

    /// <summary>
    /// The last-known unstaged line stats for <paramref name="cwd"/>, or null when disabled, not yet
    /// known, or not a git repo. Never blocks: a missing/stale entry schedules a background refresh
    /// (which raises <see cref="StatsUpdated"/> when it lands) and the current value is returned as-is.
    /// So the first paint after enabling shows nothing, then fills in a beat later.
    /// </summary>
    public GitLineStats? Get(string cwd)
    {
        if (!_enabled || _disposed || string.IsNullOrEmpty(cwd))
            return null;

        bool cached = _cache.TryGetValue(cwd, out var entry);
        if (cached && DateTime.UtcNow - entry.FetchedAt < Ttl)
            return entry.Stats;

        ScheduleRefresh(cwd);
        return cached ? entry.Stats : null;
    }

    // Kicks off a single background git run for this directory (a no-op if one is already in flight),
    // updates the cache when it returns, and raises StatsUpdated only when the numbers actually moved.
    private void ScheduleRefresh(string cwd)
    {
        if (!_fetching.TryAdd(cwd, 0))
            return;

        Task.Run(() =>
        {
            GitLineStats? result = RunGitDiff(cwd);
            try
            {
                if (_disposed || !_enabled)
                    return;
                bool changed = !_cache.TryGetValue(cwd, out var old) || !Nullable.Equals(old.Stats, result);
                _cache[cwd] = new Entry(result, DateTime.UtcNow);
                if (changed)
                    StatsUpdated?.Invoke();
            }
            finally
            {
                _fetching.TryRemove(cwd, out _);
            }
        });
    }

    // Runs `git --no-optional-locks diff --numstat` in cwd and sums the added/deleted columns. Returns
    // null on any failure (git missing, not a repo, timeout) — best-effort, so the overlay just shows no
    // chip. --no-optional-locks keeps us from touching index.lock and interfering with a live session's
    // git; --numstat is machine-readable ("<added>\t<deleted>\t<path>", binary files show "-").
    private static GitLineStats? RunGitDiff(string cwd)
    {
        if (!Directory.Exists(cwd))
            return null;

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = "--no-optional-locks diff --numstat",
                WorkingDirectory = cwd,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var proc = Process.Start(psi);
            if (proc == null)
                return null;

            // Read both pipes async so a large diff (or any stderr) can't fill a buffer and deadlock
            // the child before it exits.
            var stdout = proc.StandardOutput.ReadToEndAsync();
            _ = proc.StandardError.ReadToEndAsync();

            if (!proc.WaitForExit(GitTimeoutMs))
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                return null;
            }
            if (proc.ExitCode != 0)
                return null;

            return ParseNumstat(stdout.GetAwaiter().GetResult());
        }
        catch
        {
            // git not on PATH, access denied, etc. — no chip.
            return null;
        }
    }

    /// <summary>
    /// Sums the added/deleted columns of <c>git diff --numstat</c> output. Each non-blank line is
    /// tab-separated "<c>&lt;added&gt;\t&lt;deleted&gt;\t&lt;path&gt;</c>"; binary files carry "-" in the
    /// numeric columns, which don't parse and are simply skipped (they contribute no line count).
    /// Tolerates <c>\r\n</c> and malformed lines. Internal for unit testing.
    /// </summary>
    internal static GitLineStats ParseNumstat(string output)
    {
        int added = 0, deleted = 0;
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0)
                continue;
            var parts = line.Split('\t');
            if (parts.Length < 3)
                continue;
            if (int.TryParse(parts[0], out var a)) added += a;      // "-" (binary) fails to parse → skipped
            if (int.TryParse(parts[1], out var d)) deleted += d;
        }
        return new GitLineStats(added, deleted);
    }

    public void Dispose()
    {
        _disposed = true;
        _cache.Clear();
    }

    // One cached directory result: the stats (null = not a repo / unreadable) and when they were fetched.
    private readonly record struct Entry(GitLineStats? Stats, DateTime FetchedAt);
}
