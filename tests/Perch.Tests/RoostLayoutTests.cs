using Perch.Data.Roost;
using Xunit;
using static Perch.Data.Roost.RoostPaneSize;

namespace Perch.Tests;

/// <summary>
/// Guards <see cref="RoostLayout"/>: the collapse resolver's truth table (pin → status → typing hold →
/// focused-never-collapses), Tiled cell packing (order preserved, expanded panes own a cell, minis stack to
/// capacity), the viewport overflow pill, scroll-to-reveal, and the Main + stack / Zoom picks.
/// </summary>
public class RoostLayoutTests
{
    private static RoostSizeDecision Resolve(RoostGroup group, RoostPaneSize? current = null,
        RoostPin pin = RoostPin.Auto, bool ended = false, bool focused = false, bool typing = false) =>
        RoostLayout.ResolveSize(new RoostSizeInputs(pin, group, ended, focused, typing, current));

    // ── Collapse resolver ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData(RoostGroup.NeedsYou, Expanded)]
    [InlineData(RoostGroup.DoneReview, Expanded)]
    [InlineData(RoostGroup.Working, Collapsed)]
    [InlineData(RoostGroup.Quiet, Collapsed)]
    public void StatusDecidesTheDefault(RoostGroup group, RoostPaneSize expected)
    {
        Assert.Equal(new RoostSizeDecision(expected, false), Resolve(group));
        Assert.Equal(new RoostSizeDecision(expected, false), Resolve(group, current: Collapsed));
        Assert.Equal(expected, Resolve(group, current: Expanded).Size);
    }

    [Fact]
    public void EndedPanesCollapseWhateverTheirLastStatus()
    {
        Assert.Equal(Collapsed, Resolve(RoostGroup.NeedsYou, ended: true).Size);
        Assert.Equal(Collapsed, Resolve(RoostGroup.DoneReview, current: Expanded, ended: true).Size);
    }

    [Theory]
    [InlineData(RoostGroup.NeedsYou)]
    [InlineData(RoostGroup.Working)]
    public void PinWinsOverEverything(RoostGroup group)
    {
        Assert.Equal(new RoostSizeDecision(Collapsed, false),
            Resolve(group, current: Expanded, pin: RoostPin.Collapsed, focused: true));
        Assert.Equal(new RoostSizeDecision(Expanded, false),
            Resolve(group, current: Collapsed, pin: RoostPin.Expanded, typing: true, ended: true));
    }

    [Fact]
    public void AutoExpandIsHeldWhileTypingElsewhere()
    {
        Assert.Equal(new RoostSizeDecision(Collapsed, true), Resolve(RoostGroup.NeedsYou, current: Collapsed, typing: true));
        Assert.Equal(new RoostSizeDecision(Expanded, false), Resolve(RoostGroup.NeedsYou, current: Collapsed, typing: false));
    }

    [Fact]
    public void TypingHoldNeverAppliesToANewPaneOrAnAlreadyExpandedOne()
    {
        Assert.Equal(new RoostSizeDecision(Expanded, false), Resolve(RoostGroup.NeedsYou, current: null, typing: true));
        Assert.Equal(new RoostSizeDecision(Expanded, false), Resolve(RoostGroup.NeedsYou, current: Expanded, typing: true));
    }

    [Fact]
    public void TypingHoldNeverCollapsesAPane()
    {
        // Typing only ever holds an expand back; a pane that should collapse still does (unless focused).
        Assert.Equal(new RoostSizeDecision(Collapsed, false), Resolve(RoostGroup.Working, current: Expanded, typing: true));
    }

    [Fact]
    public void FocusedPaneNeverAutoCollapses()
    {
        Assert.Equal(Expanded, Resolve(RoostGroup.Quiet, current: Expanded, focused: true).Size);
        Assert.Equal(Collapsed, Resolve(RoostGroup.Quiet, current: Expanded, focused: false).Size);
        // …but focus alone doesn't expand a collapsed pane.
        Assert.Equal(Collapsed, Resolve(RoostGroup.Working, current: Collapsed, focused: true).Size);
    }

    // ── Packing ───────────────────────────────────────────────────────────────────

    private static (string, RoostPaneSize)[] P(string spec) =>
        // "A b c D" → uppercase = expanded, lowercase = collapsed
        spec.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(k => (k, char.IsUpper(k[0]) ? Expanded : Collapsed)).ToArray();

    private static string Shape(IReadOnlyList<RoostCell> cells) =>
        string.Join(" | ", cells.Select(c => (c.Expanded ? "E:" : "") + string.Join(",", c.Keys)));

    [Fact]
    public void AllCollapsedFillCellsToCapacity()
    {
        Assert.Equal("a,b,c | d,e,f | g", Shape(RoostLayout.Pack(P("a b c d e f g"), 3)));
        Assert.Equal("a,b,c,d | e,f,g", Shape(RoostLayout.Pack(P("a b c d e f g"), 4)));
    }

    [Fact]
    public void AllExpandedTakeACellEach()
    {
        Assert.Equal("E:A | E:B | E:C", Shape(RoostLayout.Pack(P("A B C"), 3)));
    }

    [Fact]
    public void ExpandMidCellStartsAFreshCellAndPreservesOrder()
    {
        Assert.Equal("a | E:B | c,d", Shape(RoostLayout.Pack(P("a B c d"), 3)));
        Assert.Equal("a,b,c | E:D | e", Shape(RoostLayout.Pack(P("a b c D e"), 3)));
    }

    [Fact]
    public void ExpandingOnePaneOnlyMovesThePanesAfterIt()
    {
        var before = RoostLayout.Pack(P("a b c d e"), 3);
        var after = RoostLayout.Pack(P("a b C d e"), 3);
        Assert.Equal("a,b,c | d,e", Shape(before));
        Assert.Equal("a,b | E:C | d,e", Shape(after));
        Assert.Equal(before.SelectMany(c => c.Keys), after.SelectMany(c => c.Keys),   // order never changes
            StringComparer.OrdinalIgnoreCase);                                          // (spec case = size)
    }

    [Fact]
    public void EmptyAndDegenerateCapacity()
    {
        Assert.Empty(RoostLayout.Pack([], 3));
        Assert.Equal("a | b", Shape(RoostLayout.Pack(P("a b"), 0)));   // clamped to 1
    }

    [Theory]
    [InlineData(300, 64, 8, 4)]   // (300+8)/(64+8) = 4.27
    [InlineData(200, 64, 8, 2)]
    [InlineData(40, 64, 8, 1)]    // never fewer than one
    [InlineData(300, 0, 8, 1)]    // unmeasured card
    public void CellCapacityFitsWholeCards(double cell, double card, double gap, int expected) =>
        Assert.Equal(expected, RoostLayout.CellCapacity(cell, card, gap));

    // ── Viewport ──────────────────────────────────────────────────────────────────

    [Fact]
    public void OverflowCountsPanesOutsideTheTwoByTwoViewport()
    {
        // 6 cells = 3 rows. Row 0: [a,b] [C]  Row 1: [D] [e]  Row 2: [F] [g,h]
        var cells = RoostLayout.Pack(P("a b C D e F g h"), 2);
        Assert.Equal(6, cells.Count);
        var needsYou = new HashSet<string> { "C", "F" };

        var top = RoostLayout.Overflow(cells, 0, needsYou.Contains);
        Assert.Equal(new RoostOverflow(0, 0, 3, 1), top);   // F, g, h below; F needs you
        Assert.True(top.Any);

        var bottom = RoostLayout.Overflow(cells, 1, needsYou.Contains);
        Assert.Equal(new RoostOverflow(3, 1, 0, 0), bottom); // a, b, C above; C needs you
    }

    [Fact]
    public void OverflowClampsAnOutOfRangeScroll()
    {
        var cells = RoostLayout.Pack(P("A B"), 3);
        Assert.False(RoostLayout.Overflow(cells, 5, _ => false).Any);
    }

    [Fact]
    public void ScrollToRevealMovesTheMinimum()
    {
        // 8 cells = 4 rows; viewport shows 2 rows.
        Assert.Equal(0, RoostLayout.ScrollToReveal(3, 0, 8));   // row 1 — already visible
        Assert.Equal(1, RoostLayout.ScrollToReveal(4, 0, 8));   // row 2 → scroll down one
        Assert.Equal(2, RoostLayout.ScrollToReveal(7, 0, 8));   // row 3 → last page
        Assert.Equal(0, RoostLayout.ScrollToReveal(1, 2, 8));   // row 0 → scroll up to it
        Assert.Equal(2, RoostLayout.ScrollToReveal(-1, 9, 8));  // unknown cell → just clamp
        Assert.Equal(0, RoostLayout.ScrollToReveal(2, 1, 3));   // short grid never scrolls
    }

    [Fact]
    public void CellOfFindsMiniCardsInsideAStack()
    {
        var cells = RoostLayout.Pack(P("a b C d"), 3);
        Assert.Equal(0, RoostLayout.CellOf(cells, "b"));
        Assert.Equal(1, RoostLayout.CellOf(cells, "C"));
        Assert.Equal(-1, RoostLayout.CellOf(cells, "zz"));
    }

    // ── Main + stack / Zoom ───────────────────────────────────────────────────────

    [Fact]
    public void MainStackPutsTheFocusedPaneMainAndKeepsTheRestInOrder()
    {
        var (main, stack) = RoostLayout.MainStack(["a", "b", "c"], "b");
        Assert.Equal("b", main);
        Assert.Equal(["a", "c"], stack);

        (main, stack) = RoostLayout.MainStack(["a", "b", "c"], "gone");
        Assert.Equal("a", main);
        Assert.Equal(["b", "c"], stack);

        (main, stack) = RoostLayout.MainStack([], "a");
        Assert.Null(main);
        Assert.Empty(stack);
    }

    [Fact]
    public void ZoomTargetsTheFocusedPaneElseTheFirst()
    {
        Assert.Equal("b", RoostLayout.ZoomTarget(["a", "b"], "b"));
        Assert.Equal("a", RoostLayout.ZoomTarget(["a", "b"], null));
        Assert.Null(RoostLayout.ZoomTarget([], null));
    }
}
