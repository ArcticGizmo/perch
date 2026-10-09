using Perch.Data;
using Xunit;

namespace Perch.Tests;

/// <summary>
/// Guards W3 of docs/github-dashboard-plan.md: inferring where a user keeps a repo's worktrees from the ones they
/// already have (or an ignore-file hint), and rendering a PR worktree's path the same way. Pure.
/// </summary>
public class WorktreeLayoutTests
{
    private static readonly string Src = OperatingSystem.IsWindows() ? @"C:\src" : "/src";
    private static string P(params string[] parts) => Path.Combine([Src, .. parts]);
    private static readonly string Root = P("web");

    private static WorktreeLayout Infer(params (string Path, string? Branch)[] wts) =>
        WorktreeLayoutInference.Infer(Root, false, wts.Select(w => new GitWorktree(w.Path, w.Branch)));

    [Fact]
    public void SiblingFoldersNamedRepoDashBranch()
    {
        var l = Infer((P("web-feature-x"), "feature/x"), (P("web-fix-login"), "fix/login"), (P("web-spike"), "spike"));
        Assert.Equal("{parent}/{repo}-{name}", l.Template);
        Assert.Equal(WorktreeLayoutSource.Detected, l.Source);
        Assert.Equal(3, l.Evidence);
        Assert.Equal(P("web-pr-12"), l.Render(Root, "pr-12"));
        Assert.False(l.InsideRoot(bare: false));
        Assert.Null(l.ExcludeEntry(bare: false));
    }

    [Fact]
    public void SiblingFoldersNamedRepoDotBranch() =>
        Assert.Equal("{parent}/{repo}.{name}", Infer((P("web.feature-x"), "feature/x")).Template);

    [Fact]
    public void NestedFolderWithNestedBranchPaths()
    {
        var l = Infer((P("web", ".worktrees", "feature", "x"), "feature/x"), (P("web", ".worktrees", "main2"), "main2"));
        Assert.Equal("{root}/.worktrees/{name}", l.Template);
        Assert.Equal(P("web", ".worktrees", "pr-7"), l.Render(Root, "pr-7"));
        Assert.True(l.InsideRoot(bare: false));
        Assert.Equal("/.worktrees/", l.ExcludeEntry(bare: false));
    }

    [Fact]
    public void ClaudeCodeWorktreesVoteForTheirFolderEvenWhenNamesDontFollowBranches()
    {
        // Claude Code names the folder, not after the branch: only "where it sits" evidence.
        var l = Infer((P("web", ".claude", "worktrees", "brave-otter"), "worktree-brave-otter"),
                      (P("web", ".claude", "worktrees", "calm-heron"), null));
        Assert.Equal("{root}/.claude/worktrees/{name}", l.Template);
        Assert.Equal("/.claude/worktrees/", l.ExcludeEntry(bare: false));
    }

    [Fact]
    public void TheMostCommonLayoutWins()
    {
        var l = Infer((P("web-a"), "a"), (P("web-b"), "b"), (P("web", ".worktrees", "c"), "c"));
        Assert.Equal("{parent}/{repo}-{name}", l.Template);
        Assert.Equal(2, l.Evidence);
    }

    [Fact]
    public void LastSegmentAndUnderscoreRenderingsCount()
    {
        Assert.Equal("{parent}/{repo}-{name}", Infer((P("web-x"), "feature/x")).Template);
        Assert.Equal("{parent}/{repo}-{name}", Infer((P("web-feature_x"), "feature/x")).Template);
    }

    [Fact]
    public void ADifferentPrefixIsKeptLiterally() =>
        Assert.Equal("{parent}/wt/{name}", Infer((P("wt", "feature-x"), "feature/x")).Template);

    [Fact]
    public void WorktreesElsewhereAndPerchsOwnDontVote()
    {
        var elsewhere = OperatingSystem.IsWindows() ? @"D:\scratch\thing" : "/scratch/thing";
        var l = Infer((elsewhere, "thing"), (P(".perch-worktrees", "web-pr-3"), "perch/pr-3"));
        Assert.Equal(WorktreeLayout.DefaultTemplate, l.Template);
        Assert.Equal(WorktreeLayoutSource.Default, l.Source);
    }

    [Theory]
    [InlineData("node_modules/\n.worktrees/\n", "{root}/.worktrees/{name}")]
    [InlineData("# comment\n/.claude/worktrees\n", "{root}/.claude/worktrees/{name}")]
    [InlineData("worktrees/*\n", "{root}/worktrees/{name}")]
    [InlineData("bin/\nobj/\n", WorktreeLayout.DefaultTemplate)]
    public void WithNoWorktreesAnIgnoreEntryIsAHint(string gitignore, string template)
    {
        var l = WorktreeLayoutInference.Infer(Root, false, [], gitignore, null);
        Assert.Equal(template, l.Template);
    }

    [Fact]
    public void TheBareLayoutPutsWorktreesBesideBare()
    {
        var root = P("api");
        var detected = WorktreeLayoutInference.Infer(root, true,
            [new GitWorktree(P("api", "main"), "main"), new GitWorktree(P("api", "feature-y"), "feature/y")]);
        Assert.Equal("{root}/{name}", detected.Template);
        Assert.False(detected.InsideRoot(bare: true));     // the bare root isn't a working tree: nothing to ignore
        Assert.Null(detected.ExcludeEntry(bare: true));

        var none = WorktreeLayoutInference.Infer(root, true, []);
        Assert.Equal("{root}/{name}", none.Template);
        Assert.Equal(P("api", "pr-4"), none.Render(root, "pr-4"));
    }

    [Fact]
    public void ChoicesPutTheInferredLayoutFirstThenTheSavedOneThenThePresets()
    {
        var inferred = new WorktreeLayout("{parent}/{repo}-{name}", WorktreeLayoutSource.Detected, 3);
        var choices = WorktreeLayout.ChoicesFor(inferred, bare: false, saved: "{parent}/wt/{name}");
        Assert.Same(inferred, choices[0]);
        Assert.Equal("{parent}/wt/{name}", choices[1].Template);
        Assert.Equal(WorktreeLayoutSource.Chosen, choices[1].Source);
        // No duplicates: the inferred template is also a preset.
        Assert.Equal(choices.Count, choices.Select(c => c.Template).Distinct().Count());
        Assert.All(WorktreeLayout.Presets, p => Assert.Contains(choices, c => c.Template == p));
        Assert.DoesNotContain(choices, c => c.Template == "{root}/{name}");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("{parent}/no-name-placeholder")]
    public void ASavedLayoutWithoutANameIsIgnored(string? saved)
    {
        var inferred = new WorktreeLayout(WorktreeLayout.DefaultTemplate, WorktreeLayoutSource.Default);
        Assert.Equal(WorktreeLayout.Presets.Count, WorktreeLayout.ChoicesFor(inferred, false, saved).Count);
    }

    [Fact]
    public void ABareRepoIsOfferedItsOwnLayout()
    {
        var inferred = new WorktreeLayout(WorktreeLayout.DefaultTemplate, WorktreeLayoutSource.Default);
        Assert.Contains(WorktreeLayout.ChoicesFor(inferred, bare: true), c => c.Template == "{root}/{name}");
    }

    [Fact]
    public void RenderCarriesALayoutToAnotherRepo()
    {
        var l = new WorktreeLayout("{parent}/{repo}-{name}", WorktreeLayoutSource.Detected);
        Assert.Equal(P("api-pr-9"), l.Render(P("api") + Path.DirectorySeparatorChar, "pr-9"));
    }
}
