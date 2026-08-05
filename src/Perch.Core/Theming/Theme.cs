namespace Perch.Theming;

/// <summary>
/// A complete Perch colour theme: every chrome, text, accent and semantic-status role the UI paints with,
/// as <see cref="Rgb"/> triples. This is the single source of truth the UI resolves colour through — the
/// overlay, the settings surface and every window read the <em>active</em> theme rather than hand-picked
/// literals (the pre-theme code kept three-plus divergent copies).
///
/// <para>Lives in <c>Perch.Core</c> and stays UI-free (see <see cref="Rgb"/>); the UI edge converts each
/// role to an Avalonia brush. A theme is immutable — build variants with a <c>with</c> expression off an
/// existing one (see <c>Themes</c>), and only override the roles that actually differ.</para>
///
/// <para><b>Design rule:</b> a theme tints <em>neutrals, chrome and the accent</em>. The semantic status
/// hues (running/attention/awaiting/idle/error) carry the overlay's glanceable meaning and keep their
/// identity across themes — a preset may nudge their lightness to hold contrast on a darker base, but not
/// their hue.</para>
/// </summary>
public sealed record Theme
{
    /// <summary>Stable id persisted in settings; keep it kebab-case and never reuse across meanings.</summary>
    public required string Id { get; init; }

    /// <summary>Human-facing name shown in the picker.</summary>
    public required string Name { get; init; }

    /// <summary>True for a dark theme (surface darker than text). Drives future light/dark-only logic.</summary>
    public bool IsDark { get; init; } = true;

    // ── Surfaces & chrome ──────────────────────────────────────────────────────
    /// <summary>Solid window background (settings and the other windows).</summary>
    public Rgb Surface { get; init; }
    /// <summary>Recessed surface: nav rails, list/body backgrounds, toolbars (darker than
    /// <see cref="Surface"/>).</summary>
    public Rgb SurfaceSunken { get; init; }
    /// <summary>Raised surface: cards, flat buttons, text inputs, badges.</summary>
    public Rgb SurfaceRaised { get; init; }
    /// <summary>Hover state of a raised surface / button.</summary>
    public Rgb SurfaceRaisedHover { get; init; }
    /// <summary>The floating overlay panel's background (darker than <see cref="Surface"/>; painted
    /// translucent at the draw site).</summary>
    public Rgb OverlaySurface { get; init; }
    /// <summary>Hover wash on an overlay session row.</summary>
    public Rgb OverlayRowHover { get; init; }
    /// <summary>Trough behind usage / metric bars (and the small count badges).</summary>
    public Rgb Track { get; init; }
    /// <summary>Control and panel border.</summary>
    public Rgb Border { get; init; }
    /// <summary>Hairline separator between rows / sections.</summary>
    public Rgb Separator { get; init; }
    /// <summary>The sub-agent / teammate tree connector line.</summary>
    public Rgb TreeLine { get; init; }

    // ── Text ───────────────────────────────────────────────────────────────────
    /// <summary>Primary body text.</summary>
    public Rgb TextPrimary { get; init; }
    /// <summary>Emphasised heading text.</summary>
    public Rgb TextTitle { get; init; }
    /// <summary>Secondary / muted text (must clear WCAG AA on its surface).</summary>
    public Rgb TextMuted { get; init; }
    /// <summary>The light neutral tick marking expected usage on a bar.</summary>
    public Rgb ExpectedMark { get; init; }

    // ── Accent & brand ─────────────────────────────────────────────────────────
    /// <summary>Primary accent (links, selection, nav/cycle, remote).</summary>
    public Rgb Accent { get; init; }
    /// <summary>Hover/brightened accent.</summary>
    public Rgb AccentHover { get; init; }
    /// <summary>The Perch brand red-orange (update affordances).</summary>
    public Rgb Brand { get; init; }
    /// <summary>Hover/brightened brand.</summary>
    public Rgb BrandHover { get; init; }
    /// <summary>Destructive-action colour (delete/reset text and buttons).</summary>
    public Rgb Danger { get; init; }
    /// <summary>Keyboard-focus ring — a bright, high-contrast outline drawn on the focused control.</summary>
    public Rgb FocusRing { get; init; }

    // ── Semantic status (theme-stable identity) ────────────────────────────────
    /// <summary>Session running / usage healthy (green).</summary>
    public Rgb StatusRunning { get; init; }
    /// <summary>Session needs attention / done (orange).</summary>
    public Rgb StatusAttention { get; init; }
    /// <summary>Session awaiting input / usage warning (yellow).</summary>
    public Rgb StatusAwaiting { get; init; }
    /// <summary>Session idle (slate).</summary>
    public Rgb StatusIdle { get; init; }
    /// <summary>Error / API failure / usage critical (red).</summary>
    public Rgb StatusError { get; init; }
    /// <summary>Stuck / caution glyph (amber).</summary>
    public Rgb StatusWarn { get; init; }
    /// <summary>Sub-agent / unknown-teammate accent (purple).</summary>
    public Rgb SubAgent { get; init; }
    /// <summary>Mail / teal-teammate accent.</summary>
    public Rgb Teal { get; init; }
    /// <summary>Token burn-rate readout (blue).</summary>
    public Rgb Burn { get; init; }
    /// <summary>Bot / grey-teammate neutral accent.</summary>
    public Rgb TeamGray { get; init; }
    /// <summary>The "Accept edits" permission-mode badge (blue-purple); the other modes reuse
    /// <see cref="Accent"/> / <see cref="StatusAwaiting"/> / <see cref="StatusError"/>.</summary>
    public Rgb ModeAcceptEdits { get; init; }
}
