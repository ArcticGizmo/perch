using Perch.Theming;

namespace Perch.Data.GitGraph;

/// <summary>
/// The graph's lane colours, one set per polarity of the Tree window's own reading palette (Aurora), which stays
/// independent of the app theme. A lane's colour is its index modulo the set, so a reused lane reuses its colour.
/// Kept UI-free here so <c>GraphLaneContrastTests</c> can hold every hue to the 3:1 non-text floor on the pane it is
/// painted on.
/// </summary>
public static class GraphLanes
{
    /// <summary>The pane the dark lanes are painted on (Aurora Dusk's raised surface).</summary>
    public static readonly Rgb DarkPane = new(0x20, 0x23, 0x29);

    /// <summary>The pane the light lanes are painted on (Aurora Light's raised surface).</summary>
    public static readonly Rgb LightPane = new(0xFF, 0xFF, 0xFF);

    // Blue, violet, teal, pink, lime, amber, sky, orange: distinct from one another and from the diff's
    // add/remove green/red. The dark set is a little desaturated, so a busy graph doesn't buzz (each still ≥5.7:1).
    public static readonly Rgb[] Dark =
    [
        Rgb.FromHex("#6AA0E8"), Rgb.FromHex("#A391E6"), Rgb.FromHex("#45C2B1"), Rgb.FromHex("#DE7DAF"),
        Rgb.FromHex("#A2CC5C"), Rgb.FromHex("#E3B44E"), Rgb.FromHex("#52B2DF"), Rgb.FromHex("#E39858"),
    ];

    public static readonly Rgb[] Light =
    [
        Rgb.FromHex("#2563EB"), Rgb.FromHex("#7C3AED"), Rgb.FromHex("#0F766E"), Rgb.FromHex("#DB2777"),
        Rgb.FromHex("#4D7C0F"), Rgb.FromHex("#B45309"), Rgb.FromHex("#0369A1"), Rgb.FromHex("#C2410C"),
    ];

    public static Rgb[] For(bool light) => light ? Light : Dark;
}
