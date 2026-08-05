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
        FocusRing   = new(147, 197, 253),

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

    /// <summary>
    /// Soft pink-tinted dark: the neutral ramp rotated toward magenta (~330°) at low chroma (R and B a
    /// touch over G, for a mauve-grey). The "very slightly pink" flavour — restrained, like Ember. Accent,
    /// brand and status hues inherited from Midnight.
    /// </summary>
    public static readonly Theme Blush = Midnight with
    {
        Id = "blush",
        Name = "Blush",

        Surface            = new(25, 21, 24),
        SurfaceSunken      = new(20, 17, 20),
        SurfaceRaised      = new(44, 38, 43),
        SurfaceRaisedHover = new(57, 50, 55),
        OverlaySurface     = new(18, 14, 17),
        OverlayRowHover    = new(30, 24, 28),
        Track              = new(41, 35, 40),
        Border             = new(52, 44, 50),
        Separator          = new(38, 32, 36),
        TreeLine           = new(61, 52, 58),

        TextPrimary  = new(231, 225, 228),
        TextTitle    = new(248, 243, 246),
        TextMuted    = new(167, 157, 163),
        ExpectedMark = new(190, 181, 186),
    };

    /// <summary>
    /// Monokai-inspired warm dark: an olive-brown neutral ramp (Monokai's #272822 family) with crisp
    /// near-white text, for the high-contrast-code crowd. Accent, brand and status hues inherited from
    /// Midnight.
    /// </summary>
    public static readonly Theme Dim = Midnight with
    {
        Id = "dim",
        Name = "Dim",

        Surface            = new(39, 40, 34),
        SurfaceSunken      = new(32, 33, 28),
        SurfaceRaised      = new(62, 63, 54),
        SurfaceRaisedHover = new(78, 79, 68),
        OverlaySurface     = new(30, 31, 26),
        OverlayRowHover    = new(48, 49, 42),
        Track              = new(55, 56, 48),
        Border             = new(68, 69, 58),
        Separator          = new(52, 53, 45),
        TreeLine           = new(85, 84, 70),

        TextPrimary  = new(248, 248, 242),
        TextTitle    = new(253, 253, 248),
        TextMuted    = new(176, 173, 156),
        ExpectedMark = new(202, 199, 182),
    };

    /// <summary>
    /// High-contrast dark for maximum legibility: a near-black surface with bright text (well past AAA) and
    /// deliberately brighter borders so control boundaries clear the non-text floor with room to spare. The
    /// accessibility flagship. (Thicker borders / focus rings are a rendering concern layered on in M5.)
    /// </summary>
    public static readonly Theme HighContrast = Midnight with
    {
        Id = "high-contrast",
        Name = "High Contrast",

        Surface            = new(10, 10, 12),
        SurfaceSunken      = new(5, 5, 7),
        SurfaceRaised      = new(32, 32, 38),
        SurfaceRaisedHover = new(48, 48, 56),
        OverlaySurface     = new(4, 4, 6),
        OverlayRowHover    = new(28, 28, 34),
        Track              = new(30, 30, 36),
        Border             = new(96, 96, 112),
        Separator          = new(74, 74, 88),
        TreeLine           = new(104, 104, 120),

        TextPrimary  = new(246, 246, 249),
        TextTitle    = new(255, 255, 255),
        TextMuted    = new(192, 192, 202),
        ExpectedMark = new(212, 212, 220),
        FocusRing    = new(255, 255, 255),   // maximum-visibility focus outline
    };

    /// <summary>
    /// A nod to classic Winamp: dark metallic surfaces with a faint LCD-green cast to the text and a bright
    /// lime-green accent (links / selection / nav). Deliberately retro and playful. The accent green is
    /// limier and brighter than the deeper status "running" green, so the two stay distinguishable. Brand
    /// and status hues inherited from Midnight.
    /// </summary>
    public static readonly Theme Winamp = Midnight with
    {
        Id = "winamp",
        Name = "Winamp",

        Surface            = new(20, 21, 20),
        SurfaceSunken      = new(14, 15, 14),
        SurfaceRaised      = new(38, 40, 38),
        SurfaceRaisedHover = new(50, 53, 50),
        OverlaySurface     = new(12, 13, 12),
        OverlayRowHover    = new(24, 27, 24),
        Track              = new(34, 37, 34),
        Border             = new(52, 56, 52),
        Separator          = new(36, 39, 36),
        TreeLine           = new(60, 66, 60),

        TextPrimary  = new(223, 233, 223),
        TextTitle    = new(240, 250, 240),
        TextMuted    = new(158, 173, 158),
        ExpectedMark = new(184, 199, 184),

        // The iconic Winamp visualiser green as the accent.
        Accent      = new(110, 240, 120),
        AccentHover = new(150, 250, 160),
    };

    /// <summary>Every built-in theme, in display order. Custom themes are appended by the UI.</summary>
    public static readonly IReadOnlyList<Theme> BuiltIn = [Midnight, Ember, Blush, Dim, HighContrast, Winamp];

    /// <summary>Finds a built-in theme by id, or null.</summary>
    public static Theme? ById(string? id)
    {
        if (id is null) return null;
        foreach (var t in BuiltIn)
            if (t.Id == id) return t;
        return null;
    }
}
