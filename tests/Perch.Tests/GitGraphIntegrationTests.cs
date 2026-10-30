using System.Diagnostics;
using Perch.Data.GitGraph;
using Xunit;

namespace Perch.Tests;

/// <summary>
/// <see cref="GraphLogReader"/> against a real throwaway repo: the loader's git arguments, topological order and
/// tag peeling can only be checked end to end. Skipped when <c>git</c> isn't on PATH.
/// </summary>
public sealed class GitGraphIntegrationTests : IDisposable
{
    private readonly string _repo;
    private readonly bool _ready;

    public GitGraphIntegrationTests()
    {
        _repo = Path.Combine(Path.GetTempPath(), "perch-graph-it-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_repo);
        _ready = SetUpRepo();
    }

    // main: root → m1 → merge(feature); feature: root → f1. An annotated tag on m1.
    private bool SetUpRepo()
    {
        if (Run("init").exit != 0) return false;
        Run("config", "user.email", "test@example.com");
        Run("config", "user.name", "Perch Test");
        Run("config", "commit.gpgsign", "false");
        Run("config", "tag.gpgsign", "false");
        return Commit("a.txt", "root")
            && Run("branch", "-M", "main").exit == 0
            && Run("checkout", "-b", "feature").exit == 0
            && Commit("f.txt", "f1")
            && Run("checkout", "main").exit == 0
            && Commit("m.txt", "m1")
            && Run("tag", "-a", "v1", "-m", "release").exit == 0
            && Run("merge", "--no-ff", "-m", "merge feature", "feature").exit == 0;
    }

    [Fact]
    public void LoadsTopoOrderedLogAndRefs()
    {
        if (!_ready) return;
        var log = GraphLogReader.LoadLog(_repo, 100);
        var refs = GraphLogReader.LoadRefs(_repo);

        Assert.Equal(4, log.Count);
        Assert.Equal("merge feature", log[0].Subject);
        Assert.Equal("root", log[^1].Subject);
        // Topological: every parent comes after its child.
        var row = log.Select((c, i) => (c.Hash, i)).ToDictionary(t => t.Hash, t => t.i);
        Assert.All(log, c => Assert.All(c.Parents, p => Assert.True(row[p] > row[c.Hash])));
        Assert.Equal(2, log[0].Parents.Length);
        Assert.Empty(log[^1].Parents);

        var m1 = log.Single(c => c.Subject == "m1");
        Assert.Contains(refs, r => r is { Name: "v1", Kind: GraphRefKind.Tag } && r.Target == m1.Hash);
        Assert.Contains(refs, r => r.Name == "main" && r.Target == log[0].Hash);
        Assert.Contains(refs, r => r.Name == "feature" && r.Kind == GraphRefKind.LocalBranch);

        var lanes = new LaneAllocator(new LaneAllocator.Options(log[0].Hash));
        lanes.Add(log);
        Assert.Equal(2, lanes.Width);
        Assert.Equal(0, lanes.Rows[0].Lane);
        Assert.Equal(1, lanes.Rows[log.ToList().FindIndex(c => c.Subject == "f1")].Lane);
    }

    [Fact]
    public void Paging_SkipsAlreadyLoadedCommits()
    {
        if (!_ready) return;
        var all = GraphLogReader.LoadLog(_repo, 100);
        var page2 = GraphLogReader.LoadLog(_repo, 2, skip: 2);

        Assert.Equal(all.Skip(2).Take(2).Select(c => c.Hash), page2.Select(c => c.Hash));
    }

    private bool Commit(string file, string message)
    {
        File.WriteAllText(Path.Combine(_repo, file), message + "\n");
        return Run("add", file).exit == 0 && Run("commit", "-m", message).exit == 0;
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
            foreach (var f in Directory.EnumerateFiles(_repo, "*", SearchOption.AllDirectories))
                File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(_repo, recursive: true);
        }
        catch { /* best-effort temp cleanup */ }
    }
}
