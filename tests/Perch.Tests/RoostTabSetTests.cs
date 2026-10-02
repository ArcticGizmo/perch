using System.Text.Json;
using Perch.Data;
using Perch.Data.Roost;
using Xunit;

namespace Perch.Tests;

/// <summary>
/// Guards <see cref="RoostTabSet"/> (docs/roost-tabs-plan.md T3): the interaction table (assign / swap / rail click
/// / Focus), one place per session as a policy, layouts carrying sessions by region id, following adoptions,
/// pruning, persistence by pid/sessionId token, and the tab light (<see cref="RoostTabStatus"/>).
/// </summary>
public class RoostTabSetTests
{
    private static readonly DateTime T0 = new(2026, 10, 1, 9, 0, 0);

    private static ClaudeSession S(string pid, SessionStatus status = SessionStatus.Running, string? sessionId = null) =>
        new(pid, sessionId ?? $"sess-{pid}", status, @"C:\fixtures\proj", $"proj-{pid}", T0);

    private static RoostRoster Roster(params ClaudeSession[] sessions)
    {
        var r = new RoostRoster();
        r.Update(sessions, T0);
        return r;
    }

    private static RoostGridLayout Two => RoostGridLayout.FromTemplate(RoostSnapTemplate.Columns2);   // regions 0 | 1

    private static RoostGridLayout Three => RoostGridLayout.FromTemplate(RoostSnapTemplate.MainPlusTwo);   // 0 | 1 / 2

    private static (RoostTabSet Set, RoostTab A, RoostTab B) TwoTabs()
    {
        var set = new RoostTabSet();
        var a = set.AddTab("A", Two)!;
        var b = set.AddTab("B", Two)!;
        return (set, a, b);
    }

    // ── Tabs ──────────────────────────────────────────────────────────────────

    [Fact]
    public void FocusIsFixedAndFirst()
    {
        var (set, a, _) = TwoTabs();
        Assert.Equal([RoostTabSet.FocusId, a.Id, set.Tabs[1].Id], set.All.Select(t => t.Id));
        Assert.True(set.Focus.IsFocus);
        Assert.Equal(RoostTabSet.FocusId, set.ActiveId);
        Assert.False(set.RenameTab(RoostTabSet.FocusId, "Mine"));
        Assert.False(set.CloseTab(RoostTabSet.FocusId));
        Assert.Empty(set.ApplyLayout(RoostTabSet.FocusId, Two));
        Assert.Same(RoostGridLayout.Full, set.Focus.Layout);
    }

    [Fact]
    public void NewTabsGetTheFirstFreeDefaultName()
    {
        var set = new RoostTabSet();
        var one = set.AddTab()!;
        var two = set.AddTab()!;
        Assert.Equal(["Tab 1", "Tab 2"], set.Tabs.Select(t => t.Name));
        set.CloseTab(one.Id);
        Assert.Equal("Tab 1", set.AddTab()!.Name);
        Assert.NotEqual(one.Id, set.Tabs[^1].Id);   // ids are never reused
        Assert.Same(RoostGridLayout.Full, two.Layout);
    }

    [Fact]
    public void RenamingTrimsCapsAndRefusesBlanks()
    {
        var (set, a, _) = TwoTabs();
        Assert.True(set.RenameTab(a.Id, "  Infra  "));
        Assert.Equal("Infra", a.Name);
        Assert.False(set.RenameTab(a.Id, "   "));
        Assert.False(set.RenameTab(a.Id, "Infra"));
        set.RenameTab(a.Id, new string('x', 100));
        Assert.Equal(RoostTabSet.MaxNameLength, a.Name.Length);
    }

    [Fact]
    public void TabsCapOut()
    {
        var set = new RoostTabSet();
        for (int i = 0; i < RoostTabSet.MaxTabs; i++) Assert.NotNull(set.AddTab());
        Assert.Null(set.AddTab());
        Assert.Null(set.DuplicateTab(set.Tabs[0].Id));
    }

    [Fact]
    public void ClosingTheActiveTabActivatesTheOneBeforeIt()
    {
        var (set, a, b) = TwoTabs();
        set.Activate(b.Id);
        set.CloseTab(b.Id);
        Assert.Equal(a.Id, set.ActiveId);
        set.CloseTab(a.Id);
        Assert.Equal(RoostTabSet.FocusId, set.ActiveId);
    }

    [Fact]
    public void ClosingATabSendsItsSessionsBackToTheRail()
    {
        var (set, a, _) = TwoTabs();
        var roster = Roster(S("1"), S("2"));
        set.Assign(a.Id, 0, "1");
        set.CloseTab(a.Id);
        Assert.Equal(["1", "2"], set.Unplaced(roster.Panes).Select(p => p.Key));
    }

    [Fact]
    public void DuplicateCopiesTheLayoutButNotTheSessions()
    {
        var (set, a, b) = TwoTabs();
        set.Assign(a.Id, 0, "1");
        var copy = set.DuplicateTab(a.Id)!;
        Assert.Equal("A copy", copy.Name);
        Assert.Equal(a.Layout.Signature, copy.Layout.Signature);
        Assert.Empty(copy.Cells);
        Assert.Equal([a.Id, copy.Id, b.Id], set.Tabs.Select(t => t.Id));
    }

    [Fact]
    public void MoveTabReordersAndClamps()
    {
        var (set, a, b) = TwoTabs();
        var c = set.AddTab("C")!;
        Assert.True(set.MoveTab(c.Id, 0));
        Assert.Equal([c.Id, a.Id, b.Id], set.Tabs.Select(t => t.Id));
        Assert.True(set.MoveTab(c.Id, 99));
        Assert.Equal([a.Id, b.Id, c.Id], set.Tabs.Select(t => t.Id));
        Assert.False(set.MoveTab(c.Id, 2));
    }

    // ── The interaction table ────────────────────────────────────────────────

    [Fact]
    public void ARailClickOnAPlacedSessionGoesToItsTab()
    {
        var (set, _, b) = TwoTabs();
        set.Assign(b.Id, 1, "1");
        Assert.Equal(new RoostPlacement(b.Id, 1), set.Show("1"));
        Assert.Equal(b.Id, set.ActiveId);
    }

    [Fact]
    public void ARailClickOnAnUnplacedSessionPutsItInFocusReplacingTheOneOff()
    {
        var (set, _, _) = TwoTabs();
        var roster = Roster(S("1"), S("2"));
        Assert.Equal(new RoostPlacement(RoostTabSet.FocusId, 0), set.Show("1"));
        Assert.Equal(RoostTabSet.FocusId, set.ActiveId);
        set.Show("2");
        Assert.Equal("2", set.Focus.At(0));
        Assert.Equal(["1"], set.Unplaced(roster.Panes).Select(p => p.Key));
    }

    [Fact]
    public void ARailClickUnzoomsTheTabWhenAnotherRegionIsZoomed()
    {
        var (set, a, _) = TwoTabs();
        set.Assign(a.Id, 0, "1");
        set.Assign(a.Id, 1, "2");
        Assert.Equal(0, set.ToggleZoom(a.Id, 0));
        set.Show("1");
        Assert.Equal(0, a.Zoomed);   // the zoomed one itself: stays zoomed
        set.Show("2");
        Assert.Null(a.Zoomed);
    }

    [Fact]
    public void DroppingIntoAnEmptyRegionMovesTheSession()
    {
        var (set, a, b) = TwoTabs();
        set.Assign(a.Id, 0, "1");
        Assert.Null(set.Assign(b.Id, 1, "1"));
        Assert.Empty(a.Cells);
        Assert.Equal([new RoostPlacement(b.Id, 1)], set.Locate("1"));
    }

    // ── Drop on a tab header (T5) ─────────────────────────────────────────────

    [Fact]
    public void DroppingOnATabHeaderFillsItsFirstEmptyRegionInReadingOrder()
    {
        var set = new RoostTabSet();
        var t = set.AddTab("T", Three)!;
        set.Assign(t.Id, 0, "1");
        Assert.Equal(new RoostPlacement(t.Id, 1), set.DropOnTab(t.Id, "2"));
        Assert.Equal(new RoostPlacement(t.Id, 2), set.DropOnTab(t.Id, "3"));
        Assert.Equal(RoostTabSet.FocusId, set.ActiveId);   // the drop doesn't switch tabs
    }

    [Fact]
    public void DroppingOnAFullTabSwapsWithItsLastFocusedRegion()
    {
        var (set, a, b) = TwoTabs();
        set.Assign(a.Id, 0, "1");
        set.Assign(b.Id, 0, "2");
        set.Assign(b.Id, 1, "3");
        set.NoteFocus("2");
        Assert.Equal(new RoostPlacement(b.Id, 0), set.DropOnTab(b.Id, "1"));
        Assert.Equal("1", b.At(0));
        Assert.Equal("2", a.At(0));   // the occupant took the dropped session's old place
        Assert.Equal("3", b.At(1));
    }

    [Fact]
    public void DroppingAnUnplacedSessionOnAFullTabSendsTheOccupantToTheRail()
    {
        var (set, _, b) = TwoTabs();
        var roster = Roster(S("1"), S("2"), S("3"));
        set.Assign(b.Id, 0, "2");
        set.Assign(b.Id, 1, "3");   // the last assignment is region 1
        set.DropOnTab(b.Id, "1");
        Assert.Equal("1", b.At(1));
        Assert.Equal(["3"], set.Unplaced(roster.Panes).Select(p => p.Key));
    }

    [Fact]
    public void DroppingOnTheTabItIsAlreadyInLeavesItPut()
    {
        var (set, a, _) = TwoTabs();
        set.Assign(a.Id, 1, "1");
        int changes = 0;
        set.Changed += () => changes++;
        Assert.Equal(new RoostPlacement(a.Id, 1), set.DropOnTab(a.Id, "1"));
        Assert.Equal(0, changes);
        Assert.Null(set.DropOnTab("nope", "1"));
    }

    [Fact]
    public void DroppingOnTheFocusHeaderSwapsWithTheOneOff()
    {
        var (set, a, _) = TwoTabs();
        var roster = Roster(S("1"), S("2"));
        set.Show("1");
        set.Assign(a.Id, 0, "2");
        Assert.Equal(new RoostPlacement(RoostTabSet.FocusId, 0), set.DropOnTab(RoostTabSet.FocusId, "2"));
        Assert.Equal("1", a.At(0));   // swapped into the dropped session's old region
        Assert.Empty(set.Unplaced(roster.Panes));
    }

    [Fact]
    public void DroppingAPlacedSessionOnAnOccupiedRegionSwaps()
    {
        var (set, a, b) = TwoTabs();
        set.Assign(a.Id, 0, "1");
        set.Assign(a.Id, 1, "2");
        Assert.Null(set.Assign(a.Id, 1, "1"));   // a header drag within the tab
        Assert.Equal("1", a.At(1));
        Assert.Equal("2", a.At(0));

        set.Assign(b.Id, 0, "3");
        Assert.Null(set.Assign(b.Id, 0, "2"));   // across tabs: 3 takes 2's old place
        Assert.Equal("3", a.At(0));
        Assert.Equal("2", b.At(0));
    }

    [Fact]
    public void DroppingAnUnplacedSessionOnAnOccupiedRegionSendsTheOccupantToTheRail()
    {
        var (set, a, _) = TwoTabs();
        set.Assign(a.Id, 0, "1");
        Assert.Equal("1", set.Assign(a.Id, 0, "2"));
        Assert.False(set.IsPlaced("1"));
    }

    [Fact]
    public void AssigningToAMissingRegionOrTabDoesNothing()
    {
        var (set, a, _) = TwoTabs();
        Assert.Null(set.Assign(a.Id, 7, "1"));
        Assert.Null(set.Assign("nope", 0, "1"));
        Assert.False(set.IsPlaced("1"));
    }

    [Fact]
    public void UnassignEmptiesTheRegionAndItsZoom()
    {
        var (set, a, _) = TwoTabs();
        set.Assign(a.Id, 0, "1");
        set.ToggleZoom(a.Id, 0);
        Assert.True(set.Unassign("1"));
        Assert.Empty(a.Cells);
        Assert.Null(a.Zoomed);
        Assert.False(set.Unassign("1"));
    }

    [Fact]
    public void ZoomTogglesAndIgnoresMissingRegions()
    {
        var (set, a, _) = TwoTabs();
        Assert.Equal(1, set.ToggleZoom(a.Id, 1));
        Assert.Null(set.ToggleZoom(a.Id, 1));
        Assert.Null(set.ToggleZoom(a.Id, 9));
    }

    [Fact]
    public void WithoutUniquePlacementASessionCanBeInTwoTabs()
    {
        // D1's door: the model holds duplicates; only the policy forbids them.
        var set = new RoostTabSet(uniquePlacement: false);
        var a = set.AddTab("A", Two)!;
        var b = set.AddTab("B", Two)!;
        set.Assign(a.Id, 0, "1");
        set.Assign(b.Id, 1, "1");
        Assert.Equal([new RoostPlacement(a.Id, 0), new RoostPlacement(b.Id, 1)], set.Locate("1"));
        set.Activate(a.Id);
        Assert.Equal(a.Id, set.Show("1").TabId);   // the most recently shown of its tabs
        set.Activate(b.Id);
        set.Activate(RoostTabSet.FocusId);
        Assert.Equal(b.Id, set.Show("1").TabId);
        // An occupant still shown elsewhere isn't "unplaced".
        Assert.Null(set.Assign(a.Id, 0, "2"));
    }

    // ── Layouts ───────────────────────────────────────────────────────────────

    [Fact]
    public void AnEditedLayoutKeepsSessionsByRegionId()
    {
        var set = new RoostTabSet();
        var tab = set.AddTab("T", Three)!;
        set.Assign(tab.Id, 0, "1");
        set.Assign(tab.Id, 1, "2");
        set.Assign(tab.Id, 2, "3");
        // The painter removes region 1 (2 grows into its place) and splits region 0.
        var edited = Three.Remove(1)!.Split(0, RoostSplit.Rows)!;
        Assert.Equal(["2"], set.ApplyLayout(tab.Id, edited));
        Assert.Equal("1", tab.At(0));
        Assert.Equal("3", tab.At(2));
        Assert.Null(tab.At(3));   // the new half starts empty
    }

    [Fact]
    public void APresetMapsSessionsInReadingOrder()
    {
        var set = new RoostTabSet();
        var tab = set.AddTab("T", Three)!;
        set.Assign(tab.Id, 0, "1");
        set.Assign(tab.Id, 1, "2");
        set.Assign(tab.Id, 2, "3");
        var preset = RoostGridLayout.FromTemplate(RoostSnapTemplate.Columns2).AdoptIds(tab.Layout);
        Assert.Equal(["3"], set.ApplyLayout(tab.Id, preset));
        var order = tab.Layout.ReadingOrder;
        Assert.Equal("1", tab.At(order[0].Id));
        Assert.Equal("2", tab.At(order[1].Id));
    }

    [Fact]
    public void AdoptIdsRenumbersInReadingOrderAndMintsFreshIdsForExtras()
    {
        var before = RoostGridLayout.Create([new(4, 0, 0, 12, 6), new(9, 0, 6, 12, 6)])!;
        var grid = RoostGridLayout.FromTemplate(RoostSnapTemplate.Grid2x2).AdoptIds(before);
        Assert.Equal([4, 9, 10, 11], grid.ReadingOrder.Select(r => r.Id));
    }

    // ── Sync ──────────────────────────────────────────────────────────────────

    [Fact]
    public void ARegionEmptiesWhenItsSessionLeavesTheRoster()
    {
        var (set, a, _) = TwoTabs();
        var roster = Roster(S("1"), S("2"));
        set.Assign(a.Id, 0, "1");
        set.Assign(a.Id, 1, "2");
        roster.Close("2");                       // hidden: gone from Panes
        roster.Update([S("2")], T0);             // 1 ended — lingers, still placed
        set.Sync(roster.Panes, roster.Adopted);
        Assert.Equal("1", a.At(0));
        Assert.Null(a.At(1));
        roster.Update([S("2")], T0 + RoostRoster.EndedLinger);   // 1 drops
        set.Sync(roster.Panes, roster.Adopted);
        Assert.Empty(a.Cells);
    }

    [Fact]
    public void ATakeOverKeepsTheRegion()
    {
        var (set, a, _) = TwoTabs();
        var roster = Roster(S("100", sessionId: "conv"));
        set.Assign(a.Id, 1, "100");
        // The terminal process stops and Perch resumes the same conversation under a new pid.
        roster.Update([S("200", sessionId: "conv")], T0.AddSeconds(5));
        Assert.Equal("200", roster.Adopted["100"]);
        set.Sync(roster.Panes, roster.Adopted);
        Assert.Equal("200", a.At(1));
    }

    [Fact]
    public void AdoptedIsOnlyTheLatestUpdates()
    {
        var roster = Roster(S("100", sessionId: "conv"));
        roster.Update([S("200", sessionId: "conv")], T0);
        Assert.Single(roster.Adopted);
        roster.Update([S("200", sessionId: "conv")], T0);
        Assert.Empty(roster.Adopted);
    }

    // ── Persistence ───────────────────────────────────────────────────────────

    private static RoostTabsState RoundTrip(RoostTabsState state) =>
        JsonSerializer.Deserialize<RoostTabsState>(JsonSerializer.Serialize(state))!;

    [Fact]
    public void TabsRoundTripAndCellsResolveByPidAndSessionId()
    {
        var (set, a, b) = TwoTabs();
        var roster = Roster(S("1"), S("2"), S("3"));
        set.Sync(roster.Panes);
        set.Assign(a.Id, 0, "1");
        set.Assign(b.Id, 1, "2");
        set.Show("3");
        set.Activate(b.Id);
        var state = RoundTrip(set.ToState());
        Assert.Equal("1/sess-1", state.Tabs[0].Cells![0]);
        Assert.Equal("3/sess-3", state.Focus);

        var back = new RoostTabSet();
        back.Seed(state);
        Assert.Equal(["A", "B"], back.Tabs.Select(t => t.Name));
        Assert.Equal(b.Id, back.ActiveId);
        Assert.Empty(back.Tabs[0].Cells);   // placed on the first Sync
        // After a restart: pid 2 now belongs to an unrelated session (recycled), the others still match.
        back.Sync(Roster(S("1"), S("2", sessionId: "other"), S("3")).Panes);
        Assert.Equal("1", back.Tabs[0].At(0));
        Assert.Null(back.Tabs[1].At(1));
        Assert.Equal("3", back.Focus.At(0));
        Assert.DoesNotContain(back.AddTab()!.Id, new[] { a.Id, b.Id });   // new ids don't collide with seeded ones
    }

    [Fact]
    public void UnresolvedCellsSurviveASaveBeforeTheFirstSync()
    {
        var (set, a, _) = TwoTabs();
        set.Sync(Roster(S("1")).Panes);
        set.Assign(a.Id, 0, "1");
        var back = new RoostTabSet();
        back.Seed(RoundTrip(set.ToState()));
        Assert.Equal("1/sess-1", back.ToState().Tabs[0].Cells![0]);
    }

    [Fact]
    public void ASeedRepairsWhatItCantTrust()
    {
        var state = new RoostTabsState
        {
            Active = "missing",
            Tabs =
            [
                new() { Id = "t1", Name = "  ", Layout = [new(0, 0, 0, 12, 5)] },   // bad layout, blank name
                new() { Id = "t1", Name = "dupe" },                                 // duplicate id
                new() { Id = RoostTabSet.FocusId, Name = "reserved" },
                new() { Id = "t7", Name = "ok", Cells = new() { [0] = "bare-pid", [5] = "1/sess-1" } },
            ],
        };
        var set = new RoostTabSet();
        set.Seed(state);
        Assert.Equal(["t1", "t7"], set.Tabs.Select(t => t.Id));
        Assert.Equal("Tab 1", set.Tabs[0].Name);
        Assert.Same(RoostGridLayout.Full, set.Tabs[0].Layout);
        Assert.Equal(RoostTabSet.FocusId, set.ActiveId);
        set.Sync(Roster(S("1")).Panes);
        Assert.Empty(set.Tabs[1].Cells);   // a bare pid and a missing region both ignored
        Assert.Equal("t8", set.AddTab()!.Id);
    }

    [Fact]
    public void ANullTabInTheFileIsSkipped()
    {
        // System.Text.Json reads a null list element without complaint, so the settings salvage never sees it.
        var state = JsonSerializer.Deserialize<RoostTabsState>(
            """{"Tabs":[null,{"Id":"t2","Name":"ok","Layout":null,"Cells":null}],"Active":null,"Focus":null}""")!;
        var set = new RoostTabSet();
        set.Seed(state);
        Assert.Equal(["t2"], set.Tabs.Select(t => t.Id));
        Assert.Same(RoostGridLayout.Full, set.Tabs[0].Layout);
    }

    [Fact]
    public void ASyncThatDoesNotResolveSeedsKeepsThemForTheFirstScan()
    {
        var (set, a, _) = TwoTabs();
        set.Sync(Roster(S("1")).Panes);
        set.Assign(a.Id, 0, "1");
        var back = new RoostTabSet();
        back.Seed(RoundTrip(set.ToState()));
        int fired = 0;
        back.Changed += () => fired++;

        // The Roost opened before the first scan: its own sync sees an empty roster and must not settle the cells.
        back.Sync([], resolveSeeds: false);
        Assert.Equal(0, fired);
        Assert.Equal("1/sess-1", back.ToState().Tabs[0].Cells![0]);

        back.Sync(Roster(S("1")).Panes);   // the scan
        Assert.Equal("1", back.Tabs[0].At(0));
    }

    [Fact]
    public void ChangedFiresOnlyWhenThePersistedFormMoves()
    {
        var set = new RoostTabSet();
        int fired = 0;
        set.Changed += () => fired++;
        var a = set.AddTab("A", Two)!;
        Assert.Equal(1, fired);
        set.Sync(Roster(S("1")).Panes);
        Assert.Equal(1, fired);
        set.Assign(a.Id, 0, "1");
        Assert.Equal(2, fired);
        set.ToggleZoom(a.Id, 0);   // zoom is transient
        set.Assign(a.Id, 0, "1");  // no-op
        Assert.Equal(2, fired);
        set.Sync(Roster(S("1", sessionId: "after-clear")).Panes);   // /clear: same pid, new token
        Assert.Equal(3, fired);
    }

    [Fact]
    public void TheDefaultTabFillsWithTheLiveSessions()
    {
        var set = new RoostTabSet();
        var roster = Roster(S("1"), S("2"), S("3"));
        var main = set.CreateDefault(roster.Panes, aspect: 1.6)!;
        Assert.Equal("Main", main.Name);
        Assert.Equal(3, main.Layout.Regions.Count);
        Assert.Equal(["1", "2", "3"], main.Layout.ReadingOrder.Select(r => main.At(r.Id)));
        Assert.Equal(main.Id, set.ActiveId);
        Assert.Null(set.CreateDefault(roster.Panes, 1.6));   // only for a set with no tabs
    }

    // ── Lights ────────────────────────────────────────────────────────────────

    [Fact]
    public void TheTabLightIsTheMostUrgentStateAndCountsWhatWantsYou()
    {
        var roster = Roster(S("1"), S("2", SessionStatus.NeedsAttention), S("3", SessionStatus.AwaitingInput),
            S("4", SessionStatus.ApiError), S("5", SessionStatus.Idle));
        RoostTabLight L(params string[] keys) => RoostTabStatus.Of(keys, roster.Panes);
        Assert.Equal(new RoostTabLight(RoostLight.None, 0), L());
        Assert.Equal(new RoostTabLight(RoostLight.Quiet, 0), L("5"));
        Assert.Equal(new RoostTabLight(RoostLight.Working, 0), L("1", "5"));
        Assert.Equal(new RoostTabLight(RoostLight.Done, 1), L("1", "2"));
        Assert.Equal(new RoostTabLight(RoostLight.Awaiting, 2), L("2", "3"));
        Assert.Equal(new RoostTabLight(RoostLight.Error, 3), L("2", "3", "4"));
    }

    [Fact]
    public void ElsewhereCountsOtherTabsOnly()
    {
        var (set, a, b) = TwoTabs();
        var roster = Roster(S("1", SessionStatus.AwaitingInput), S("2", SessionStatus.NeedsAttention));
        set.Assign(a.Id, 0, "1");
        set.Assign(b.Id, 0, "2");
        set.Activate(a.Id);
        Assert.Equal(new RoostTabLight(RoostLight.Done, 1), set.Elsewhere(roster.Panes));
        Assert.Equal(new RoostTabLight(RoostLight.Awaiting, 1), RoostTabStatus.For(a, roster.Panes));
    }

    [Fact]
    public void ElsewhereLeavesOutWhatTheActiveTabAlsoShows()
    {
        // Two places per session only happens without UniquePlacement; the pill must still not send you away from it.
        var set = new RoostTabSet(uniquePlacement: false);
        var a = set.AddTab("A", Two)!;
        var b = set.AddTab("B", Two)!;
        set.Assign(a.Id, 0, "1");
        set.Assign(b.Id, 0, "1");
        set.Assign(b.Id, 1, "2");
        set.Activate(a.Id);
        Assert.Equal(["2"], set.ElsewhereKeys());
    }

    [Fact]
    public void MostUrgentGoesByLightThenRosterOrder()
    {
        var roster = Roster(S("1", SessionStatus.Running), S("2", SessionStatus.NeedsAttention),
            S("3", SessionStatus.AwaitingInput), S("4", SessionStatus.AwaitingInput));
        Assert.Equal("3", RoostTabStatus.MostUrgent(["1", "2", "3", "4"], roster.Panes));
        Assert.Equal("2", RoostTabStatus.MostUrgent(["1", "2"], roster.Panes));
        Assert.Null(RoostTabStatus.MostUrgent(["gone"], roster.Panes));
    }

    // ── Window helpers ────────────────────────────────────────────────────────

    [Fact]
    public void CycleWrapsThroughFocusAndTheTabs()
    {
        var (set, a, b) = TwoTabs();
        Assert.Equal(a.Id, set.Cycle(+1));                   // from Focus
        Assert.Equal(b.Id, set.Cycle(-1));                   // Focus wraps back to the last tab
        set.Activate(b.Id);
        Assert.Equal(RoostTabSet.FocusId, set.Cycle(+1));    // the last tab wraps to Focus
        Assert.Equal(a.Id, set.Cycle(-1));
    }

    [Fact]
    public void PickerCandidatesPutUnplacedFirstThenUrgencyThenName()
    {
        var (set, a, b) = TwoTabs();
        var roster = Roster(S("1"), S("2", SessionStatus.AwaitingInput), S("3"), S("4", SessionStatus.AwaitingInput), S("5"));
        set.Assign(a.Id, 0, "1");   // already here: never offered
        set.Assign(b.Id, 0, "4");   // in another tab: offered after the unplaced, however urgent
        var names = set.Candidates(a.Id, roster.Panes).Select(p => p.Key);
        Assert.Equal(["2", "3", "5", "4"], names);
        Assert.Empty(set.Candidates("missing", roster.Panes));
    }

    [Fact]
    public void ASeedDropsTabIdsItWouldNeverHaveMade()
    {
        var state = new RoostTabsState
        {
            Tabs =
            [
                new() { Id = "t3", Name = "ok" },
                new() { Id = "work", Name = "hand-typed" },
                new() { Id = "t0", Name = "zero" },
                new() { Id = "t1234567890", Name = "too long" },
                new() { Id = new string('t', 1) + new string('9', 2000), Name = "huge" },
            ],
        };
        var set = new RoostTabSet();
        set.Seed(state);
        Assert.Equal(["t3"], set.Tabs.Select(t => t.Id));
        Assert.Equal("t4", set.AddTab()!.Id);
    }

    [Fact]
    public void TokensRoundTripAndRejectWhatIsMalformed()
    {
        Assert.Equal("12/abc", RoostToken.Format("12", "abc"));
        Assert.Equal(("12", "abc"), RoostToken.Parse("12/abc"));
        Assert.Equal(("12", "a/b"), RoostToken.Parse("12/a/b"));   // a session id is everything after the first slash
        Assert.Null(RoostToken.Parse(null));
        Assert.Null(RoostToken.Parse("12"));
        Assert.Null(RoostToken.Parse("/abc"));
        Assert.Null(RoostToken.Parse("12/"));
    }
}
