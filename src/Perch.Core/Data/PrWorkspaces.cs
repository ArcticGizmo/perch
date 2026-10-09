namespace Perch.Data;

using System.Text.RegularExpressions;

/// <summary>Where a PR session's code lives (W5 in docs/github-dashboard-plan.md).</summary>
public enum PrWorkspace
{
    /// <summary>A worktree the user already has with the PR's head branch checked out.</summary>
    ExistingWorktree,
    /// <summary>A new worktree of the user's checkout, laid out like their others (<see cref="PrWorktree"/>).</summary>
    Worktree,
    /// <summary>A separate clone in Perch's data folder, sharing nothing with the user's repo (<see cref="PrClone"/>).</summary>
    Clone,
    /// <summary>An empty scratch folder; Claude reads the PR through <c>gh</c> only (<see cref="PrScratch"/>).</summary>
    DiffOnly,
    /// <summary>The user's checkout as it is.</summary>
    Checkout,
}

/// <summary>Perch-owned folders for PR sessions, under the local (not roaming) app data folder: clones can be big.</summary>
public static class PrWorkspaceFolders
{
    public static string Base => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppProfile.DataFolderName);

    /// <summary><c>&lt;base&gt;/&lt;kind&gt;/&lt;owner&gt;-&lt;repo&gt;-pr-&lt;n&gt;</c>. Owner and repo are already
    /// restricted to <c>[A-Za-z0-9._-]</c> by <see cref="GitRemote"/>; anything else is replaced anyway.</summary>
    public static string For(string baseDir, string kind, GitRepoRef repo, int number) =>
        Path.Combine(baseDir, kind, $"{Safe(repo.Owner)}-{Safe(repo.Repo)}-pr-{number}");

    private static string Safe(string s) =>
        new(s.Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '-').ToArray());
}

/// <summary>
/// A separate clone for one PR: <c>git clone --filter=blob:none --no-checkout</c> (history without file contents,
/// fetched as needed), then the PR's head fetched onto <c>perch/pr-&lt;n&gt;</c> and checked out. Shares nothing with
/// the user's own repo — the right home for code from someone else's fork, or a repo the user hasn't cloned. Reused
/// when it already exists. The clone URL is built from the validated owner/repo and host, never from PR text.
///
/// <para>Blocking; call it off the UI thread. Never throws. A failed clone removes its half-made folder.</para>
/// </summary>
public static class PrClone
{
    private const int CloneTimeoutMs = 600_000;
    private const int FetchTimeoutMs = 120_000;

    private static readonly Regex HostShape = new(@"^[A-Za-z0-9](?:[A-Za-z0-9.-]{0,252})$", RegexOptions.CultureInvariant);

    /// <summary>The https URL to clone, or null when the host or names aren't safe to put in one.</summary>
    public static string? CloneUrl(GitRepoRef repo)
    {
        if (!HostShape.IsMatch(repo.Host) || GitRemote.Parse($"https://{repo.Host}/{repo.Owner}/{repo.Repo}") is not { } check
            || !check.SameRepo(repo))
            return null;
        return $"https://{repo.Host}/{repo.Owner}/{repo.Repo}.git";
    }

    public static PrWorktreeResult Ensure(GitRepoRef repo, int number, string? baseDir = null)
    {
        string? dir = null;
        bool created = false;
        try
        {
            if (number <= 0 || CloneUrl(repo) is not { } url) return new(null, false, $"Can't clone {repo.Slug}");
            dir = PrWorkspaceFolders.For(baseDir ?? PrWorkspaceFolders.Base, "pr-clones", repo, number);
            var branch = PrWorktree.BranchFor(number);

            if (Directory.Exists(Path.Combine(dir, ".git")))
                return new(dir, true, null);
            if (Directory.Exists(dir) && Directory.EnumerateFileSystemEntries(dir).Any())
                return new(null, false, $"{dir} already exists and isn't a clone");

            var parent = Path.GetDirectoryName(dir)!;
            Directory.CreateDirectory(parent);
            created = true;
            var clone = GitRunner.Run(parent, CloneTimeoutMs, GitRunner.Trust.UserAction, null,
                "clone", "--filter=blob:none", "--no-checkout", "--", url, dir);
            if (clone.Exit != 0) return Fail(dir, Describe("Couldn't clone the repo", clone));

            var fetch = GitRunner.Run(dir, FetchTimeoutMs, GitRunner.Trust.UserAction, null,
                "fetch", "--no-tags", "origin", $"pull/{number}/head:{branch}");
            if (fetch.Exit != 0) return Fail(dir, Describe("Couldn't fetch the PR", fetch));

            var checkout = GitRunner.Run(dir, FetchTimeoutMs, GitRunner.Trust.UserAction, null, "checkout", branch);
            if (checkout.Exit != 0) return Fail(dir, Describe("Couldn't check out the PR", checkout));
            return new(dir, false, null);
        }
        catch (Exception ex)
        {
            return created && dir is not null ? Fail(dir, ex.Message) : new(null, false, ex.Message);
        }
    }

    private static PrWorktreeResult Fail(string dir, string error)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { }
        return new(null, false, error);
    }

    private static string Describe(string what, GitRunner.Result r)
    {
        if (r.Exit == -1) return $"{what} (git didn't respond)";
        var line = r.Stderr.Split('\n').Select(l => l.Trim())
            .FirstOrDefault(l => l.StartsWith("fatal:", StringComparison.Ordinal) || l.StartsWith("error:", StringComparison.Ordinal))
            ?? r.Stderr.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);
        return line is null ? what : $"{what}: {line}";
    }
}

/// <summary>
/// An empty scratch folder for a diff-only PR session: nothing is fetched, and Claude reads the PR through
/// <c>gh pr diff</c> / <c>gh api</c>. Perch made it and it holds no code, so it can be given folder trust without
/// asking. Reused when it exists.
/// </summary>
public static class PrScratch
{
    public static PrWorktreeResult Ensure(GitRepoRef repo, int number, string? baseDir = null)
    {
        try
        {
            if (number <= 0) return new(null, false, "No PR number");
            var dir = PrWorkspaceFolders.For(baseDir ?? PrWorkspaceFolders.Base, "pr-scratch", repo, number);
            bool existed = Directory.Exists(dir);
            Directory.CreateDirectory(dir);
            return new(dir, existed, null);
        }
        catch (Exception ex)
        {
            return new(null, false, ex.Message);
        }
    }
}
