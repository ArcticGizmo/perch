using System.Text.Json.Serialization;

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
/// <para><b>Design rule:</b> a theme tints <em>neutrals, chrome and the accent</em> — and nothing else. The
/// brand, the semantic status hues (running/attention/awaiting/idle/error) and the teammate/mode accents
/// carry fixed meaning across every theme, so they don't live here at all: see <see cref="FixedColors"/>.
/// A theme therefore stores only what a user can actually change.</para>
/// </summary>
[JsonConverter(typeof(ThemeJsonConverter))]
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

    // ── Accent ───────────────────────────────────────────────────────────────
    /// <summary>Primary accent (links, selection, nav/cycle, remote).</summary>
    public Rgb Accent { get; init; }
    /// <summary>Hover/brightened accent.</summary>
    public Rgb AccentHover { get; init; }
    /// <summary>Keyboard-focus ring — a bright, high-contrast outline drawn on the focused control.</summary>
    public Rgb FocusRing { get; init; }

    // The brand red-orange, the destructive-action red, the semantic status hues and the teammate/mode
    // accents are theme-independent — they live in FixedColors, not here (see the design rule above).
}
