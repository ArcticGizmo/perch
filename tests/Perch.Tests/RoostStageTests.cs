using Perch.Data;
using Perch.Data.Roost;
using Xunit;

namespace Perch.Tests;

/// <summary>
/// Guards <see cref="RoostStage"/>: free places fill most-urgent-first at the user's order position; a full stage
/// only admits a strictly more urgent pane, into the weakest pane's cell; focused / typed-in / pinned panes are
/// never bumped automatically; typing holds every change but departures; and Bring / Place for the rail and drags.
/// </summary>
public class RoostStageTests
{
    private static readonly DateTime T0 = new(2026, 9, 29, 9, 0, 0);

    // "a:w" = key a, Working. n = needs you, d = done, w = working, q = quiet, x = ended; "a:w!" = pinned.
    private static List<RoostPane> P(string spec) => spec.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(t =>
    {
        var parts = t.Split(':');
        var code = parts[1].TrimEnd('!');
        var status = code switch
        {
            "n" => SessionStatus.AwaitingInput,
            "d" => SessionStatus.NeedsAttention,
            "w" => SessionStatus.Running,
            _ => SessionStatus.Idle,
        };
        var session = new ClaudeSession(parts[0], $"sess-{parts[0]}", status, @"C:\fixtures\proj", parts[0], T0);
        return new RoostPane(parts[0], session, RoostRoster.GroupFor(status, code == "x"),
            parts[1].EndsWith('!') ? RoostPin.Expanded : RoostPin.Auto, code == "x" ? T0 : null);
    }).ToList();

    private static string Slots(RoostStage s) => string.Join(" ", s.Slots);

    private static readonly RoostStageGuard None = new(null, null);

    [Fact]
    public void EverythingFitsInOrder()
    {
        var s = new RoostStage();
        s.Sync(P("a:q b:w c:n"), None, hold: false);
        Assert.Equal("a b c", Slots(s));
    }

    [Fact]
    public void OverCapacityTakesTheMostUrgentAndKeepsTheirOrder()
    {
        var s = new RoostStage();
        s.Sync(P("a:q b:w c:w d:n e:q f:w g:d h:w"), None, hold: false);
        // 6 places: n, d, then the four working (b c f h) — the quiet a and e wait.
        Assert.Equal("b c d f g h", Slots(s));
    }

    [Fact]
    public void ANewcomerWaitsUnlessItOutranksTheWeakest()
    {
        var s = new RoostStage();
        var six = "a:w b:w c:w d:w e:w f:w";
        s.Sync(P(six), None, hold: false);
        s.Sync(P(six + " g:w"), None, hold: false);
        Assert.False(s.IsOn("g"));
        Assert.False(s.WasEverOn("g"));   // the rail's "new" marker

        // g needs the user: it takes the least recently viewed working pane's cell; nothing else moves.
        s.Viewed("a");
        s.Viewed("c"); s.Viewed("d"); s.Viewed("e"); s.Viewed("f");
        s.Sync(P(six + " g:n"), None, hold: false);
        Assert.Equal("a g c d e f", Slots(s));
    }

    [Fact]
    public void QuietAndEndedPanesGoFirst()
    {
        var s = new RoostStage();
        s.Sync(P("a:w b:q c:w d:x e:w f:w"), None, hold: false);
        s.Sync(P("a:w b:q c:w d:x e:w f:w g:w h:w"), None, hold: false);
        Assert.Equal("a h c g e f", Slots(s));   // ended d goes before quiet b; each newcomer takes that cell
    }

    [Fact]
    public void FocusedTypedInAndPinnedPanesAreNeverBumped()
    {
        var s = new RoostStage();
        var six = "a:q b:q! c:q d:w e:w f:w";
        s.Sync(P(six), None, hold: false);
        s.Sync(P(six + " g:n h:n i:n"), new RoostStageGuard(Focused: "a", TypingIn: null), hold: false);
        // a focused, b pinned, c the only other quiet pane: c goes, then the working panes by recency.
        Assert.True(s.IsOn("a"));
        Assert.True(s.IsOn("b"));
        Assert.False(s.IsOn("c"));
        Assert.Equal(["g", "h", "i"], s.Slots.Where(k => k is "g" or "h" or "i").Order());
    }

    [Fact]
    public void TypingHoldsArrivalsButNotDepartures()
    {
        var s = new RoostStage();
        s.Sync(P("a:w b:w c:w d:w e:w f:w"), None, hold: false);
        s.Sync(P("a:w b:w c:w d:w e:w g:n"), new RoostStageGuard(null, "a"), hold: true);
        Assert.Equal("a b c d e", Slots(s));   // f left; g waits until the typing stops
        s.Sync(P("a:w b:w c:w d:w e:w g:n"), None, hold: false);
        Assert.Equal("a b c d e g", Slots(s));
    }

    [Fact]
    public void BringTakesTheWeakestCellAndCountsAsAView()
    {
        var s = new RoostStage();
        var panes = P("a:n b:w c:w d:w e:w f:w g:q");
        s.Sync(panes, None, hold: false);
        Assert.False(s.IsOn("g"));
        s.Bring("g", panes, new RoostStageGuard(Focused: "b", TypingIn: null));
        Assert.True(s.IsOn("g"));
        Assert.True(s.IsOn("b"));      // focused, spared
        Assert.False(s.IsOn("c"));     // the least recently viewed working pane
        Assert.Equal(2, s.Slots.ToList().IndexOf("g"));
    }

    [Fact]
    public void BringWithEveryPaneProtectedStillSparesTheOneBeingTypedIn()
    {
        var s = new RoostStage();
        var panes = P("a:w! b:w! c:w! d:w! e:w! f:w! g:q");
        s.Sync(panes, None, hold: false);
        s.Bring("g", panes, new RoostStageGuard(null, TypingIn: "a"));
        Assert.True(s.IsOn("g"));
        Assert.True(s.IsOn("a"));
    }

    [Fact]
    public void PlaceSwapsOnStageAndReplacesFromTheRail()
    {
        var s = new RoostStage();
        var panes = P("a:w b:w c:w d:w e:w f:w g:q");
        s.Sync(panes, None, hold: false);

        Assert.Equal("d", s.Place("a", 3, panes));
        Assert.Equal("d b c a e f", Slots(s));

        Assert.Equal("b", s.Place("g", 1, panes));   // from the rail: b returns to it
        Assert.Equal("d g c a e f", Slots(s));
        Assert.False(s.IsOn("b"));

        Assert.Null(s.Place("d", 9, panes));          // past the end = move to the end
        Assert.Equal("g c a e f d", Slots(s));
    }

    [Fact]
    public void APaneTheUserChoseIsOnlyBumpedByOneThatNeedsThem()
    {
        var s = new RoostStage();
        var panes = P("a:w b:w c:w d:w e:w f:w g:q h:w");
        s.Sync(panes, None, hold: false);
        s.Place("g", 0, panes);                    // the user drags the quiet g onto a's cell
        s.Sync(panes, None, hold: false);          // focus moved on: the waiting working panes don't outrank it
        Assert.True(s.IsOn("g"));
        s.Sync(P("a:w b:w c:w d:w e:w f:w g:q h:n"), None, hold: false);
        Assert.True(s.IsOn("h"));                  // needs you: bumps the weakest, which g now counts among
    }

    [Fact]
    public void StrictIsTheMostUrgentInOrder() =>
        Assert.Equal(["b", "c", "d", "f", "g", "h"], RoostStage.Strict(P("a:q b:w c:w d:n e:q f:w g:d h:w")));
}
