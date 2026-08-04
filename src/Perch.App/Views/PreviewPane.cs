using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Perch.Avalonia.Rendering;
using Perch.Avalonia.Services;
using Perch.Avalonia.Theming;
using Perch.Data;

namespace Perch.Avalonia.Views;

/// <summary>
/// A miniature, display-only copy of the overlay for the redesigned Settings window — the "see it before
/// you set it" pane. It hosts a real <see cref="OverlayCanvas"/> seeded with <see cref="SampleData"/> and
/// re-gates it through <see cref="OverlaySettingsGates"/> whenever <see cref="Apply"/> is handed a settings
/// snapshot, so the preview and the live overlay render from identical code and can't drift.
///
/// <para>The embedded canvas is deliberately inert: it's never put in dense mode and its
/// <see cref="OverlayCanvas.OwnerWindow"/> stays null (so the window-coupled relayout paths are no-ops),
/// and hit-testing is off so it's a picture, not a control. A <see cref="Viewbox"/> scales the canvas's
/// natural 280-dip width down to <see cref="PreviewWidth"/>, staying crisp at any DPI (it never upscales).</para>
/// </summary>
internal sealed class PreviewPane : Border
{
    // The miniature's target width in DIP. The canvas draws itself at 280; the Viewbox scales down to this.
    private const double PreviewWidth = 244;

    private readonly OverlayCanvas _canvas = new();

    // A translucent accent ring drawn over the preview; a quick pulse of it draws the eye to the miniature
    // when a catalogue card is hovered.
    private readonly Border _flash = new()
    {
        BorderBrush = Palette.AccentBrush, BorderThickness = new Thickness(2.5),
        CornerRadius = new CornerRadius(8), Background = null, IsHitTestVisible = false, Opacity = 0,
    };

    private readonly Animation _pulse = new()
    {
        Duration = TimeSpan.FromMilliseconds(720),
        Easing = new CubicEaseOut(),
        Children =
        {
            new KeyFrame { Cue = new Cue(0d), Setters = { new Setter(Visual.OpacityProperty, 0.85) } },
            new KeyFrame { Cue = new Cue(1d), Setters = { new Setter(Visual.OpacityProperty, 0.0) } },
        },
    };

    public PreviewPane()
    {
        // Seed once with the shared sample overlay state — the same rows/usage/metrics the render harness
        // uses, so every glyph a setting can toggle is present to be shown or hidden.
        _canvas.Update(SampleData.Sessions());
        _canvas.UpdateUsage(SampleData.Usage());
        _canvas.UpdateSystemMetrics(SampleData.SystemMetrics());
        _canvas.UpdateSessionMetrics(SampleData.SessionMetrics());
        _canvas.SetDaemonWorkers(SampleData.DaemonWorkers());
        _canvas.UpdateMedia(SampleData.Media());
        _canvas.UpdateMic(SampleData.Mic());
        _canvas.IsHitTestVisible = false;   // display-only: no clicks, no dense drag, no hovers

        var box = new Viewbox
        {
            Child = _canvas,
            Stretch = Stretch.Uniform,
            StretchDirection = StretchDirection.DownOnly,
            Width = PreviewWidth,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
        };

        // The flash ring overlays the miniature: same width, stretched to the scaled canvas's height (the
        // Grid sizes to the Viewbox, so a stretched ring traces the preview's bounds).
        _flash.HorizontalAlignment = HorizontalAlignment.Center;
        _flash.VerticalAlignment = VerticalAlignment.Stretch;
        _flash.Width = PreviewWidth;

        Child = new Grid { Children = { box, _flash } };

        Padding = new Thickness(12);
        Background = Palette.FormBgBrush;
    }

    /// <summary>Re-gates the preview overlay to reflect <paramref name="settings"/> (typically a working clone).</summary>
    public void Apply(AppSettings settings) => OverlaySettingsGates.Apply(_canvas, settings);

    /// <summary>
    /// Draws the eye to the preview when a card for a visual setting is hovered — a quick accent-ring pulse
    /// around the miniature. Settings with no overlay glyph (<see cref="PreviewTarget.None"/>) don't pulse,
    /// since there's nothing new to notice. (A pane-level cue, not a per-glyph highlight — the latter would
    /// need the owner-drawn canvas to expose each glyph's rect.)
    /// </summary>
    public void Highlight(PreviewTarget target)
    {
        if (target == PreviewTarget.None) return;
        _ = _pulse.RunAsync(_flash);
    }
}
