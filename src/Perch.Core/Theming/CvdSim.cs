namespace Perch.Theming;

/// <summary>The kinds of colour-vision deficiency the designer can simulate.</summary>
public enum CvdType
{
    None,
    Protanopia,    // red-blind
    Deuteranopia,  // green-blind
    Tritanopia,    // blue-blind
}

/// <summary>
/// Simulates how a theme's colours appear to someone with a colour-vision deficiency, so a designer can
/// check that the status hues (running/error/…) don't collapse into each other. Uses the common sRGB 3×3
/// approximation matrices — good enough to reveal a confusable pair; not a clinical model. Applying a
/// simulated theme through the normal pipeline shows the whole overlay "as they'd see it".
/// </summary>
public static class CvdSim
{
    // sRGB-space approximations (rows = output R/G/B as a mix of input R/G/B).
    private static readonly double[][] Protan =
    [
        [0.567, 0.433, 0.000],
        [0.558, 0.442, 0.000],
        [0.000, 0.242, 0.758],
    ];

    private static readonly double[][] Deutan =
    [
        [0.625, 0.375, 0.000],
        [0.700, 0.300, 0.000],
        [0.000, 0.300, 0.700],
    ];

    private static readonly double[][] Tritan =
    [
        [0.950, 0.050, 0.000],
        [0.000, 0.433, 0.567],
        [0.000, 0.475, 0.525],
    ];

    /// <summary>The colour as seen under <paramref name="type"/> (unchanged for <see cref="CvdType.None"/>).</summary>
    public static Rgb Simulate(Rgb c, CvdType type)
    {
        var m = type switch
        {
            CvdType.Protanopia   => Protan,
            CvdType.Deuteranopia => Deutan,
            CvdType.Tritanopia   => Tritan,
            _                    => null,
        };
        if (m is null) return c;

        double r = c.R, g = c.G, b = c.B;
        return new Rgb(
            Clamp(m[0][0] * r + m[0][1] * g + m[0][2] * b),
            Clamp(m[1][0] * r + m[1][1] * g + m[1][2] * b),
            Clamp(m[2][0] * r + m[2][1] * g + m[2][2] * b));
    }

    /// <summary>Every <em>theme</em> role of <paramref name="theme"/> run through
    /// <see cref="Simulate(Rgb, CvdType)"/> — including the themeable semantic status hues, so the overlay's
    /// glyphs preview under the deficiency too. The four fixed brand/Jira/destructive hues aren't theme roles;
    /// they simulate themselves via <see cref="FixedColors.Simulate"/> at the Palette apply site. Id/Name are
    /// preserved. <b>Add every new <see cref="Theme"/> role here by hand</b> — this block does not reflect.</summary>
    public static Theme Simulate(Theme theme, CvdType type)
    {
        if (type == CvdType.None) return theme;
        return theme with
        {
            Surface = Simulate(theme.Surface, type),
            SurfaceSunken = Simulate(theme.SurfaceSunken, type),
            SurfaceRaised = Simulate(theme.SurfaceRaised, type),
            SurfaceRaisedHover = Simulate(theme.SurfaceRaisedHover, type),
            OverlaySurface = Simulate(theme.OverlaySurface, type),
            OverlayRowHover = Simulate(theme.OverlayRowHover, type),
            Track = Simulate(theme.Track, type),
            Border = Simulate(theme.Border, type),
            Separator = Simulate(theme.Separator, type),
            TreeLine = Simulate(theme.TreeLine, type),
            TextPrimary = Simulate(theme.TextPrimary, type),
            TextTitle = Simulate(theme.TextTitle, type),
            TextMuted = Simulate(theme.TextMuted, type),
            ExpectedMark = Simulate(theme.ExpectedMark, type),
            Accent = Simulate(theme.Accent, type),
            AccentHover = Simulate(theme.AccentHover, type),
            FocusRing = Simulate(theme.FocusRing, type),
            // Themeable semantic status / glyph hues.
            StatusRunning = Simulate(theme.StatusRunning, type),
            StatusAttention = Simulate(theme.StatusAttention, type),
            StatusAwaiting = Simulate(theme.StatusAwaiting, type),
            StatusIdle = Simulate(theme.StatusIdle, type),
            StatusError = Simulate(theme.StatusError, type),
            StatusWarn = Simulate(theme.StatusWarn, type),
            SubAgent = Simulate(theme.SubAgent, type),
            Teal = Simulate(theme.Teal, type),
            Burn = Simulate(theme.Burn, type),
            TeamGray = Simulate(theme.TeamGray, type),
            ModeAcceptEdits = Simulate(theme.ModeAcceptEdits, type),
        };
    }

    private static byte Clamp(double v) => (byte)Math.Clamp(Math.Round(v), 0, 255);
}
