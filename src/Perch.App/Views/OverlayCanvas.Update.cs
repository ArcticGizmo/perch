using Avalonia;
using Avalonia.Media;
using Perch.Avalonia.Rendering;

namespace Perch.Avalonia.Views;

/// <summary>
/// The update progress strip: fixed chrome directly under the header, shown only while an update is
/// downloading or installing so it's plain the click on the update badge did something. It isn't one of the
/// movable sections — it shows even with the panel collapsed to its header — so <c>Draw</c>, <c>FullPanelHeight</c>
/// and <c>PanelBodyHeight</c> lay it out ahead of the ordered sections via <see cref="BodyTop"/>. While it's up,
/// the header's update badge is greyed out and ignores clicks.
/// </summary>
public sealed partial class OverlayCanvas
{
    private const double UpdateStripPadTop = 6;
    private const double UpdateStripGap = 4;       // between the caption line and the track
    private const double UpdateTrackH = 3;
    private const double UpdateStripPadBottom = 8;

    private string? _updateCaption;
    private int? _updatePercent;

    private bool UpdateInProgress => _updateCaption is not null;

    private static double? _updateLineH;
    private static double UpdateLineHeight => _updateLineH ??= OverlayDraw.Text("Xg", 11, FgBrush).Height;

    private double UpdateStripHeight => UpdateInProgress
        ? UpdateStripPadTop + UpdateLineHeight + UpdateStripGap + UpdateTrackH + UpdateStripPadBottom
        : 0;

    // Where the content under the header starts: the movable sections begin below the progress strip.
    private double BodyTop => HeaderHeight + UpdateStripHeight;

    /// <summary>Shows the progress strip with <paramref name="caption"/> and the download
    /// <paramref name="percent"/> (null when the stage has no measurable progress), or hides it when
    /// <paramref name="caption"/> is null. Appearing/disappearing changes the panel height, so relayout;
    /// a percent tick is only a repaint.</summary>
    public void SetUpdateProgress(string? caption, int? percent)
    {
        bool wasShown = UpdateInProgress;
        _updateCaption = caption;
        _updatePercent = percent;
        if (UpdateInProgress != wasShown)
        {
            _hoveredUpdateIcon = false;
            RemeasurePanel();
        }
        else
        {
            InvalidateVisual();
        }
    }

    // One caption line (the download glyph, the stage, the percent at the right) over a thin track filled in
    // the brand colour, matching the header badge.
    private void DrawUpdateStrip(DrawingContext ctx, double width)
    {
        double top = HeaderHeight;
        double h = UpdateStripHeight;
        double right = width - HorizPad;
        double midY = top + UpdateStripPadTop + UpdateLineHeight / 2;

        const double glyph = 14;
        DrawUpdateIcon(ctx, new Rect(HorizPad, midY - glyph / 2, glyph, glyph), hovered: false, disabled: false);

        double textX = HorizPad + glyph + 6;
        double captionRight = right;
        if (_updatePercent is { } pct)
        {
            var pctFt = OverlayDraw.Text($"{pct}%", 11, MutedBrush);
            OverlayDraw.TextLeftMid(ctx, pctFt, right - pctFt.Width, midY);
            captionRight = right - pctFt.Width - 8;
        }
        if (captionRight > textX)
        {
            using (ctx.PushClip(new Rect(textX, top, captionRight - textX, h)))
                OverlayDraw.TextLeftMid(ctx, OverlayDraw.Text(_updateCaption ?? "", 11, FgBrush), textX, midY);
        }

        double trackTop = top + UpdateStripPadTop + UpdateLineHeight + UpdateStripGap;
        var track = new Rect(HorizPad, trackTop, Math.Max(0, right - HorizPad), UpdateTrackH);
        OverlayDraw.Pill(ctx, OverlayDraw.Brush(UsageTrackColor), track);
        if (_updatePercent is { } fillPct && fillPct > 0)
            OverlayDraw.Pill(ctx, UpdateBrush, new Rect(track.X, track.Y, Math.Round(track.Width * fillPct / 100.0), track.Height));
    }
}
