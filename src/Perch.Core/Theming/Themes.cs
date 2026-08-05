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

        // Warm charcoal ramp (hue ~15°, low chroma).
        Surface            = new(27, 21, 19),
        SurfaceRaised      = new(48, 37, 33),
        SurfaceRaisedHover = new(62, 48, 43),
        OverlaySurface     = new(20, 15, 13),
        OverlayRowHover    = new(38, 27, 23),
        Track              = new(46, 36, 33),
        Border             = new(60, 44, 39),
        Separator          = new(44, 32, 29),
        TreeLine           = new(76, 55, 48),

        // Warm-tinted neutrals for text.
        TextPrimary  = new(237, 228, 223),
        TextTitle    = new(250, 244, 240),
        TextMuted    = new(179, 164, 156),
        ExpectedMark = new(198, 182, 174),
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
