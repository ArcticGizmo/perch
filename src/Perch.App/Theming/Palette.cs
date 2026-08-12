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
    /// <summary>The active theme every member reads from. Defaults to <see cref="Themes.Midnight"/>; swapped
    /// via <see cref="Apply"/>.</summary>
    public static Theme Active { get; private set; } = Themes.Midnight;

    /// <summary>Switch the active theme and re-point every cached brush at its new colour. Idempotent.
    /// This is the single mutation point for the whole app's cached fills — the overlay and windows alias
    /// these brushes, so one call re-colours them all (surfaces then invalidate to repaint).</summary>
    public static void Apply(Theme theme)
    {
        Active = theme;
        // Core chrome / text.
        FormBgBrush.Color   = theme.Surface.ToColor();
        FgBrush.Color       = theme.TextPrimary.ToColor();
        TitleBrush.Color    = theme.TextTitle.ToColor();
        MutedBrush.Color    = theme.TextMuted.ToColor();
        AccentBrush.Color   = theme.Accent.ToColor();
        OnAccentBrush.Color = Contrast.BestForeground(theme.Accent).ToColor();
        BorderBrush.Color   = theme.Border.ToColor();
        ButtonBgBrush.Color = theme.SurfaceRaised.ToColor();
        TrackBrush.Color    = theme.Track.ToColor();
        BrandBrush.Color    = theme.Brand.ToColor();
        // Extended roles (overlay chrome + status), aliased by the overlay/windows.
        SurfaceSunkenBrush.Color  = theme.SurfaceSunken.ToColor();
        OverlaySurfaceBrush.Color = theme.OverlaySurface.ToColor();
        OverlayScrimBrush.Color   = theme.OverlaySurface.ToColor(ScrimAlpha);
        OverlayRowHoverBrush.Color = theme.OverlayRowHover.ToColor();
        SeparatorBrush.Color   = theme.Separator.ToColor();
        TreeLineBrush.Color    = theme.TreeLine.ToColor();
        BrandHoverBrush.Color  = theme.BrandHover.ToColor();
        RunningBrush.Color     = theme.StatusRunning.ToColor();
        AttentionBrush.Color   = theme.StatusAttention.ToColor();
        AwaitingBrush.Color    = theme.StatusAwaiting.ToColor();
        ErrorBrush.Color       = theme.StatusError.ToColor();
        WarnBrush.Color        = theme.StatusWarn.ToColor();
        SubAgentBrush.Color    = theme.SubAgent.ToColor();
        TealBrush.Color        = theme.Teal.ToColor();
        BurnBrush.Color        = theme.Burn.ToColor();
        JiraBrush.Color        = theme.Jira.ToColor();
        TeamGrayBrush.Color    = theme.TeamGray.ToColor();
        FocusRingBrush.Color   = theme.FocusRing.ToColor();
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
    public static Color Danger      => Active.Danger.ToColor();
    public static Color FocusRing   => Active.FocusRing.ToColor();

    // The perch-logo red-orange, used to draw attention to the update affordances so they read as one accent.
    public static Color Brand       => Active.Brand.ToColor();

    // Jira brand blue for the ticket deep-link glyph; a fixed brand hue, stable across themes.
    public static Color Jira        => Active.Jira.ToColor();

    // Usage bar / status palette (same thresholds the overlay uses).
    public static Color Green        => Active.StatusRunning.ToColor();
    public static Color Yellow       => Active.StatusAwaiting.ToColor();
    public static Color Orange       => Active.StatusAttention.ToColor();
    public static Color Red          => Active.StatusError.ToColor();
    public static Color Track        => Active.Track.ToColor();
    public static Color ExpectedMark => Active.ExpectedMark.ToColor();
    public static Color Idle         => Active.StatusIdle.ToColor();

    // A neutral accent for teammates with no (or an unknown) colour — the overlay's sub-agent purple.
    public static Color TeamDefault  => Active.SubAgent.ToColor();

    public static Color ModeColor(PermissionMode m) => m switch
    {
        PermissionMode.AcceptEdits => Active.ModeAcceptEdits.ToColor(),
        PermissionMode.Plan        => Active.Accent.ToColor(),
        PermissionMode.Auto        => Active.StatusAwaiting.ToColor(),
        PermissionMode.Bypass      => Active.StatusError.ToColor(),
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
        "cyan" or "teal"                 => Active.Teal.ToColor(),
        "magenta" or "pink" or "purple"  => Active.SubAgent.ToColor(),
        "gray" or "grey"                 => Active.TeamGray.ToColor(),
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
    public static readonly SolidColorBrush BrandBrush    = new(Themes.Midnight.Brand.ToColor());

    // ── Extended role brushes (aliased by the overlay + windows so one Apply() re-colours everything) ──
    public static readonly SolidColorBrush SurfaceSunkenBrush   = new(Themes.Midnight.SurfaceSunken.ToColor());
    public static readonly SolidColorBrush OverlaySurfaceBrush  = new(Themes.Midnight.OverlaySurface.ToColor());
    public static readonly SolidColorBrush OverlayScrimBrush    = new(Themes.Midnight.OverlaySurface.ToColor(ScrimAlpha));
    public static readonly SolidColorBrush OverlayRowHoverBrush = new(Themes.Midnight.OverlayRowHover.ToColor());
    public static readonly SolidColorBrush SeparatorBrush = new(Themes.Midnight.Separator.ToColor());
    public static readonly SolidColorBrush TreeLineBrush  = new(Themes.Midnight.TreeLine.ToColor());
    public static readonly SolidColorBrush BrandHoverBrush = new(Themes.Midnight.BrandHover.ToColor());
    public static readonly SolidColorBrush RunningBrush   = new(Themes.Midnight.StatusRunning.ToColor());
    public static readonly SolidColorBrush AttentionBrush = new(Themes.Midnight.StatusAttention.ToColor());
    public static readonly SolidColorBrush AwaitingBrush  = new(Themes.Midnight.StatusAwaiting.ToColor());
    public static readonly SolidColorBrush ErrorBrush     = new(Themes.Midnight.StatusError.ToColor());
    public static readonly SolidColorBrush WarnBrush      = new(Themes.Midnight.StatusWarn.ToColor());
    public static readonly SolidColorBrush SubAgentBrush  = new(Themes.Midnight.SubAgent.ToColor());
    public static readonly SolidColorBrush TealBrush      = new(Themes.Midnight.Teal.ToColor());
    public static readonly SolidColorBrush BurnBrush      = new(Themes.Midnight.Burn.ToColor());
    public static readonly SolidColorBrush JiraBrush      = new(Themes.Midnight.Jira.ToColor());
    public static readonly SolidColorBrush TeamGrayBrush  = new(Themes.Midnight.TeamGray.ToColor());
    public static readonly SolidColorBrush FocusRingBrush = new(Themes.Midnight.FocusRing.ToColor());
}
