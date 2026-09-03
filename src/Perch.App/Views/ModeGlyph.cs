using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Perch.Avalonia.Theming;
using Perch.Data;

namespace Perch.Avalonia.Views;

/// <summary>
/// The permission-mode badge as a small control — the same double-chevron glyph the overlay paints on a
/// session row (<see cref="OverlayCanvas.DrawModeChevrons"/>), in the same <see cref="Palette.ModeColor"/>,
/// so the session window's mode pill and its menu read identically to the session runner. The default
/// ("Normal") mode has no colour of its own on the overlay (no badge); here it draws in <see cref="Neutral"/>
/// so the pill still shows a glyph.
/// </summary>
internal sealed class ModeGlyph : Control
{
    public static readonly StyledProperty<PermissionMode> ModeProperty =
        AvaloniaProperty.Register<ModeGlyph, PermissionMode>(nameof(Mode));

    public PermissionMode Mode
    {
        get => GetValue(ModeProperty);
        set => SetValue(ModeProperty, value);
    }

    /// <summary>The brush for the default mode, which the palette leaves transparent.</summary>
    public IBrush Neutral { get; set; } = Brushes.Gray;

    /// <summary>The brush for plan mode. The overlay paints plan in the theme's accent, which is blue in the
    /// default theme but not in every theme; the session UI pins it to its own blue so plan always reads as
    /// plan (and never as the amber "awaiting" colour).</summary>
    public IBrush Plan { get; set; } = Brushes.DodgerBlue;

    public ModeGlyph()
    {
        Width = 12;
        Height = 10;
        AffectsRender<ModeGlyph>(ModeProperty);
    }

    /// <summary>The brush the overlay uses for this mode (neutral for the default mode, the given blue for plan).</summary>
    public static IBrush BrushFor(PermissionMode mode, IBrush neutral, IBrush plan) => mode switch
    {
        PermissionMode.Normal => neutral,
        PermissionMode.Plan   => plan,
        _                     => new SolidColorBrush(Palette.ModeColor(mode)),
    };

    /// <summary>Maps the CLI's mode token to the overlay's enum.</summary>
    public static PermissionMode Parse(string? mode) => mode switch
    {
        "acceptEdits"       => PermissionMode.AcceptEdits,
        "plan"              => PermissionMode.Plan,
        "bypassPermissions" => PermissionMode.Bypass,
        "auto"              => PermissionMode.Auto,
        _                   => PermissionMode.Normal,
    };

    public override void Render(DrawingContext ctx)
    {
        OverlayCanvas.DrawModeChevrons(ctx, BrushFor(Mode, Neutral, Plan), 0.5, Bounds.Height / 2);
    }
}
