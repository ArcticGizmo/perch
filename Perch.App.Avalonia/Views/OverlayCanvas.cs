using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Perch.Avalonia.Rendering;
using Perch.Data;

namespace Perch.Avalonia.Views;

/// <summary>
/// The owner-drawn overlay body — the Avalonia port of <c>OverlayForm</c>'s painting, replacing the
/// thin-vertical XAML <c>OverlayView</c>. A single <see cref="Draw"/> routine both measures (returns the
/// content height when given a null context) and paints (when given a real one), so the measured height
/// and painted layout can never drift — the same measure-or-paint discipline the WinForms dashboards use.
///
/// Built up over Phase 4: this step (4.1) draws only the rounded panel; the header, rows, sub-agents,
/// glyphs, bars, and interaction land in the following steps.
/// </summary>
public sealed class OverlayCanvas : Control
{
    // ── Layout (mirrors OverlayForm's constants) ──────────────────────────────
    private const double FormWidth    = 280;
    private const double HeaderHeight = 44;
    private const double Corner       = 10;
    private const double HorizPad     = 12;

    // ── Palette (the overlay's own, darker than the settings palette) ─────────
    private static readonly IBrush BgBrush   = new SolidColorBrush(Color.FromArgb(245, 15, 15, 20));
    private static readonly IPen   BorderPen = new Pen(new SolidColorBrush(Color.FromRgb(45, 45, 60)), 1);

    private IReadOnlyList<ClaudeSession> _sessions = [];

    /// <summary>Feeds the latest session list (called on the UI thread by the monitor host) and
    /// repaints. Stored now; rendered as rows from step 4.3 onward.</summary>
    public void Update(IReadOnlyList<ClaudeSession> sessions)
    {
        _sessions = sessions;
        InvalidateMeasure();
        InvalidateVisual();
    }

    protected override Size MeasureOverride(Size availableSize)
        => new(FormWidth, Draw(null, FormWidth));

    public override void Render(DrawingContext ctx) => Draw(ctx, Bounds.Width);

    // Measure-or-paint: returns the content height; paints only when ctx is non-null.
    private double Draw(DrawingContext? ctx, double width)
    {
        double height = HeaderHeight + HorizPad; // placeholder until rows land (4.3)

        if (ctx != null)
        {
            // Inset by 0.5 so the 1px border renders crisp rather than straddling a pixel boundary.
            var panel = new Rect(0.5, 0.5, width - 1, height - 1);
            OverlayDraw.Panel(ctx, panel, BgBrush, BorderPen, Corner);
        }

        return height;
    }
}
