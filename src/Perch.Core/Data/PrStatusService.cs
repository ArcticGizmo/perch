namespace Perch.Data;

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;

/// <summary>The lifecycle state of a pull request, as reported by <c>gh</c> — an <c>OPEN</c> PR that is a
/// draft is surfaced as its own <see cref="Draft"/> state so the overlay can dim it.</summary>
public enum PrState { Open, Draft, Merged, Closed }

/// <summary>The outcome of a single CI check on a PR, folded into the three buckets the overlay renders
/// (a green / red / blue dot): still <see cref="Pending"/>, a <see cref="Success"/>, or a
/// <see cref="Failure"/>. Neutral/skipped runs count as success (non-blocking); cancelled/timed-out/
/// action-required count as failure.</summary>
public enum PrCheckState { Pending, Success, Failure }

/// <summary>The aggregate CI status across a PR's checks, driving the small status dot on the overlay's PR
/// glyph. <see cref="None"/> = no checks reported (no dot); otherwise: any failure ⇒ <see cref="Failing"/>,
/// else any still-running ⇒ <see cref="Pending"/>, else all green ⇒ <see cref="Passing"/>.</summary>
public enum PrChecksRollup { None, Pending, Passing, Failing }

/// <summary>A single CI check on a pull request: its name (the check-run name or legacy status context), the
/// <see cref="PrCheckState"/> it folds down to, and the browser URL for its detail/logs page (empty when
/// <c>gh</c> reports none). Listed as children in the PR hover tooltip and the click flyout.</summary>
public readonly record struct PrCheck(string Name, PrCheckState State, string Url = "");

/// <summary>
/// A pull request associated with a working directory's current branch, as read from the GitHub CLI
/// (<c>gh pr view</c>). Only the fields the overlay needs: the number, the browser URL, a title for the
/// hover/flyout, the <see cref="PrState"/> that drives its colour, and the <see cref="Checks"/> read from
/// <c>statusCheckRollup</c> (surfaced as tooltip children + an aggregate status dot).
/// </summary>
/// <remarks>Equality is by value including the check list (sequence-compared), so a background refresh that
/// returns identical checks doesn't count as a change — see <see cref="PrStatusService"/>'s change gate.</remarks>
public readonly record struct PullRequestInfo(int Number, string Url, string Title, PrState State)
{
    /// <summary>The CI checks reported for the PR's head commit, in the order <c>gh</c> lists them. Empty
    /// when the PR has no checks or they weren't fetched.</summary>
    public IReadOnlyList<PrCheck> Checks { get; init; } = Array.Empty<PrCheck>();

    /// <summary>The aggregate check status driving the overlay's status dot, folded from <see cref="Checks"/>.</summary>
    public PrChecksRollup ChecksRollup =>
        Checks.Count == 0                             ? PrChecksRollup.None
      : Checks.Any(c => c.State == PrCheckState.Failure) ? PrChecksRollup.Failing
      : Checks.Any(c => c.State == PrCheckState.Pending) ? PrChecksRollup.Pending
      :                                                    PrChecksRollup.Passing;

    public bool Equals(PullRequestInfo other) =>
        Number == other.Number && Url == other.Url && Title == other.Title && State == other.State
        && Checks.SequenceEqual(other.Checks);

    public override int GetHashCode() => HashCode.Combine(Number, Url, Title, State, Checks.Count);
}

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

        var (exit, stdout) = RunGh("pr view --json number,url,title,state,isDraft,statusCheckRollup", cwd, GhTimeoutMs);
        if (exit != 0 || string.IsNullOrWhiteSpace(stdout))
            return null; // non-zero == no PR for the branch (or an error) — either way, no glyph.

        return ParsePrJson(stdout);
    }

    /// <summary>
    /// Parses the JSON object <c>gh pr view --json number,url,title,state,isDraft,statusCheckRollup</c> emits
    /// into a <see cref="PullRequestInfo"/>, mapping <c>state</c> + <c>isDraft</c> onto <see cref="PrState"/>
    /// and folding each <c>statusCheckRollup</c> entry into a <see cref="PrCheck"/>. Returns null for
    /// empty/malformed output or a missing number. Internal for unit testing.
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

            return new PullRequestInfo(number, url, title, MapState(state, isDraft)) { Checks = ParseChecks(root) };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Reads the <c>statusCheckRollup</c> array <c>gh</c> emits into a list of <see cref="PrCheck"/>. The
    /// array mixes two shapes: modern <c>CheckRun</c> objects (<c>name</c> + <c>status</c> + <c>conclusion</c>)
    /// and legacy <c>StatusContext</c> commit statuses (<c>context</c> + <c>state</c>); each is folded onto a
    /// <see cref="PrCheckState"/>. A missing/non-array field yields an empty list (a PR with no checks).
    /// Internal for unit testing.
    /// </summary>
    internal static IReadOnlyList<PrCheck> ParseChecks(JsonElement root)
    {
        if (!root.TryGetProperty("statusCheckRollup", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return Array.Empty<PrCheck>();

        var checks = new List<PrCheck>();
        foreach (var el in arr.EnumerateArray())
        {
            if (el.ValueKind != JsonValueKind.Object)
                continue;

            string typename = el.TryGetProperty("__typename", out var tn) ? tn.GetString() ?? "" : "";
            if (string.Equals(typename, "StatusContext", StringComparison.Ordinal))
            {
                // Legacy commit-status context: a single `state`, keyed by `context`, linking to `targetUrl`.
                string name  = el.TryGetProperty("context", out var c) ? c.GetString() ?? "" : "";
                string cstate = el.TryGetProperty("state", out var st) ? st.GetString() ?? "" : "";
                string url    = el.TryGetProperty("targetUrl", out var tu) ? tu.GetString() ?? "" : "";
                checks.Add(new PrCheck(name, MapStatusContextState(cstate), url));
            }
            else
            {
                // A CheckRun (GitHub Actions / most integrations): `status` is the lifecycle, `conclusion`
                // the outcome once it reaches COMPLETED, `detailsUrl` its logs page.
                string name       = el.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                string rstatus    = el.TryGetProperty("status", out var s) ? s.GetString() ?? "" : "";
                string conclusion = el.TryGetProperty("conclusion", out var cc) ? cc.GetString() ?? "" : "";
                string url        = el.TryGetProperty("detailsUrl", out var du) ? du.GetString() ?? "" : "";
                checks.Add(new PrCheck(name, MapCheckRunState(rstatus, conclusion), url));
            }
        }
        return checks;
    }

    // A CheckRun is only decided once status is COMPLETED; before that it's queued/in-progress ⇒ Pending.
    // On completion the conclusion buckets it: SUCCESS/NEUTRAL/SKIPPED are non-blocking greens; everything
    // else (FAILURE, TIMED_OUT, CANCELLED, ACTION_REQUIRED, STARTUP_FAILURE, STALE) is a red.
    private static PrCheckState MapCheckRunState(string status, string conclusion)
    {
        if (!string.Equals(status, "COMPLETED", StringComparison.OrdinalIgnoreCase))
            return PrCheckState.Pending;
        return conclusion.ToUpperInvariant() switch
        {
            "SUCCESS" or "NEUTRAL" or "SKIPPED" => PrCheckState.Success,
            "" => PrCheckState.Pending, // completed but no conclusion reported yet — treat as still-settling
            _  => PrCheckState.Failure,
        };
    }

    // A legacy commit-status context reports a single state: SUCCESS green, PENDING/EXPECTED still-running,
    // FAILURE/ERROR (or anything unexpected) red.
    private static PrCheckState MapStatusContextState(string state) =>
        state.ToUpperInvariant() switch
        {
            "SUCCESS"               => PrCheckState.Success,
            "PENDING" or "EXPECTED" => PrCheckState.Pending,
            _                       => PrCheckState.Failure,
        };

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
