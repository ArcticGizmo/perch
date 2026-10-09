namespace Perch.Data;

using System.Text.RegularExpressions;

/// <summary>A GitHub-style repository reference: host plus <c>owner/repo</c>. Matching is case-insensitive, as GitHub's is.</summary>
public readonly record struct GitRepoRef(string Host, string Owner, string Repo)
{
    /// <summary><c>owner/repo</c>, the key the dashboard and the remembered checkouts use.</summary>
    public string Slug => $"{Owner}/{Repo}";

    public bool SameRepo(GitRepoRef other) =>
        string.Equals(Owner, other.Owner, StringComparison.OrdinalIgnoreCase)
        && string.Equals(Repo, other.Repo, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Parses the remote URLs <c>git remote -v</c> prints, and pull-request URLs, into a <see cref="GitRepoRef"/>. Pure.
///
/// <para>Accepted remote shapes: <c>https://[user@]host[:port]/owner/repo[.git][/]</c> (and http, git://),
/// <c>ssh://[user@]host[:port]/owner/repo[.git]</c>, and scp-style <c>[user@]host:owner/repo[.git]</c>. The host
/// is kept but matching goes by <c>owner/repo</c>, because an SSH config alias (<c>git@github-work:acme/web</c>)
/// names a host that isn't github.com.</para>
/// </summary>
public static class GitRemote
{
    // owner: GitHub's login shape; repo: letters, digits, '.', '-', '_' (".git" stripped separately).
    private const string OwnerRepo = @"(?<owner>[A-Za-z0-9](?:[A-Za-z0-9-]{0,38}))/(?<repo>[A-Za-z0-9._-]{1,100})";

    private static readonly Regex UrlForm = new(
        @"^(?:https?|ssh|git)://(?:[^@/]+@)?(?<host>[^/:]+)(?::\d+)?/" + OwnerRepo + @"/?$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex ScpForm = new(
        @"^(?:[^@/:]+@)?(?<host>[^/:]+):" + OwnerRepo + @"/?$",
        RegexOptions.CultureInvariant);

    private static readonly Regex PrUrl = new(
        @"^https?://(?<host>[^/:]+)(?::\d+)?/" + OwnerRepo + @"/pull/\d+(?:[/?#].*)?$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>The repository a remote URL points at, or null when it isn't a recognisable owner/repo remote
    /// (a local path, a bare name, a deeper path such as GitLab subgroups).</summary>
    public static GitRepoRef? Parse(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        url = url.Trim();
        // A Windows path ("C:/src/repo") reads like scp form; a drive letter is never a host.
        if (url.Length >= 2 && url[1] == ':' && char.IsLetter(url[0])) return null;
        var m = UrlForm.Match(url);
        if (!m.Success) m = ScpForm.Match(url);
        return m.Success ? Ref(m) : null;
    }

    /// <summary>The repository a pull-request URL (<c>https://github.com/owner/repo/pull/12</c>) belongs to.</summary>
    public static GitRepoRef? FromPullRequestUrl(string? url) =>
        url is not null && PrUrl.Match(url.Trim()) is { Success: true } m ? Ref(m) : null;

    private static GitRepoRef? Ref(Match m)
    {
        var repo = m.Groups["repo"].Value;
        if (repo.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) repo = repo[..^4];
        if (repo.Length == 0 || repo is "." or "..") return null;
        return new GitRepoRef(m.Groups["host"].Value.ToLowerInvariant(), m.Groups["owner"].Value, repo);
    }
}

/// <summary>One git remote of a local checkout, as <c>git remote -v</c> lists it.</summary>
public readonly record struct GitRemoteEntry(string Name, string Url);

/// <summary>A local repository root Perch knows about (from the folders Claude sessions ran in) and its remotes.</summary>
public sealed record CheckoutCandidate(string Root, IReadOnlyList<GitRemoteEntry> Remotes);

/// <summary>
/// Where a repository is checked out locally. <see cref="Path"/> is set when the answer is unambiguous; otherwise
/// <see cref="Candidates"/> lists the folders to choose from (best first), empty when nothing matches.
/// </summary>
public sealed record CheckoutMatch(string? Path, IReadOnlyList<string> Candidates)
{
    public static readonly CheckoutMatch None = new(null, []);
    public bool NeedsChoice => Path is null && Candidates.Count > 1;
}

/// <summary>
/// Picks the local checkout to run a session in for a pull request's repository. Pure: the caller supplies the
/// known repo roots with their remotes (read through git), the folder the user picked last time, and a file-system
/// existence check.
///
/// <para>Rules, in order: a remembered folder that still exists wins (the user chose it). Otherwise a checkout with
/// any remote naming the repo is a match — <c>origin</c> or <c>upstream</c> alike, since a fork's checkout is the
/// right place to work on the upstream PR — ranked origin first. One match (or exactly one origin match) is the
/// answer; several are a choice for the user. Perch's own worktrees (under <see cref="WorktreeFolderName"/>) are
/// never candidates: they're per-PR copies, not the user's checkout.</para>
/// </summary>
public static class RepoCheckoutResolver
{
    /// <summary>The folder Perch puts per-PR worktrees under, beside the user's checkout.</summary>
    public const string WorktreeFolderName = ".perch-worktrees";

    public static CheckoutMatch Resolve(GitRepoRef repo, IEnumerable<CheckoutCandidate> known, string? remembered,
        Func<string, bool> exists)
    {
        if (!string.IsNullOrWhiteSpace(remembered) && exists(remembered)) return new(remembered, [remembered]);

        var ranked = known
            .Where(c => !IsPerchWorktree(c.Root))
            .GroupBy(c => Normalize(c.Root), StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .Select(c => (c.Root, Rank: Rank(c, repo)))
            .Where(x => x.Rank < int.MaxValue && exists(x.Root))
            .OrderBy(x => x.Rank)
            .ThenBy(x => x.Root, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (ranked.Count == 0) return CheckoutMatch.None;
        var roots = ranked.Select(x => x.Root).ToList();
        if (ranked.Count == 1 || ranked.Count(x => x.Rank == 0) == 1)   // sorted, so the lone origin match is first
            return new(roots[0], roots);
        return new(null, roots);
    }

    // 0 = origin names the repo, 1 = another remote does, MaxValue = no remote does.
    private static int Rank(CheckoutCandidate c, GitRepoRef repo)
    {
        int best = int.MaxValue;
        foreach (var r in c.Remotes)
        {
            if (GitRemote.Parse(r.Url) is not { } parsed || !parsed.SameRepo(repo)) continue;
            best = Math.Min(best, string.Equals(r.Name, "origin", StringComparison.OrdinalIgnoreCase) ? 0 : 1);
        }
        return best;
    }

    /// <summary>Whether <paramref name="path"/> sits inside a Perch worktree folder.</summary>
    public static bool IsPerchWorktree(string path) =>
        Normalize(path).Split('/').Any(seg => string.Equals(seg, WorktreeFolderName, StringComparison.OrdinalIgnoreCase));

    private static string Normalize(string path) => path.Replace('\\', '/').TrimEnd('/');
}
