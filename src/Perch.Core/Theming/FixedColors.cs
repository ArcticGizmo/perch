namespace Perch.Theming;

/// <summary>
/// The colours that are <b>not</b> part of a theme: Perch's brand red-orange, the destructive-action red,
/// the semantic status hues (running/attention/awaiting/idle/error/warn), and the teammate/mode accents.
/// These carry fixed meaning — running is green, error is red, a Jira link is Jira-blue — so they stay
/// constant across every theme, built-in or custom. Keeping them out of <see cref="Theme"/> means they are
/// never persisted (a theme file / share code only stores what a user can actually change) and can never
/// drift to the wrong value (the bug where an old custom theme deserialised a newly-added role to black).
///
/// <para>There is exactly one instance of the real values — <see cref="Default"/>. <see cref="Simulate"/>
/// produces a colour-vision-deficiency view of them for the theme designer's live preview, the only place
/// these are ever anything but their true values.</para>
/// </summary>
public sealed record FixedColors
{
    /// <summary>The Perch brand red-orange (update affordances).</summary>
    public required Rgb Brand { get; init; }
    /// <summary>Hover/brightened brand.</summary>
    public required Rgb BrandHover { get; init; }
    /// <summary>Destructive-action colour (delete/reset text and buttons).</summary>
    public required Rgb Danger { get; init; }

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
    /// <summary>The Jira ticket deep-link glyph (Jira brand blue).</summary>
    public required Rgb Jira { get; init; }
    /// <summary>Bot / grey-teammate neutral accent.</summary>
    public required Rgb TeamGray { get; init; }
    /// <summary>The "Accept edits" permission-mode badge (blue-purple).</summary>
    public required Rgb ModeAcceptEdits { get; init; }

    /// <summary>The one true palette. (Values match the pre-refactor Midnight roles, which every theme
    /// inherited unchanged.)</summary>
    public static readonly FixedColors Default = new()
    {
        Brand           = new(255, 68, 45),
        BrandHover      = new(255, 104, 84),
        Danger          = new(248, 113, 113),
        StatusRunning   = new(34, 197, 94),
        StatusAttention = new(251, 146, 60),
        StatusAwaiting  = new(250, 204, 21),
        StatusIdle      = new(100, 116, 139),
        StatusError     = new(239, 68, 68),
        StatusWarn      = new(245, 158, 11),
        SubAgent        = new(168, 85, 247),
        Teal            = new(94, 234, 212),
        Burn            = new(125, 185, 232),
        Jira            = new(38, 132, 255),   // Jira brand blue (#2684FF)
        TeamGray        = new(148, 163, 184),
        ModeAcceptEdits = new(167, 139, 250),
    };

    /// <summary>These colours as seen under a colour-vision deficiency, for the designer's live preview
    /// (identity for <see cref="CvdType.None"/>). Lets a designer confirm the status hues stay
    /// distinguishable even though they can't edit them.</summary>
    public FixedColors Simulate(CvdType type)
    {
        if (type == CvdType.None) return this;
        return new()
        {
            Brand           = CvdSim.Simulate(Brand, type),
            BrandHover      = CvdSim.Simulate(BrandHover, type),
            Danger          = CvdSim.Simulate(Danger, type),
            StatusRunning   = CvdSim.Simulate(StatusRunning, type),
            StatusAttention = CvdSim.Simulate(StatusAttention, type),
            StatusAwaiting  = CvdSim.Simulate(StatusAwaiting, type),
            StatusIdle      = CvdSim.Simulate(StatusIdle, type),
            StatusError     = CvdSim.Simulate(StatusError, type),
            StatusWarn      = CvdSim.Simulate(StatusWarn, type),
            SubAgent        = CvdSim.Simulate(SubAgent, type),
            Teal            = CvdSim.Simulate(Teal, type),
            Burn            = CvdSim.Simulate(Burn, type),
            Jira            = CvdSim.Simulate(Jira, type),
            TeamGray        = CvdSim.Simulate(TeamGray, type),
            ModeAcceptEdits = CvdSim.Simulate(ModeAcceptEdits, type),
        };
    }
}
