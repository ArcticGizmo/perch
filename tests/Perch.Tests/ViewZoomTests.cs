using Perch.Data;
using Xunit;

namespace Perch.Tests;

public class ViewZoomTests
{
    [Theory]
    [InlineData(1.0, +1, 1.1)]
    [InlineData(1.1, +1, 1.25)]
    [InlineData(1.0, -1, 0.9)]
    [InlineData(0.9, +1, 1.0)]
    [InlineData(1.05, +1, 1.1)]    // between steps: to the neighbour in that direction
    [InlineData(1.05, -1, 1.0)]
    [InlineData(2.0, +1, 2.0)]     // pinned at the ends
    [InlineData(0.75, -1, 0.75)]
    public void Step_MovesAlongTheLadder(double current, int direction, double expected) =>
        Assert.Equal(expected, ViewZoom.Step(current, direction), 3);

    [Fact]
    public void Step_ToleratesFloatNoiseOnAStep() =>
        Assert.Equal(1.25, ViewZoom.Step(1.1000000001, +1), 3);

    [Theory]
    [InlineData(double.NaN, 1.0)]
    [InlineData(double.PositiveInfinity, 1.0)]
    [InlineData(0, 1.0)]
    [InlineData(-2, 1.0)]
    [InlineData(5, 2.0)]
    [InlineData(0.1, 0.75)]
    [InlineData(1.3, 1.3)]
    public void Normalize_FallsBackOrClamps(double zoom, double expected) =>
        Assert.Equal(expected, ViewZoom.Normalize(zoom), 3);

    [Theory]
    [InlineData(1.0, "100%")]
    [InlineData(1.1, "110%")]
    [InlineData(0.75, "75%")]
    public void Label_IsAWholePercentage(double zoom, string expected) =>
        Assert.Equal(expected, ViewZoom.Label(zoom));
}
