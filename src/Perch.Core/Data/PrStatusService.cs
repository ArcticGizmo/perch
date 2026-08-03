namespace Perch.Data;

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;

/// <summary>The lifecycle state of a pull request, as reported by <c>gh</c> — an <c>OPEN</c> PR that is a
/// draft is surfaced as its own <see cref="Draft"/> state so the overlay can dim it.</summary>
public enum PrState { Open, Draft, Merged, Closed }

/// <summary>
/// A pull request associated with a working directory's current branch, as read from the GitHub CLI
/// (<c>gh pr view</c>). Only the fields the overlay needs: the number, the browser URL, a title for the
/// hover/flyout, and the <see cref="PrState"/> that drives its colour.
/// </summary>
public readonly record struct PullRequestInfo(int Number, string Url, string Title, PrState State);

/// <summary>
/// Resolves the pull request for a working directory's current branch by shelling out to the GitHub CLI
/// (<c>gh pr view --json …</c>), cached with a long TTL (the configurable poll interval) and refreshed on a
/// background thread so the hot session scan never blocks on a process spawn. Reads are non-blocking: a
/// missing/stale entry schedules a refresh and returns whatever is cached (possibly null), then
/// <see cref="Updated"/> nudges a repaint once the fresh value lands.
///
/// Mirrors <see cref="GitStatsService"/>. Entirely opt-in: while <see cref="Enabled"/> is false nothing is
/// cached and — crucially — no <c>gh</c> process is ever launched, so the feature costs zero cycles when
/// off. A directory with no PR (or no GitHub remote, or no <c>gh</c>) caches a <c>null</c> for the full
/// interval, so a repo that will never have a PR isn't re-probed on every scan. Concurrent refreshes are
/// capped so opening Perch on a dozen sessions can't spawn a dozen <c>gh</c> processes at once.
/// </summary>
internal sealed class PrStatusService : IDisposable
{
    // Hard ceiling on a single gh invocation. gh talks to the GitHub API, so this is generous compared to
    // the git-diff timeout — but still bounded so a hung network can't wedge a worker forever.
    private const int GhTimeoutMs = 8000;

    // At most this many gh processes run at once, so a Perch opening onto many sessions staggers its
    // lookups instead of forking a process per row simultaneously.
    private const int MaxConcurrent = 3;

    private readonly ConcurrentDictionary<string, Entry> _cache = new();
    // Directories with a refresh in flight, so concurrent scans don't pile up duplicate gh processes.
    private readonly ConcurrentDictionary<string, byte> _fetching = new();
    private readonly SemaphoreSlim _gate = new(MaxConcurrent);

    private volatile bool _enabled;
    private volatile bool _disposed;
    private volatile int _intervalMinutes = 5;

    /// <summary>Raised (on a thread-pool thread) when a background refresh changed a directory's PR, so the
    /// owner can re-scan and repaint. Never raised while disabled or disposed.</summary>
    public event Action? Updated;

    /// <summary>Master switch. Turning it off clears the cache and guarantees no further gh process is
    /// launched until it's turned back on. Driven by the "GitHub pull requests" integration setting.</summary>
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

    /// <summary>How long a cached result (PR or "no PR") is served before a scan schedules a background
    /// refresh — the user's poll interval, floored at a minute. Lowering it drops nothing; the next stale
    /// read simply re-runs gh sooner.</summary>
    public int IntervalMinutes
    {
        set => _intervalMinutes = Math.Max(1, value);
    }

    private TimeSpan Ttl => TimeSpan.FromMinutes(Math.Max(1, _intervalMinutes));

    /// <summary>
    /// The last-known pull request for <paramref name="cwd"/>'s current branch, or null when disabled, not
    /// yet known, not a GitHub repo, or the branch simply has no PR. Never blocks: a missing/stale entry
    /// schedules a background refresh (which raises <see cref="Updated"/> when it lands) and the current
    /// value is returned as-is. So the first paint after a session appears shows nothing, then fills in a
    /// few beats later once gh returns.
    /// </summary>
    public PullRequestInfo? Get(string cwd)
    {
        if (!_enabled || _disposed || string.IsNullOrEmpty(cwd))
            return null;

        bool cached = _cache.TryGetValue(cwd, out var entry);
        if (cached && DateTime.UtcNow - entry.FetchedAt < Ttl)
            return entry.Pr;

        ScheduleRefresh(cwd);
        return cached ? entry.Pr : null;
    }

    // Kicks off a single background gh run for this directory (a no-op if one is already in flight),
    // updates the cache when it returns, and raises Updated only when the PR actually changed. The gate
    // caps how many run at once; a queued run still holds its _fetching slot so no duplicate is scheduled.
    private void ScheduleRefresh(string cwd)
    {
        if (!_fetching.TryAdd(cwd, 0))
            return;

        Task.Run(() =>
        {
            try
            {
                _gate.Wait();
                try
                {
                    if (_disposed || !_enabled)
                        return;
                    PullRequestInfo? result = RunGhPrView(cwd);
                    if (_disposed || !_enabled)
                        return;
                    bool changed = !_cache.TryGetValue(cwd, out var old) || !Nullable.Equals(old.Pr, result);
                    _cache[cwd] = new Entry(result, DateTime.UtcNow);
                    if (changed)
                        Updated?.Invoke();
                }
                finally
                {
                    _gate.Release();
                }
            }
            finally
            {
                _fetching.TryRemove(cwd, out _);
            }
        });
    }

    // Runs `gh pr view --json …` in cwd and parses the PR for its current branch. Returns null on any
    // failure (gh missing, not a git repo, no GitHub remote, no PR for the branch, timeout) — best-effort,
    // so the overlay just shows no glyph. A cheap ".git" walk skips gh entirely for non-repos.
    private static PullRequestInfo? RunGhPrView(string cwd)
    {
        if (!Directory.Exists(cwd) || !HasGitRepo(cwd))
            return null;

        var (exit, stdout) = RunGh("pr view --json number,url,title,state,isDraft", cwd, GhTimeoutMs);
        if (exit != 0 || string.IsNullOrWhiteSpace(stdout))
            return null; // non-zero == no PR for the branch (or an error) — either way, no glyph.

        return ParsePrJson(stdout);
    }

    /// <summary>
    /// Parses the JSON object <c>gh pr view --json number,url,title,state,isDraft</c> emits into a
    /// <see cref="PullRequestInfo"/>, mapping <c>state</c> + <c>isDraft</c> onto <see cref="PrState"/>.
    /// Returns null for empty/malformed output or a missing number. Internal for unit testing.
    /// </summary>
    internal static PullRequestInfo? ParsePrJson(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;
            if (!root.TryGetProperty("number", out var numEl) || !numEl.TryGetInt32(out var number))
                return null;

            string url   = root.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "";
            string title = root.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
            string state = root.TryGetProperty("state", out var s) ? s.GetString() ?? "" : "";
            bool isDraft = root.TryGetProperty("isDraft", out var d) && d.ValueKind == JsonValueKind.True;

            return new PullRequestInfo(number, url, title, MapState(state, isDraft));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // gh reports state as OPEN / CLOSED / MERGED; a draft is an OPEN PR flagged isDraft, which we surface as
    // its own state so the overlay can dim it. Anything unexpected falls back to Open.
    private static PrState MapState(string state, bool isDraft)
    {
        if (string.Equals(state, "MERGED", StringComparison.OrdinalIgnoreCase)) return PrState.Merged;
        if (string.Equals(state, "CLOSED", StringComparison.OrdinalIgnoreCase)) return PrState.Closed;
        return isDraft ? PrState.Draft : PrState.Open;
    }

    /// <summary>Whether <c>gh</c> is on PATH and — separately — whether it's authenticated. Used by the
    /// settings page to explain, before anything is turned on, why no PRs might appear. Runs gh twice
    /// (<c>--version</c>, then <c>auth status</c>); safe to call off the UI thread.</summary>
    public static (bool Installed, bool Authenticated) DescribeGh()
    {
        if (RunGh("--version", null, 4000).Exit != 0)
            return (false, false);
        bool authed = RunGh("auth status", null, 6000).Exit == 0;
        return (true, authed);
    }

    // Runs `gh <args>` (optionally in workingDir), returning its exit code and stdout. Exit is -1 on any
    // failure to launch/complete (gh not on PATH, timeout, …). stderr is drained but discarded.
    private static (int Exit, string Stdout) RunGh(string args, string? workingDir, int timeoutMs)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "gh",
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            if (!string.IsNullOrEmpty(workingDir))
                psi.WorkingDirectory = workingDir;

            using var proc = Process.Start(psi);
            if (proc == null)
                return (-1, "");

            // Read both pipes async so a chatty gh can't fill a buffer and deadlock before it exits.
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

    // Cheap filesystem check: is cwd inside a git working tree? Walks up looking for a ".git" entry (a dir
    // in a normal clone, a file in a worktree/submodule). Lets us skip spawning gh for plain directories.
    private static bool HasGitRepo(string cwd)
    {
        try
        {
            var dir = new DirectoryInfo(cwd);
            for (var d = dir; d != null; d = d.Parent)
            {
                var git = Path.Combine(d.FullName, ".git");
                if (Directory.Exists(git) || File.Exists(git))
                    return true;
            }
        }
        catch { }
        return false;
    }

    public void Dispose()
    {
        _disposed = true;
        _cache.Clear();
        _gate.Dispose();
    }

    // One cached directory result: the PR (null = no PR / not GitHub / unreadable) and when it was fetched.
    private readonly record struct Entry(PullRequestInfo? Pr, DateTime FetchedAt);
}
