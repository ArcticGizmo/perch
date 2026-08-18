namespace Perch.Theming;

/// <summary>
/// The colours that are <b>not</b> part of a theme: Perch's brand red-orange, the destructive-action red,
/// and the Jira brand blue. These carry a constant identity — the brand is the brand, a Jira link is
/// Jira-blue, delete is red — and they read on both light and dark, so they stay the same under every theme,
/// built-in or custom, and are never persisted.
///
/// <para>The <em>semantic status hues</em> (running/attention/awaiting/idle/error/warn, the sub-agent/mail/
/// teammate accents, the burn readout and the accept-edits badge) used to live here too, but since per-glyph
/// colouring landed they are real <see cref="Theme"/> roles. Their default values live in
/// <see cref="SemanticDark"/> / <see cref="SemanticLight"/> so a fresh theme seeds from the set matching its
/// polarity and looks identical until a user edits it in the designer.</para>
///
/// <para>There is exactly one instance of the real fixed values — <see cref="Default"/>. <see cref="Simulate"/>
/// produces a colour-vision-deficiency view of them for the theme designer's live preview.</para>
/// </summary>
public sealed record FixedColors
{
    /// <summary>The Perch brand red-orange (update affordances).</summary>
    public required Rgb Brand { get; init; }
    /// <summary>Hover/brightened brand.</summary>
    public required Rgb BrandHover { get; init; }
    /// <summary>Destructive-action colour (delete/reset text and buttons).</summary>
    public required Rgb Danger { get; init; }
    /// <summary>The Jira ticket deep-link glyph (Jira brand blue).</summary>
    public required Rgb Jira { get; init; }

    /// <summary>The one true fixed palette.</summary>
    public static readonly FixedColors Default = new()
    {
        Brand      = new(255, 68, 45),
        BrandHover = new(255, 104, 84),
        Danger     = new(248, 113, 113),
        Jira       = new(38, 132, 255),   // Jira brand blue (#2684FF)
    };

    /// <summary>These colours as seen under a colour-vision deficiency, for the designer's live preview
    /// (identity for <see cref="CvdType.None"/>).</summary>
    public FixedColors Simulate(CvdType type)
    {
        if (type == CvdType.None) return this;
        return new()
        {
            Brand      = CvdSim.Simulate(Brand, type),
            BrandHover = CvdSim.Simulate(BrandHover, type),
            Danger     = CvdSim.Simulate(Danger, type),
            Jira       = CvdSim.Simulate(Jira, type),
        };
    }

    // ── Semantic-role default seeds (now themeable Theme roles) ─────────────────
    /// <summary>The dark default seed for the themeable semantic hues — the exact pre-per-glyph values, so
    /// dark themes look unchanged. Themes seed their semantic roles from here (or <see cref="SemanticLight"/>)
    /// based on polarity; the runtime reads the live values off the active <see cref="Theme"/>.</summary>
    public static readonly SemanticColors SemanticDark = new()
    {
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

    /// <summary>The light default seed for the themeable semantic hues — darker, more-saturated variants
    /// (roughly the Tailwind-700 family) that keep their identity while clearing the 3:1 non-text floor on a
    /// light overlay surface, where the bright dark-theme hues would wash out. Light themes seed from here.</summary>
    public static readonly SemanticColors SemanticLight = new()
    {
        StatusRunning   = new(21, 128, 61),    // green-700
        StatusAttention = new(194, 65, 12),    // orange-700
        StatusAwaiting  = new(161, 98, 7),     // yellow/amber-700 (a dark gold — pure yellow can't clear 3:1 on light)
        StatusIdle      = new(71, 85, 105),    // slate-600
        StatusError     = new(185, 28, 28),    // red-700
        StatusWarn      = new(180, 83, 9),     // amber-700
        SubAgent        = new(126, 34, 206),   // purple-700
        Teal            = new(15, 118, 110),   // teal-700
        Burn            = new(29, 78, 216),     // blue-700
        TeamGray        = new(71, 85, 105),    // slate-600
        ModeAcceptEdits = new(109, 40, 217),   // violet-700
    };
}

/// <summary>
/// The themeable semantic hues, as a default seed set (see <see cref="FixedColors.SemanticDark"/> /
/// <c>SemanticLight</c>). These map 1:1 onto the semantic roles on <see cref="Theme"/>; a theme copies the
/// set matching its polarity into those roles, after which they're independently editable.
/// </summary>
public sealed record SemanticColors
{
    /// <summary>Session running / usage healthy (green).</summary>
    public required Rgb StatusRunning { get; init; }
    /// <summary>Session needs attention / done (orange).</summary>
    public required Rgb StatusAttention { get; init; }
    /// <summary>Session awaiting input / usage warning (yellow).</summary>
    public required Rgb StatusAwaiting { get; init; }
    /// <summary>Session idle (slate).</summary>
    public required Rgb StatusIdle { get; init; }
    /// <summary>Error / API failure / usage critical (red).</summary>
    public required Rgb StatusError { get; init; }
    /// <summary>Stuck / caution glyph (amber).</summary>
    public required Rgb StatusWarn { get; init; }
    /// <summary>Sub-agent / unknown-teammate accent (purple).</summary>
    public required Rgb SubAgent { get; init; }
    /// <summary>Mail / teal-teammate accent.</summary>
    public required Rgb Teal { get; init; }
    /// <summary>Token burn-rate readout (blue).</summary>
    public required Rgb Burn { get; init; }
    /// <summary>Bot / grey-teammate neutral accent.</summary>
    public required Rgb TeamGray { get; init; }
    /// <summary>The "Accept edits" permission-mode badge (blue-purple).</summary>
    public required Rgb ModeAcceptEdits { get; init; }
}
