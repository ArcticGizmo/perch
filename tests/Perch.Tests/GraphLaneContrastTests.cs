using Perch.Data.GitGraph;
using Perch.Theming;
using Xunit;

namespace Perch.Tests;

/// <summary>
/// Every graph lane colour must clear the WCAG 3:1 non-text floor on the pane it is painted on, in both of the
/// Tree window's polarities: the lanes, nodes and ref pills are the graph's only way of telling branches apart.
/// </summary>
public class GraphLaneContrastTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LanesClearTheNonTextFloor(bool light)
    {
        var pane = light ? GraphLanes.LightPane : GraphLanes.DarkPane;
        foreach (var lane in GraphLanes.For(light))
            Assert.True(Contrast.Ratio(lane, pane) >= 3.0, $"{lane} on {pane}: {Contrast.Ratio(lane, pane):F2}");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LanesAreDistinct(bool light) =>
        Assert.Equal(GraphLanes.For(light).Length, GraphLanes.For(light).Distinct().Count());
}
