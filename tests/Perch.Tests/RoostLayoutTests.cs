using Perch.Data.Roost;
using Xunit;

namespace Perch.Tests;

/// <summary>Guards <see cref="RoostLayout"/>'s Main + stack / Zoom picks.</summary>
public class RoostLayoutTests
{
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
