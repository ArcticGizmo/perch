using System.Text.Json;
using Perch.Data;
using Perch.Data.Roost;
using Xunit;

namespace Perch.Tests;

/// <summary>
/// Guards the Roost's dormant panes (docs/session-recovery-plan.md, R6): the roster shows the sessions the app supplies
/// with no process in the Recent group, a region keeps its session from live to dormant and back (adoption both ways),
/// and a persisted cell comes back after a restart as the dormant pane of the same conversation.
/// </summary>
public class RoostDormantTests
{
    private static readonly DateTime T0 = new(2026, 10, 2, 9, 0, 0);

    private static ClaudeSession S(string pid, string sessionId) =>
        new(pid, sessionId, SessionStatus.Running, @"C:\fixtures\proj", $"proj-{pid}", T0);

    private static RoostDormant D(string sessionId, RoostDormantKind kind = RoostDormantKind.NotRunning, string? title = null) =>
        new(sessionId, @"C:\fixtures\proj", "proj", title, T0, kind);

    private static string[] Keys(IEnumerable<RoostPane> panes) => panes.Select(p => p.Key).ToArray();

    private static string Dk(string sessionId) => RoostToken.DormantKey(sessionId);

    private static RoostTabsState RoundTrip(RoostTabsState state) =>
        JsonSerializer.Deserialize<RoostTabsState>(JsonSerializer.Serialize(state))!;

    // ── Roster ────────────────────────────────────────────────────────────────

    [Fact]
    public void DormantPanesFillTheRecentGroupInTheAppsOrder()
    {
        var r = new RoostRoster();
        r.Update([S("1", "live")], T0, [D("b", title: "Bravo"), D("a", title: "Alpha")]);

        Assert.Equal(["1", Dk("b"), Dk("a")], Keys(r.Panes));
        var recent = r.Rail.Single(g => g.Group == RoostGroup.Recent);
        Assert.Equal([Dk("b"), Dk("a")], Keys(recent.Panes));
        Assert.All(recent.Panes, p => Assert.True(p.IsDormant && !p.Ended && !p.IsLive));
        Assert.Equal(SessionStatus.Idle, recent.Panes[0].Session.Status);
        // A–Z: live first, then dormant by name.
        Assert.Equal(["1", Dk("a"), Dk("b")], Keys(r.RailAlphabetical));

        // The app re-ranks (e.g. an interrupted one now sorts first): the group follows.
        r.Update([S("1", "live")], T0, [D("a"), D("b")]);
        Assert.Equal([Dk("a"), Dk("b")], Keys(r.Rail.Single(g => g.Group == RoostGroup.Recent).Panes));
    }

    [Fact]
    public void ALiveConversationIsNeverAlsoDormant()
    {
        var r = new RoostRoster();
        r.Update([S("1", "conv")], T0, [D("conv"), D("conv"), D("other")]);
        Assert.Equal(["1", Dk("other")], Keys(r.Panes));
    }

    [Fact]
    public void ADormantPaneTheAppStopsNamingGoesAtOnce()
    {
        var r = new RoostRoster();
        r.Update([], T0, [D("a")]);
        r.Update([], T0, []);
        Assert.Empty(r.Panes);
        Assert.Empty(r.Adopted);
    }

    [Fact]
    public void TheFirstSendsProcessTakesTheDormantPanesSlot()
    {
        var r = new RoostRoster();
        r.Update([S("1", "x")], T0, [D("conv"), D("y")]);
        Assert.Equal(["1", Dk("conv"), Dk("y")], Keys(r.Panes));

        // It woke: the app still names it until the scan sees the process, which must win either way.
        r.Update([S("1", "x"), S("9", "conv")], T0, [D("conv"), D("y")]);
        Assert.Equal(["1", "9", Dk("y")], Keys(r.Panes));
        Assert.Equal("9", r.Adopted[Dk("conv")]);
    }

    [Fact]
    public void AnEndedPaneBecomesDormantInItsPlace()
    {
        var r = new RoostRoster();
        r.Update([S("1", "x"), S("2", "conv"), S("3", "z")], T0);
        r.Update([S("1", "x"), S("3", "z")], T0.AddSeconds(5));
        Assert.True(r.Find("2")!.Ended);

        r.Update([S("1", "x"), S("3", "z")], T0.AddSeconds(10), [D("conv", RoostDormantKind.Ended)]);
        Assert.Equal(["1", Dk("conv"), "3"], Keys(r.Panes));
        Assert.Equal(Dk("conv"), r.Adopted["2"]);
        Assert.Equal(RoostDormantKind.Ended, r.Panes[1].Dormant!.Kind);
    }

    [Fact]
    public void ADormantPaneCantBeClosed()
    {
        var r = new RoostRoster();
        r.Update([], T0, [D("a")]);
        Assert.False(r.Close(Dk("a")));
        Assert.Empty(r.PersistedClosed);
    }

    // ── Tabs ──────────────────────────────────────────────────────────────────

    [Fact]
    public void ARegionKeepsItsSessionFromLiveToDormantAndBack()
    {
        var r = new RoostRoster();
        var set = new RoostTabSet();
        var tab = set.AddTab("A")!;
        r.Update([S("1", "conv")], T0);
        set.Sync(r.Panes, r.Adopted);
        set.Assign(tab.Id, 0, "1");

        // Perch closed (or the terminal did): the process goes, the app shows it dormant.
        r.Update([], T0.AddSeconds(5), [D("conv")]);
        set.Sync(r.Panes, r.Adopted);
        Assert.Equal(Dk("conv"), tab.At(0));
        Assert.Equal("~/conv", set.ToState().Tabs[0].Cells![0]);

        // The first send (or a --resume anywhere) starts a new process.
        r.Update([S("7", "conv")], T0.AddSeconds(9), [D("conv")]);
        set.Sync(r.Panes, r.Adopted);
        Assert.Equal("7", tab.At(0));
    }

    [Fact]
    public void AfterARestartACellComesBackAsTheDormantPaneOfItsConversation()
    {
        var r = new RoostRoster();
        var set = new RoostTabSet();
        var tab = set.AddTab("A", RoostGridLayout.FromTemplate(RoostSnapTemplate.Columns2))!;
        r.Update([S("1", "conv"), S("2", "moved")], T0);
        set.Sync(r.Panes);
        set.Assign(tab.Id, 0, "1");
        set.Assign(tab.Id, 1, "2");

        var back = new RoostTabSet();
        back.Seed(RoundTrip(set.ToState()));
        Assert.Equal(["conv", "moved"], back.SessionIds().Order());

        // Every pid is new. "moved" is live again under another process; "conv" is supplied dormant only later.
        var after = new RoostRoster();
        after.Update([S("1", "unrelated"), S("40", "moved")], T0);
        back.Sync(after.Panes, settleSeeds: false);
        Assert.Null(back.Tabs[0].At(0));          // the recycled pid 1 places nothing
        Assert.Equal("40", back.Tabs[0].At(1));
        Assert.Equal(["conv"], back.SessionIds().Except(["moved"]));   // still waiting, still named

        after.Update([S("1", "unrelated"), S("40", "moved")], T0, [D("conv")]);
        back.Sync(after.Panes, settleSeeds: true);
        Assert.Equal(Dk("conv"), back.Tabs[0].At(0));
    }

    [Fact]
    public void SettlingDropsCellsThatMatchNothing()
    {
        var set = new RoostTabSet();
        set.Seed(new RoostTabsState { Tabs = [new() { Id = "t1", Name = "A", Cells = new() { [0] = "~/gone" } }] });
        set.Sync([], settleSeeds: false);
        Assert.Equal("~/gone", set.ToState().Tabs[0].Cells![0]);
        set.Sync([], settleSeeds: true);
        Assert.Null(set.ToState().Tabs[0].Cells);
        Assert.Empty(set.SessionIds());
    }

    [Fact]
    public void TheFirstRunTabLeavesDormantPanesOut()
    {
        var r = new RoostRoster();
        r.Update([S("1", "x")], T0, [D("a")]);
        var set = new RoostTabSet();
        var main = set.CreateDefault(r.Panes, aspect: 1.6)!;
        Assert.Equal(["1"], main.Cells.Values);
    }

    [Fact]
    public void DormantTokensParseWithTheirMarker()
    {
        Assert.Equal("~/abc", RoostToken.ForPane(Dk("abc"), "abc"));
        Assert.Equal("12/abc", RoostToken.ForPane("12", "abc"));
        Assert.Equal(("~", "abc"), RoostToken.Parse("~/abc"));
        Assert.True(RoostToken.IsDormantKey(Dk("abc")));
        Assert.False(RoostToken.IsDormantKey("12"));
        Assert.False(RoostToken.IsDormantKey("~"));
    }
}
