using Perch.Data;
using Xunit;

namespace Perch.Tests;

/// <summary>
/// Guards the touch-nothing "Copy command" (part 5 of docs/github-dashboard-plan.md): the setup a PR session's folder
/// needs, as steps a terminal runs, matching what <see cref="PrWorktree.Ensure"/>, <see cref="PrClone.Ensure"/> and
/// <see cref="PrScratch.Ensure"/> do. Pure.
/// </summary>
public class PrSetupTests
{
    private static readonly string Src = OperatingSystem.IsWindows() ? @"C:\src" : "/src";
    private static string P(params string[] parts) => Path.Combine([Src, .. parts]);
    private static readonly GitRepoRef Repo = new("github.com", "acme", "api");

    private static PrWorktreeContext Ctx(string template, params GitWorktree[] linked) =>
        new(new GitWorktreeSet(P("api"), false, "main", linked), new WorktreeLayout(template, WorktreeLayoutSource.Detected, 1));

    [Fact]
    public void ANewWorktreeIsFetchedThenAdded()
    {
        var plan = PrSetup.Worktree(Ctx("{parent}/{repo}-{name}"), "origin", 77, P("api", ".git"), null)!;
        Assert.Equal(P("api-pr-77"), plan.Path);
        Assert.Collection(plan.Steps,
            s => Assert.Equal(new[] { "fetch", "--no-tags", "origin", "pull/77/head:perch/pr-77" }, ((PrGitStep)s).Args),
            s => Assert.Equal(new[] { "worktree", "add", P("api-pr-77"), "perch/pr-77" }, ((PrGitStep)s).Args));
        Assert.All(plan.Steps, s => Assert.Equal(P("api"), ((PrGitStep)s).Dir));
    }

    [Fact]
    public void ANestedLayoutIsExcludedOnceBeforeTheAdd()
    {
        var common = P("api", ".git");
        var plan = PrSetup.Worktree(Ctx(WorktreeLayout.DefaultTemplate), "upstream", 9, common, "# git ls-files --others\n*.log")!;
        var append = Assert.IsType<PrAppendLineStep>(plan.Steps[1]);
        Assert.Equal(Path.Combine(common, "info", "exclude"), append.File);
        Assert.Equal("/.claude/worktrees/", append.Line);
        Assert.True(append.NewlineFirst);   // the file doesn't end in a line break
        Assert.Equal("upstream", ((PrGitStep)plan.Steps[0]).Args[2]);

        var already = PrSetup.Worktree(Ctx(WorktreeLayout.DefaultTemplate), "origin", 9, common, "/.claude/worktrees/\n")!;
        Assert.DoesNotContain(already.Steps, s => s is PrAppendLineStep);
    }

    [Fact]
    public void AnEarlierPerchWorktreeIsReusedWithNoSteps()
    {
        var earlier = P("api-pr-77");
        var plan = PrSetup.Worktree(Ctx("{root}/.worktrees/{name}", new GitWorktree(earlier, "perch/pr-77")), "origin", 77, null, null)!;
        Assert.Equal(earlier, plan.Path);
        Assert.Empty(plan.Steps);
    }

    [Fact]
    public void AFreshCloneMatchesPrCloneEnsure()
    {
        var baseDir = P("data");
        var plan = PrSetup.Clone(Repo, 12, baseDir, cloned: false)!;
        var dir = PrWorkspaceFolders.For(baseDir, "pr-clones", Repo, 12);
        Assert.Equal(dir, plan.Path);
        Assert.IsType<PrMakeDirStep>(plan.Steps[0]);
        Assert.Equal(new[] { "clone", "--filter=blob:none", "--no-checkout", "--", "https://github.com/acme/api.git", dir },
            ((PrGitStep)plan.Steps[1]).Args);
        Assert.Equal(new[] { "fetch", "--no-tags", "origin", "pull/12/head:perch/pr-12" }, ((PrGitStep)plan.Steps[2]).Args);
        Assert.Equal(new[] { "checkout", "perch/pr-12" }, ((PrGitStep)plan.Steps[3]).Args);
        Assert.Empty(PrSetup.Clone(Repo, 12, baseDir, cloned: true)!.Steps);
    }

    [Fact]
    public void AScratchFolderIsMadeUnlessItExists()
    {
        var plan = PrSetup.Scratch(Repo, 5, P("data"), exists: false)!;
        Assert.Equal(PrWorkspaceFolders.For(P("data"), "pr-scratch", Repo, 5), Assert.IsType<PrMakeDirStep>(Assert.Single(plan.Steps)).Path);
        Assert.Empty(PrSetup.Scratch(Repo, 5, P("data"), exists: true)!.Steps);
    }
}
