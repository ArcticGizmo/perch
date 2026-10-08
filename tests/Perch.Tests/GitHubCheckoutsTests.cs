using Perch.Data;
using Xunit;

namespace Perch.Tests;

/// <summary>
/// Guards the pure half of "start a session from a PR", step S1 (docs/github-dashboard-plan.md): parsing git remote
/// and pull-request URLs into owner/repo, and picking which local checkout a PR's session should run in.
/// </summary>
public class GitHubCheckoutsTests
{
    [Theory]
    [InlineData("https://github.com/acme/web.git", "github.com", "acme", "web")]
    [InlineData("https://github.com/acme/web", "github.com", "acme", "web")]
    [InlineData("https://github.com/acme/web/", "github.com", "acme", "web")]
    [InlineData("https://jon@github.com/acme/web.git", "github.com", "acme", "web")]
    [InlineData("HTTPS://GitHub.com/Acme/Web.GIT", "github.com", "Acme", "Web")]
    [InlineData("git@github.com:acme/web.git", "github.com", "acme", "web")]
    [InlineData("git@github.com:acme/web", "github.com", "acme", "web")]
    [InlineData("git@github-work:acme/web.git", "github-work", "acme", "web")]     // ssh config alias
    [InlineData("ssh://git@github.com/acme/web.git", "github.com", "acme", "web")]
    [InlineData("ssh://git@github.com:22/acme/web.git", "github.com", "acme", "web")]
    [InlineData("git://github.com/acme/web.git", "github.com", "acme", "web")]
    [InlineData("https://ghe.corp.example/team/my.repo_name-2.git", "ghe.corp.example", "team", "my.repo_name-2")]
    public void ParsesRemoteShapes(string url, string host, string owner, string repo) =>
        Assert.Equal(new GitRepoRef(host, owner, repo), GitRemote.Parse(url));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("origin")]
    [InlineData("C:/src/web")]
    [InlineData(@"C:\src\web")]
    [InlineData("/srv/git/web.git")]
    [InlineData("../web")]
    [InlineData("https://gitlab.com/group/sub/web.git")]        // subgroups aren't owner/repo
    [InlineData("https://github.com/acme")]
    [InlineData("https://github.com/acme/.git")]
    public void RejectsWhatIsNotAnOwnerRepoRemote(string? url) => Assert.Null(GitRemote.Parse(url));

    [Theory]
    [InlineData("https://github.com/acme/web/pull/412", "acme/web")]
    [InlineData("https://github.com/acme/web/pull/412/files", "acme/web")]
    [InlineData("https://github.com/acme/web/pull/412#discussion_r1", "acme/web")]
    [InlineData("https://github.com/acme/web/issues/412", null)]
    [InlineData("not a url", null)]
    public void ReadsTheRepoOfAPullRequestUrl(string url, string? slug) =>
        Assert.Equal(slug, GitRemote.FromPullRequestUrl(url)?.Slug);

    private static readonly GitRepoRef Web = new("github.com", "acme", "web");

    private static CheckoutCandidate C(string root, params (string Name, string Url)[] remotes) =>
        new(root, remotes.Select(r => new GitRemoteEntry(r.Name, r.Url)).ToList());

    private static CheckoutMatch Resolve(string? remembered, params CheckoutCandidate[] known) =>
        RepoCheckoutResolver.Resolve(Web, known, remembered, _ => true);

    [Fact]
    public void OneMatchingCheckoutIsTheAnswer()
    {
        var m = Resolve(null,
            C(@"C:\src\web", ("origin", "git@github.com:acme/web.git")),
            C(@"C:\src\api", ("origin", "git@github.com:acme/api.git")));
        Assert.Equal(@"C:\src\web", m.Path);
        Assert.False(m.NeedsChoice);
    }

    [Fact]
    public void AForkCheckoutMatchesThroughUpstream()
    {
        var m = Resolve(null, C(@"C:\src\my-web", ("origin", "https://github.com/me/web"), ("upstream", "https://github.com/acme/web")));
        Assert.Equal(@"C:\src\my-web", m.Path);
    }

    [Fact]
    public void ALoneOriginMatchBeatsUpstreamMatches()
    {
        var m = Resolve(null,
            C(@"C:\src\fork", ("origin", "https://github.com/me/web"), ("upstream", "https://github.com/acme/web")),
            C(@"C:\src\web", ("origin", "https://github.com/acme/web")));
        Assert.Equal(@"C:\src\web", m.Path);
        Assert.Equal([@"C:\src\web", @"C:\src\fork"], m.Candidates);
    }

    [Fact]
    public void SeveralEqualMatchesAreAChoice()
    {
        var m = Resolve(null,
            C(@"D:\work\web", ("origin", "https://github.com/acme/web")),
            C(@"C:\src\web", ("origin", "git@github.com:acme/web.git")));
        Assert.Null(m.Path);
        Assert.True(m.NeedsChoice);
        Assert.Equal([@"C:\src\web", @"D:\work\web"], m.Candidates);
    }

    [Fact]
    public void NoMatchIsNone()
    {
        var m = Resolve(null, C(@"C:\src\api", ("origin", "https://github.com/acme/api")));
        Assert.Null(m.Path);
        Assert.Empty(m.Candidates);
        Assert.False(m.NeedsChoice);
    }

    [Fact]
    public void ARememberedFolderWinsWhileItExists()
    {
        var known = new[]
        {
            C(@"D:\work\web", ("origin", "https://github.com/acme/web")),
            C(@"C:\src\web", ("origin", "https://github.com/acme/web")),
        };
        Assert.Equal(@"D:\work\web", RepoCheckoutResolver.Resolve(Web, known, @"D:\work\web", _ => true).Path);
        // Gone from disk: back to matching (a choice here).
        var m = RepoCheckoutResolver.Resolve(Web, known, @"E:\gone", p => !p.StartsWith(@"E:\", StringComparison.Ordinal));
        Assert.True(m.NeedsChoice);
    }

    [Fact]
    public void MissingFoldersAndPerchWorktreesAreNeverCandidates()
    {
        var m = RepoCheckoutResolver.Resolve(Web,
            [
                C(@"C:\src\.perch-worktrees\web-pr-412", ("origin", "https://github.com/acme/web")),
                C(@"C:\old\web", ("origin", "https://github.com/acme/web")),
                C(@"C:\src\web", ("origin", "https://github.com/acme/web")),
            ],
            null, p => !p.StartsWith(@"C:\old", StringComparison.Ordinal));
        Assert.Equal(@"C:\src\web", m.Path);
        Assert.Equal([@"C:\src\web"], m.Candidates);
    }

    // ── Scanner: remotes straight from .git/config ──

    [Fact]
    public void ParsesRemotesFromAGitConfig()
    {
        const string config = """
            [core]
            	repositoryformatversion = 0
            	url = not-a-remote
            [remote "origin"]
            	url = git@github.com:me/web.git
            	fetch = +refs/heads/*:refs/remotes/origin/*
            ; a comment
            [Remote "upstream"]
            	URL = "https://github.com/acme/web"
            [remote "broken"
            	url = ignored
            [branch "main"]
            	remote = origin
            [remote "mirror"]
            	url = https://ghe.example/acme/web.git # inline comment
            """;
        Assert.Equal(
            [
                new GitRemoteEntry("origin", "git@github.com:me/web.git"),
                new GitRemoteEntry("upstream", "https://github.com/acme/web"),
                new GitRemoteEntry("mirror", "https://ghe.example/acme/web.git"),
            ],
            GitCheckoutScanner.ParseRemotes(config));
    }

    [Fact]
    public void ScanFindsRootsFromSubfoldersAndFollowsALinkedWorktree()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "perch-tests", Guid.NewGuid().ToString("N"));
        try
        {
            // An ordinary checkout, scanned from a subfolder.
            var main = Path.Combine(tmp, "web");
            Directory.CreateDirectory(Path.Combine(main, ".git", "worktrees", "pr-1"));
            Directory.CreateDirectory(Path.Combine(main, "src", "app"));
            File.WriteAllText(Path.Combine(main, ".git", "config"), "[remote \"origin\"]\n\turl = https://github.com/acme/web.git\n");

            // A linked worktree: ".git" is a file pointing at main's worktrees/pr-1, whose commondir points back.
            var wt = Path.Combine(tmp, "web-pr-1");
            Directory.CreateDirectory(wt);
            File.WriteAllText(Path.Combine(wt, ".git"), $"gitdir: {Path.Combine(main, ".git", "worktrees", "pr-1")}\n");
            File.WriteAllText(Path.Combine(main, ".git", "worktrees", "pr-1", "commondir"), "../..\n");

            // A folder with no checkout, and a checkout with no remotes.
            var loose = Path.Combine(tmp, "loose");
            Directory.CreateDirectory(loose);
            var local = Path.Combine(tmp, "local");
            Directory.CreateDirectory(Path.Combine(local, ".git"));
            File.WriteAllText(Path.Combine(local, ".git", "config"), "[core]\n\tbare = false\n");

            var found = GitCheckoutScanner.Scan([Path.Combine(main, "src", "app"), main, wt, loose, local, Path.Combine(tmp, "missing")]);

            Assert.Equal([main, wt], found.Select(c => c.Root));
            Assert.All(found, c => Assert.Equal("https://github.com/acme/web.git", Assert.Single(c.Remotes).Url));
        }
        finally
        {
            try { Directory.Delete(tmp, recursive: true); } catch { }
        }
    }

    // ── Worktree plan ──

    [Fact]
    public void TheWorktreeGoesBesideTheCheckoutOnAPerchBranch()
    {
        var root = Path.GetPathRoot(Path.GetTempPath())!;                  // C:\ or /
        var src = Path.Combine(root, "src");
        var plan = PrWorktree.PlanFor(Path.Combine(src, "web") + Path.DirectorySeparatorChar, Web, 412)!;
        Assert.Equal(Path.Combine(src, ".perch-worktrees", "web-pr-412"), plan.Path);
        Assert.Equal("perch/pr-412", plan.Branch);
        Assert.True(RepoCheckoutResolver.IsPerchWorktree(plan.Path));      // so it never resolves as the checkout
        Assert.Null(PrWorktree.PlanFor(root, Web, 412));
        Assert.Null(PrWorktree.PlanFor(Path.Combine(src, "web"), Web, 0));
        Assert.Null(PrWorktree.PlanFor("relative/web", Web, 412));
    }

    [Fact]
    public void TheFetchRemoteIsOriginWhenItNamesTheRepoElseTheFirstThatDoes()
    {
        GitRemoteEntry R(string n, string u) => new(n, u);
        Assert.Equal("origin", PrWorktree.RemoteFor([R("upstream", "https://github.com/acme/web"), R("origin", "git@github.com:acme/web")], Web));
        Assert.Equal("upstream", PrWorktree.RemoteFor([R("origin", "https://github.com/me/web"), R("upstream", "https://github.com/acme/web")], Web));
        Assert.Null(PrWorktree.RemoteFor([R("origin", "https://github.com/me/web")], Web));
    }

    [Fact]
    public void TheSameRootSeenTwiceCountsOnce()
    {
        var m = Resolve(null,
            C(@"C:\src\web", ("origin", "https://github.com/acme/web")),
            C(@"C:/src/web/", ("origin", "https://github.com/acme/web")));
        Assert.Equal(@"C:\src\web", m.Path);
    }
}
