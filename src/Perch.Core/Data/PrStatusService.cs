namespace Perch.Data;

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
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

/// <summary>The state of a single reviewer's latest review on a PR, as reported by <c>gh</c>'s
/// <c>latestReviews[].state</c>. Drives the "review added" / "approved" alerts.</summary>
public enum PrReviewState { Pending, Commented, ChangesRequested, Approved, Dismissed }

/// <summary>A reviewer's latest review on a pull request: who left it, its <see cref="PrReviewState"/>, and
/// when it was submitted (used to pick the newest one when an alert needs a single "who").</summary>
public readonly record struct PrReview(string Author, PrReviewState State, DateTime SubmittedAt);

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

    /// <summary>The latest review from each reviewer (<c>gh</c>'s <c>latestReviews</c>). Empty when the PR
    /// has no reviews or they weren't fetched. Drives the "review added" / "approved" alerts.</summary>
    public IReadOnlyList<PrReview> LatestReviews { get; init; } = Array.Empty<PrReview>();

    /// <summary>The aggregate check status driving the overlay's status dot, folded from <see cref="Checks"/>.</summary>
    public PrChecksRollup ChecksRollup =>
        Checks.Count == 0                             ? PrChecksRollup.None
      : Checks.Any(c => c.State == PrCheckState.Failure) ? PrChecksRollup.Failing
      : Checks.Any(c => c.State == PrCheckState.Pending) ? PrChecksRollup.Pending
      :                                                    PrChecksRollup.Passing;

    /// <summary>The most recently submitted review overall (the one that triggered a "review added"), or
    /// null when there are none. Used to name "who" in the alert.</summary>
    public PrReview? NewestReview =>
        LatestReviews.Count == 0 ? null : LatestReviews.Aggregate((a, b) => b.SubmittedAt > a.SubmittedAt ? b : a);

    /// <summary>The most recently submitted <em>approving</em> review, or null when none — names the approver
    /// in the "approved" alert.</summary>
    public PrReview? NewestApproval
    {
        get
        {
            PrReview? best = null;
            foreach (var r in LatestReviews)
                if (r.State == PrReviewState.Approved && (best is null || r.SubmittedAt > best.Value.SubmittedAt))
                    best = r;
            return best;
        }
    }

    public bool Equals(PullRequestInfo other) =>
        Number == other.Number && Url == other.Url && Title == other.Title && State == other.State
        && Checks.SequenceEqual(other.Checks) && LatestReviews.SequenceEqual(other.LatestReviews);

    public override int GetHashCode() =>
        HashCode.Combine(Number, Url, Title, State, Checks.Count, LatestReviews.Count);
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
///
/// The cache key is the PR's true identity: the current branch within a specific working tree (its git
/// dir). Worktrees — and the main checkout — of one repo resolve distinct git dirs, so their PRs never
/// bleed together; a branch switch produces a different key, so the overlay drops the old branch's PR at
/// once and refetches (and hopping back to a branch still cached within the interval is an instant hit).
/// Both halves of the key come from cheap filesystem reads on the scan — no process spawn.
/// </summary>
internal sealed class PrStatusService : IDisposable
{
    // Hard ceiling on a single gh invocation. gh talks to the GitHub API, so this is generous compared to
    // the git-diff timeout — but still bounded so a hung network can't wedge a worker forever.
    private const int GhTimeoutMs = 8000;

    // At most this many gh processes run at once, so a Perch opening onto many sessions staggers its
    // lookups instead of forking a process per row simultaneously.
    private const int MaxConcurrent = 3;

    private readonly ConcurrentDictionary<PrKey, Entry> _cache = new();
    // Keys with a refresh in flight, so concurrent scans don't pile up duplicate gh processes.
    private readonly ConcurrentDictionary<PrKey, byte> _fetching = new();
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

        // Resolve the working tree's git dir (distinct per worktree) and its current branch — both cheap
        // filesystem reads, no process spawn. Together they are the PR's true identity and the cache key: a
        // branch switch, or a hop to a sibling worktree of the same repo, lands on a different key, so the
        // previous PR is never shown for the new state. A non-repo can never have a PR — skip it entirely.
        var gitDir = FindGitDir(cwd);
        if (gitDir == null)
            return null;

        var key = new PrKey(gitDir, ReadHead(gitDir) ?? "");

        bool cached = _cache.TryGetValue(key, out var entry);
        if (cached && DateTime.UtcNow - entry.FetchedAt < Ttl)
            return entry.Pr;

        ScheduleRefresh(key, cwd);
        return cached ? entry.Pr : null;
    }

    // Kicks off a single background gh run for this (worktree, branch) key (a no-op if one is already in
    // flight), updates the cache when it returns, and raises Updated only when the PR actually changed. The
    // gate caps how many run at once; a queued run still holds its _fetching slot so no duplicate is scheduled.
    private void ScheduleRefresh(PrKey key, string cwd)
    {
        if (!_fetching.TryAdd(key, 0))
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
                    // Any session directory under the worktree resolves the same PR, so cwd is just where
                    // gh runs; the (worktree, branch) key is what the result is filed under.
                    PullRequestInfo? result = RunGhPrView(cwd);
                    if (_disposed || !_enabled)
                        return;
                    bool changed = !_cache.TryGetValue(key, out var old) || !Nullable.Equals(old.Pr, result);
                    _cache[key] = new Entry(result, DateTime.UtcNow);
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
                _fetching.TryRemove(key, out _);
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

        var (exit, stdout) = RunGh("pr view --json number,url,title,state,isDraft,statusCheckRollup,latestReviews", cwd, GhTimeoutMs);
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

            return new PullRequestInfo(number, url, title, MapState(state, isDraft))
                { Checks = ParseChecks(root), LatestReviews = ParseReviews(root) };
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

    /// <summary>
    /// Reads the <c>latestReviews</c> array <c>gh</c> emits into a list of <see cref="PrReview"/> — each the
    /// latest review from one reviewer: <c>author.login</c>, <c>state</c> folded onto <see cref="PrReviewState"/>,
    /// and <c>submittedAt</c> (parsed to UTC; <see cref="DateTime.MinValue"/> when absent/unparseable). A
    /// missing/non-array field yields an empty list. Internal for unit testing.
    /// </summary>
    internal static IReadOnlyList<PrReview> ParseReviews(JsonElement root)
    {
        if (!root.TryGetProperty("latestReviews", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return Array.Empty<PrReview>();

        var reviews = new List<PrReview>();
        foreach (var el in arr.EnumerateArray())
        {
            if (el.ValueKind != JsonValueKind.Object)
                continue;

            string author = "";
            if (el.TryGetProperty("author", out var a) && a.ValueKind == JsonValueKind.Object
                && a.TryGetProperty("login", out var l))
                author = l.GetString() ?? "";

            string state = el.TryGetProperty("state", out var s) ? s.GetString() ?? "" : "";

            DateTime submitted = DateTime.MinValue;
            if (el.TryGetProperty("submittedAt", out var sa) && sa.ValueKind == JsonValueKind.String
                && DateTime.TryParse(sa.GetString(), CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var dt))
                submitted = dt;

            reviews.Add(new PrReview(author, MapReviewState(state), submitted));
        }
        return reviews;
    }

    // Folds gh's review state onto PrReviewState. Anything unexpected is treated as a plain comment.
    private static PrReviewState MapReviewState(string state) =>
        state.ToUpperInvariant() switch
        {
            "APPROVED"          => PrReviewState.Approved,
            "CHANGES_REQUESTED" => PrReviewState.ChangesRequested,
            "DISMISSED"         => PrReviewState.Dismissed,
            "PENDING"           => PrReviewState.Pending,
            _                   => PrReviewState.Commented,
        };

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

    // Cheap filesystem check: is cwd inside a git working tree? True when a git directory is found.
    private static bool HasGitRepo(string cwd) => FindGitDir(cwd) != null;

    /// <summary>
    /// Walks up from <paramref name="cwd"/> to the repo's git directory: the <c>.git</c> folder in a normal
    /// clone, or — in a worktree or submodule, where <c>.git</c> is a file reading
    /// <c>gitdir: &lt;path&gt;</c> — the directory that file points at (resolved relative to the
    /// <c>.git</c> file's folder). Null when <paramref name="cwd"/> isn't inside a git working tree. Lets us
    /// both skip gh for plain directories and read HEAD without spawning git. Internal for unit testing.
    /// </summary>
    internal static string? FindGitDir(string cwd)
    {
        try
        {
            for (var d = new DirectoryInfo(cwd); d != null; d = d.Parent)
            {
                var git = Path.Combine(d.FullName, ".git");
                if (Directory.Exists(git))
                    return git;
                if (File.Exists(git))
                {
                    const string prefix = "gitdir:";
                    var line = File.ReadAllText(git).Trim();
                    if (!line.StartsWith(prefix, StringComparison.Ordinal))
                        return null;
                    var p = line[prefix.Length..].Trim();
                    return Path.IsPathRooted(p) ? p : Path.GetFullPath(Path.Combine(d.FullName, p));
                }
            }
        }
        catch { }
        return null;
    }

    /// <summary>
    /// The current branch identity for <paramref name="cwd"/>: <see cref="FindGitDir"/> followed by
    /// <see cref="ReadHead"/>. On a normal checkout this is <c>ref: refs/heads/&lt;branch&gt;</c> — stable
    /// across commits, changing only when the branch is switched — which makes it the ideal cache-key half
    /// for a branch-scoped PR. Null when <paramref name="cwd"/> isn't a repo or HEAD is unreadable. Internal
    /// for unit testing.
    /// </summary>
    internal static string? ReadHeadRef(string cwd)
    {
        var gitDir = FindGitDir(cwd);
        return gitDir == null ? null : ReadHead(gitDir);
    }

    /// <summary>
    /// The trimmed contents of <paramref name="gitDir"/>'s HEAD file — <c>ref: refs/heads/&lt;branch&gt;</c>
    /// on a normal checkout, or the raw commit SHA when detached (rare; it simply re-probes per commit).
    /// Null when HEAD is unreadable. Opened shared: git rewrites HEAD via an atomic rename, so a concurrent
    /// switch reads either the old or new value whole, never a torn one.
    /// </summary>
    private static string? ReadHead(string gitDir)
    {
        try
        {
            var head = Path.Combine(gitDir, "HEAD");
            using var fs = new FileStream(head, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var sr = new StreamReader(fs);
            return sr.ReadToEnd().Trim();
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _cache.Clear();
        _gate.Dispose();
    }

    // The identity a pull request actually depends on: a branch, within a specific working tree. Each
    // worktree of a repo (and the main checkout) resolves a distinct git dir — <root>/.git for the main
    // tree, <root>/.git/worktrees/<name> for a linked one — so keying by (gitDir, branch) keeps each
    // worktree's PR separate, while a branch switch yields a different key that drops the old branch's PR at
    // once. Sessions in different sub-directories of one worktree collapse onto a single key (one gh fetch).
    // Stale keys (branches since left) simply age past their TTL and sit unused — bounded by branches
    // visited, and cleared wholesale when the feature is toggled off.
    private readonly record struct PrKey(string GitDir, string Branch);

    // One cached result: the PR (null = no PR / not GitHub / unreadable) and when it was fetched. The
    // branch and worktree it belongs to are carried by the cache key, not stored here.
    private readonly record struct Entry(PullRequestInfo? Pr, DateTime FetchedAt);
}
