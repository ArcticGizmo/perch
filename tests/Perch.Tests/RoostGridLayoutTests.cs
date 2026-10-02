using System.Text.Json;
using Perch.Data.Roost;
using Xunit;

namespace Perch.Tests;

/// <summary>
/// Guards <see cref="RoostGridLayout"/> (docs/roost-tabs-plan.md T2): presets tile the grid, validation rejects
/// anything that doesn't, and split / remove / divider moves either produce a valid layout or are refused.
/// </summary>
public class RoostGridLayoutTests
{
    private const int U = RoostGridLayout.Units;

    private static RoostGridLayout Preset(RoostSnapTemplate t) => RoostGridLayout.FromTemplate(t);

    private static RoostRegion R(int id, int row, int col, int rowSpan, int colSpan) => new(id, row, col, rowSpan, colSpan);

    [Fact]
    public void EveryPresetTilesTheGridWithOneRegionPerTemplateCell()
    {
        foreach (var (template, _, layout) in RoostGridLayout.Presets)
        {
            Assert.True(RoostGridLayout.IsValid(layout.Regions), template.ToString());
            Assert.Equal(RoostTemplates.Shape(template).Slots.Count, layout.Regions.Count);
        }
    }

    [Fact]
    public void PresetWeightsRoundToUnitLines()
    {
        // 60 / 40 → 7 / 5; two + three → halves on top, thirds below.
        Assert.Equal([R(0, 0, 0, U, 7), R(1, 0, 7, U, 5)], Preset(RoostSnapTemplate.Wide60).Regions);
        Assert.Equal(
            [R(0, 0, 0, 6, 6), R(1, 0, 6, 6, 6), R(2, 6, 0, 6, 4), R(3, 6, 4, 6, 4), R(4, 6, 8, 6, 4)],
            Preset(RoostSnapTemplate.TwoPlusThree).Regions);
    }

    [Fact]
    public void ValidationRejectsGapsOverlapsSlivers()
    {
        Assert.True(RoostGridLayout.IsValid([R(0, 0, 0, U, 6), R(1, 0, 6, U, 6)]));
        Assert.False(RoostGridLayout.IsValid([]));
        Assert.False(RoostGridLayout.IsValid([R(0, 0, 0, U, 6)]));                          // gap
        Assert.False(RoostGridLayout.IsValid([R(0, 0, 0, U, 7), R(1, 0, 6, U, 6)]));        // overlap (and over)
        Assert.False(RoostGridLayout.IsValid([R(0, 0, 0, U, 11), R(1, 0, 11, U, 1)]));      // under MinSpan
        Assert.False(RoostGridLayout.IsValid([R(0, 0, 0, U, 6), R(0, 0, 6, U, 6)]));        // duplicate id
        Assert.False(RoostGridLayout.IsValid([R(-1, 0, 0, U, U)]));                         // negative id
        Assert.False(RoostGridLayout.IsValid([R(0, 0, 2, U, U)]));                          // out of bounds
        var nine = Enumerable.Range(0, 9).Select(i => R(i, i / 3 * 4, i % 3 * 4, 4, 4)).ToList();
        Assert.False(RoostGridLayout.IsValid(nine));                                        // past MaxRegions
    }

    [Fact]
    public void ValidationIsOverflowSafe()
    {
        // Row + RowSpan wraps to int.MinValue and RowSpan × ColumnSpan to 0, so a summing check would let this
        // off-grid region through alongside a full one.
        const int Half = 1 << 30;
        Assert.False(RoostGridLayout.IsValid([R(0, 0, 0, U, U), R(1, Half, 0, Half, 4)]));
        Assert.False(RoostGridLayout.IsValid([R(0, 0, 0, U, U), R(1, 0, Half, 4, Half)]));
        Assert.False(RoostGridLayout.IsValid([R(0, int.MaxValue, 0, U, U)]));
        Assert.False(RoostGridLayout.IsValid([R(0, 0, 0, int.MaxValue, U)]));
    }

    [Fact]
    public void RegionIdsStopAtTheCeiling()
    {
        Assert.True(RoostGridLayout.IsValid([R(RoostGridLayout.MaxRegionId, 0, 0, U, U)]));
        Assert.False(RoostGridLayout.IsValid([R(RoostGridLayout.MaxRegionId + 1, 0, 0, U, U)]));
        Assert.False(RoostGridLayout.IsValid([R(int.MaxValue, 0, 0, U, U)]));
        // A layout at the ceiling can't mint a fresh id: adopting it keeps the preset's own ids rather than failing.
        var atCeiling = RoostGridLayout.FromPersisted([R(RoostGridLayout.MaxRegionId, 0, 0, U, U)]);
        var two = Preset(RoostSnapTemplate.Columns2);
        Assert.Same(two, two.AdoptIds(atCeiling));
        Assert.Null(atCeiling.Split(RoostGridLayout.MaxRegionId, RoostSplit.Columns));
    }

    [Fact]
    public void AnInvalidPersistedLayoutReadsBackAsFull()
    {
        Assert.Same(RoostGridLayout.Full, RoostGridLayout.FromPersisted(null));
        Assert.Same(RoostGridLayout.Full, RoostGridLayout.FromPersisted([R(0, 0, 0, U, 6)]));
        var ok = RoostGridLayout.FromPersisted([R(3, 0, 0, U, 6), R(5, 0, 6, U, 6)]);
        Assert.Equal([3, 5], ok.Regions.Select(r => r.Id));
    }

    [Fact]
    public void ALayoutRoundTripsThroughJson()
    {
        var layout = Preset(RoostSnapTemplate.MainPlusTwo);
        var json = JsonSerializer.Serialize(layout.Regions);
        var back = RoostGridLayout.FromPersisted(JsonSerializer.Deserialize<List<RoostRegion>>(json));
        Assert.Equal(layout.Signature, back.Signature);
    }

    [Fact]
    public void SplitKeepsTheIdOnTheFirstHalf()
    {
        var two = RoostGridLayout.Full.Split(0, RoostSplit.Columns)!;
        Assert.Equal([R(0, 0, 0, U, 6), R(1, 0, 6, U, 6)], two.Regions);
        var three = two.Split(1, RoostSplit.Rows)!;
        Assert.Equal([R(0, 0, 0, U, 6), R(1, 0, 6, 6, 6), R(2, 6, 6, 6, 6)], three.Regions);
        Assert.NotEqual(two.Signature, three.Signature);
    }

    [Fact]
    public void SplitRefusesSliversAndTheRegionCap()
    {
        var quarter = RoostGridLayout.Full.Split(0, RoostSplit.Columns)!.Split(0, RoostSplit.Columns)!;   // 3 / 3 / 6
        Assert.Equal(3, quarter.Find(0)!.Value.ColumnSpan);
        Assert.Null(quarter.Split(0, RoostSplit.Columns));   // 1 / 2 would be a sliver
        Assert.False(quarter.CanSplit(0, RoostSplit.Columns));
        Assert.Null(quarter.Split(99, RoostSplit.Rows));     // no such region

        var layout = Preset(RoostSnapTemplate.Grid2x2);
        for (int id = 0; id < 4; id++) layout = layout.Split(id, RoostSplit.Columns)!;
        Assert.Equal(RoostGridLayout.MaxRegions, layout.Regions.Count);
        Assert.Null(layout.Split(0, RoostSplit.Rows));
    }

    [Fact]
    public void RemoveGrowsANeighbourThatSharesAWholeSide()
    {
        Assert.Equal([R(0, 0, 0, U, U)], Preset(RoostSnapTemplate.Columns2).Remove(1)!.Regions);

        // One + two: removing the top right grows the bottom right to the full height.
        var one2 = Preset(RoostSnapTemplate.MainPlusTwo);
        Assert.Equal([R(0, 0, 0, U, 7), R(2, 0, 7, U, 5)], one2.Remove(1)!.Regions);
        // The tall left region has no neighbour sharing its whole side.
        Assert.False(one2.CanRemove(0));
        Assert.Null(one2.Remove(0));
    }

    [Fact]
    public void RemovePrefersTheLargerThenTheLeftNeighbour()
    {
        // Three columns: removing the middle, both neighbours are equal → the left one grows.
        Assert.Equal([R(0, 0, 0, U, 8), R(2, 0, 8, U, 4)], Preset(RoostSnapTemplate.Columns3).Remove(1)!.Regions);
        // 60 / 40 with the right split (7 | 2 | 3): removing the narrow middle grows the wider left.
        var layout = Preset(RoostSnapTemplate.Wide60).Split(1, RoostSplit.Columns)!;
        Assert.Equal([R(0, 0, 0, U, 7), R(1, 0, 7, U, 2), R(2, 0, 9, U, 3)], layout.Regions);
        Assert.Equal(0, layout.MergeTarget(1)!.Value.Id);
        Assert.Null(RoostGridLayout.Full.Remove(0));    // the only region
    }

    [Fact]
    public void DividersAreTheSmallestRunsTheirRegionsFitInside()
    {
        Assert.Equal([new RoostDivider(RoostAxis.Vertical, 6, 0, U)], Preset(RoostSnapTemplate.Columns2).Dividers);

        // One + two: the vertical line spans the tall region; the horizontal one only the right column.
        Assert.Equal(
            [new RoostDivider(RoostAxis.Vertical, 7, 0, U), new RoostDivider(RoostAxis.Horizontal, 6, 7, U)],
            Preset(RoostSnapTemplate.MainPlusTwo).Dividers);

        // 2 × 2: each line is two dividers, so either half moves on its own.
        Assert.Equal(
            [
                new RoostDivider(RoostAxis.Vertical, 6, 0, 6), new RoostDivider(RoostAxis.Vertical, 6, 6, U),
                new RoostDivider(RoostAxis.Horizontal, 6, 0, 6), new RoostDivider(RoostAxis.Horizontal, 6, 6, U),
            ],
            Preset(RoostSnapTemplate.Grid2x2).Dividers);

        Assert.Empty(RoostGridLayout.Full.Dividers);
        Assert.Equal(new RoostDivider(RoostAxis.Horizontal, 6, 7, U),
            Preset(RoostSnapTemplate.MainPlusTwo).DividerAt(RoostAxis.Horizontal, 6, 9));
        Assert.Null(Preset(RoostSnapTemplate.MainPlusTwo).DividerAt(RoostAxis.Horizontal, 6, 3));
    }

    [Fact]
    public void MovingADividerMovesBothSidesAndClampsAtMinSpan()
    {
        var two = Preset(RoostSnapTemplate.Columns2);
        var d = two.Dividers[0];
        Assert.Equal((2, 10), two.DividerRange(d));
        Assert.Equal([R(0, 0, 0, U, 8), R(1, 0, 8, U, 4)], two.MoveDivider(d, 8)!.Regions);
        Assert.Equal([R(0, 0, 0, U, 10), R(1, 0, 10, U, 2)], two.MoveDivider(d, 11)!.Regions);   // stops at the limit
        Assert.Equal([R(0, 0, 0, U, 2), R(1, 0, 2, U, 10)], two.MoveDivider(d, -5)!.Regions);
        Assert.Same(two, two.MoveDivider(d, 6));
        Assert.Null(two.MoveDivider(new RoostDivider(RoostAxis.Vertical, 3, 0, U), 4));            // not a divider here
    }

    [Fact]
    public void MovingHalfA2x2LineLeavesTheOtherHalf()
    {
        var grid = Preset(RoostSnapTemplate.Grid2x2);   // 0 1 / 2 3
        var leftHalf = grid.DividerAt(RoostAxis.Horizontal, 6, 0)!.Value;
        var moved = grid.MoveDivider(leftHalf, 4)!;
        Assert.Equal([R(0, 0, 0, 4, 6), R(1, 0, 6, 6, 6), R(2, 4, 0, 8, 6), R(3, 6, 6, 6, 6)], moved.Regions);
        // The vertical line is now a single divider again: the regions beside it no longer line up across it.
        Assert.Equal([new RoostDivider(RoostAxis.Vertical, 6, 0, U)], moved.Dividers.Where(x => x.Axis == RoostAxis.Vertical));
    }

    [Fact]
    public void ReadingOrderGoesRowThenColumn()
    {
        var layout = RoostGridLayout.Create([R(7, 6, 0, 6, U), R(2, 0, 6, 6, 6), R(4, 0, 0, 6, 6)])!;
        Assert.Equal([4, 2, 7], layout.ReadingOrder.Select(r => r.Id));
    }

    [Fact]
    public void RegionsAreNumberedInReadingOrder()
    {
        // Created bottom-first, so creation order and reading order differ: Alt+1 is the top-left region.
        var layout = RoostGridLayout.Create([R(7, 6, 0, 6, U), R(2, 0, 6, 6, 6), R(4, 0, 0, 6, 6)])!;
        Assert.Equal([1, 2, 3], new[] { 4, 2, 7 }.Select(layout.NumberOf));
        Assert.Equal(0, layout.NumberOf(5));
        Assert.Equal(4, layout.ByNumber(1)!.Value.Id);
        Assert.Equal(7, layout.ByNumber(3)!.Value.Id);
        Assert.Null(layout.ByNumber(0));
        Assert.Null(layout.ByNumber(4));
    }
}
