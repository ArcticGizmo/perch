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
    /// <summary>The pane the dark lanes are painted on (Aurora Dark's raised surface).</summary>
    public static readonly Rgb DarkPane = new(0x1F, 0x1F, 0x2A);

    /// <summary>The pane the light lanes are painted on (Aurora Light's raised surface).</summary>
    public static readonly Rgb LightPane = new(0xFF, 0xFF, 0xFF);

    // Blue, violet, teal, pink, lime, amber, sky, orange: distinct from one another and from the diff's
    // add/remove green/red.
    public static readonly Rgb[] Dark =
    [
        Rgb.FromHex("#60A5FA"), Rgb.FromHex("#A78BFA"), Rgb.FromHex("#2DD4BF"), Rgb.FromHex("#F472B6"),
        Rgb.FromHex("#A3E635"), Rgb.FromHex("#FBBF24"), Rgb.FromHex("#38BDF8"), Rgb.FromHex("#FB923C"),
    ];

    public static readonly Rgb[] Light =
    [
        Rgb.FromHex("#2563EB"), Rgb.FromHex("#7C3AED"), Rgb.FromHex("#0F766E"), Rgb.FromHex("#DB2777"),
        Rgb.FromHex("#4D7C0F"), Rgb.FromHex("#B45309"), Rgb.FromHex("#0369A1"), Rgb.FromHex("#C2410C"),
    ];

    public static Rgb[] For(bool light) => light ? Light : Dark;
}
