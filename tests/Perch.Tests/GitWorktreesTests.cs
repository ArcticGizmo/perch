using Perch.Data;
using Xunit;

namespace Perch.Tests;

/// <summary>
/// Guards W2 of docs/github-dashboard-plan.md: reading a repository's worktrees straight from its git directory
/// (no git process), across the layouts people use — sibling folders, nested folders, the ".bare" layout — and not
/// mistaking a submodule for a worktree. Every repo here is plain files in a temp folder.
/// </summary>
public sealed class GitWorktreesTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "perch-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_tmp, recursive: true); } catch { }
    }

    private string Dir(params string[] parts)
    {
        var p = Path.Combine([_tmp, .. parts]);
        Directory.CreateDirectory(p);
        return p;
    }

    private static void Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    // A linked worktree the way `git worktree add` records it: <common>/worktrees/<name>/{gitdir,HEAD,commondir}
    // and the worktree's own ".git" file pointing back.
    private static void AddWorktree(string common, string name, string path, string? branch)
    {
        var meta = Path.Combine(common, "worktrees", name);
        Write(Path.Combine(meta, "gitdir"), Path.Combine(path, ".git") + "\n");
        Write(Path.Combine(meta, "HEAD"), branch is null ? "0123456789abcdef0123456789abcdef01234567\n" : $"ref: refs/heads/{branch}\n");
        Write(Path.Combine(meta, "commondir"), "../..\n");
        Write(Path.Combine(path, ".git"), $"gitdir: {meta}\n");
    }

    private string OrdinaryRepo(string name = "web")
    {
        var root = Dir("src", name);
        var git = Dir("src", name, ".git");
        Write(Path.Combine(git, "HEAD"), "ref: refs/heads/main\n");
        Write(Path.Combine(git, "config"), "[core]\n\tbare = false\n[remote \"origin\"]\n\turl = https://github.com/acme/web.git\n");
        return root;
    }

    [Fact]
    public void ListsSiblingAndNestedWorktreesFromAnyOfThem()
    {
        var root = OrdinaryRepo();
        var git = Path.Combine(root, ".git");
        var sibling = Dir("src", "web-feature-x");
        var nested = Dir("src", "web", ".claude", "worktrees", "pr-12");
        var detached = Dir("src", "web-detached");
        AddWorktree(git, "web-feature-x", sibling, "feature/x");
        AddWorktree(git, "pr-12", nested, "perch/pr-12");
        AddWorktree(git, "web-detached", detached, null);
        AddWorktree(git, "gone", Path.Combine(_tmp, "src", "web-gone"), "old");       // records a folder...
        Directory.Delete(Path.Combine(_tmp, "src", "web-gone"), recursive: true);    // ...that no longer exists

        foreach (var from in new[] { root, sibling, nested })
        {
            var set = GitWorktreeScanner.Read(from)!;
            Assert.Equal(root, set.Root);
            Assert.False(set.Bare);
            Assert.Equal("main", set.MainBranch);
            Assert.Equal(
                [new GitWorktree(nested, "perch/pr-12"), new GitWorktree(detached, null), new GitWorktree(sibling, "feature/x")],
                set.Linked);
        }

        var s = GitWorktreeScanner.Read(root)!;
        Assert.Equal(sibling, s.PathOfBranch("feature/x"));
        Assert.Equal(root, s.PathOfBranch("main"));
        Assert.Null(s.PathOfBranch("old"));       // pruned away
        Assert.Null(s.PathOfBranch(""));
    }

    [Fact]
    public void ReadsTheBareLayout()
    {
        // root/.git is a file pointing at ./.bare (core.bare = true); every branch is a worktree under root.
        var root = Dir("work", "api");
        var bare = Dir("work", "api", ".bare");
        Write(Path.Combine(bare, "config"), "[core]\n\tbare = true\n[remote \"origin\"]\n\turl = git@github.com:acme/api.git\n");
        Write(Path.Combine(bare, "HEAD"), "ref: refs/heads/main\n");
        Write(Path.Combine(root, ".git"), "gitdir: ./.bare\n");
        var main = Dir("work", "api", "main");
        var feature = Dir("work", "api", "feature-y");
        AddWorktree(bare, "main", main, "main");
        AddWorktree(bare, "feature-y", feature, "feature/y");

        var set = GitWorktreeScanner.Read(feature)!;
        Assert.Equal(root, set.Root);
        Assert.True(set.Bare);
        Assert.Null(set.MainBranch);
        Assert.Equal(main, set.PathOfBranch("main"));             // the "main" worktree, not the bare root

        // The scanner folds every worktree into the one repo, whose remotes live in .bare/config.
        var found = GitCheckoutScanner.Scan([main, feature, root]);
        var only = Assert.Single(found);
        Assert.Equal(root, only.Root);
        Assert.Equal("git@github.com:acme/api.git", Assert.Single(only.Remotes).Url);
    }

    [Fact]
    public void ASubmoduleIsItsOwnRootNotAWorktreeOfItsParent()
    {
        var super = OrdinaryRepo("super");
        var modules = Dir("src", "super", ".git", "modules", "lib");
        Write(Path.Combine(modules, "HEAD"), "ref: refs/heads/main\n");
        Write(Path.Combine(modules, "config"), "[core]\n\tbare = false\n\tworktree = ../../../lib\n");
        var sub = Dir("src", "super", "lib");
        Write(Path.Combine(sub, ".git"), "gitdir: ../.git/modules/lib\n");

        var set = GitWorktreeScanner.Read(sub)!;
        Assert.Equal(sub, set.Root);
        Assert.Empty(set.Linked);
        Assert.Equal(super, GitWorktreeScanner.Read(super)!.Root);
    }

    [Fact]
    public void NotARepoIsNull() => Assert.Null(GitWorktreeScanner.Read(Dir("loose")));

    [Theory]
    [InlineData("[core]\n\tbare = true\n", "core", "bare", "true")]
    [InlineData("[Core]\n\tBARE = false ; comment\n", "core", "bare", "false")]
    [InlineData("[core]\n\tbare = false\n[core]\n\tbare = true\n", "core", "bare", "true")]       // last wins
    [InlineData("[remote \"origin\"]\n\tbare = true\n", "core", "bare", null)]                     // other section
    [InlineData("[core]\n\tname = \"quoted value\"\n", "core", "name", "quoted value")]
    public void ConfigValueReadsAPlainSection(string config, string section, string key, string? expected) =>
        Assert.Equal(expected, GitCheckoutScanner.ConfigValue(config, section, key));
}
