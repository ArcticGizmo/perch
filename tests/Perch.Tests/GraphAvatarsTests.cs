using Perch.Data;
using Perch.Data.GitGraph;
using Xunit;

namespace Perch.Tests;

/// <summary>
/// Author pictures for the graph: the pure helpers (initials, colour, noreply URLs, the GraphQL query and its answer)
/// and <see cref="AvatarFetcher"/> with gh and the download faked, against a throwaway cache folder and a checkout
/// that's only a <c>.git/config</c>.
/// </summary>
public class GraphAvatarsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "perch-avatars-" + Guid.NewGuid().ToString("N")[..8]);
    private string Cache => Path.Combine(_dir, "cache");
    private string Repo => Path.Combine(_dir, "repo");

    public GraphAvatarsTests()
    {
        Directory.CreateDirectory(Path.Combine(Repo, ".git"));
        File.WriteAllText(Path.Combine(Repo, ".git", "config"), "[remote \"origin\"]\n\turl = git@github.com:acme/web.git\n");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static GraphCommit C(string hash, string name, string email) => new(hash, [], name, email, 0, "s");

    [Fact]
    public void Initials_FirstAndLastWord_ElseTheEmail()
    {
        Assert.Equal("JH", GraphAvatars.Initials("Jonathan Howell", "j@x"));
        Assert.Equal("AB", GraphAvatars.Initials("ann marie bell", "a@x"));
        Assert.Equal("D", GraphAvatars.Initials("dependabot[bot]", "d@x"));
        Assert.Equal("Z", GraphAvatars.Initials("", "zed@x"));
        Assert.Equal("?", GraphAvatars.Initials("", ""));
    }

    [Fact]
    public void ColorIndex_IsStableAndIgnoresCase()
    {
        Assert.Equal(GraphAvatars.ColorIndex("Jon@X.com", 8), GraphAvatars.ColorIndex("jon@x.com", 8));
        Assert.InRange(GraphAvatars.ColorIndex("someone@else", 8), 0, 7);
        Assert.Equal(0, GraphAvatars.ColorIndex("a@b", 0));
    }

    [Fact]
    public void NoReplyUrl_ReadsTheAccountId()
    {
        Assert.Equal("https://avatars.githubusercontent.com/u/49699333?s=64&v=4",
            GraphAvatars.NoReplyUrl("49699333+dependabot[bot]@users.noreply.github.com"));
        Assert.Equal("https://avatars.githubusercontent.com/u/12?s=64&v=4", GraphAvatars.NoReplyUrl("12+jo@Users.NoReply.GitHub.com"));
        Assert.Null(GraphAvatars.NoReplyUrl("jo@users.noreply.github.com"));   // the old form has no id
        Assert.Null(GraphAvatars.NoReplyUrl("jo@example.com"));
    }

    [Fact]
    public void IsAvatarUrl_OnlyHttpsOnGitHubsAvatarHost()
    {
        Assert.True(GraphAvatars.IsAvatarUrl("https://avatars.githubusercontent.com/u/1?v=4"));
        Assert.False(GraphAvatars.IsAvatarUrl("http://avatars.githubusercontent.com/u/1"));
        Assert.False(GraphAvatars.IsAvatarUrl("https://evil.example/avatars.githubusercontent.com"));
        Assert.False(GraphAvatars.IsAvatarUrl("https://avatars.githubusercontent.com.evil.example/u/1"));
        Assert.False(GraphAvatars.IsAvatarUrl(null));
    }

    [Fact]
    public void GitHubRepo_OriginFirst_AndOnlyGitHub()
    {
        Assert.Equal(new GitRepoRef("github.com", "acme", "web"), GraphAvatars.GitHubRepo(
            [new("upstream", "https://github.com/other/web.git"), new("origin", "git@github.com:acme/web.git")]));
        Assert.Equal("acme", GraphAvatars.GitHubRepo([new("origin", "git@github-work:acme/web")])?.Owner);   // SSH alias
        Assert.Null(GraphAvatars.GitHubRepo([new("origin", "https://gitlab.com/acme/web.git")]));
        Assert.Null(GraphAvatars.GitHubRepo([]));
    }

    [Fact]
    public void PickCommits_OnePerAuthor_TheirOldest_SkippingKnownAndPseudo()
    {
        var wip = new GraphCommit("wip:C:/r", ["a1"], "", "", 0, "1 uncommitted");
        var picks = GraphAvatars.PickCommits(
            [wip, C("a1", "Ann", "ann@x"), C("b1", "Bob", "bob@x"), C("a2", "Ann", "ANN@x"), C("k1", "Kim", "kim@x")],
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "kim@x" });
        Assert.Equal([("ann@x", "a2"), ("bob@x", "b1")], picks);

        Assert.Single(GraphAvatars.PickCommits([C("a1", "Ann", "ann@x"), C("b1", "Bob", "bob@x"), C("a2", "Ann", "ann@x")],
            new HashSet<string>(), max: 1));
    }

    [Fact]
    public void BuildQuery_AliasesEachCommit_AndSkipsAnythingNotAHash()
    {
        var body = GraphAvatars.BuildQuery(new GitRepoRef("github.com", "acme", "web"),
            [("a@x", "abc1234def"), ("b@x", "\") { evil }"), ("c@x", "0123456789")]);
        var query = System.Text.Json.JsonDocument.Parse(body).RootElement.GetProperty("query").GetString()!;
        Assert.Contains("a0: object(oid: \"abc1234def\")", query);
        Assert.DoesNotContain("a1:", query);
        Assert.DoesNotContain("evil", query);
        Assert.Contains("a2: object(oid: \"0123456789\")", query);
        Assert.Contains("repository(owner: \"acme\", name: \"web\")", query);
    }

    [Fact]
    public void ParseResponse_RecordsNoAccountButNotAnUnknownCommit()
    {
        var picks = new List<(string, string)> { ("a@x", "1111111"), ("b@x", "2222222"), ("c@x", "3333333"), ("d@x", "4444444") };
        const string json = """
            {"data":{"repository":{
              "a0":{"author":{"user":{"avatarUrl":"https://avatars.githubusercontent.com/u/7?s=64&v=4"}}},
              "a1":{"author":{"user":null}},
              "a2":null,
              "a3":{"author":{"user":{"avatarUrl":"https://elsewhere.example/x.png"}}}}}}
            """;
        var r = GraphAvatars.ParseResponse(json, picks);
        Assert.Equal("https://avatars.githubusercontent.com/u/7?s=64&v=4", r["a@x"]);
        Assert.Null(r["b@x"]);                 // GitHub has the commit, no account for its author
        Assert.False(r.ContainsKey("c@x"));    // not pushed: unknown, not "no picture"
        Assert.Null(r["d@x"]);                 // only GitHub's avatar host is trusted

        Assert.Empty(GraphAvatars.ParseResponse("not json", picks));
        Assert.Empty(GraphAvatars.ParseResponse("""{"errors":[{"message":"no"}]}""", picks));
    }

    [Fact]
    public void Fetcher_NoReplyNeedsNoLookup_OthersAreLookedUpOnceAndCached()
    {
        int lookups = 0, downloads = 0;
        string? lastQuery = null;
        byte[] png = [1, 2, 3];
        var now = DateTimeOffset.UtcNow;
        AvatarFetcher Make() => new(Cache,
            graphql: q => { lookups++; lastQuery = q; return """{"data":{"repository":{"a0":{"author":{"user":{"avatarUrl":"https://avatars.githubusercontent.com/u/5?v=4"}}},"a1":{"author":{"user":null}}}}}"""; },
            download: _ => { downloads++; return png; },
            now: () => now);

        List<(string, string)> authors = [("ann@x", "aaaaaaa"), ("bob@x", "bbbbbbb"), ("1+bot[bot]@users.noreply.github.com", "ccccccc")];
        var first = Make().Fetch(Repo, authors);
        Assert.Equal(1, lookups);
        Assert.DoesNotContain("ccccccc", lastQuery);                 // the noreply author wasn't asked about
        Assert.Equal(["1+bot[bot]@users.noreply.github.com", "ann@x"], first.Keys.Order());
        Assert.Equal(2, downloads);

        // A second run (a new window) asks nothing and downloads nothing: both answers and both images are cached.
        var second = Make().Fetch(Repo, authors);
        Assert.Equal(1, lookups);
        Assert.Equal(2, downloads);
        Assert.Equal(first.Keys.Order(), second.Keys.Order());
        Assert.DoesNotContain("ann@x", File.ReadAllText(Path.Combine(Cache, "index.json")));   // hashed, never the email

        // A week on, the answer is asked again and both pictures fetched again.
        now = now.AddDays(8);
        Make().Fetch(Repo, authors);
        Assert.Equal(2, lookups);
        Assert.Equal(4, downloads);
    }

    [Fact]
    public void Fetcher_AFailedLookupRecordsNothing_AndOversizedImagesAreRefused()
    {
        int lookups = 0;
        var failing = new AvatarFetcher(Cache, graphql: _ => { lookups++; return null; }, download: _ => null);
        Assert.Empty(failing.Fetch(Repo, [("ann@x", "aaaaaaa")]));
        Assert.Empty(failing.LoadIndex());
        failing.Fetch(Repo, [("ann@x", "aaaaaaa")]);
        Assert.Equal(2, lookups);                                     // asked again: the failure wasn't cached

        var huge = new AvatarFetcher(Cache, graphql: _ => null, download: _ => new byte[AvatarFetcher.MaxBytes + 1]);
        Assert.Empty(huge.Fetch(Repo, [("1+x@users.noreply.github.com", "aaaaaaa")]));
    }

    [Fact]
    public void Fetcher_NonGitHubRemoteAsksNothing()
    {
        File.WriteAllText(Path.Combine(Repo, ".git", "config"), "[remote \"origin\"]\n\turl = https://gitlab.com/acme/web.git\n");
        int lookups = 0;
        new AvatarFetcher(Cache, graphql: _ => { lookups++; return null; }, download: _ => null).Fetch(Repo, [("ann@x", "aaaaaaa")]);
        Assert.Equal(0, lookups);
    }
}
