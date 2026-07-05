using Avalonia;
using Avalonia.Media;
using Perch.Avalonia.Theming;

namespace Perch.Avalonia.Rendering;

/// <summary>
/// The single renderer for a labelled rate-limit usage bar — caption on the left, a rounded track, a
/// fill coloured by the percentage (<see cref="Palette.UsageColor"/>), the percentage text right-aligned,
/// and an optional expected-rate marker — dimmed toward a background colour when the reading is stale.
/// The Avalonia port of the WinForms <c>UsageBarRenderer</c>: the overlay's compact strip and (later) the
/// Settings usage bars draw the same way at different widths/fonts/shades, so every one of those
/// differences is a parameter here and both call sites stay pixel-identical to their WinForms originals.
/// </summary>
internal static class UsageBarRenderer
{
    /// <summary>
    /// Draws one usage bar spanning <paramref name="left"/>…<paramref name="right"/>, vertically centred
    /// on <paramref name="midY"/>. The caption occupies <paramref name="captionW"/> on the left and the
    /// percentage text <paramref name="pctW"/> on the right; the track fills the gap. A null
    /// <paramref name="percent"/> renders an em-dash; a null <paramref name="expectedPct"/> hides the
    /// marker. When <paramref name="stale"/>, every colour is blended toward <paramref name="bgBlend"/>.
    /// </summary>
    public static void Draw(
        DrawingContext ctx, double left, double right, double midY,
        string caption, double? percent, double? expectedPct, bool stale,
        double capSize, double pctSize,
        Color muted, Color track, Color expectedMark, Color bgBlend,
        double captionW, double pctW, double trackH)
    {
        // Caption (left)
        Color capColor = stale ? Palette.Blend(muted, bgBlend, 0.5f) : muted;
        OverlayDraw.TextLeftMid(ctx, OverlayDraw.Text(caption, capSize, new SolidColorBrush(capColor)), left, midY);

        // Track
        double trackLeft  = left + captionW;
        double trackRight = right - pctW;
        double trackW     = Math.Max(0, trackRight - trackLeft);
        double trackY     = midY - trackH / 2;

        Color trackColor = stale ? Palette.Blend(track, bgBlend, 0.4f) : track;
        OverlayDraw.Pill(ctx, new SolidColorBrush(trackColor), new Rect(trackLeft, trackY, trackW, trackH));

        // Fill + percentage text
        string pctText;
        Color textColor;
        if (percent is { } p)
        {
            double clamped = Math.Clamp(p, 0, 100);
            Color barColor = Palette.UsageColor(clamped);
            if (stale) barColor = Palette.Blend(barColor, bgBlend, 0.5f);

            double fillW = Math.Round(trackW * clamped / 100.0);
            if (fillW > 0)
                OverlayDraw.Pill(ctx, new SolidColorBrush(barColor), new Rect(trackLeft, trackY, fillW, trackH));

            pctText   = $"{(int)Math.Round(clamped)}%";
            textColor = barColor;
        }
        else
        {
            pctText   = "—";
            textColor = capColor;
        }

        // Expected-rate marker: thin vertical bar at the elapsed-time position. It turns red once actual
        // usage has pulled ahead of the expected pace — but only once the window is at least 5% elapsed,
        // so it doesn't flip red on the first sip of usage while the expected line still sits near zero
        // (where any reading trivially "exceeds" it).
        if (expectedPct is { } ep && trackW > 0)
        {
            double markerX = trackLeft + Math.Round(trackW * ep / 100.0);
            bool overRate  = ep >= 5 && percent is { } actual && actual > ep;
            Color baseMark = overRate ? Palette.Red : expectedMark;
            Color markerColor = stale ? Palette.Blend(baseMark, bgBlend, 0.5f) : baseMark;
            ctx.DrawRectangle(new SolidColorBrush(markerColor), null,
                new Rect(markerX - 1, trackY - 1, 2, trackH + 2));
        }

        var pctFt = OverlayDraw.Text(pctText, pctSize, new SolidColorBrush(textColor), FontWeight.Bold);
        OverlayDraw.TextLeftMid(ctx, pctFt, right - pctFt.Width, midY);
    }

    /// <summary>The fraction (0–100) of a rate-limit window that has elapsed, used to place the
    /// expected-rate marker. Null when the reset time is unknown.</summary>
    public static double? ElapsedPercent(DateTime? resetsAt, TimeSpan window)
    {
        if (resetsAt is null) return null;
        var elapsed = DateTime.Now - (resetsAt.Value - window);
        return Math.Clamp(elapsed.TotalSeconds / window.TotalSeconds * 100.0, 0, 100);
    }
}
