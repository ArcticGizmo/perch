using System.Diagnostics;
using System.Linq;
using Perch.Data;
using Xunit;

namespace Perch.Tests;

/// <summary>
/// End-to-end coverage for the parts of <see cref="GitRepoService"/> that can only be exercised against a
/// real repository — the Phase 2 write path (stage / unstage / commit) and the branch-scoping queries
/// (<see cref="GitRepoService.GetBranchLog"/>, <see cref="GitRepoService.GetMergeBase"/>,
/// <see cref="GitRepoService.GetBaseRefCandidates"/>). Each test gets a throwaway repo (temp dir, isolated
/// user config, gpg-signing off) created in the constructor, and is skipped when <c>git</c> isn't on PATH.
/// The pure parsers stay covered by <see cref="GitRepoServiceTests"/>; this is the process-spawning side the
/// unit suite deliberately doesn't touch.
/// </summary>
public sealed class GitRepoServiceIntegrationTests : IDisposable
{
    private readonly string _repo;
    private readonly bool _ready;
    private readonly GitRepoService _git = new();

    public GitRepoServiceIntegrationTests()
    {
        _repo = Path.Combine(Path.GetTempPath(), "perch-git-it-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_repo);
        _ready = SetUpRepo();
    }

    // Builds a minimal repo: main with one root commit (a.txt), then a `feature` branch checked out. Returns
    // false if git isn't available or any step failed, so tests can skip rather than fail on a git-less host.
    private bool SetUpRepo()
    {
        if (Run("init").exit != 0) return false;
        Run("config", "user.email", "test@example.com");
        Run("config", "user.name", "Perch Test");
        Run("config", "commit.gpgsign", "false");

        File.WriteAllText(Path.Combine(_repo, "a.txt"), "root\n");
        if (Run("add", "a.txt").exit != 0) return false;
        if (Run("commit", "-m", "root commit").exit != 0) return false;
        Run("branch", "-M", "main");             // normalise the default branch name to main
        return Run("checkout", "-b", "feature").exit == 0;
    }

    [Fact]
    public void Stage_Unstage_Commit_RoundTrip()
    {
        if (!_ready) return;
        File.WriteAllText(Path.Combine(_repo, "b.txt"), "hello\n");

        // Untracked -> staged (Added) -> unstaged (untracked again) -> staged.
        Assert.True(_git.StageFile(_repo, "b.txt").Ok);
        var staged = FindChange("b.txt");
        Assert.Equal(GitChangeKind.Added, staged.Staged);

        Assert.True(_git.UnstageFile(_repo, "b.txt").Ok);
        Assert.True(FindChange("b.txt").Untracked);

        Assert.True(_git.StageFile(_repo, "b.txt").Ok);

        // Commit, then the tree is clean and the new commit tops the log.
        var (ok, err) = _git.Commit(_repo, "add b");
        Assert.True(ok, err);
        Assert.True(_git.GetStatus(_repo)!.Value.IsClean);
        Assert.Equal("add b", _git.GetLog(_repo, 5)[0].Subject);
    }

    [Fact]
    public void Commit_WithNothingStaged_FailsWithMessage()
    {
        if (!_ready) return;
        var (ok, err) = _git.Commit(_repo, "nothing here");
        Assert.False(ok);
        Assert.NotEqual("", err); // git explains "nothing to commit"
    }

    [Fact]
    public void BranchLog_ScopesToDivergenceFromBase()
    {
        if (!_ready) return;
        // Add a commit on feature.
        File.WriteAllText(Path.Combine(_repo, "b.txt"), "on feature\n");
        _git.StageFile(_repo, "b.txt");
        Assert.True(_git.Commit(_repo, "feature work").Ok);

        // Scoped to main..HEAD: only the feature commit, not the root that main shares.
        var scoped = _git.GetBranchLog(_repo, "main", 50);
        Assert.NotNull(scoped);
        Assert.Single(scoped!);
        Assert.Equal("feature work", scoped![0].Subject);

        // Unscoped log still has both commits.
        Assert.Equal(2, _git.GetLog(_repo, 50).Count);

        // The merge-base with main is the root commit.
        var mb = _git.GetMergeBase(_repo, "main");
        Assert.NotNull(mb);
        Assert.Equal(_git.GetLog(_repo, 50)[^1].Hash, mb);
    }

    [Fact]
    public void BranchLog_NullBaseFallsBackToFullHead()
    {
        if (!_ready) return;
        var all = _git.GetBranchLog(_repo, null, 50);
        Assert.NotNull(all);
        Assert.Single(all!); // just the root commit so far
    }

    [Fact]
    public void BaseRefCandidates_IncludesLocalMainFromFeature()
    {
        if (!_ready) return;
        var picks = _git.GetBaseRefCandidates(_repo, "feature");
        Assert.Contains("main", picks);           // local main is a candidate base
        Assert.DoesNotContain("feature", picks);  // never its own base
    }

    [Fact]
    public void StageHunk_ThenUnstageHunk_PartialStagesOneRegion()
    {
        if (!_ready) return;
        // A 20-line file, committed, then edited at the top and bottom so the working diff has two hunks
        // (the changed regions are far enough apart that git doesn't merge them).
        var lines = Enumerable.Range(1, 20).Select(i => $"line {i}").ToArray();
        File.WriteAllText(Path.Combine(_repo, "multi.txt"), string.Join("\n", lines) + "\n");
        _git.StageFile(_repo, "multi.txt");
        Assert.True(_git.Commit(_repo, "add multi").Ok);

        lines[0] = "LINE ONE";
        lines[19] = "LINE TWENTY";
        File.WriteAllText(Path.Combine(_repo, "multi.txt"), string.Join("\n", lines) + "\n");

        var diff = _git.GetWorkingDiff(_repo, "multi.txt", staged: false);
        Assert.NotNull(diff);
        var file = Assert.Single(diff!.Value.Files);
        Assert.Equal(2, file.Hunks.Count);            // two separate hunks
        string firstHeader = file.Hunks[0].Header;

        // Stage just the first hunk → the file is now partially staged (both slots Modified).
        Assert.True(_git.StageHunk(_repo, "multi.txt", firstHeader).Ok);
        var fc = FindChange("multi.txt");
        Assert.Equal(GitChangeKind.Modified, fc.Staged);
        Assert.Equal(GitChangeKind.Modified, fc.Unstaged);

        // The staged diff has exactly one hunk (the top edit); the bottom edit is still unstaged.
        var staged = _git.GetWorkingDiff(_repo, "multi.txt", staged: true);
        Assert.Single(staged!.Value.Files[0].Hunks);

        // Unstage that hunk → nothing staged, the change is back in the worktree only.
        Assert.True(_git.UnstageHunk(_repo, "multi.txt", firstHeader).Ok);
        var fc2 = FindChange("multi.txt");
        Assert.Equal(GitChangeKind.None, fc2.Staged);
        Assert.Equal(GitChangeKind.Modified, fc2.Unstaged);
    }

    // The one change record for a path in the current status.
    private GitFileChange FindChange(string path)
    {
        var status = _git.GetStatus(_repo);
        Assert.NotNull(status);
        return Assert.Single(status!.Value.Changes, c => c.Path == path);
    }

    private (int exit, string stdout) Run(params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = _repo, RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        try
        {
            using var p = Process.Start(psi);
            if (p is null) return (-1, "");
            string o = p.StandardOutput.ReadToEnd();
            _ = p.StandardError.ReadToEnd();
            p.WaitForExit(10_000);
            return (p.ExitCode, o);
        }
        catch { return (-1, ""); }
    }

    public void Dispose()
    {
        try
        {
            // .git holds read-only pack files on Windows; clear the bit before deleting.
            foreach (var f in Directory.EnumerateFiles(_repo, "*", SearchOption.AllDirectories))
                File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(_repo, recursive: true);
        }
        catch { /* best-effort temp cleanup */ }
    }
}
