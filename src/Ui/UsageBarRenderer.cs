namespace Perch.Ui;

/// <summary>
/// The single renderer for a labelled rate-limit usage bar — caption on the left, a rounded track,
/// a fill coloured by the percentage (<see cref="Theme.UsageColor"/>), the percentage text
/// right-aligned, and an optional expected-rate marker — dimmed toward a background colour when the
/// reading is stale. The overlay's compact strip and the settings <c>UsageBarsControl</c> drew this
/// the same way but at different widths, fonts and shades; every one of those differences is a
/// parameter here, so both call sites stay pixel-identical to what they drew before.
/// </summary>
internal static class UsageBarRenderer
{
    /// <summary>
    /// Draws one usage bar spanning <paramref name="left"/>…<paramref name="right"/>, vertically
    /// centred on <paramref name="midY"/>. The caption occupies <paramref name="captionW"/> on the
    /// left and the percentage text <paramref name="pctW"/> on the right; the track fills the gap.
    /// A null <paramref name="percent"/> renders an em-dash; a null <paramref name="expectedPct"/>
    /// hides the marker. When <paramref name="stale"/>, every colour is blended toward
    /// <paramref name="bgBlend"/>. Fonts are owned by the caller.
    /// </summary>
    public static void Draw(
        Graphics g, int left, int right, int midY,
        string caption, double? percent, double? expectedPct, bool stale,
        Font capFont, Font pctFont,
        Color muted, Color track, Color expectedMark, Color bgBlend,
        int captionW, int pctW, int trackH)
    {
        // Caption (left)
        Color capColor = stale ? Theme.Blend(muted, bgBlend, 0.5f) : muted;
        using (var capBrush = new SolidBrush(capColor))
        {
            var capSz = g.MeasureString(caption, capFont);
            g.DrawString(caption, capFont, capBrush, left, midY - capSz.Height / 2);
        }

        // Track
        int trackLeft  = left + captionW;
        int trackRight = right - pctW;
        int trackW     = Math.Max(0, trackRight - trackLeft);
        int trackY     = midY - trackH / 2;

        Color trackColor = stale ? Theme.Blend(track, bgBlend, 0.4f) : track;
        using (var trackBrush = new SolidBrush(trackColor))
            PaintKit.FillRoundedBar(g, trackBrush, trackLeft, trackY, trackW, trackH);

        // Fill + percentage text
        string pctText;
        Color textColor;
        if (percent is { } p)
        {
            double clamped = Math.Clamp(p, 0, 100);
            Color barColor = Theme.UsageColor(clamped);
            if (stale) barColor = Theme.Blend(barColor, bgBlend, 0.5f);

            int fillW = (int)Math.Round(trackW * clamped / 100.0);
            if (fillW > 0)
                using (var fillBrush = new SolidBrush(barColor))
                    PaintKit.FillRoundedBar(g, fillBrush, trackLeft, trackY, fillW, trackH);

            pctText   = $"{(int)Math.Round(clamped)}%";
            textColor = barColor;
        }
        else
        {
            pctText   = "—";
            textColor = capColor;
        }

        // Expected-rate marker: thin vertical bar at the elapsed-time position. It turns red once
        // actual usage has pulled ahead of the expected pace — but only once the window is at least
        // 5% elapsed, so it doesn't flip red on the first sip of usage while the expected line still
        // sits near zero (where any reading trivially "exceeds" it).
        if (expectedPct is { } ep && trackW > 0)
        {
            int markerX = trackLeft + (int)Math.Round(trackW * ep / 100.0);
            bool overRate  = ep >= 5 && percent is { } actual && actual > ep;
            Color baseMark = overRate ? Theme.Red : expectedMark;
            Color markerColor = stale ? Theme.Blend(baseMark, bgBlend, 0.5f) : baseMark;
            using var markerBrush = new SolidBrush(markerColor);
            g.FillRectangle(markerBrush, markerX - 1, trackY - 1, 2, trackH + 2);
        }

        using var textBrush = new SolidBrush(textColor);
        var txtSz = g.MeasureString(pctText, pctFont);
        g.DrawString(pctText, pctFont, textBrush, right - txtSz.Width, midY - txtSz.Height / 2);
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
