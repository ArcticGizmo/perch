using Avalonia.Media;
using Perch.Data;
using Perch.Theming;

namespace Perch.Avalonia.Theming;

/// <summary>
/// The app's colour façade over the <em>active</em> <see cref="Theme"/>. Every member here resolves through
/// <see cref="Active"/> rather than a hard-coded literal, so a theme swap re-colours the whole settings /
/// window surface at once. The member names are unchanged from the pre-theme static palette, so the ~200
/// existing <c>Palette.X</c> call-sites keep working — only the source of the colours moved (into
/// <c>Perch.Core</c>'s <see cref="Theme"/>).
///
/// <para>Colours are exposed as both <see cref="Color"/> (owner-drawn <c>DrawingContext</c> work) and cached
/// <see cref="SolidColorBrush"/> fills. The cached brushes are mutated <em>in place</em> by
/// <see cref="Apply"/> on a theme change, so any surface holding a brush reference repaints in the new
/// colour (owner-drawn surfaces additionally invalidate; see the theme service that drives the swap).</para>
/// </summary>
public static class Palette
{
    /// <summary>The active theme every chrome/text/accent member reads from. Defaults to
    /// <see cref="Themes.Midnight"/>; swapped via <see cref="Apply"/>.</summary>
    public static Theme Active { get; private set; } = Themes.Midnight;

    /// <summary>The theme-independent brand/status/semantic palette (see <see cref="FixedColors"/>). Normally
    /// <see cref="FixedColors.Default"/>; only the theme designer's colour-blind preview swaps it for a
    /// simulated view.</summary>
    public static FixedColors Fixed { get; private set; } = FixedColors.Default;

    /// <summary>Switch the active theme and re-point every cached brush at its new colour. Idempotent.
    /// This is the single mutation point for the whole app's cached fills — the overlay and windows alias
    /// these brushes, so one call re-colours them all (surfaces then invalidate to repaint). Pass a
    /// <paramref name="cvd"/> other than <see cref="CvdType.None"/> for the designer's colour-blind preview:
    /// it simulates both the theme roles and the fixed palette.</summary>
    public static void Apply(Theme theme, CvdType cvd = CvdType.None)
    {
        var t = CvdSim.Simulate(theme, cvd);
        Active = t;
        Fixed  = FixedColors.Default.Simulate(cvd);

        // Core chrome / text (from the theme).
        FormBgBrush.Color   = t.Surface.ToColor();
        FgBrush.Color       = t.TextPrimary.ToColor();
        TitleBrush.Color    = t.TextTitle.ToColor();
        MutedBrush.Color    = t.TextMuted.ToColor();
        AccentBrush.Color   = t.Accent.ToColor();
        OnAccentBrush.Color = Contrast.BestForeground(t.Accent).ToColor();
        BorderBrush.Color   = t.Border.ToColor();
        ButtonBgBrush.Color = t.SurfaceRaised.ToColor();
        TrackBrush.Color    = t.Track.ToColor();
        // Extended chrome roles, aliased by the overlay/windows.
        SurfaceSunkenBrush.Color  = t.SurfaceSunken.ToColor();
        OverlaySurfaceBrush.Color = t.OverlaySurface.ToColor();
        OverlayScrimBrush.Color   = t.OverlaySurface.ToColor(ScrimAlpha);
        OverlayRowHoverBrush.Color = t.OverlayRowHover.ToColor();
        SeparatorBrush.Color   = t.Separator.ToColor();
        TreeLineBrush.Color    = t.TreeLine.ToColor();
        FocusRingBrush.Color   = t.FocusRing.ToColor();

        // Brand / status / semantic accents (theme-independent — from the fixed palette).
        BrandBrush.Color       = Fixed.Brand.ToColor();
        BrandHoverBrush.Color  = Fixed.BrandHover.ToColor();
        RunningBrush.Color     = Fixed.StatusRunning.ToColor();
        AttentionBrush.Color   = Fixed.StatusAttention.ToColor();
        AwaitingBrush.Color    = Fixed.StatusAwaiting.ToColor();
        ErrorBrush.Color       = Fixed.StatusError.ToColor();
        WarnBrush.Color        = Fixed.StatusWarn.ToColor();
        SubAgentBrush.Color    = Fixed.SubAgent.ToColor();
        TealBrush.Color        = Fixed.Teal.ToColor();
        BurnBrush.Color        = Fixed.Burn.ToColor();
        JiraBrush.Color        = Fixed.Jira.ToColor();
        TeamGrayBrush.Color    = Fixed.TeamGray.ToColor();
    }

    // The overlay panel is painted as a translucent scrim over the desktop; this is its fixed alpha.
    private const byte ScrimAlpha = 245;

    // ── Chrome / text ──────────────────────────────────────────────────────────
    public static Color FormBg      => Active.Surface.ToColor();
    public static Color Fg          => Active.TextPrimary.ToColor();
    public static Color Title       => Active.TextTitle.ToColor();
    public static Color Muted       => Active.TextMuted.ToColor();
    public static Color Accent      => Active.Accent.ToColor();
    public static Color AccentHover => Active.AccentHover.ToColor();
    // Black or white, whichever reads on the current accent — the foreground for accent-filled buttons/chips.
    public static Color OnAccent    => Contrast.BestForeground(Active.Accent).ToColor();
    public static Color Border      => Active.Border.ToColor();
    public static Color ButtonBg    => Active.SurfaceRaised.ToColor();
    public static Color ButtonHover => Active.SurfaceRaisedHover.ToColor();
    public static Color Sunken      => Active.SurfaceSunken.ToColor();
    public static Color Danger      => Fixed.Danger.ToColor();
    public static Color FocusRing   => Active.FocusRing.ToColor();

    // The perch-logo red-orange, used to draw attention to the update affordances so they read as one accent.
    public static Color Brand       => Fixed.Brand.ToColor();

    // Jira brand blue for the ticket deep-link glyph; a fixed brand hue, the same under every theme.
    public static Color Jira        => Fixed.Jira.ToColor();

    // Usage bar / status palette (same thresholds the overlay uses).
    public static Color Green        => Fixed.StatusRunning.ToColor();
    public static Color Yellow       => Fixed.StatusAwaiting.ToColor();
    public static Color Orange       => Fixed.StatusAttention.ToColor();
    public static Color Red          => Fixed.StatusError.ToColor();
    public static Color Track        => Active.Track.ToColor();
    public static Color ExpectedMark => Active.ExpectedMark.ToColor();
    public static Color Idle         => Fixed.StatusIdle.ToColor();

    // A neutral accent for teammates with no (or an unknown) colour — the overlay's sub-agent purple.
    public static Color TeamDefault  => Fixed.SubAgent.ToColor();

    public static Color ModeColor(PermissionMode m) => m switch
    {
        PermissionMode.AcceptEdits => Fixed.ModeAcceptEdits.ToColor(),
        PermissionMode.Plan        => Active.Accent.ToColor(),
        PermissionMode.Auto        => Fixed.StatusAwaiting.ToColor(),
        PermissionMode.Bypass      => Fixed.StatusError.ToColor(),
        _                          => Colors.Transparent,
    };

    public static Color UsageColor(double pct) => pct switch
    {
        < 50 => Green,
        < 75 => Yellow,
        < 90 => Orange,
        _    => Red,
    };

    // Maps an Agent-Teams member colour name onto the shared palette so a given teammate is tinted the
    // same way everywhere. Unknown/missing names fall back to the neutral team accent.
    public static Color TeamColor(string? name) => name?.Trim().ToLowerInvariant() switch
    {
        "green"                          => Green,
        "yellow"                         => Yellow,
        "orange"                         => Orange,
        "red"                            => Red,
        "blue"                           => Accent,
        "cyan" or "teal"                 => Fixed.Teal.ToColor(),
        "magenta" or "pink" or "purple"  => Fixed.SubAgent.ToColor(),
        "gray" or "grey"                 => Fixed.TeamGray.ToColor(),
        _                                => TeamDefault,
    };

    public static Color Blend(Color a, Color b, float t) => Color.FromRgb(
        (byte)(a.R * (1 - t) + b.R * t),
        (byte)(a.G * (1 - t) + b.G * t),
        (byte)(a.B * (1 - t) + b.B * t));

    // ── Cached brushes for the most-used fills (owner-draw + XAML code-behind) ──
    // Mutated in place by Apply(); seeded from the default theme.
    public static readonly SolidColorBrush FormBgBrush   = new(Themes.Midnight.Surface.ToColor());
    public static readonly SolidColorBrush FgBrush       = new(Themes.Midnight.TextPrimary.ToColor());
    public static readonly SolidColorBrush TitleBrush    = new(Themes.Midnight.TextTitle.ToColor());
    public static readonly SolidColorBrush MutedBrush    = new(Themes.Midnight.TextMuted.ToColor());
    public static readonly SolidColorBrush AccentBrush   = new(Themes.Midnight.Accent.ToColor());
    public static readonly SolidColorBrush OnAccentBrush = new(Contrast.BestForeground(Themes.Midnight.Accent).ToColor());
    public static readonly SolidColorBrush BorderBrush   = new(Themes.Midnight.Border.ToColor());
    public static readonly SolidColorBrush ButtonBgBrush = new(Themes.Midnight.SurfaceRaised.ToColor());
    public static readonly SolidColorBrush TrackBrush    = new(Themes.Midnight.Track.ToColor());

    // ── Extended chrome brushes (aliased by the overlay + windows so one Apply() re-colours everything) ──
    public static readonly SolidColorBrush SurfaceSunkenBrush   = new(Themes.Midnight.SurfaceSunken.ToColor());
    public static readonly SolidColorBrush OverlaySurfaceBrush  = new(Themes.Midnight.OverlaySurface.ToColor());
    public static readonly SolidColorBrush OverlayScrimBrush    = new(Themes.Midnight.OverlaySurface.ToColor(ScrimAlpha));
    public static readonly SolidColorBrush OverlayRowHoverBrush = new(Themes.Midnight.OverlayRowHover.ToColor());
    public static readonly SolidColorBrush SeparatorBrush = new(Themes.Midnight.Separator.ToColor());
    public static readonly SolidColorBrush TreeLineBrush  = new(Themes.Midnight.TreeLine.ToColor());
    public static readonly SolidColorBrush FocusRingBrush = new(Themes.Midnight.FocusRing.ToColor());

    // ── Fixed brand/status/semantic brushes (theme-independent; only the CVD preview re-colours them) ──
    public static readonly SolidColorBrush BrandBrush     = new(FixedColors.Default.Brand.ToColor());
    public static readonly SolidColorBrush BrandHoverBrush = new(FixedColors.Default.BrandHover.ToColor());
    public static readonly SolidColorBrush RunningBrush   = new(FixedColors.Default.StatusRunning.ToColor());
    public static readonly SolidColorBrush AttentionBrush = new(FixedColors.Default.StatusAttention.ToColor());
    public static readonly SolidColorBrush AwaitingBrush  = new(FixedColors.Default.StatusAwaiting.ToColor());
    public static readonly SolidColorBrush ErrorBrush     = new(FixedColors.Default.StatusError.ToColor());
    public static readonly SolidColorBrush WarnBrush      = new(FixedColors.Default.StatusWarn.ToColor());
    public static readonly SolidColorBrush SubAgentBrush  = new(FixedColors.Default.SubAgent.ToColor());
    public static readonly SolidColorBrush TealBrush      = new(FixedColors.Default.Teal.ToColor());
    public static readonly SolidColorBrush BurnBrush      = new(FixedColors.Default.Burn.ToColor());
    public static readonly SolidColorBrush JiraBrush      = new(FixedColors.Default.Jira.ToColor());
    public static readonly SolidColorBrush TeamGrayBrush  = new(FixedColors.Default.TeamGray.ToColor());
}
