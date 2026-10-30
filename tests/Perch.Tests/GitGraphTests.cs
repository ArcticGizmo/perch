using Perch.Data.GitGraph;
using Xunit;

namespace Perch.Tests;

/// <summary>
/// The git graph's read model: the log/ref parsers on canned git output, and the lane allocator on hand-built
/// histories. Commits are listed in topological order (children first), as <c>git log --topo-order</c> emits them.
/// </summary>
public class GitGraphTests
{
    private const char Us = '\u001f';

    private static GraphCommit C(string hash, params string[] parents) => new(hash, parents, "Jon", "jon@x", 0, hash);

    private static LaneAllocator Layout(IReadOnlyList<GraphCommit> commits, string? primary = null, IReadOnlyDictionary<string, int>? hints = null)
    {
        var a = new LaneAllocator(new LaneAllocator.Options(primary, hints));
        a.Add(commits);
        return a;
    }

    private static GraphEdge E(int from, int to, int color) => new((short)from, (short)to, (short)color);

    // ---- parsers --------------------------------------------------------------------------------------

    [Fact]
    public void ParseLog_ReadsFieldsAndParents()
    {
        var output =
            $"aaaaaaa1111{Us}bbbbbbb2222 ccccccc3333{Us}Jon Howell{Us}jon@x{Us}1761800000{Us}Merge branch 'feeds'\0" +
            $"bbbbbbb2222{Us}{Us}Jon Howell{Us}jon@x{Us}1761700000{Us}Root\0";
        var log = GraphLogReader.ParseLog(output);

        Assert.Equal(2, log.Count);
        Assert.Equal("aaaaaaa", log[0].ShortHash);
        Assert.Equal(["bbbbbbb2222", "ccccccc3333"], log[0].Parents);
        Assert.True(log[0].IsMerge);
        Assert.Equal("Merge branch 'feeds'", log[0].Subject);
        Assert.Equal(1761800000, log[0].UnixTime);
        Assert.Empty(log[1].Parents);
        // The parent's own hash reuses the child's copy of the string.
        Assert.Same(log[0].Parents[0], log[1].Hash);
    }

    [Fact]
    public void ParseLog_SkipsMalformedRecordsAndKeepsSeparatorsOutOfSubjects()
    {
        var output = $"garbage\0aaaaaaa1111{Us}{Us}A{Us}a@x{Us}notatime{Us}x\0bbbbbbb2222{Us}{Us}A{Us}a@x{Us}5{Us}Fix: a—b\n\0";
        var log = GraphLogReader.ParseLog(output);

        var only = Assert.Single(log);
        Assert.Equal("Fix: a—b", only.Subject);
    }

    [Fact]
    public void ParseRefs_ClassifiesAndPeelsAnnotatedTags()
    {
        var output = string.Join('\n',
            $"1111111{Us}{Us}refs/heads/main",
            $"2222222{Us}{Us}refs/heads/feature/x",
            $"1111111{Us}{Us}refs/remotes/origin/main",
            $"1111111{Us}{Us}refs/remotes/origin/HEAD",
            $"9999999{Us}3333333{Us}refs/tags/v0.6.1",
            $"4444444{Us}{Us}refs/tags/light",
            $"5555555{Us}{Us}refs/stash",
            $"6666666{Us}{Us}refs/notes/commits",
            "");
        var refs = GraphLogReader.ParseRefs(output);

        Assert.Equal(6, refs.Count);
        Assert.Contains(refs, r => r is { Name: "feature/x", Kind: GraphRefKind.LocalBranch });
        Assert.Contains(refs, r => r is { Name: "origin/main", Kind: GraphRefKind.RemoteBranch });
        Assert.DoesNotContain(refs, r => r.Name == "origin/HEAD");
        Assert.Contains(refs, r => r is { Name: "v0.6.1", Kind: GraphRefKind.Tag, Target: "3333333" });
        Assert.Contains(refs, r => r is { Name: "light", Target: "4444444" });
        Assert.Contains(refs, r => r is { Name: "stash", Kind: GraphRefKind.Stash });
    }

    // ---- lane allocator -------------------------------------------------------------------------------

    [Fact]
    public void Linear_StaysInLaneZero()
    {
        var a = Layout([C("c", "b"), C("b", "a"), C("a")]);

        Assert.All(a.Rows, r => Assert.Equal(0, r.Lane));
        Assert.Empty(a.Rows[0].Up);
        Assert.Equal([E(0, 0, 0)], a.Rows[1].Up);
        Assert.Equal([E(0, 0, 0)], a.Rows[2].Up);
        Assert.Equal(1, a.Width);
        Assert.Empty(a.OpenEdges);
    }

    [Fact]
    public void Branch_ConvergesIntoParentInTheBandAboveIt()
    {
        // main: m2 → m1; feature: f2 → f1 → m1. The feature tip is newest.
        var a = Layout([C("f2", "f1"), C("m2", "m1"), C("f1", "m1"), C("m1")], primary: "m2");

        Assert.Equal([1, 0, 1, 0], a.Rows.Select(r => r.Lane));
        AssertEdges(a.Rows[1], E(1, 1, 1));
        AssertEdges(a.Rows[2], E(1, 1, 1), E(0, 0, 0));
        // The feature lane curves into main at m1, keeping its own colour.
        AssertEdges(a.Rows[3], E(1, 0, 1), E(0, 0, 0));
    }

    private static void AssertEdges(GraphRow row, params GraphEdge[] expected)
    {
        static IEnumerable<GraphEdge> Sorted(IEnumerable<GraphEdge> e) => e.OrderBy(x => x.From).ThenBy(x => x.To).ThenBy(x => x.Color);
        Assert.Equal(Sorted(expected), Sorted(row.Up));
    }

    [Fact]
    public void Merge_OpensSecondParentLaneJustBelowTheMerge()
    {
        var a = Layout([C("m3", "m2", "f1"), C("f1", "m1"), C("m2", "m1"), C("m1")], primary: "m3");

        Assert.Equal([0, 1, 0, 0], a.Rows.Select(r => r.Lane));
        AssertEdges(a.Rows[1], E(0, 0, 0), E(0, 1, 1));
        AssertEdges(a.Rows[2], E(0, 0, 0), E(1, 1, 1));
        AssertEdges(a.Rows[3], E(0, 0, 0), E(1, 0, 1));
        Assert.Equal(2, a.Width);
    }

    [Fact]
    public void Merge_JoinsALaneAlreadyWaitingForTheSecondParent()
    {
        // f2 merges main (m2) into the feature while main's own lane is already waiting for m2.
        var a = Layout([C("m3", "m2"), C("f2", "f1", "m2"), C("m2", "m1"), C("f1", "m1"), C("m1")], primary: "m3");

        Assert.Equal([0, 1, 0, 1, 0], a.Rows.Select(r => r.Lane));
        Assert.Contains(E(1, 0, 0), a.Rows[2].Up);   // the merge's second-parent line joins lane 0
        Assert.Contains(E(0, 0, 0), a.Rows[2].Up);
        Assert.Equal(2, a.Width);
    }

    [Fact]
    public void Merge_FromThePrimaryBranchHeadsForLaneZero()
    {
        // g forks from main's tip m2 (and waits for it in lane 1); then f merges m2 in. The merge line goes
        // straight to lane 0, where m2 will land, not across into g's lane.
        var a = Layout([C("g1", "m2"), C("f2", "f1", "m2"), C("m2", "m1"), C("f1", "m1"), C("m1")], primary: "m2");

        Assert.Equal([1, 2, 0, 2, 0], a.Rows.Select(r => r.Lane));
        AssertEdges(a.Rows[2], E(1, 0, 1), E(2, 0, 0), E(2, 2, 2));
    }

    [Fact]
    public void Octopus_OpensALanePerExtraParent()
    {
        var a = Layout([C("o", "a", "b", "c"), C("c", "r"), C("b", "r"), C("a", "r"), C("r")], primary: "o");

        Assert.Equal(0, a.Rows[0].Lane);
        AssertEdges(a.Rows[1], E(0, 0, 0), E(0, 1, 1), E(0, 2, 2));
        Assert.Equal(3, a.Width);
        // All three converge on the root.
        Assert.Equal(3, a.Rows[^1].Up.Count(e => e.To == 0));
    }

    [Fact]
    public void FreedLanesAreReused()
    {
        // f forks from m2 and merges back at m4; the older dangling branch g then reuses lane 1.
        var a = Layout([C("m4", "m3", "f1"), C("f1", "m2"), C("m3", "m2"), C("m2", "m1"), C("g1", "m1"), C("m1")], primary: "m4");

        Assert.Equal([0, 1, 0, 0, 1, 0], a.Rows.Select(r => r.Lane));
        Assert.Equal(2, a.Width);
    }

    [Fact]
    public void TwoRoots_EachEndsItsLane()
    {
        var a = Layout([C("m", "a", "b"), C("b"), C("a")], primary: "m");

        Assert.Equal([0, 1, 0], a.Rows.Select(r => r.Lane));
        Assert.Equal([E(0, 0, 0)], a.Rows[2].Up);    // b's lane ended at its root
        Assert.Empty(a.OpenEdges);
    }

    [Fact]
    public void TruncatedHistory_LeavesOpenEdgesDown()
    {
        var a = Layout([C("c", "b"), C("x", "y")]);

        Assert.Equal(2, a.OpenEdges.Length);
        Assert.All(a.OpenEdges, e => Assert.Equal(e.From, e.To));
    }

    [Fact]
    public void Paging_GivesTheSameRowsAsOneBatch()
    {
        var commits = Synthetic(3000, seed: 7);
        var whole = Layout(commits, primary: commits[0].Hash);

        var paged = new LaneAllocator(new LaneAllocator.Options(commits[0].Hash));
        for (int i = 0; i < commits.Count; i += 137)
            paged.Add(commits.Skip(i).Take(137).ToList());

        Assert.Equal(whole.Width, paged.Width);
        for (int i = 0; i < commits.Count; i++)
        {
            Assert.Equal(whole.Rows[i].Lane, paged.Rows[i].Lane);
            Assert.Equal(whole.Rows[i].Up, paged.Rows[i].Up);
        }
    }

    [Fact]
    public void Hints_NewCommitOnASideBranchKeepsEveryLane()
    {
        List<GraphCommit> before = [C("f2", "f1"), C("g1", "m1"), C("m2", "m1"), C("f1", "m1"), C("m1")];
        var first = Layout(before, primary: "m2");
        var hints = LaneAllocator.HintsFrom(before, first.Rows);

        // Two new commits on g, newer than everything else.
        List<GraphCommit> after = [C("g3", "g2"), C("g2", "g1"), .. before];
        var second = Layout(after, primary: "m2", hints: hints);

        for (int i = 0; i < before.Count; i++)
            Assert.Equal(first.Rows[i].Lane, second.Rows[i + 2].Lane);
        Assert.Equal(hints["g1"], second.Rows[0].Lane);
    }

    [Fact]
    public void Hints_NewBranchTipOpensAfterExistingLanes()
    {
        List<GraphCommit> before = [C("f1", "m1"), C("m2", "m1"), C("m1")];
        var first = Layout(before, primary: "m2");
        var hints = LaneAllocator.HintsFrom(before, first.Rows);

        // Without hints a new tip on top would take lane 1 and push f sideways.
        List<GraphCommit> after = [C("n1", "m2"), .. before];
        var second = Layout(after, primary: "m2", hints: hints);

        Assert.Equal(first.Rows[0].Lane, second.Rows[1].Lane);
        Assert.True(second.Rows[0].Lane >= first.Width);
    }

    [Fact]
    public void LargeSyntheticHistory_IsConsistent()
    {
        var commits = Synthetic(100_000, seed: 42);
        var a = Layout(commits, primary: commits[0].Hash);
        var index = commits.Select((c, i) => (c.Hash, i)).ToDictionary(t => t.Hash, t => t.i);
        var hasChild = new HashSet<string>(commits.SelectMany(c => c.Parents));

        Assert.Equal(commits.Count, a.Rows.Count);
        for (int i = 0; i < a.Rows.Count; i++)
        {
            var row = a.Rows[i];
            Assert.InRange(row.Lane, 0, a.Width - 1);
            Assert.All(row.Up, e => { Assert.InRange(e.From, 0, a.Width - 1); Assert.InRange(e.To, 0, a.Width - 1); });
            // Every commit with a loaded child has a line arriving at its node.
            if (hasChild.Contains(commits[i].Hash))
                Assert.Contains(row.Up, e => e.To == row.Lane);
        }
        // Lane 0 holds the primary first-parent chain all the way down.
        for (var c = commits[0]; c.Parents.Length > 0; c = commits[index[c.Parents[0]]])
            Assert.Equal(0, a.Rows[index[c.Hash]].Lane);
    }

    // ---- snapshot -------------------------------------------------------------------------------------

    private static readonly GraphRef[] Refs =
    [
        new("main", "refs/heads/main", GraphRefKind.LocalBranch, "m2"),
        new("feat", "refs/heads/feat", GraphRefKind.LocalBranch, "f1"),
        new("origin/main", "refs/remotes/origin/main", GraphRefKind.RemoteBranch, "m2"),
    ];

    private static GraphSnapshot Snap(IReadOnlyList<GraphCommit> log, IReadOnlyList<GraphWorktree> wts, int pageSize = 100) =>
        GraphSnapshot.Build("C:/r", GraphScope.All, GraphLogQuery.Everything, log, Refs, wts, "main", null, pageSize);

    [Fact]
    public void Snapshot_PutsAWipRowPerDirtyWorktreeOnTopWithItsOwnTether()
    {
        var main = new GraphWorktree("C:/r", "main", "m2", Changes: 3, IsCurrent: true);
        var feat = new GraphWorktree("C:/r--feat", "feat", "f1", Changes: 1, IsCurrent: false);
        var clean = new GraphWorktree("C:/r--old", null, "m1", Changes: 0, IsCurrent: false);
        var s = Snap([C("m2", "m1"), C("f1", "m1"), C("m1")], [feat, main, clean]);

        Assert.Equal([main.WipHash, feat.WipHash, "m2", "f1", "m1"], s.Commits.Select(c => c.Hash));
        Assert.True(s.Commits[0].IsWorkInProgress);
        Assert.Equal(3, s.RealCommitCount);
        // The primary branch's WIP heads lane 0, and the chain carries on through main.
        Assert.Equal([0, 1, 0, 1, 0], s.Rows.Select(r => r.Lane));
        Assert.Equal([new GraphTether(0, 2, 0, main), new GraphTether(1, 3, 1, feat)], s.Tethers);

        Assert.True(s.IsTether(1, E(0, 0, 0)));
        Assert.True(s.IsTether(2, E(0, 0, 0)));
        Assert.False(s.IsTether(3, E(0, 0, 0)));     // below main's HEAD: an ordinary line again
        Assert.True(s.IsTether(3, E(1, 1, 1)));
        Assert.False(s.IsTether(4, E(1, 0, 1)));

        Assert.Equal([clean], s.WorktreesAt("m1"));
        Assert.Equal(["main", "origin/main"], s.RefsAt("m2").Select(r => r.Name));
        Assert.Equal(2, s.RowOf("m2"));
        Assert.Equal(-1, s.RowOf("nope"));
    }

    [Fact]
    public void Snapshot_AppendResolvesATetherToAnUnloadedHead()
    {
        var detached = new GraphWorktree("C:/r--x", null, "m1", Changes: 2, IsCurrent: false);
        var s = Snap([C("m2", "m1")], [detached], pageSize: 1);

        Assert.True(s.HasMore);
        Assert.Equal(int.MaxValue, s.Tethers[0].HeadRow);
        Assert.True(s.IsTether(1, s.Rows[1].Up.Single(e => e.Color == s.Tethers[0].Lane)));

        var more = s.Append([C("m1")], pageSize: 1);
        Assert.Equal(2, more.Tethers[0].HeadRow);
        Assert.Equal(2, more.RealCommitCount);
        Assert.Equal(s.Rows, more.Rows.Take(2));
        Assert.Throws<InvalidOperationException>(() => s.Append([C("m0")], 1));
    }

    [Fact]
    public void Find_RefThenHashThenTextCyclingFromTheCurrentRow()
    {
        GraphCommit Named(string hash, string subject, params string[] parents) => new(hash, parents, "Jon", "j@x", 0, subject);
        var s = GraphSnapshot.Build("C:/r", GraphScope.All, GraphLogQuery.Everything,
            [Named("aaaa1111", "Fix the feed", "cccc3333"), Named("bbbb2222", "Feed images", "cccc3333"), Named("cccc3333", "Root")],
            [new("main", "refs/heads/main", GraphRefKind.LocalBranch, "aaaa1111"), new("feat", "refs/heads/feat", GraphRefKind.LocalBranch, "bbbb2222")],
            [], "main", null, 100);

        Assert.Equal(1, s.Find("FEAT", -1));          // ref name, any case
        Assert.Equal(2, s.Find("cccc", -1));          // hash prefix
        Assert.Equal(0, s.Find("feed", -1));          // subject text…
        Assert.Equal(1, s.Find("feed", 0));           // …then the next match
        Assert.Equal(0, s.Find("feed", 1));           // …wrapping round
        Assert.Equal(-1, s.Find("nothing", 0));
        Assert.Equal(-1, s.Find("  ", 0));
        Assert.Equal(0, s.LaneOf("aaaa1111"));
        Assert.Equal(-1, s.LaneOf("nope"));
    }

    // ---- checkout planning -------------------------------------------------------------------------------

    private static GraphSnapshot CheckoutSnap(params GraphWorktree[] wts) =>
        GraphSnapshot.Build("C:/r", GraphScope.All, GraphLogQuery.Everything, [C("m2", "m1"), C("f1", "m1"), C("x1", "m1"), C("m1")],
        [
            .. Refs,
            new("origin/feat", "refs/remotes/origin/feat", GraphRefKind.RemoteBranch, "f1"),
            new("origin/spike", "refs/remotes/origin/spike", GraphRefKind.RemoteBranch, "x1"),
            new("v1", "refs/tags/v1", GraphRefKind.Tag, "m1"),
            new("stash", "refs/stash", GraphRefKind.Stash, "m1"),
        ], wts, "main", null, 100);

    private static GraphRef RefNamed(GraphSnapshot s, string name) => s.Refs.Single(r => r.Name == name);

    [Fact]
    public void Checkout_BranchesSwitchUnlessHereOrHeldElsewhere()
    {
        var here = new GraphWorktree("C:/r", "main", "m2", 0, true);
        var other = new GraphWorktree("C:/r--feat", "feat", "f1", 0, false);
        var s = CheckoutSnap(here, other);

        Assert.Equal(CheckoutAction.AlreadyHere, GraphCheckout.ForRef(s, RefNamed(s, "main")).Action);
        var held = GraphCheckout.ForRef(s, RefNamed(s, "feat"));
        Assert.Equal(new CheckoutPlan(CheckoutAction.HeldElsewhere, "feat", "f1", other), held);
        Assert.False(held.MovesHead);

        var free = CheckoutSnap(here);
        Assert.Equal(new CheckoutPlan(CheckoutAction.Switch, "feat", "f1"), GraphCheckout.ForRef(free, RefNamed(free, "feat")));
    }

    [Fact]
    public void Checkout_RemoteTracksUnlessALocalOfThatNameExists()
    {
        var s = CheckoutSnap(new GraphWorktree("C:/r", "main", "m2", 0, true));

        Assert.Equal(new CheckoutPlan(CheckoutAction.Track, "origin/spike", "x1"), GraphCheckout.ForRef(s, RefNamed(s, "origin/spike")));
        Assert.Equal(new CheckoutPlan(CheckoutAction.Switch, "feat", "f1"), GraphCheckout.ForRef(s, RefNamed(s, "origin/feat")));
        Assert.Equal(CheckoutAction.AlreadyHere, GraphCheckout.ForRef(s, RefNamed(s, "origin/main")).Action);
    }

    [Fact]
    public void Checkout_TagsAndBareCommitsDetach()
    {
        var s = CheckoutSnap(new GraphWorktree("C:/r", "main", "m2", 0, true));

        Assert.Equal(new CheckoutPlan(CheckoutAction.Detach, "v1", "m1"), GraphCheckout.ForRef(s, RefNamed(s, "v1")));
        Assert.Equal(CheckoutPlan.Nothing, GraphCheckout.ForRef(s, RefNamed(s, "stash")));

        var detachedHere = CheckoutSnap(new GraphWorktree("C:/r", null, "m1", 0, true));
        Assert.Equal(CheckoutAction.AlreadyHere, GraphCheckout.ForRef(detachedHere, RefNamed(detachedHere, "v1")).Action);
    }

    [Fact]
    public void Checkout_ACommitRowPrefersItsLocalBranchThenARemoteThenTheCommit()
    {
        var here = new GraphWorktree("C:/r", "feat", "f1", 2, true);
        var s = CheckoutSnap(here);

        Assert.Equal(new CheckoutPlan(CheckoutAction.Switch, "main", "m2"), GraphCheckout.ForCommit(s, "m2"));
        Assert.Equal(CheckoutAction.AlreadyHere, GraphCheckout.ForCommit(s, "f1").Action);
        Assert.Equal(new CheckoutPlan(CheckoutAction.Track, "origin/spike", "x1"), GraphCheckout.ForCommit(s, "x1"));
        Assert.Equal(new CheckoutPlan(CheckoutAction.Detach, "m1", "m1"), GraphCheckout.ForCommit(s, "m1"));
        Assert.Equal(CheckoutPlan.Nothing, GraphCheckout.ForCommit(s, here.WipHash));
    }

    // ---- drop planning --------------------------------------------------------------------------------

    // main m2 → m1; feat f1 → m1; spike (remote only) x1 → m1; old at m1. The window's worktree C:/r is on main; a
    // second worktree is detached at x1.
    private static readonly GraphWorktree DropHere = new("C:/r", "main", "m2", 0, true);
    private static readonly GraphWorktree DropOther = new("C:/r--x", null, "x1", 0, false);

    private static GraphSnapshot DropSnap() =>
        GraphSnapshot.Build("C:/r", GraphScope.All, GraphLogQuery.Everything, [C("m2", "m1"), C("f1", "m1"), C("x1", "m1"), C("m1")],
        [
            new("main", "refs/heads/main", GraphRefKind.LocalBranch, "m2"),
            new("feat", "refs/heads/feat", GraphRefKind.LocalBranch, "f1"),
            new("old", "refs/heads/old", GraphRefKind.LocalBranch, "m1"),
            new("origin/main", "refs/remotes/origin/main", GraphRefKind.RemoteBranch, "m2"),
            new("origin/spike", "refs/remotes/origin/spike", GraphRefKind.RemoteBranch, "x1"),
            new("v1", "refs/tags/v1", GraphRefKind.Tag, "m1"),
        ], [DropHere, DropOther], "main", null, 100);

    private static DropMenu Drop(GraphSnapshot s, string from, string onto) =>
        GraphDrop.Menu(s, DragSource.Of(RefNamed(s, from)), DropTarget.Of(RefNamed(s, onto)));

    [Fact]
    public void Drop_DivergedBranchesMergeInTheTargetsWorktreeAndRebaseInTheSources()
    {
        var s = DropSnap();
        var m = Drop(s, "feat", "main");

        Assert.Null(m.Note);
        Assert.Equal([DropKind.Merge, DropKind.Rebase], m.Options.Select(o => o.Kind));
        var merge = m.Options[0];
        Assert.Equal((DropHere, "feat", "main", false, "git merge feat"), (merge.RunIn, merge.Rev, merge.Branch, merge.SwitchFirst, merge.Command));
        // No worktree has feat, so the rebase switches this one to it (rebase <onto> <branch> does that itself).
        var rebase = m.Options[1];
        Assert.Equal((DropHere, "main", "feat", true, "git rebase main feat"), (rebase.RunIn, rebase.Rev, rebase.Branch, rebase.SwitchFirst, rebase.Command));
    }

    [Fact]
    public void Drop_FastForwardsABranchThatIsBehindAndRefusesOneThatHasItAll()
    {
        var s = DropSnap();

        var ff = Assert.Single(Drop(s, "main", "old").Options);
        Assert.Equal((DropKind.FastForward, (GraphWorktree?)null, "git fetch . main:old"), (ff.Kind, ff.RunIn, ff.Command));
        Assert.Equal("main already contains old.", Drop(s, "old", "main").Note);
        Assert.Empty(Drop(s, "old", "main").Options);
        Assert.Empty(Drop(s, "main", "main").Options);
        Assert.Contains("tag", Drop(s, "feat", "v1").Note);
    }

    [Fact]
    public void Drop_ARemoteSourceMergesButNeverRebasesAndALocalRebasesOntoARemote()
    {
        var s = DropSnap();

        Assert.Equal([DropKind.Merge], Drop(s, "origin/spike", "feat").Options.Select(o => o.Kind));
        var onto = Assert.Single(Drop(s, "feat", "origin/main").Options);
        Assert.Equal((DropKind.Rebase, "origin/main", "feat"), (onto.Kind, onto.Rev, onto.Branch));
        var behind = Assert.Single(Drop(s, "old", "origin/main").Options);
        Assert.Equal((DropKind.FastForward, "origin/main", "old"), (behind.Kind, behind.Rev, behind.Branch));
        Assert.Empty(Drop(s, "origin/spike", "origin/main").Options);
    }

    [Fact]
    public void Drop_ACommitCherryPicksUnlessTheBranchHasIt()
    {
        var s = DropSnap();
        var f1 = s.Commits[s.RowOf("f1")];

        var pick = Assert.Single(GraphDrop.Menu(s, DragSource.Of(f1), DropTarget.Of(RefNamed(s, "main"))).Options);
        Assert.Equal((DropKind.CherryPick, DropHere, "f1", "main"), (pick.Kind, pick.RunIn, pick.Rev, pick.Branch));
        Assert.NotNull(GraphDrop.Menu(s, DragSource.Of(s.Commits[s.RowOf("m1")]), DropTarget.Of(RefNamed(s, "main"))).Note);
    }

    [Fact]
    public void Drop_OntoAWorktreeChecksOutThere()
    {
        var s = DropSnap();

        var sw = Assert.Single(GraphDrop.Menu(s, DragSource.Of(RefNamed(s, "feat")), DropTarget.Of(DropOther)).Options);
        Assert.Equal((DropKind.Checkout, CheckoutAction.Switch, DropOther, "feat"), (sw.Kind, sw.Checkout, sw.RunIn, sw.Rev));

        var held = GraphDrop.Menu(s, DragSource.Of(RefNamed(s, "main")), DropTarget.Of(DropOther));
        Assert.Contains("checked out in r", held.Note);
        Assert.Equal(CheckoutAction.Detach, Assert.Single(held.Options).Checkout);

        var f1 = GraphDrop.Menu(s, DragSource.Of(s.Commits[s.RowOf("f1")]), DropTarget.Of(DropHere));
        Assert.Equal([DropKind.CherryPick, DropKind.Checkout], f1.Options.Select(o => o.Kind));
    }

    [Fact]
    public void Drop_OntoNewWorktreeUsesTheLayoutAndAsksForANameWhenItMust()
    {
        var s = DropSnap();
        var layout = new Perch.Data.WorktreeLayout("{parent}/{repo}--{name}", Perch.Data.WorktreeLayoutSource.Detected);
        DropMenu New(DragSource src) => GraphDrop.Menu(s, src, DropTarget.NewWorktreeZone, layout);

        var feat = Assert.Single(New(DragSource.Of(RefNamed(s, "feat"))).Options);
        Assert.Equal(Path.GetFullPath("C:/r--feat"), feat.NewPath);
        Assert.Equal(("feat", false, false), (feat.Branch, feat.NeedsName, feat.Track));

        var spike = Assert.Single(New(DragSource.Of(RefNamed(s, "origin/spike"))).Options);
        Assert.Equal(("spike", true, "origin/spike"), (spike.Branch, spike.Track, spike.Rev));

        var main = New(DragSource.Of(RefNamed(s, "main")));
        Assert.Contains("checked out in r", main.Note);
        Assert.True(Assert.Single(main.Options).NeedsName);
        Assert.True(Assert.Single(New(DragSource.Of(s.Commits[s.RowOf("x1")])).Options).NeedsName);
    }

    [Fact]
    public void NewWorktreePath_FollowsTheUsersWorktreesElseGoesBesideTheRepo()
    {
        var detected = new Perch.Data.WorktreeLayout("{root}/.worktrees/{name}", Perch.Data.WorktreeLayoutSource.Detected);
        var hinted = new Perch.Data.WorktreeLayout("{root}/.claude/worktrees/{name}", Perch.Data.WorktreeLayoutSource.IgnoreHint);

        Assert.Equal(Path.GetFullPath("C:/r/.worktrees/feature-x"), GraphDrop.NewWorktreePath("C:/r", detected, "feature/x"));
        Assert.Equal(Path.GetFullPath("C:/r--feature-x"), GraphDrop.NewWorktreePath("C:/r", hinted, "feature/x"));
        Assert.Equal(Path.GetFullPath("C:/r--feature-x"), GraphDrop.NewWorktreePath("C:/r", null, "feature/x"));

        // Opened on a linked worktree, a new one still goes beside the main worktree, not beside the linked one.
        var fromLinked = GraphSnapshot.Build("C:/r--x", GraphScope.All, GraphLogQuery.Everything, [C("m1")],
            [new("feat", "refs/heads/feat", GraphRefKind.LocalBranch, "m1")], [], "main", null, 100);
        var opt = Assert.Single(GraphDrop.Menu(fromLinked, DragSource.Of(fromLinked.Refs[0]), DropTarget.NewWorktreeZone, null, "C:/r").Options);
        Assert.Equal(Path.GetFullPath("C:/r--feat"), opt.NewPath);
    }

    [Fact]
    public void IsAncestor_WalksTheLoadedRows()
    {
        var s = DropSnap();
        Assert.True(GraphDrop.IsAncestor(s, "m1", "m2"));
        Assert.True(GraphDrop.IsAncestor(s, "m2", "m2"));
        Assert.False(GraphDrop.IsAncestor(s, "f1", "m2"));
        Assert.False(GraphDrop.IsAncestor(s, "m2", "m1"));
        Assert.Null(GraphDrop.IsAncestor(s, "nope", "m2"));
    }

    [Fact]
    public void WorktreeContaining_PicksTheDeepestWorktreeAboveTheFolder()
    {
        var main = new GraphWorktree(@"C:\src\perch", "main", "m2", 0, true);
        var nested = new GraphWorktree(@"C:\src\perch\.claude\worktrees\x", "feat", "f1", 0, false);
        var sibling = new GraphWorktree(@"C:\src\perch--feeds", "feeds", "m1", 0, false);
        var s = Snap([C("m2", "m1"), C("m1")], [main, nested, sibling]);

        Assert.Same(main, s.WorktreeContaining(@"C:\src\perch\src\Perch.App"));
        Assert.Same(nested, s.WorktreeContaining(@"C:\src\perch\.claude\worktrees\x\docs"));
        Assert.Same(sibling, s.WorktreeContaining(@"C:\src\perch--feeds"));
        Assert.Null(s.WorktreeContaining(@"C:\src\perch-other"));
        Assert.Null(s.WorktreeContaining(null));
        if (OperatingSystem.IsWindows())
            Assert.Same(main, s.WorktreeContaining(@"c:\SRC\Perch/"));
    }

    [Fact]
    public void PrimaryBranch_PrefersLocalTrunkThenRemote()
    {
        Assert.Equal("main", GraphLoader.PrimaryBranch(Refs));
        Assert.Equal("upstream/master", GraphLoader.PrimaryBranch(
            [new("feat", "refs/heads/feat", GraphRefKind.LocalBranch, "a"), new("upstream/master", "refs/remotes/upstream/master", GraphRefKind.RemoteBranch, "b")]));
        Assert.Null(GraphLoader.PrimaryBranch([]));
    }

    [Fact]
    public void LogArgs_PutsExclusionsAfterNot()
    {
        Assert.Equal(["HEAD", "main", "--not", "abc^@"], GraphLogReader.GraphLogArgs(new(["HEAD", "main"], ["abc^@"])));
        Assert.Equal(["--branches", "--remotes", "--tags", "HEAD"], GraphLogReader.GraphLogArgs(GraphLogQuery.Everything));
    }

    // A deterministic history in topological order: a trunk with short-lived branches that fork off, sometimes
    // merge back, and sometimes are left dangling, plus occasional merges of the trunk into a branch.
    private static List<GraphCommit> Synthetic(int count, int seed)
    {
        var rng = new Random(seed);
        // Build oldest-first, then reverse into topo order (newest first).
        var built = new List<GraphCommit>(count);
        string trunk = "t0";
        built.Add(C(trunk));
        var open = new List<string>();     // branch tips
        int n = 1;
        while (built.Count < count)
        {
            int roll = rng.Next(100);
            if (roll < 50 || open.Count == 0)
            {
                var h = "t" + n++;
                if (open.Count > 0 && rng.Next(100) < 15)
                {
                    int k = rng.Next(open.Count);
                    built.Add(C(h, trunk, open[k]));
                    open.RemoveAt(k);
                }
                else built.Add(C(h, trunk));
                trunk = h;
                if (rng.Next(100) < 12 && open.Count < 12) open.Add(trunk);
            }
            else
            {
                int k = rng.Next(open.Count);
                var h = "b" + n++;
                built.Add(rng.Next(100) < 4 ? C(h, open[k], trunk) : C(h, open[k]));
                open[k] = h;
                if (rng.Next(100) < 3) open.RemoveAt(k);
            }
        }
        built.Reverse();
        return built;
    }
}
