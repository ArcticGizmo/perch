using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Perch.Avalonia.Views;

/// <summary>
/// The context-pressure thermometer as a small control — the same glass-tube-and-bulb glyph the overlay
/// paints on a session row (<see cref="OverlayCanvas.DrawThermo"/>), in the same green→yellow→orange→red
/// variants, at the same thresholds the floating overlay is configured with. Used beside the session
/// window's context pill so the two surfaces read identically. Set <see cref="Fill"/> (0..1) each turn and
/// mirror the thresholds from settings via <see cref="SetThresholds"/>.
/// </summary>
internal sealed class ThermoGlyph : Control
{
    public static readonly StyledProperty<float> FillProperty =
        AvaloniaProperty.Register<ThermoGlyph, float>(nameof(Fill));

    /// <summary>How full the context window is, 0..1.</summary>
    public float Fill
    {
        get => GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    // Fraction thresholds, mirrored from the overlay's context-pressure settings (defaults match
    // AppSettings.ContextPressure*Percent). Green below yellow, then warming yellow → orange → red.
    private float _yellow = 0.50f, _orange = 0.65f, _red = 0.80f;

    /// <summary>Mirrors the floating overlay's colour thresholds (whole percentages) onto this glyph.</summary>
    public void SetThresholds(int yellowPercent, int orangePercent, int redPercent)
    {
        _yellow = yellowPercent / 100f;
        _orange = orangePercent / 100f;
        _red    = redPercent    / 100f;
        InvalidateVisual();
    }

    /// <summary>This glyph's current variant colour — so a text readout beside it can be tinted to match.</summary>
    public Color VariantColor => OverlayCanvas.ThermoColor(Fill, _yellow, _orange, _red);

    /// <summary>The yellow threshold (0..1): the point at which the overlay first shows a coloured signal.
    /// Below it the glyph is green (the optional green-segment variant) or hidden.</summary>
    public float YellowThreshold => _yellow;

    public ThermoGlyph()
    {
        // The glyph spans midY−7..midY+8 (tube + bulb); Height 16 with the centre at 8 fits it exactly, and
        // ThermoIconWidth (12) matches the width the overlay reserves for it on a row.
        Width = 12;
        Height = 16;
        AffectsRender<ThermoGlyph>(FillProperty);
    }

    public override void Render(DrawingContext ctx)
        => OverlayCanvas.DrawThermo(ctx, Fill, _yellow, _orange, _red, 0, Bounds.Height / 2);
}
