namespace Perch.Data;

using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

/// <summary>
/// Fetches the open pull requests that involve the signed-in GitHub user, through the GitHub CLI so Perch needs
/// no token of its own: <c>gh</c> already holds the user's auth. One poll is at most two <c>gh</c> runs — the
/// login (once per process, cached) and a single GraphQL request carrying three searches (PRs you authored,
/// PRs awaiting your review, PRs assigned to you). That keeps a poll to a few rate-limit points.
///
/// <para>Blocking and best-effort: call it off the UI thread. Every failure comes back as a
/// <see cref="GitHubFetchResult.Error"/> line rather than an exception. The parsing is split out
/// (<see cref="ParseLogin"/>, <see cref="ParseSearch"/>) so it is tested without ever launching gh.</para>
/// </summary>
internal static class GitHubAlertsClient
{
    private const int LoginTimeoutMs = 10_000;
    private const int QueryTimeoutMs = 25_000;

    // A GitHub login: alphanumerics and single hyphens (bots carry a "[bot]" suffix). Validated before it is
    // spliced into the search strings, so nothing from gh's output can reshape the query.
    private static readonly Regex LoginShape = new(@"^[A-Za-z0-9](?:[A-Za-z0-9-]{0,38})$", RegexOptions.CultureInvariant);

    private static volatile string? _login;

    /// <summary>Forgets the cached login, so the next fetch re-reads it — called when the feature is switched on,
    /// which is when a user who has just run <c>gh auth login</c> (or switched account) would retry.</summary>
    public static void ResetLogin() => _login = null;

    // The three searches share one fragment. Comments and reviews are the newest 30 of each — enough to tell
    // whether anything landed since you last acted; latestReviews answers "has anyone approved".
    private const string Query = """
        query($authored: String!, $review: String!, $assigned: String!) {
          authored: search(query: $authored, type: ISSUE, first: 50) { nodes { ...pr } }
          review:   search(query: $review,   type: ISSUE, first: 50) { nodes { ...pr } }
          assigned: search(query: $assigned, type: ISSUE, first: 50) { nodes { ...pr } }
        }
        fragment pr on PullRequest {
          number title url isDraft updatedAt
          repository { nameWithOwner }
          headRefName baseRefName isCrossRepository headRepository { nameWithOwner }
          author { login }
          reviewDecision mergeable mergeStateStatus
          commits(last: 1) { nodes { commit { committedDate statusCheckRollup { state } } } }
          latestReviews(first: 20) { nodes { state } }
          reviews(last: 30) { nodes { author { __typename login } state submittedAt } }
          comments(last: 30) { nodes { author { __typename login } createdAt } }
        }
        """;

    public static GitHubFetchResult Fetch()
    {
        var now = DateTime.UtcNow;
        if (ExecutableResolver.Find("gh") is null)
            return new(null, [], "GitHub CLI (gh) not found", now);

        var login = _login;
        if (login is null)
        {
            var (exit, stdout, stderr) = RunGh(["api", "user"], null, LoginTimeoutMs);
            login = exit == 0 ? ParseLogin(stdout) : null;
            if (login is null)
                return new(null, [], DescribeFailure(exit, stderr, "Couldn't read your GitHub login"), now);
            _login = login;
        }

        var body = JsonSerializer.Serialize(new
        {
            query = Query,
            variables = new
            {
                authored = $"is:pr is:open archived:false sort:updated-desc author:{login}",
                review   = $"is:pr is:open archived:false sort:updated-desc review-requested:{login}",
                assigned = $"is:pr is:open archived:false sort:updated-desc assignee:{login}",
            },
        });
        var (qExit, qOut, qErr) = RunGh(["api", "graphql", "--input", "-"], body, QueryTimeoutMs);
        if (qExit != 0 && string.IsNullOrWhiteSpace(qOut))
            return new(login, [], DescribeFailure(qExit, qErr, "GitHub query failed"), now);

        var prs = ParseSearch(qOut);
        return prs is null
            ? new(login, [], "GitHub returned an unreadable response", now)
            : new(login, prs, null, now);
    }

    /// <summary>The <c>login</c> from <c>gh api user</c>'s JSON, or null when absent or not login-shaped.</summary>
    internal static string? ParseLogin(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("login", out var l) && l.GetString() is { } s && LoginShape.IsMatch(s)
                ? s : null;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>
    /// Parses the GraphQL response into one <see cref="GhPullRequest"/> per PR, merging the three searches by URL
    /// (a PR found by more than one gets every matching <see cref="GhPrRelation"/>). Null when the body isn't a
    /// GraphQL response with <c>data</c>; a partial response (some <c>errors</c> alongside data) still parses.
    /// </summary>
    internal static IReadOnlyList<GhPullRequest>? ParseSearch(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
                return null;

            var byUrl = new Dictionary<string, GhPullRequest>(StringComparer.OrdinalIgnoreCase);
            var order = new List<string>();
            foreach (var (alias, relation) in new[]
                     {
                         ("authored", GhPrRelation.Author),
                         ("review", GhPrRelation.ReviewRequested),
                         ("assigned", GhPrRelation.Assignee),
                     })
            {
                if (!data.TryGetProperty(alias, out var search) || search.ValueKind != JsonValueKind.Object
                    || !search.TryGetProperty("nodes", out var nodes) || nodes.ValueKind != JsonValueKind.Array)
                    continue;
                foreach (var node in nodes.EnumerateArray())
                {
                    if (ParsePr(node, relation) is not { } pr) continue;
                    if (byUrl.TryGetValue(pr.Url, out var existing))
                        byUrl[pr.Url] = existing with { Relation = existing.Relation | relation };
                    else
                    {
                        byUrl[pr.Url] = pr;
                        order.Add(pr.Url);
                    }
                }
            }
            return order.Select(u => byUrl[u]).ToList();
        }
        catch (JsonException) { return null; }
    }

    private static GhPullRequest? ParsePr(JsonElement n, GhPrRelation relation)
    {
        if (n.ValueKind != JsonValueKind.Object) return null;
        if (!n.TryGetProperty("number", out var num) || !num.TryGetInt32(out var number)) return null;
        var url = Str(n, "url");
        if (url.Length == 0) return null;

        DateTime? lastCommit = null;
        var checks = GhChecks.None;
        if (n.TryGetProperty("commits", out var commits) && Nodes(commits) is { } cn)
            foreach (var c in cn.EnumerateArray())
            {
                if (!c.TryGetProperty("commit", out var commit) || commit.ValueKind != JsonValueKind.Object) continue;
                lastCommit = Date(commit, "committedDate");
                if (commit.TryGetProperty("statusCheckRollup", out var roll) && roll.ValueKind == JsonValueKind.Object)
                    checks = Str(roll, "state").ToUpperInvariant() switch
                    {
                        "SUCCESS"               => GhChecks.Passing,
                        "FAILURE" or "ERROR"    => GhChecks.Failing,
                        "PENDING" or "EXPECTED" => GhChecks.Pending,
                        _                       => GhChecks.None,
                    };
            }

        bool approved = false;
        if (n.TryGetProperty("latestReviews", out var latest) && Nodes(latest) is { } ln)
            foreach (var r in ln.EnumerateArray())
                if (Str(r, "state").Equals("APPROVED", StringComparison.OrdinalIgnoreCase)) approved = true;

        var events = new List<GhEvent>();
        if (n.TryGetProperty("reviews", out var reviews) && Nodes(reviews) is { } rn)
            foreach (var r in rn.EnumerateArray())
            {
                // A PENDING review is the viewer's own unsubmitted draft: not activity yet.
                var kind = Str(r, "state").ToUpperInvariant() switch
                {
                    "APPROVED"          => GhEventKind.Approved,
                    "CHANGES_REQUESTED" => GhEventKind.ChangesRequested,
                    "DISMISSED"         => GhEventKind.Dismissed,
                    "COMMENTED"         => GhEventKind.Commented,
                    _                   => (GhEventKind?)null,
                };
                if (kind is { } k && Date(r, "submittedAt") is { } at)
                    events.Add(Event(r, k, at));
            }
        if (n.TryGetProperty("comments", out var comments) && Nodes(comments) is { } cmn)
            foreach (var c in cmn.EnumerateArray())
                if (Date(c, "createdAt") is { } at)
                    events.Add(Event(c, GhEventKind.Comment, at));
        events.Sort((a, b) => a.AtUtc.CompareTo(b.AtUtc));

        string repo = n.TryGetProperty("repository", out var repoEl) && repoEl.ValueKind == JsonValueKind.Object
            ? Str(repoEl, "nameWithOwner") : "";
        // Null when the fork it came from has been deleted.
        string headRepo = n.TryGetProperty("headRepository", out var headEl) && headEl.ValueKind == JsonValueKind.Object
            ? Str(headEl, "nameWithOwner") : "";

        return new GhPullRequest
        {
            Repo = repo.Length > 0 ? repo : "(unknown repo)",
            Number = number,
            Title = Str(n, "title"),
            Url = url,
            HeadBranch = Str(n, "headRefName"),
            BaseBranch = Str(n, "baseRefName"),
            HeadRepo = headRepo,
            IsCrossRepository = n.TryGetProperty("isCrossRepository", out var cross) && cross.ValueKind == JsonValueKind.True,
            Author = Login(n),
            IsDraft = n.TryGetProperty("isDraft", out var d) && d.ValueKind == JsonValueKind.True,
            UpdatedUtc = Date(n, "updatedAt") ?? DateTime.MinValue,
            LastCommitUtc = lastCommit,
            ReviewDecision = Str(n, "reviewDecision").ToUpperInvariant(),
            Mergeable = Str(n, "mergeable").ToUpperInvariant() switch
            {
                "MERGEABLE"   => GhMergeable.Mergeable,
                "CONFLICTING" => GhMergeable.Conflicting,
                _             => GhMergeable.Unknown,
            },
            MergeState = Str(n, "mergeStateStatus").ToUpperInvariant(),
            Checks = checks,
            HasApproval = approved,
            Events = events,
            Relation = relation,
        };
    }

    private static GhEvent Event(JsonElement el, GhEventKind kind, DateTime at)
    {
        bool bot = false;
        if (el.TryGetProperty("author", out var a) && a.ValueKind == JsonValueKind.Object)
            bot = Str(a, "__typename") == "Bot";
        var login = Login(el);
        return new GhEvent(login, bot || login.EndsWith("[bot]", StringComparison.OrdinalIgnoreCase), kind, at);
    }

    // author.login, or "" for a deleted ("ghost") account, which GraphQL reports as a null author.
    private static string Login(JsonElement el) =>
        el.TryGetProperty("author", out var a) && a.ValueKind == JsonValueKind.Object ? Str(a, "login") : "";

    private static JsonElement? Nodes(JsonElement conn) =>
        conn.ValueKind == JsonValueKind.Object && conn.TryGetProperty("nodes", out var nodes)
        && nodes.ValueKind == JsonValueKind.Array ? nodes : null;

    private static string Str(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static DateTime? Date(JsonElement el, string name) =>
        DateTime.TryParse(Str(el, name), CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var dt) ? dt : null;

    // A one-line reason for the strip. gh's own wording is kept for an auth problem, since it says what to run.
    private static string DescribeFailure(int exit, string stderr, string fallback)
    {
        if (exit == -1) return "GitHub didn't respond";
        if (stderr.Contains("auth login", StringComparison.OrdinalIgnoreCase)
            || stderr.Contains("not logged", StringComparison.OrdinalIgnoreCase))
            return "gh isn't signed in (run gh auth login)";
        return fallback;
    }

    // Runs gh with an argument list (no shell, no quoting) and optional stdin. Exit is -1 on a launch failure
    // or timeout. Both pipes are drained asynchronously so a large response can't deadlock the wait.
    private static (int Exit, string Stdout, string Stderr) RunGh(IReadOnlyList<string> args, string? stdin, int timeoutMs)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = ExecutableResolver.Resolve("gh"),     // absolute, like PrStatusService (CP7)
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = stdin is not null,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);

            using var proc = Process.Start(psi);
            if (proc is null) return (-1, "", "");
            var stdout = proc.StandardOutput.ReadToEndAsync();
            var stderr = proc.StandardError.ReadToEndAsync();
            if (stdin is not null)
            {
                proc.StandardInput.Write(stdin);
                proc.StandardInput.Close();
            }
            if (!proc.WaitForExit(timeoutMs))
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                return (-1, "", "");
            }
            return (proc.ExitCode, stdout.GetAwaiter().GetResult(), stderr.GetAwaiter().GetResult());
        }
        catch
        {
            return (-1, "", "");
        }
    }
}
