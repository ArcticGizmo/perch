namespace Perch.Data.Roost;

/// <summary>A built-in layout shape: the tab painter's presets (<see cref="RoostGridLayout.FromTemplate"/>), and the
/// first-run tab's shape for the live session count (<see cref="RoostTemplates.ForCount"/>). Never persisted.</summary>
public enum RoostSnapTemplate
{
    Full = 1,
    Columns2 = 2,
    Rows2 = 3,
    /// <summary>Two columns, 60 / 40.</summary>
    Wide60 = 4,
    /// <summary>One tall pane on the left, two stacked on the right.</summary>
    MainPlusTwo = 5,
    Columns3 = 6,
    Grid2x2 = 7,
    Grid3x2 = 8,
    /// <summary>Two on top, three below.</summary>
    TwoPlusThree = 9,
}

/// <summary>One cell of a template, in grid rows/columns.</summary>
public readonly record struct RoostSlot(int Row, int Column, int RowSpan = 1, int ColumnSpan = 1);

/// <summary>A template's grid: star weights per column and row, and its cells in fill order.</summary>
public sealed record RoostTemplateShape(IReadOnlyList<double> Columns, IReadOnlyList<double> Rows, IReadOnlyList<RoostSlot> Slots);

/// <summary>The built-in layout templates (UI-free, unit-tested): the tab painter's presets
/// (<see cref="RoostGridLayout.Presets"/>) and the first-run tab's shape (<see cref="ForCount"/>).</summary>
public static class RoostTemplates
{
    /// <summary>The most cells any template has — how many sessions the first-run tab takes.</summary>
    public const int AutoMaxCells = 6;

    /// <summary>The presets' order in the painter.</summary>
    public static readonly IReadOnlyList<RoostSnapTemplate> Picker =
    [
        RoostSnapTemplate.Full, RoostSnapTemplate.Columns2, RoostSnapTemplate.Rows2,
        RoostSnapTemplate.Wide60, RoostSnapTemplate.MainPlusTwo, RoostSnapTemplate.Columns3,
        RoostSnapTemplate.Grid2x2, RoostSnapTemplate.TwoPlusThree, RoostSnapTemplate.Grid3x2,
    ];

    public static string Name(RoostSnapTemplate t) => t switch
    {
        RoostSnapTemplate.Full => "Single",
        RoostSnapTemplate.Columns2 => "Two columns",
        RoostSnapTemplate.Rows2 => "Two rows",
        RoostSnapTemplate.Wide60 => "60 / 40",
        RoostSnapTemplate.MainPlusTwo => "One + two",
        RoostSnapTemplate.Columns3 => "Three columns",
        RoostSnapTemplate.Grid2x2 => "2 × 2",
        RoostSnapTemplate.Grid3x2 => "3 × 2",
        RoostSnapTemplate.TwoPlusThree => "Two + three",
        _ => t.ToString(),
    };

    /// <summary>The template's grid.</summary>
    public static RoostTemplateShape Shape(RoostSnapTemplate t) => t switch
    {
        RoostSnapTemplate.Columns2 => Uniform(2, 1),
        RoostSnapTemplate.Rows2 => Uniform(1, 2),
        RoostSnapTemplate.Wide60 => new([3, 2], [1], [new(0, 0), new(0, 1)]),
        RoostSnapTemplate.MainPlusTwo => new([3, 2], [1, 1], [new(0, 0, RowSpan: 2), new(0, 1), new(1, 1)]),
        RoostSnapTemplate.Columns3 => Uniform(3, 1),
        RoostSnapTemplate.Grid2x2 => Uniform(2, 2),
        RoostSnapTemplate.Grid3x2 => Uniform(3, 2),
        // Six columns: the top two cells span three each, the bottom three span two.
        RoostSnapTemplate.TwoPlusThree => new([1, 1, 1, 1, 1, 1], [1, 1],
            [new(0, 0, ColumnSpan: 3), new(0, 3, ColumnSpan: 3), new(1, 0, ColumnSpan: 2), new(1, 2, ColumnSpan: 2), new(1, 4, ColumnSpan: 2)]),
        _ => Uniform(1, 1),
    };

    /// <summary>
    /// The first-run tab's pick for <paramref name="cells"/> cells on a stage <paramref name="aspect"/> (width / height) wide:
    /// one fills the stage; two sit side by side (stacked on a stage taller than wide); three go one + two
    /// (three columns on a stage over twice as wide as tall); four 2×2; five two + three; six 3×2.
    /// </summary>
    public static RoostSnapTemplate ForCount(int cells, double aspect) => cells switch
    {
        <= 1 => RoostSnapTemplate.Full,
        2 => aspect >= 1 ? RoostSnapTemplate.Columns2 : RoostSnapTemplate.Rows2,
        3 => aspect >= 2.2 ? RoostSnapTemplate.Columns3 : RoostSnapTemplate.MainPlusTwo,
        4 => RoostSnapTemplate.Grid2x2,
        5 => RoostSnapTemplate.TwoPlusThree,
        _ => RoostSnapTemplate.Grid3x2,
    };

    private static RoostTemplateShape Uniform(int columns, int rows)
    {
        var slots = new List<RoostSlot>(columns * rows);
        for (int r = 0; r < rows; r++)
            for (int c = 0; c < columns; c++)
                slots.Add(new RoostSlot(r, c));
        return new RoostTemplateShape(Enumerable.Repeat(1.0, columns).ToArray(), Enumerable.Repeat(1.0, rows).ToArray(), slots);
    }
}
