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
    /// <paramref name="valueText"/>, when supplied, replaces the right-column percentage with an arbitrary
    /// caption (the spend bar's dollar figure) — the fill still tracks <paramref name="percent"/>.
    /// </summary>
    public static void Draw(
        DrawingContext ctx, double left, double right, double midY,
        string caption, double? percent, double? expectedPct, bool stale,
        double capSize, double pctSize,
        Color muted, Color track, Color expectedMark, Color bgBlend,
        double captionW, double pctW, double trackH,
        string? valueText = null)
    {
        // Caption (left). Clamped to its column with an ellipsis: captions are fixed strings for the
        // Session/Weekly bars, but a scoped bar is captioned with the model's display name from the
        // endpoint, which would otherwise run under the track.
        Color capColor = stale ? Palette.Blend(muted, bgBlend, 0.5f) : muted;
        var capFt = OverlayDraw.Text(caption, capSize, new SolidColorBrush(capColor));
        capFt.MaxTextWidth = Math.Max(0, captionW - 4);
        capFt.Trimming = TextTrimming.CharacterEllipsis;
        OverlayDraw.TextLeftMid(ctx, capFt, left, midY);

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
            // Pace-aware bars (a rate-limit window with an expected mark) colour by the gap to that mark;
            // bars with no expected mark (the credits spend bar) keep the absolute-level colouring.
            Color barColor = expectedPct is { } paceExp
                ? Palette.PaceColor(clamped, paceExp)
                : Palette.UsageColor(clamped);
            if (stale) barColor = Palette.Blend(barColor, bgBlend, 0.5f);

            double fillW = Math.Round(trackW * clamped / 100.0);
            if (fillW > 0)
                OverlayDraw.Pill(ctx, new SolidColorBrush(barColor), new Rect(trackLeft, trackY, fillW, trackH));

            // The spend bar passes a dollar caption (valueText) for the right column; the rate bars leave it
            // null and show the percentage. The fill and colour still come from the percentage either way.
            pctText   = valueText ?? $"{(int)Math.Round(clamped)}%";
            textColor = barColor;
        }
        else
        {
            pctText   = valueText ?? "—";
            textColor = capColor;
        }

        // Expected-rate marker: a thin neutral tick at the elapsed-time position, marking where usage "should"
        // be. It no longer recolours by over/under pace — the bar's own fill colour now carries that.
        if (expectedPct is { } ep && trackW > 0)
        {
            double markerX = trackLeft + Math.Round(trackW * ep / 100.0);
            Color markerColor = stale ? Palette.Blend(expectedMark, bgBlend, 0.5f) : expectedMark;
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
