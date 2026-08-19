using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
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
        _canvas.UpdateRoster(SampleData.Roster());
        _canvas.SetSocialAccount(signedIn: true, hasHandle: true);   // preview the signed-in (roster) state
        _canvas.IsHitTestVisible = false;   // display-only: no clicks, no dense drag, no hovers

        Child = new Viewbox
        {
            Child = _canvas,
            Stretch = Stretch.Uniform,
            StretchDirection = StretchDirection.DownOnly,
            Width = PreviewWidth,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
        };

        Padding = new Thickness(12);
        Background = Palette.FormBgBrush;
    }

    /// <summary>Re-gates the preview overlay to reflect <paramref name="settings"/> (typically a working clone).</summary>
    public void Apply(AppSettings settings) => OverlaySettingsGates.Apply(_canvas, settings);
}
