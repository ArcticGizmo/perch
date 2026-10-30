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

    [Fact]
    public void Loader_FindsWorktreesAndTethersADirtyOne()
    {
        if (!_ready) return;
        var wt = _repo + "-feature";
        Assert.Equal(0, Run("worktree", "add", wt, "feature").exit);
        File.WriteAllText(Path.Combine(wt, "dirty.txt"), "wip\n");

        var s = GraphLoader.Load(_repo, GraphScope.All)!;

        Assert.Equal(2, s.Worktrees.Count);
        Assert.True(s.Worktrees.Single(w => w.Branch == "main").IsCurrent);
        var feature = s.Worktrees.Single(w => w.Branch == "feature");
        Assert.Equal(1, feature.Changes);
        Assert.Equal(feature.WipHash, s.Commits[0].Hash);
        var t = Assert.Single(s.Tethers);
        Assert.Equal(s.RowOf(feature.Head!), t.HeadRow);
        Assert.Equal("f1", s.Commits[t.HeadRow].Subject);
        Assert.Equal(0, s.Rows[s.RowOf(s.Worktrees.Single(w => w.Branch == "main").Head!)].Lane);

        // Opened from the feature worktree, "This branch" walks feature and its base back to where they split. Main
        // has merged feature, so the merge-base is f1 itself and the root is out of scope.
        var branch = GraphLoader.Load(wt, GraphScope.Branch)!;
        Assert.True(branch.Worktrees.Single(w => w.Branch == "feature").IsCurrent);
        Assert.Equal(["f1", "m1", "merge feature"], branch.Commits.Where(c => !c.IsWorkInProgress).Select(c => c.Subject).Order());
    }

    [Fact]
    public void Ops_SwitchAndRestore()
    {
        if (!_ready) return;
        var before = GitOps.Head(_repo)!;
        Assert.Equal("main", before.Branch);

        var r = GitOps.Switch(_repo, "feature");
        Assert.True(r.Ok, r.Message);
        Assert.Equal("git switch feature", r.Command);
        Assert.Equal("feature", GitOps.Head(_repo)!.Branch);

        Assert.True(GitOps.Restore(_repo, before).Ok);
        Assert.Equal(before, GitOps.Head(_repo));

        Assert.True(GitOps.SwitchDetach(_repo, before.Hash).Ok);
        Assert.Null(GitOps.Head(_repo)!.Branch);
    }

    [Fact]
    public void Ops_RefuseABranchHeldByAnotherWorktree()
    {
        if (!_ready) return;
        Assert.Equal(0, Run("worktree", "add", _repo + "-feature", "feature").exit);

        var r = GitOps.Switch(_repo, "feature");
        Assert.False(r.Ok);
        Assert.Contains("worktree", r.Message);
        Assert.DoesNotContain("fatal:", r.Message);
    }

    [Fact]
    public void Ops_StashCleansTheTree()
    {
        if (!_ready) return;
        File.WriteAllText(Path.Combine(_repo, "a.txt"), "changed\n");
        File.WriteAllText(Path.Combine(_repo, "new.txt"), "untracked\n");

        Assert.True(GitOps.StashPush(_repo, "Perch: before switching").Ok);
        Assert.True(new Perch.Data.GitRepoService().GetStatus(_repo)!.Value.IsClean);
        Assert.Single(GraphLogReader.LoadStashes(_repo));
    }

    [Fact]
    public void Ops_MergeConflictThenAbort()
    {
        if (!_ready) return;
        Assert.Equal(0, Run("checkout", "-b", "clash", "HEAD~1").exit);
        File.WriteAllText(Path.Combine(_repo, "m.txt"), "theirs\n");
        Run("add", "m.txt");
        Run("commit", "-m", "clash");
        Run("checkout", "main");
        Assert.True(Commit("m.txt", "ours"));

        var r = GitOps.Merge(_repo, "clash");
        Assert.False(r.Ok);
        Assert.True(r.Conflicted);
        Assert.Equal(GitInProgress.Merge, GitOps.InProgress(_repo));

        Assert.True(GitOps.Abort(_repo, GitInProgress.Merge).Ok);
        Assert.Equal(GitInProgress.None, GitOps.InProgress(_repo));
    }

    [Fact]
    public void Ops_CherryPickAndFastForward()
    {
        if (!_ready) return;
        var f1 = GraphLogReader.LoadLog(_repo, 10).Single(c => c.Subject == "f1").Hash;
        Assert.Equal(0, Run("checkout", "-b", "pick", "HEAD~1").exit);   // m1, before the merge
        Assert.True(GitOps.CherryPick(_repo, f1, noCommit: false).Ok);
        Assert.Equal("f1", GraphLogReader.LoadLog(_repo, 1, query: new(["HEAD"], []))[0].Subject);

        Assert.True(GitOps.Switch(_repo, "feature").Ok);
        Assert.False(GitOps.FastForward(_repo, "pick").Ok);              // feature has f1 itself; not an ancestor
        Assert.True(GitOps.FastForward(_repo, "main").Ok);               // main merged feature, so it fast-forwards
    }

    [Fact]
    public void Ops_WorktreeAddOnANewBranch()
    {
        if (!_ready) return;
        var path = _repo + "-feature";
        var r = GitOps.WorktreeAdd(_repo, path, null, "spike", "main");
        Assert.True(r.Ok, r.Message);
        var s = GraphLoader.Load(_repo, GraphScope.All)!;
        Assert.Contains(s.Worktrees, w => w.Branch == "spike");
    }

    [Fact]
    public void Ops_RefuseOptionLookingInput()
    {
        if (!_ready) return;
        var r = GitOps.Switch(_repo, "--orphan");
        Assert.False(r.Ok);
        Assert.Equal("main", GitOps.Head(_repo)!.Branch);
    }

    [Fact]
    public void Checkout_StashesSwitchesAndUndoesBothSteps()
    {
        if (!_ready) return;
        File.WriteAllText(Path.Combine(_repo, "a.txt"), "changed\n");
        File.WriteAllText(Path.Combine(_repo, "new.txt"), "untracked\n");

        var (r, undo) = GitOps.Checkout(_repo, CheckoutAction.Switch, "feature", DirtyChoice.Stash);
        Assert.True(r.Ok, r.Message);
        Assert.Equal("feature", GitOps.Head(_repo)!.Branch);
        Assert.True(new Perch.Data.GitRepoService().GetStatus(_repo)!.Value.IsClean);
        Assert.NotNull(undo!.Stash);

        // Another stash on top must not be the one Undo pops.
        File.WriteAllText(Path.Combine(_repo, "f.txt"), "other\n");
        Assert.True(GitOps.StashPush(_repo, "unrelated").Ok);

        Assert.True(GitOps.Undo(_repo, undo).Ok);
        Assert.Equal("main", GitOps.Head(_repo)!.Branch);
        Assert.Equal("changed", File.ReadAllText(Path.Combine(_repo, "a.txt")).Trim());   // autocrlf may rewrite the ending
        Assert.True(File.Exists(Path.Combine(_repo, "new.txt")));
        Assert.Equal("unrelated", Run("stash", "list", "--format=%s").stdout.Trim().Split(": ").Last());
    }

    [Fact]
    public void Checkout_ThatFailsPutsTheStashBack()
    {
        if (!_ready) return;
        Assert.Equal(0, Run("worktree", "add", _repo + "-feature", "feature").exit);
        File.WriteAllText(Path.Combine(_repo, "a.txt"), "changed\n");

        var (r, undo) = GitOps.Checkout(_repo, CheckoutAction.Switch, "feature", DirtyChoice.Stash);
        Assert.False(r.Ok);
        Assert.Null(undo);
        Assert.Equal("changed", File.ReadAllText(Path.Combine(_repo, "a.txt")).Trim());   // autocrlf may rewrite the ending
        Assert.Equal("", Run("stash", "list").stdout.Trim());
    }

    [Fact]
    public void Checkout_CarriesChangesAcrossAndBack()
    {
        if (!_ready) return;
        File.WriteAllText(Path.Combine(_repo, "a.txt"), "carried\n");   // a.txt is the same on both branches

        var (r, undo) = GitOps.Checkout(_repo, CheckoutAction.Switch, "feature", DirtyChoice.Carry);
        Assert.True(r.Ok, r.Message);
        Assert.Equal("feature", GitOps.Head(_repo)!.Branch);
        Assert.Equal("carried\n", File.ReadAllText(Path.Combine(_repo, "a.txt")));

        Assert.True(GitOps.Undo(_repo, undo!).Ok);
        Assert.Equal("main", GitOps.Head(_repo)!.Branch);
        Assert.Equal("carried\n", File.ReadAllText(Path.Combine(_repo, "a.txt")));
    }

    [Fact]
    public void Checkout_DetachedOrOnANewBranch()
    {
        if (!_ready) return;
        var f1 = GraphLogReader.LoadLog(_repo, 10).Single(c => c.Subject == "f1").Hash;

        var (detached, undo) = GitOps.Checkout(_repo, CheckoutAction.Detach, f1, DirtyChoice.None);
        Assert.True(detached.Ok, detached.Message);
        Assert.Equal(new GitHeadState(null, f1), GitOps.Head(_repo));
        Assert.True(GitOps.Undo(_repo, undo!).Ok);

        var (named, _) = GitOps.Checkout(_repo, CheckoutAction.Detach, f1, DirtyChoice.None, newBranch: "spot");
        Assert.True(named.Ok, named.Message);
        Assert.Equal(new GitHeadState("spot", f1), GitOps.Head(_repo));
    }

    // A branch "side" off m1 with one commit of its own, for the drop tests.
    private bool AddSide() =>
        Run("checkout", "-b", "side", "HEAD~1").exit == 0 && Commit("s.txt", "s1") && Run("checkout", "main").exit == 0;

    private static DropOption DropOf(GraphSnapshot s, string from, string onto, DropKind kind) =>
        GraphDrop.Menu(s, DragSource.Of(s.Refs.Single(r => r.Name == from)), DropTarget.Of(s.Refs.Single(r => r.Name == onto)))
            .Options.Single(o => o.Kind == kind);

    private string Hash(string rev) => Run("rev-parse", rev).stdout.Trim();

    [Fact]
    public void Drop_MergeThenUndo()
    {
        if (!_ready || !AddSide()) return;
        var before = Hash("main");
        var s = GraphLoader.Load(_repo, GraphScope.All)!;

        var (r, undo) = GitOps.RunDrop(s.Root, DropOf(s, "side", "main", DropKind.Merge));
        Assert.True(r.Ok, r.Message);
        Assert.Equal(before, Hash("main^1"));
        Assert.Equal(Hash("side"), Hash("main^2"));

        Assert.True(undo!().Ok);
        Assert.Equal(before, Hash("main"));
    }

    [Fact]
    public void Drop_FastForwardABranchNoWorktreeHasThenUndo()
    {
        if (!_ready) return;
        Assert.Equal(0, Run("branch", "behind", "HEAD~1").exit);
        var old = Hash("behind");
        var s = GraphLoader.Load(_repo, GraphScope.All)!;
        var ff = DropOf(s, "main", "behind", DropKind.FastForward);
        Assert.Null(ff.RunIn);

        var (r, undo) = GitOps.RunDrop(s.Root, ff);
        Assert.True(r.Ok, r.Message);
        Assert.Equal(Hash("main"), Hash("behind"));

        Assert.True(undo!().Ok);
        Assert.Equal(old, Hash("behind"));
    }

    [Fact]
    public void Drop_RebaseABranchNoWorktreeHasSwitchesAndUndoSwitchesBack()
    {
        if (!_ready || !AddSide()) return;
        var old = Hash("side");
        var s = GraphLoader.Load(_repo, GraphScope.All)!;
        var rebase = DropOf(s, "side", "main", DropKind.Rebase);
        Assert.True(rebase.SwitchFirst);

        var (r, undo) = GitOps.RunDrop(s.Root, rebase);
        Assert.True(r.Ok, r.Message);
        Assert.Equal("side", GitOps.Head(_repo)!.Branch);
        Assert.Equal(Hash("main"), Hash("side^"));

        Assert.True(undo!().Ok);
        Assert.Equal(old, Hash("side"));
        Assert.Equal("main", GitOps.Head(_repo)!.Branch);
    }

    [Fact]
    public void Drop_NewWorktreeFromTheLayoutThenUndo()
    {
        if (!_ready) return;
        var s = GraphLoader.Load(_repo, GraphScope.All)!;
        var layout = new Perch.Data.WorktreeLayout("{parent}/{repo}-{name}", Perch.Data.WorktreeLayoutSource.Detected);   // the cleanup's -feature folder
        var add = Assert.Single(GraphDrop.Menu(s, DragSource.Of(s.Refs.Single(r => r.Name == "feature")), DropTarget.NewWorktreeZone, layout).Options);
        Assert.Equal(Path.GetFullPath(_repo + "-feature"), add.NewPath);

        var (r, undo) = GitOps.RunDrop(s.Root, add);
        Assert.True(r.Ok, r.Message);
        Assert.Contains(GraphLoader.Load(_repo, GraphScope.All)!.Worktrees, w => w.Branch == "feature");

        Assert.True(undo!().Ok);
        Assert.DoesNotContain(GraphLoader.Load(_repo, GraphScope.All)!.Worktrees, w => w.Branch == "feature");
    }

    [Fact]
    public void Drop_NewWorktreeOnANamedBranchFromACommitThenUndoDeletesTheBranch()
    {
        if (!_ready) return;
        var s = GraphLoader.Load(_repo, GraphScope.All)!;
        var m1 = s.Commits.Single(c => c.Subject == "m1");
        var add = Assert.Single(GraphDrop.Menu(s, DragSource.Of(m1), DropTarget.NewWorktreeZone).Options);
        Assert.True(add.NeedsName);

        var (r, undo) = GitOps.RunDrop(s.Root, add, newBranch: "spike", newPath: _repo + "-feature");
        Assert.True(r.Ok, r.Message);
        Assert.Equal(m1.Hash, Hash("spike"));

        Assert.True(undo!().Ok);
        Assert.NotEqual(0, Run("rev-parse", "--verify", "--quiet", "refs/heads/spike").exit);
    }

    [Fact]
    public void HiddenBranchLeavesTheWalkAndTheUsersOwnBranchCountsAsMine()
    {
        if (!_ready || !AddSide()) return;
        Assert.Equal(0, Run("branch", "bot", "main").exit);   // nothing over trunk, so not the user's

        var shown = GraphLoader.Load(_repo, GraphScope.All)!;
        Assert.Contains(shown.Commits, c => c.Subject == "s1");
        var hidden = GraphLoader.Load(_repo, GraphScope.All, hidden: new HashSet<string> { "refs/heads/side" })!;
        Assert.DoesNotContain(hidden.Commits, c => c.Subject == "s1");
        Assert.Contains(hidden.Refs, r => r.Name == "side");

        var candidates = shown.Refs.Where(r => r.Name is "side" or "bot").ToList();
        Assert.Equal(["refs/heads/side"], BranchVisibility.Mine(_repo, candidates, BranchVisibility.TrunkRefs(shown.Refs, "main")));
    }

    [Fact]
    public void DeleteBranch_RefusesUnmergedWorkUnlessForcedAndCanBeRecreated()
    {
        if (!_ready || !AddSide()) return;
        var side = Hash("side");

        var refused = GitOps.DeleteBranch(_repo, "side", force: false);
        Assert.True(GitOps.IsUnmerged(refused), refused.Message);
        Assert.True(GitOps.DeleteBranch(_repo, "side", force: true).Ok);
        Assert.NotEqual(0, Run("rev-parse", "--verify", "--quiet", "refs/heads/side").exit);

        Assert.True(GitOps.CreateBranch(_repo, "side", side, checkout: false).Ok);   // what Undo does
        Assert.Equal(side, Hash("side"));
        Assert.True(GitOps.DeleteBranch(_repo, "feature", force: false).Ok);       // merged: no question
    }

    [Fact]
    public void WorktreeRemove_RefusesChangesUnlessForced()
    {
        if (!_ready) return;
        var wt = _repo + "-feature";
        Assert.Equal(0, Run("worktree", "add", wt, "feature").exit);
        File.WriteAllText(Path.Combine(wt, "dirty.txt"), "wip\n");

        Assert.False(GitOps.WorktreeRemove(_repo, wt, force: false).Ok);
        Assert.True(Directory.Exists(wt));
        Assert.True(GitOps.WorktreeRemove(_repo, wt, force: true).Ok);
        Assert.False(Directory.Exists(wt));
        Assert.Equal(0, Run("rev-parse", "--verify", "--quiet", "refs/heads/feature").exit);   // the branch stays
    }

    [Fact]
    public void DiscardFileEntirely_PutsStagedUnstagedAddedAndRenamedFilesBack()
    {
        if (!_ready) return;
        var git = new Perch.Data.GitRepoService();
        string Text(string f) => File.ReadAllText(Path.Combine(_repo, f)).Trim();

        File.WriteAllText(Path.Combine(_repo, "a.txt"), "staged\n");
        Run("add", "a.txt");
        File.WriteAllText(Path.Combine(_repo, "a.txt"), "staged and more\n");
        Assert.True(git.DiscardFileEntirely(_repo, "a.txt", null).Ok);
        Assert.Equal("root", Text("a.txt"));

        File.WriteAllText(Path.Combine(_repo, "new.txt"), "new\n");
        Run("add", "new.txt");
        Assert.True(git.DiscardFileEntirely(_repo, "new.txt", null).Ok);
        Assert.False(File.Exists(Path.Combine(_repo, "new.txt")));

        Run("mv", "m.txt", "n.txt");
        Assert.True(git.DiscardFileEntirely(_repo, "n.txt", "m.txt").Ok);
        Assert.Equal("m1", Text("m.txt"));
        Assert.False(File.Exists(Path.Combine(_repo, "n.txt")));

        Assert.Equal("", Run("status", "--porcelain").stdout.Trim());
    }

    [Fact]
    public void DeleteFile_StagesATrackedDeletionAndRemovesUntrackedFolders()
    {
        if (!_ready) return;
        var git = new Perch.Data.GitRepoService();

        Assert.True(git.DeleteFile(_repo, "a.txt", untracked: false).Ok);
        Assert.False(File.Exists(Path.Combine(_repo, "a.txt")));
        Assert.Equal("D\ta.txt", Run("diff", "--cached", "--name-status").stdout.Trim());

        Directory.CreateDirectory(Path.Combine(_repo, "scratch"));
        File.WriteAllText(Path.Combine(_repo, "scratch", "x.txt"), "x\n");
        Assert.True(git.DeleteFile(_repo, "scratch/", untracked: true).Ok);
        Assert.False(Directory.Exists(Path.Combine(_repo, "scratch")));

        Assert.False(git.DeleteFile(_repo, "../outside.txt", untracked: true).Ok);
    }

    [Fact]
    public void StashThenPopTop_RoundTrips()
    {
        if (!_ready) return;
        File.WriteAllText(Path.Combine(_repo, "a.txt"), "wip\n");
        File.WriteAllText(Path.Combine(_repo, "u.txt"), "untracked\n");

        Assert.True(GitOps.StashPush(_repo, "WIP on main").Ok);
        Assert.Equal("", Run("status", "--porcelain").stdout.Trim());
        Assert.True(GitOps.StashPopTop(_repo).Ok);
        Assert.Equal("wip", File.ReadAllText(Path.Combine(_repo, "a.txt")).Trim());
        Assert.True(File.Exists(Path.Combine(_repo, "u.txt")));
        Assert.Equal("", Run("stash", "list").stdout.Trim());
    }

    [Fact]
    public void Stashes_LoadAsRowsAndApplyDropAndComeBack()
    {
        if (!_ready) return;
        File.WriteAllText(Path.Combine(_repo, "a.txt"), "first\n");
        Assert.True(GitOps.StashPush(_repo, "WIP on main").Ok);
        File.WriteAllText(Path.Combine(_repo, "a.txt"), "second\n");
        Assert.True(GitOps.StashPush(_repo, "second one").Ok);

        var snap = GraphLoader.Load(_repo, GraphScope.All)!;
        Assert.Equal(2, snap.Stashes.Count);
        Assert.Equal("On main: second one", snap.Stashes[0].Message);
        Assert.Equal(Hash("main"), snap.Stashes[0].Base);
        Assert.Equal(2, snap.StashTethers.Count);
        var older = snap.Stashes[1];

        var diff = new Perch.Data.GitRepoService().GetStashDiff(_repo, older.Hash);
        Assert.NotNull(diff);
        Assert.Contains(diff.Value.Files, f => f.NewPath == "a.txt");

        Assert.True(GitOps.StashApply(_repo, older.Hash).Ok);              // by commit, though it's stash@{1}
        Assert.Equal("first", File.ReadAllText(Path.Combine(_repo, "a.txt")).Trim());
        Assert.Equal(2, GraphLogReader.LoadStashes(_repo).Count);           // apply keeps it
        Run("checkout", "--", "a.txt");

        Assert.True(GitOps.StashDrop(_repo, older.Hash).Ok);
        Assert.Single(GraphLogReader.LoadStashes(_repo));
        Assert.True(GitOps.StashRestore(_repo, older.Hash, older.Message).Ok);   // what Undo does
        Assert.Contains(GraphLogReader.LoadStashes(_repo), s => s.Hash == older.Hash);
    }

    [Fact]
    public void PushPublishesANewBranch_AndPullFastForwardsWithUndo()
    {
        if (!_ready) return;
        string origin = _repo + "-origin.git", other = _repo + "-other";
        Assert.Equal(0, Run("clone", "--bare", _repo, origin).exit);
        Run("remote", "add", "origin", origin);
        Run("fetch", "origin");
        Assert.Equal(0, Run("branch", "-u", "origin/main", "main").exit);

        // Another clone moves main on the remote.
        Assert.Equal(0, Run("clone", origin, other).exit);
        Assert.Equal(0, RunIn(other, "-c", "user.email=o@example.com", "-c", "user.name=Other", "-c", "commit.gpgsign=false",
            "commit", "--allow-empty", "-m", "upstream work").exit);
        Assert.Equal(0, RunIn(other, "push", "origin", "main").exit);

        var before = Hash("main");
        var (pulled, undo) = GitOps.Pull(_repo, PullMode.FastForwardOnly);
        Assert.True(pulled.Ok, pulled.Message);
        Assert.Equal(RunIn(origin, "rev-parse", "main").stdout.Trim(), Hash("main"));
        Assert.NotNull(undo);
        Assert.True(undo!().Ok);
        Assert.Equal(before, Hash("main"));

        Assert.True(GitOps.Pull(_repo, PullMode.FetchAll).Result.Ok);

        Assert.Equal(0, Run("switch", "-c", "pub").exit);
        Assert.True(Commit("p.txt", "pub work"));
        var pushed = GitOps.Push(_repo);
        Assert.True(pushed.Ok, pushed.Message);
        Assert.Equal("git push -u origin pub", pushed.Command);
        Assert.Equal(Hash("pub"), RunIn(origin, "rev-parse", "pub").stdout.Trim());
        Assert.Equal("origin/pub", Run("rev-parse", "--abbrev-ref", "pub@{upstream}").stdout.Trim());

        Assert.True(Commit("p2.txt", "more"));
        var again = GitOps.Push(_repo);
        Assert.True(again.Ok, again.Message);
        Assert.Equal("git push", again.Command);
    }

    [Theory]
    [InlineData("hint: Waiting for your editor\nfatal: 'feature' is already used by worktree at 'C:/x'", "'feature' is already used by worktree at 'C:/x'")]
    [InlineData("error: Your local changes would be overwritten\n", "Your local changes would be overwritten")]
    [InlineData("", "fallback")]
    public void Ops_FirstLineDropsHintsAndPrefixes(string stderr, string expected) =>
        Assert.Equal(expected, GitOps.FirstLine(stderr, "fallback"));

    private bool Commit(string file, string message)
    {
        File.WriteAllText(Path.Combine(_repo, file), message + "\n");
        return Run("add", file).exit == 0 && Run("commit", "-m", message).exit == 0;
    }

    private (int exit, string stdout) Run(params string[] args) => RunIn(_repo, args);

    private static (int exit, string stdout) RunIn(string dir, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true,
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
        foreach (var dir in new[] { _repo, _repo + "-feature", _repo + "-origin.git", _repo + "-other" })
        {
            try
            {
                if (!Directory.Exists(dir)) continue;
                foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                    File.SetAttributes(f, FileAttributes.Normal);
                Directory.Delete(dir, recursive: true);
            }
            catch { /* best-effort temp cleanup */ }
        }
    }
}
