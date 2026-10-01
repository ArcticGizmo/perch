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
}
