using Perch.Data;
using Xunit;

namespace Perch.Tests;

/// <summary>
/// Review fixes CP24: the Change Review window's recursive watcher ignores dependency folders and git's object
/// store, but still refreshes on working-tree edits and on the .git files that change what it shows.
/// </summary>
public sealed class RepoWatchFilterTests
{
    [Theory]
    [InlineData("src/app.cs")]
    [InlineData(@"src\app.cs")]
    [InlineData("bin/deploy.sh")]            // a tracked bin/ of scripts is common, so build folders aren't ignored
    [InlineData("obj/project.assets.json")]
    [InlineData(".git")]
    [InlineData(".git/HEAD")]
    [InlineData(@".git\index")]
    [InlineData(".git/packed-refs")]
    [InlineData(".git/refs/heads/main")]
    [InlineData("sub/.git/HEAD")]
    [InlineData("")]
    [InlineData(null)]
    public void Relevant(string? path) => Assert.True(RepoWatchFilter.IsRelevant(path));

    [Theory]
    [InlineData("node_modules/react/index.js")]
    [InlineData(@"web\node_modules\.cache\x")]
    [InlineData("node_modules")]
    [InlineData(".git/objects/ab/cdef")]
    [InlineData(".git/logs/HEAD")]
    [InlineData(".git/index.lock")]
    [InlineData(".git/FETCH_HEAD")]
    public void Ignored(string path) => Assert.False(RepoWatchFilter.IsRelevant(path));

    // The git graph's watcher on the shared git directory.
    [Theory]
    [InlineData("HEAD")]
    [InlineData("index")]
    [InlineData("packed-refs")]
    [InlineData(@"refs\heads\feature\x")]
    [InlineData("worktrees")]
    [InlineData("worktrees/perch--feeds")]
    [InlineData("worktrees/perch--feeds/HEAD")]
    [InlineData(@"worktrees\perch--feeds\index")]
    [InlineData("worktrees/perch--feeds/rebase-merge")]
    [InlineData("MERGE_HEAD")]
    [InlineData("CHERRY_PICK_HEAD")]
    [InlineData("rebase-merge")]
    [InlineData(null)]
    public void RelevantInGitDir(string? path) => Assert.True(RepoWatchFilter.IsRelevantInGitDir(path));

    [Theory]
    [InlineData("objects/ab/cdef")]
    [InlineData("logs/HEAD")]
    [InlineData("index.lock")]
    [InlineData("FETCH_HEAD")]
    [InlineData("worktrees/perch--feeds/logs/HEAD")]
    [InlineData("worktrees/perch--feeds/index.lock")]
    [InlineData("rebase-merge/msgnum")]
    [InlineData("hooks/pre-commit")]
    public void IgnoredInGitDir(string path) => Assert.False(RepoWatchFilter.IsRelevantInGitDir(path));
}
