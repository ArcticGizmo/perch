using Perch.Data;
using Perch.Data.Roost;
using Xunit;

namespace Perch.Tests;

/// <summary>
/// Guards <see cref="RoostRoster"/>: panes keep first-seen order through status flips, the rail regroups by
/// urgency, ended sessions linger then drop, closed keys hide and prune, and <see cref="RoostRoster.NextNeedingYou"/>
/// cycles Needs you → Done and wraps.
/// </summary>
public class RoostRosterTests
{
    private static readonly DateTime T0 = new(2026, 9, 25, 9, 0, 0);

    private static ClaudeSession S(string pid, SessionStatus status = SessionStatus.Running,
        string? sessionId = null, DateTime? awaitingSince = null, DateTime? lastUpdated = null) =>
        new(pid, sessionId ?? $"sess-{pid}", status, @"C:\fixtures\proj", $"proj-{pid}", lastUpdated ?? T0,
            AwaitingSince: awaitingSince);

    private static string[] Keys(IEnumerable<RoostPane> panes) => panes.Select(p => p.Key).ToArray();

    private static RoostRailGroup Group(RoostRoster r, RoostGroup g) => r.Rail.Single(x => x.Group == g);

    [Fact]
    public void PanesKeepFirstSeenOrderThroughStatusFlips()
    {
        var r = new RoostRoster();
        r.Update([S("1"), S("2"), S("3")], T0);
        Assert.Equal(["1", "2", "3"], Keys(r.Panes));

        // "3" becomes the most urgent, "1" goes quiet: pane order is untouched, only the rail moves.
        r.Update([S("1", SessionStatus.Idle), S("2"), S("3", SessionStatus.AwaitingInput, awaitingSince: T0)], T0);
        Assert.Equal(["1", "2", "3"], Keys(r.Panes));
        Assert.Equal(["3"], Keys(Group(r, RoostGroup.NeedsYou).Panes));
        Assert.Equal(["1"], Keys(Group(r, RoostGroup.Quiet).Panes));
    }

    [Fact]
    public void ScanOrderDoesNotReorderExistingPanes()
    {
        var r = new RoostRoster();
        r.Update([S("1"), S("2")], T0);
        r.Update([S("3"), S("2"), S("1")], T0);
        Assert.Equal(["1", "2", "3"], Keys(r.Panes));
    }

    [Fact]
    public void SessionIdChangeUnderSameProcessKeepsThePane()
    {
        var r = new RoostRoster();
        r.Update([S("1", sessionId: "a"), S("2")], T0);
        r.Update([S("1", sessionId: "b"), S("2")], T0);   // /clear
        Assert.Equal(["1", "2"], Keys(r.Panes));
        Assert.Equal("b", r.Panes[0].Session.SessionId);
    }

    [Fact]
    public void RailGroupsEveryStatus()
    {
        var r = new RoostRoster();
        r.Update([
            S("idle", SessionStatus.Idle),
            S("run", SessionStatus.Running),
            S("done", SessionStatus.NeedsAttention),
            S("wait", SessionStatus.AwaitingInput, awaitingSince: T0),
            S("err", SessionStatus.ApiError),
        ], T0);

        Assert.Equal([RoostGroup.NeedsYou, RoostGroup.DoneReview, RoostGroup.Working, RoostGroup.Quiet],
            r.Rail.Select(g => g.Group));
        Assert.Equal(new RoostCounts(NeedsYou: 2, DoneReview: 1, Working: 1, Quiet: 1), r.Counts);
        Assert.Equal(["done"], Keys(Group(r, RoostGroup.DoneReview).Panes));
        Assert.Equal(["run"], Keys(Group(r, RoostGroup.Working).Panes));
    }

    [Fact]
    public void NeedsYouLeadsWithTheLongestWaiting()
    {
        var r = new RoostRoster();
        r.Update([
            S("recent", SessionStatus.AwaitingInput, awaitingSince: T0.AddMinutes(5)),
            S("err", SessionStatus.ApiError, lastUpdated: T0.AddMinutes(2)),
            S("oldest", SessionStatus.AwaitingInput, awaitingSince: T0),
        ], T0.AddMinutes(6));
        Assert.Equal(["oldest", "err", "recent"], Keys(Group(r, RoostGroup.NeedsYou).Panes));
        Assert.Equal(["recent", "err", "oldest"], Keys(r.Panes));   // panes still first-seen
    }

    [Fact]
    public void EndedSessionLingersGreyedThenDrops()
    {
        var r = new RoostRoster();
        r.Update([S("1"), S("2", SessionStatus.AwaitingInput, awaitingSince: T0)], T0);

        r.Update([S("1")], T0.AddMinutes(1));
        var ended = r.Find("2")!;
        Assert.True(ended.Ended);
        Assert.Equal(T0.AddMinutes(1), ended.EndedAt);
        Assert.Equal(RoostGroup.Quiet, ended.Group);                 // no longer "needs you"
        Assert.Equal(0, r.Counts.NeedsYou);
        Assert.Equal(["1", "2"], Keys(r.Panes));                    // still in place

        r.Update([S("1")], T0.AddMinutes(10).AddSeconds(59));        // 9m59s after ending
        Assert.NotNull(r.Find("2"));

        r.Update([S("1")], T0.AddMinutes(11));                       // 10m after ending
        Assert.Null(r.Find("2"));
        Assert.Equal(["1"], Keys(r.Panes));
    }

    [Fact]
    public void EndedPanesSortLastInQuiet()
    {
        var r = new RoostRoster();
        r.Update([S("gone", SessionStatus.Idle), S("idle", SessionStatus.Idle)], T0);
        r.Update([S("idle", SessionStatus.Idle)], T0);
        Assert.Equal(["idle", "gone"], Keys(Group(r, RoostGroup.Quiet).Panes));
    }

    [Fact]
    public void SessionReturningWhileLingeringComesBackInPlace()
    {
        var r = new RoostRoster();
        r.Update([S("1"), S("2")], T0);
        r.Update([S("2")], T0.AddMinutes(1));
        r.Update([S("1"), S("2"), S("3")], T0.AddMinutes(2));
        Assert.Equal(["1", "2", "3"], Keys(r.Panes));
        Assert.False(r.Find("1")!.Ended);
    }

    [Fact]
    public void NewSessionsAppendAfterLingeringOnes()
    {
        var r = new RoostRoster();
        r.Update([S("1"), S("2")], T0);
        r.Update([S("2"), S("3")], T0.AddMinutes(1));
        Assert.Equal(["1", "2", "3"], Keys(r.Panes));
    }

    [Fact]
    public void ClosedPaneIsHiddenAndReopenable()
    {
        var r = new RoostRoster();
        r.Update([S("1"), S("2")], T0);
        Assert.True(r.Close("1"));
        Assert.False(r.Close("1"));                 // already closed: no change to persist
        Assert.Equal(["2"], Keys(r.Panes));
        Assert.Equal(["1"], r.ClosedKeys);

        r.Update([S("1"), S("2")], T0);            // stays closed across rescans
        Assert.Equal(["2"], Keys(r.Panes));

        Assert.True(r.Reopen("1"));
        Assert.Equal(["1", "2"], Keys(r.Panes));   // back in its original slot
        Assert.Empty(r.ClosedKeys);
    }

    [Fact]
    public void ClosedPaneDropsAtOnceWhenItsSessionEnds()
    {
        var r = new RoostRoster();
        r.Update([S("1"), S("2")], T0);
        r.Close("1");
        r.Update([S("2")], T0.AddMinutes(1));
        Assert.Empty(r.ClosedKeys);                // pruned — a new process can never be pre-closed

        r.Update([S("1"), S("2")], T0.AddMinutes(2));   // same key again (pid reuse) = a fresh pane
        Assert.Equal(["2", "1"], Keys(r.Panes));
    }

    [Fact]
    public void PersistedClosedKeysPruneToLiveSessions()
    {
        var r = new RoostRoster(["1", "stale"]);
        r.Update([S("1"), S("2")], T0);
        Assert.Equal(["2"], Keys(r.Panes));
        Assert.Equal(["1"], r.ClosedKeys);
    }

    [Fact]
    public void CloseOfUnknownKeyIsANoOp()
    {
        var r = new RoostRoster();
        r.Update([S("1")], T0);
        Assert.False(r.Close("nope"));
        Assert.Empty(r.ClosedKeys);
    }

    [Fact]
    public void PinsSurviveRescansAndClearWithAuto()
    {
        var r = new RoostRoster();
        r.Update([S("1")], T0);
        r.SetPin("1", RoostPin.Collapsed);
        r.Update([S("1", SessionStatus.AwaitingInput, awaitingSince: T0)], T0);
        Assert.Equal(RoostPin.Collapsed, r.Find("1")!.Pin);

        r.SetPin("1", RoostPin.Auto);
        Assert.Equal(RoostPin.Auto, r.Find("1")!.Pin);
    }

    [Fact]
    public void NextNeedingYouCyclesNeedsYouThenDoneAndWraps()
    {
        var r = new RoostRoster();
        r.Update([
            S("run"),
            S("done", SessionStatus.NeedsAttention),
            S("wait2", SessionStatus.AwaitingInput, awaitingSince: T0.AddMinutes(1)),
            S("wait1", SessionStatus.AwaitingInput, awaitingSince: T0),
        ], T0.AddMinutes(2));

        Assert.Equal("wait1", r.NextNeedingYou(null));
        Assert.Equal("wait2", r.NextNeedingYou("wait1"));
        Assert.Equal("done", r.NextNeedingYou("wait2"));
        Assert.Equal("wait1", r.NextNeedingYou("done"));   // wraps
        Assert.Equal("wait1", r.NextNeedingYou("run"));    // not a candidate → first
    }

    [Fact]
    public void NextNeedingYouIsNullWhenNothingWantsYou()
    {
        var r = new RoostRoster();
        Assert.Null(r.NextNeedingYou(null));
        r.Update([S("1"), S("2", SessionStatus.Idle)], T0);
        Assert.Null(r.NextNeedingYou("1"));
    }

    [Fact]
    public void TakeOverResumesIntoTheEndedPanesSlot()
    {
        // "Take over in Perch": the terminal process (pid 2) stops and Perch resumes the same session under pid 9.
        var r = new RoostRoster();
        r.Update([S("1"), S("2", sessionId: "conv"), S("3")], T0);
        r.SetPin("2", RoostPin.Expanded);

        r.Update([S("1"), S("3"), S("9", sessionId: "conv")], T0.AddSeconds(5));
        Assert.Equal(["1", "9", "3"], Keys(r.Panes));          // in place, not appended
        Assert.False(r.Find("9")!.Ended);
        Assert.Equal(RoostPin.Expanded, r.Find("9")!.Pin);    // the pin carries over
        Assert.Null(r.Find("2"));
    }

    [Fact]
    public void BothProcessesBrieflyLiveThenMergeWhenTheOldOneEnds()
    {
        var r = new RoostRoster();
        r.Update([S("2", sessionId: "conv"), S("3")], T0);
        r.Update([S("2", sessionId: "conv"), S("3"), S("9", sessionId: "conv")], T0);   // overlap scan
        Assert.Equal(["2", "3", "9"], Keys(r.Panes));
        r.Update([S("3"), S("9", sessionId: "conv")], T0.AddSeconds(2));
        Assert.Equal(["9", "3"], Keys(r.Panes));
    }

    [Fact]
    public void AContinuationKeepsItsOwnPin()
    {
        var r = new RoostRoster();
        r.Update([S("2", sessionId: "conv"), S("9", sessionId: "conv")], T0);
        r.SetPin("2", RoostPin.Expanded);
        r.SetPin("9", RoostPin.Collapsed);
        r.Update([S("9", sessionId: "conv")], T0.AddSeconds(1));
        Assert.Equal(RoostPin.Collapsed, r.Find("9")!.Pin);
    }

    [Fact]
    public void UnrelatedNewSessionsStillAppend()
    {
        var r = new RoostRoster();
        r.Update([S("1"), S("2")], T0);
        r.Update([S("1"), S("9")], T0.AddSeconds(1));   // pid 2 ended, pid 9 is a different conversation
        Assert.Equal(["1", "2", "9"], Keys(r.Panes));
        Assert.True(r.Find("2")!.Ended);
    }

    [Fact]
    public void NeedsYouArrivalsAreOnlyTheNewcomers()
    {
        var r = new RoostRoster();
        r.Update([S("1"), S("2", SessionStatus.AwaitingInput, awaitingSince: T0)], T0);
        Assert.Equal(["2"], r.NeedsYouArrivals);

        r.Update([S("1"), S("2", SessionStatus.AwaitingInput, awaitingSince: T0)], T0);
        Assert.Empty(r.NeedsYouArrivals);                        // still waiting — not a new arrival

        r.Update([S("1", SessionStatus.ApiError), S("2", SessionStatus.AwaitingInput, awaitingSince: T0)], T0);
        Assert.Equal(["1"], r.NeedsYouArrivals);

        r.Update([S("1"), S("2")], T0);                            // both resolved
        Assert.Empty(r.NeedsYouArrivals);
        r.Update([S("1"), S("2", SessionStatus.AwaitingInput, awaitingSince: T0)], T0);
        Assert.Equal(["2"], r.NeedsYouArrivals);                  // blocked again = a fresh arrival
    }

    [Fact]
    public void DuplicatePidInOneScanYieldsOnePane()
    {
        var r = new RoostRoster();
        r.Update([S("1"), S("1", SessionStatus.Idle)], T0);
        Assert.Single(r.Panes);
        Assert.Equal(SessionStatus.Running, r.Panes[0].Session.Status);   // first wins
    }
}
