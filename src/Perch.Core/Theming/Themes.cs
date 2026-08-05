namespace Perch.Theming;

/// <summary>
/// The built-in theme catalogue. <see cref="Midnight"/> is seeded from Perch's original hand-picked
/// palette (post-M0 contrast fixes), so switching to it is a visual no-op — it is the baseline every other
/// preset is derived from with a <c>with</c> expression. Later presets (Ember, Blush, Dim, High Contrast)
/// land in M3.
/// </summary>
public static class Themes
{
    /// <summary>The default: Perch's original cool-charcoal dark palette, exact current values.</summary>
    public static readonly Theme Midnight = new()
    {
        Id = "midnight",
        Name = "Midnight",
        IsDark = true,

        // Surfaces & chrome
        Surface            = new(24, 24, 32),
        SurfaceSunken      = new(18, 18, 24),
        SurfaceRaised      = new(45, 45, 60),
        SurfaceRaisedHover = new(60, 60, 80),
        OverlaySurface     = new(15, 15, 20),
        OverlayRowHover    = new(25, 25, 38),
        Track              = new(38, 38, 52),
        Border             = new(45, 45, 60),
        Separator          = new(35, 35, 50),
        TreeLine           = new(55, 55, 72),

        // Text
        TextPrimary  = new(225, 225, 235),
        TextTitle    = new(245, 245, 250),
        TextMuted    = new(140, 140, 160),
        ExpectedMark = new(180, 180, 195),

        // Accent & brand
        Accent      = new(96, 165, 250),
        AccentHover = new(147, 197, 253),
        Brand       = new(255, 68, 45),
        BrandHover  = new(255, 104, 84),
        Danger      = new(248, 113, 113),

        // Semantic status
        StatusRunning   = new(34, 197, 94),
        StatusAttention = new(251, 146, 60),
        StatusAwaiting  = new(250, 204, 21),
        StatusIdle      = new(100, 116, 139),
        StatusError     = new(239, 68, 68),
        StatusWarn      = new(245, 158, 11),
        SubAgent        = new(168, 85, 247),
        Teal            = new(94, 234, 212),
        Burn            = new(125, 185, 232),
        TeamGray        = new(148, 163, 184),
        ModeAcceptEdits = new(167, 139, 250),
    };

    /// <summary>
    /// Perch-flavoured warm dark: the neutral ramp rotated off cool-blue toward the brand's red-orange
    /// (~15°) at low chroma, so the surfaces read as warm charcoal with a faint red undertone. Only the
    /// neutrals and text are re-tinted — the accent, brand and semantic status hues are inherited from
    /// <see cref="Midnight"/> unchanged, so the overlay's glanceable meaning (running/awaiting/error) is
    /// untouched. Text roles are verified against WCAG AA on the warm surfaces by the preset-contrast test.
    /// </summary>
    public static readonly Theme Ember = Midnight with
    {
        Id = "ember",
        Name = "Ember",

        // Restrained warm charcoal: only a faint warm bias (R a few points over B), not an obvious red —
        // near-neutral chrome that reads as "warm" beside Midnight's cool blue-grey without shouting.
        Surface            = new(24, 22, 21),
        SurfaceSunken      = new(20, 18, 17),
        SurfaceRaised      = new(43, 40, 38),
        SurfaceRaisedHover = new(56, 52, 49),
        OverlaySurface     = new(17, 15, 14),
        OverlayRowHover    = new(29, 25, 23),
        Track              = new(40, 37, 35),
        Border             = new(51, 46, 43),
        Separator          = new(37, 33, 31),
        TreeLine           = new(60, 54, 50),

        // Faintly warm neutrals for text.
        TextPrimary  = new(231, 227, 224),
        TextTitle    = new(248, 245, 243),
        TextMuted    = new(165, 158, 153),
        ExpectedMark = new(189, 182, 177),
    };

    /// <summary>Every built-in theme, in display order. Custom themes are appended by the UI.</summary>
    public static readonly IReadOnlyList<Theme> BuiltIn = [Midnight, Ember];

    /// <summary>Finds a built-in theme by id, or null.</summary>
    public static Theme? ById(string? id)
    {
        if (id is null) return null;
        foreach (var t in BuiltIn)
            if (t.Id == id) return t;
        return null;
    }
}
