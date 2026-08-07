using Perch.Data;
using Xunit;

namespace Perch.Tests;

/// <summary>
/// Covers <see cref="PlacementMath"/> — the corner-anchored geometry behind the initial-placement editor —
/// plus that an <see cref="OverlayPlacement"/> survives the JSON round-trip <see cref="AppSettings.Clone"/>
/// (and therefore save/load) intact. All coordinates are physical pixels; offsets are DIP.
/// </summary>
public class PlacementMathTests
{
    // A primary-ish work area (origin 0,0) and a left-of-primary secondary monitor with negative X,
    // to catch any code that confuses an offset for an absolute coordinate.
    private const int PrimW = 1920, PrimH = 1040; // 1080 screen minus a taskbar
    private const int PhysW = 280, PhysH = 400;

    [Theory]
    // Each corner of the primary work area, at a non-trivial inset, at three DPI scales.
    [InlineData(200, 150, 1.0)]
    [InlineData(200, 150, 1.5)]
    [InlineData(200, 150, 2.0)]
    [InlineData(1920 - 280 - 200, 150, 1.5)]        // near right edge
    [InlineData(200, 600, 1.5)]                     // lower half -> bottom (header) anchored
    [InlineData(1920 - 280 - 200, 600, 1.25)]       // bottom-right
    public void FromPosition_ThenToPosition_RoundTrips(int x, int y, double scale)
    {
        var placement = PlacementMath.FromPosition(x, y, 0, 0, PrimW, PrimH, scale, PhysW, PhysH);
        var (rx, ry) = PlacementMath.ToPosition(placement, 0, 0, PrimW, PrimH, scale, PhysW, PhysH);

        Assert.Equal(x, rx);
        Assert.Equal(y, ry);
    }

    [Fact]
    public void FromPosition_PicksNearestCorner()
    {
        // Top-left quadrant.
        var tl = PlacementMath.FromPosition(40, 30, 0, 0, PrimW, PrimH, 1.0, PhysW, PhysH);
        Assert.Equal(HAnchor.Left, tl.HAnchor);
        Assert.Equal(VAnchor.Top, tl.VAnchor);
        Assert.Equal(40, tl.OffsetX);
        Assert.Equal(30, tl.OffsetY);

        // Bottom-right quadrant: X measures the window's right edge from the right; Y measures the header
        // (top edge) from the bottom, independent of panel height.
        var brX = PrimW - PhysW - 60; // window right edge 60 DIP from the right edge (scale 1.0)
        var brY = PrimH - 425;        // header 425 DIP above the bottom edge -> bottom half, anchors bottom
        var br = PlacementMath.FromPosition(brX, brY, 0, 0, PrimW, PrimH, 1.0, PhysW, PhysH);
        Assert.Equal(HAnchor.Right, br.HAnchor);
        Assert.Equal(VAnchor.Bottom, br.VAnchor);
        Assert.Equal(60, br.OffsetX);
        Assert.Equal(425, br.OffsetY);
    }

    [Fact]
    public void ToPosition_ScalesOffsetByDpi()
    {
        // 16 DIP from the top-left, at 1.5× DPI, is 24 physical px from the work-area origin.
        var p = new OverlayPlacement { HAnchor = HAnchor.Left, VAnchor = VAnchor.Top, OffsetX = 16, OffsetY = 16 };
        var (x, y) = PlacementMath.ToPosition(p, 0, 0, PrimW, PrimH, 1.5, PhysW, PhysH);
        Assert.Equal(24, x);
        Assert.Equal(24, y);
    }

    [Fact]
    public void ToPosition_RightBottomAnchorsMeasureFromFarEdges()
    {
        // X: window right edge 16 DIP from the right. Y: header (top edge) 432 DIP above the bottom edge —
        // with a 400px panel that leaves the window's bottom 32 px above the work-area bottom.
        var p = new OverlayPlacement { HAnchor = HAnchor.Right, VAnchor = VAnchor.Bottom, OffsetX = 16, OffsetY = 432 };
        var (x, y) = PlacementMath.ToPosition(p, 0, 0, PrimW, PrimH, 1.0, PhysW, PhysH);
        Assert.Equal(PrimW - PhysW - 16, x);
        Assert.Equal(PrimH - PhysH - 32, y);
    }

    [Fact]
    public void ToPosition_BottomAnchor_HeaderYIndependentOfPanelHeight()
    {
        // The point of anchoring the header: a bottom-anchored overlay keeps the same top-edge Y no matter
        // how tall the panel is (2 sessions vs 5), as long as it still fits on screen.
        var p = new OverlayPlacement { HAnchor = HAnchor.Right, VAnchor = VAnchor.Bottom, OffsetY = 300 };
        var (_, yShort) = PlacementMath.ToPosition(p, 0, 0, PrimW, PrimH, 1.0, PhysW, 120);
        var (_, yTall)  = PlacementMath.ToPosition(p, 0, 0, PrimW, PrimH, 1.0, PhysW, 260);
        Assert.Equal(PrimH - 300, yShort); // 740: header 300 above the bottom edge
        Assert.Equal(yShort, yTall);       // header didn't move when the panel grew
    }

    [Fact]
    public void ToPosition_BottomAnchor_TooTallPanelClampsBottomFlush()
    {
        // A panel too tall to sit at the requested header spot is clamped fully on-screen (bottom-flush),
        // keeping the header reachable instead of hanging off the bottom.
        var p = new OverlayPlacement { HAnchor = HAnchor.Right, VAnchor = VAnchor.Bottom, OffsetY = 100 };
        var (_, y) = PlacementMath.ToPosition(p, 0, 0, PrimW, PrimH, 1.0, PhysW, PhysH); // PhysH = 400
        // Header 100 above the bottom would push the bottom 300px off-screen; clamp pulls it up.
        Assert.Equal(PrimH - PhysH, y); // 640
    }

    [Fact]
    public void RoundTrip_OnSecondaryMonitorWithNegativeOrigin()
    {
        // A monitor to the left of primary: work-area origin is negative.
        const int waX = -1920, waY = 0, waW = 1920, waH = 1080;
        const int x = -1920 + 300, y = 500;

        var p = PlacementMath.FromPosition(x, y, waX, waY, waW, waH, 1.0, PhysW, PhysH);
        var (rx, ry) = PlacementMath.ToPosition(p, waX, waY, waW, waH, 1.0, PhysW, PhysH);
        Assert.Equal(x, rx);
        Assert.Equal(y, ry);
    }

    [Fact]
    public void Clamp_KeepsWindowInsideWorkArea()
    {
        // Way off the right / bottom → pinned flush against the far edges.
        var (x, y) = PlacementMath.Clamp(9999, 9999, 0, 0, PrimW, PrimH, PhysW, PhysH);
        Assert.Equal(PrimW - PhysW, x);
        Assert.Equal(PrimH - PhysH, y);

        // Negative → pinned to the origin.
        (x, y) = PlacementMath.Clamp(-500, -500, 0, 0, PrimW, PrimH, PhysW, PhysH);
        Assert.Equal(0, x);
        Assert.Equal(0, y);
    }

    [Fact]
    public void Clamp_WindowLargerThanWorkArea_PinsToOrigin()
    {
        // A window wider/taller than the work area can't fit; it lands flush at the top-left.
        var (x, y) = PlacementMath.Clamp(50, 50, 100, 100, 200, 200, 400, 400);
        Assert.Equal(100, x);
        Assert.Equal(100, y);
    }

    [Fact]
    public void ToPosition_ClampsAnOverlargeOffsetBackOnScreen()
    {
        // An offset that would push the window off the bottom is clamped, not honoured literally.
        var p = new OverlayPlacement { HAnchor = HAnchor.Left, VAnchor = VAnchor.Top, OffsetX = 0, OffsetY = 100000 };
        var (_, y) = PlacementMath.ToPosition(p, 0, 0, PrimW, PrimH, 1.0, PhysW, PhysH);
        Assert.Equal(PrimH - PhysH, y);
    }

    [Fact]
    public void PickMostOverlapping_ChoosesTheScreenTheWindowSitsOnMost()
    {
        // Two side-by-side 1920-wide monitors; the window straddles the seam but sits mostly on #1.
        var screens = new (int, int, int, int)[] { (0, 0, 1920, 1080), (1920, 0, 1920, 1080) };
        Assert.Equal(1, PlacementMath.PickMostOverlapping(screens, 1900, 100, 280, 400));
        Assert.Equal(0, PlacementMath.PickMostOverlapping(screens, 1700, 100, 280, 400));
    }

    [Fact]
    public void PickMostOverlapping_ResolutionChangeStillResolvesSameMonitor()
    {
        // The same physical monitor after it shrank 1920x1080 -> 1280x720: exact bounds no longer match,
        // but the window (still parked near the old top-right) overlaps it and nothing else.
        var shrunk = new (int, int, int, int)[] { (0, 0, 1280, 720) };
        Assert.Equal(0, PlacementMath.PickMostOverlapping(shrunk, 1000, 40, 280, 400));
    }

    [Fact]
    public void PickMostOverlapping_StrandedOffEveryScreen_FallsBackToNearest()
    {
        // Window way off to the right of every monitor (its home was unplugged): nearest wins, not -1.
        var screens = new (int, int, int, int)[] { (0, 0, 1920, 1080), (-1920, 0, 1920, 1080) };
        Assert.Equal(0, PlacementMath.PickMostOverlapping(screens, 5000, 100, 280, 400));
        Assert.Equal(-1, PlacementMath.PickMostOverlapping(System.Array.Empty<(int, int, int, int)>(), 0, 0, 1, 1));
    }

    [Fact]
    public void CornerRelative_SurvivesLargeToSmallToLarge()
    {
        // The reported bug, at the geometry layer: a panel 16 DIP in from the top-right of a large monitor.
        const int bigW = 3840, bigH = 2120, smallW = 1280, smallH = 680;
        var placement = PlacementMath.FromPosition(
            bigW - PhysW - 16, 24, 0, 0, bigW, bigH, 1.0, PhysW, PhysH);

        // On the small monitor the same corner distance is honoured (it fits: 16 px in from the right).
        var (sx, _) = PlacementMath.ToPosition(placement, 0, 0, smallW, smallH, 1.0, PhysW, PhysH);
        Assert.Equal(smallW - PhysW - 16, sx);

        // Back on the large monitor it returns to exactly the original spot — not stranded toward centre.
        var (bx, by) = PlacementMath.ToPosition(placement, 0, 0, bigW, bigH, 1.0, PhysW, PhysH);
        Assert.Equal(bigW - PhysW - 16, bx);
        Assert.Equal(24, by);
    }

    [Fact]
    public void OverlayPlacement_SurvivesSettingsCloneRoundTrip()
    {
        var settings = new AppSettings
        {
            FloatingPlacement = new OverlayPlacement
            {
                MonitorX = -1920, MonitorY = 0, MonitorW = 1920, MonitorH = 1080,
                HAnchor = HAnchor.Left, VAnchor = VAnchor.Bottom, OffsetX = 12.5, OffsetY = 48,
            },
            DensePlacement = new OverlayPlacement
            {
                HAnchor = HAnchor.Right, VAnchor = VAnchor.Top, OffsetY = 64,
            },
        };

        var clone = settings.Clone();

        Assert.NotNull(clone.FloatingPlacement);
        Assert.Equal(-1920, clone.FloatingPlacement!.MonitorX);
        Assert.Equal(1920, clone.FloatingPlacement.MonitorW);
        Assert.Equal(HAnchor.Left, clone.FloatingPlacement.HAnchor);
        Assert.Equal(VAnchor.Bottom, clone.FloatingPlacement.VAnchor);
        Assert.Equal(12.5, clone.FloatingPlacement.OffsetX);
        Assert.Equal(48, clone.FloatingPlacement.OffsetY);

        Assert.NotNull(clone.DensePlacement);
        Assert.Null(clone.DensePlacement!.MonitorX);
        Assert.Equal(HAnchor.Right, clone.DensePlacement.HAnchor);
        Assert.Equal(64, clone.DensePlacement.OffsetY);
    }
}
