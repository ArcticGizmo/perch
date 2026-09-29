using Perch.Data.Roost;
using Xunit;

namespace Perch.Tests;

/// <summary>Guards <see cref="RoostTemplates"/>: Auto's pick per pane count and stage shape, and that every
/// template's cells tile its grid exactly once.</summary>
public class RoostTemplatesTests
{
    [Theory]
    [InlineData(0, 1.6, RoostSnapTemplate.Full)]
    [InlineData(1, 1.6, RoostSnapTemplate.Full)]
    [InlineData(2, 1.6, RoostSnapTemplate.Columns2)]
    [InlineData(2, 0.8, RoostSnapTemplate.Rows2)]       // taller than wide → stacked
    [InlineData(3, 1.6, RoostSnapTemplate.MainPlusTwo)]
    [InlineData(3, 2.5, RoostSnapTemplate.Columns3)]    // ultrawide
    [InlineData(4, 1.6, RoostSnapTemplate.Grid2x2)]
    [InlineData(5, 1.6, RoostSnapTemplate.TwoPlusThree)]
    [InlineData(6, 1.6, RoostSnapTemplate.Grid3x2)]
    public void AutoFitsThePaneCount(int cells, double aspect, RoostSnapTemplate expected) =>
        Assert.Equal(expected, RoostTemplates.ForCount(cells, aspect));

    [Fact]
    public void APickForTheCountWinsWhileItFits()
    {
        var picks = new Dictionary<int, RoostSnapTemplate>
        {
            [2] = RoostSnapTemplate.Rows2,
            [3] = RoostSnapTemplate.Columns2,     // too few cells for 3 — ignored
            [4] = RoostSnapTemplate.Auto,         // Auto = the default
        };
        Assert.Equal(RoostSnapTemplate.Rows2, RoostTemplates.For(2, picks, 1.6));
        Assert.Equal(RoostSnapTemplate.MainPlusTwo, RoostTemplates.For(3, picks, 1.6));
        Assert.Equal(RoostSnapTemplate.Grid2x2, RoostTemplates.For(4, picks, 1.6));
        Assert.Equal(RoostSnapTemplate.Columns2, RoostTemplates.For(2, null, 1.6));
    }

    [Fact]
    public void EveryTemplateTilesItsGridOnce()
    {
        foreach (var t in Enum.GetValues<RoostSnapTemplate>().Where(t => t != RoostSnapTemplate.Auto))
        {
            var shape = RoostTemplates.Shape(t);
            var covered = new int[shape.Rows.Count, shape.Columns.Count];
            foreach (var s in shape.Slots)
                for (int r = s.Row; r < s.Row + s.RowSpan; r++)
                    for (int c = s.Column; c < s.Column + s.ColumnSpan; c++)
                        covered[r, c]++;
            foreach (var n in covered) Assert.Equal(1, n);
            Assert.InRange(shape.Slots.Count, 1, RoostTemplates.AutoMaxCells);
        }
    }

    [Fact]
    public void ThePickerOffersEveryTemplate() =>
        Assert.Equal(Enum.GetValues<RoostSnapTemplate>().OrderBy(t => t), RoostTemplates.Picker.OrderBy(t => t));

    [Fact]
    public void CapacityIsTheSlotCountOrAutosCeiling()
    {
        Assert.Equal(RoostTemplates.AutoMaxCells, RoostTemplates.Capacity(RoostSnapTemplate.Auto));
        Assert.Equal(3, RoostTemplates.Capacity(RoostSnapTemplate.MainPlusTwo));
        Assert.Equal(1, RoostTemplates.Capacity(RoostSnapTemplate.Full));
    }
}
