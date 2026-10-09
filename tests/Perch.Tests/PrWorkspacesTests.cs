using Perch.Data;
using Xunit;

namespace Perch.Tests;

/// <summary>
/// Guards W5 of docs/github-dashboard-plan.md: the Perch-owned clone and scratch folders for PR sessions — their
/// naming, the clone URL being built only from validated parts, and the scratch folder's reuse. Nothing here runs git.
/// </summary>
public sealed class PrWorkspacesTests : IDisposable
{
    private readonly string _base = Path.Combine(Path.GetTempPath(), "perch-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_base, recursive: true); } catch { }
    }

    private static readonly GitRepoRef Web = new("github.com", "acme", "web.app");

    [Fact]
    public void FoldersAreNamedOwnerRepoPr() =>
        Assert.Equal(Path.Combine("B", "pr-clones", "acme-web.app-pr-12"), PrWorkspaceFolders.For("B", "pr-clones", Web, 12));

    [Theory]
    [InlineData("github.com", "acme", "web", "https://github.com/acme/web.git")]
    [InlineData("ghe.corp.example", "team", "my.repo_x", "https://ghe.corp.example/team/my.repo_x.git")]
    [InlineData("github.com/evil", "acme", "web", null)]            // a host that isn't one
    [InlineData("github.com", "-upload-pack=x", "web", null)]       // an option-shaped owner
    [InlineData("github.com", "acme", "web space", null)]
    [InlineData("", "acme", "web", null)]
    public void TheCloneUrlIsBuiltOnlyFromValidatedParts(string host, string owner, string repo, string? expected) =>
        Assert.Equal(expected, PrClone.CloneUrl(new GitRepoRef(host, owner, repo)));

    [Fact]
    public void ScratchIsCreatedOnceThenReused()
    {
        var first = PrScratch.Ensure(Web, 12, _base);
        Assert.Null(first.Error);
        Assert.False(first.Reused);
        Assert.True(Directory.Exists(first.Path));
        Assert.Equal(PrWorkspaceFolders.For(_base, "pr-scratch", Web, 12), first.Path);

        var again = PrScratch.Ensure(Web, 12, _base);
        Assert.True(again.Reused);
        Assert.Equal(first.Path, again.Path);
        Assert.NotNull(PrScratch.Ensure(Web, 0, _base).Error);
    }

    [Fact]
    public void AnExistingCloneIsReusedWithoutRunningGit()
    {
        var dir = PrWorkspaceFolders.For(_base, "pr-clones", Web, 12);
        Directory.CreateDirectory(Path.Combine(dir, ".git"));
        var r = PrClone.Ensure(Web, 12, _base);
        Assert.Equal((dir, true, (string?)null), (r.Path, r.Reused, r.Error));
    }

    [Fact]
    public void AFolderInTheWayIsAnErrorNotOverwritten()
    {
        var dir = PrWorkspaceFolders.For(_base, "pr-clones", Web, 13);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "keep.txt"), "mine");
        var r = PrClone.Ensure(Web, 13, _base);
        Assert.Null(r.Path);
        Assert.Contains("isn't a clone", r.Error);
        Assert.True(File.Exists(Path.Combine(dir, "keep.txt")));
    }
}
