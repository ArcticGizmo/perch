using Avalonia;
using Avalonia.Media;
using Perch.Avalonia.Rendering;
using Perch.Avalonia.Theming;
using Perch.Social;

namespace Perch.Avalonia.Views;

/// <summary>
/// The overlay's social feed strip: a slim owner-drawn band below the session rows showing friends' most
/// recent statuses (avatar dot + <c>@handle</c> + status text + relative time), newest first. Opt-in and
/// only present while the feed setting is on and there's at least one post — otherwise it takes no height.
/// Follows the same measure-or-paint discipline as the rest of <see cref="OverlayCanvas"/>: its height is
/// folded into <c>PanelBodyHeight</c>/<c>Draw</c>, and — because owner-drawn text must never be boxed by a
/// magic pixel value — every height is derived from the measured font line height (cached), so the rows
/// survive a DPI or font change without clipping the glyph bottoms.
/// </summary>
public sealed partial class OverlayCanvas
{
    private const double FeedCaptionSize = 9;    // the "FRIENDS" caption
    private const double FeedHandleSize  = 10;   // @handle
    private const double FeedBodySize    = 10;   // the status text
    private const double FeedTimeSize    = 9;    // relative time on the right
    private const int    FeedMaxRows     = 3;    // most-recent N shown

    private bool _feedEnabled;
    private FeedSnapshot? _feed;

    // Measured once and cached — FormattedText.Height is a constant in DIPs (render scale doesn't enter into
    // it), so caching costs no correctness on a DPI change and saves re-measuring every paint.
    private static double? _feedLineH, _feedCaptionH;
    private static double FeedLineHeight
        => _feedLineH ??= OverlayDraw.Text("Xg", FeedBodySize, FgBrush).Height + 8;
    private static double FeedCaptionHeight
        => _feedCaptionH ??= OverlayDraw.Text("Xg", FeedCaptionSize, MutedBrush).Height + 4;

    private int FeedRowCount => Math.Min(FeedMaxRows, _feed?.Items.Count ?? 0);

    // On screen only when enabled and there's something to show — an empty feed takes no height at all.
    private bool FeedStripVisible => _feedEnabled && FeedRowCount > 0;

    private double FeedStripHeight => !FeedStripVisible ? 0 : FeedCaptionHeight + FeedRowCount * FeedLineHeight + 8;

    /// <summary>Show/hide the whole feed strip. Toggling it can change the panel height (when there are
    /// posts), so relayout in that case; otherwise nothing visible changes.</summary>
    public void SetShowFeedStrip(bool enabled)
    {
        if (_feedEnabled == enabled) return;
        bool before = FeedStripVisible;
        _feedEnabled = enabled;
        if (FeedStripVisible != before) RemeasurePanel();
    }

    /// <summary>Feeds the latest friends' statuses (on the UI thread), or null to clear. When the strip's
    /// visibility or row count changes the panel height changes, so relayout; otherwise just repaint.</summary>
    public void UpdateFeed(FeedSnapshot? feed)
    {
        bool before = FeedStripVisible;
        double beforeH = FeedStripHeight;
        _feed = feed;
        if (FeedStripVisible != before || FeedStripHeight != beforeH) RemeasurePanel();
        else if (_feedEnabled) InvalidateVisual();
    }

    // Paints the strip at y=top (its height is already reserved in Draw). A separator, the "FRIENDS" caption,
    // then up to FeedMaxRows lines of "• @handle status … 2m".
    private void DrawFeedStrip(DrawingContext ctx, double width, double top)
    {
        if (_feed is not { Items.Count: > 0 } feed) return;

        ctx.DrawLine(SepPen, new Point(HorizPad, top + 0.5), new Point(width - HorizPad, top + 0.5));

        double y = top + 4;
        OverlayDraw.TextLeftMid(ctx, OverlayDraw.Text("FRIENDS", FeedCaptionSize, MutedBrush),
            HorizPad, y + FeedCaptionHeight / 2);
        y += FeedCaptionHeight;

        int rows = Math.Min(FeedMaxRows, feed.Items.Count);
        for (int i = 0; i < rows; i++)
        {
            var item = feed.Items[i];
            double midY = y + FeedLineHeight / 2;

            // Avatar: a small filled dot, its colour derived stably from the handle (a theme status hue, so
            // it recolours with the theme rather than being hand-picked ARGB).
            const double dotR = 3.5;
            ctx.DrawEllipse(new SolidColorBrush(AvatarColor(item.Author.Handle)), null,
                new Point(HorizPad + dotR, midY), dotR, dotR);
            double x = HorizPad + dotR * 2 + 6;

            // Relative time, right-aligned and muted.
            var agoFt = OverlayDraw.Text(FormatAgo(item.CreatedAt), FeedTimeSize, MutedBrush);
            double agoX = width - HorizPad - agoFt.Width;
            OverlayDraw.TextLeftMid(ctx, agoFt, agoX, midY);

            // @handle in the accent, then the status filling whatever space is left before the time.
            var handleFt = OverlayDraw.Text("@" + item.Author.Handle, FeedHandleSize, Palette.AccentBrush, FontWeight.SemiBold);
            OverlayDraw.TextLeftMid(ctx, handleFt, x, midY);
            x += handleFt.Width + 6;

            double bodyMax = agoX - 8 - x;
            if (bodyMax > 12)
            {
                string shown = OverlayDraw.Truncate(item.Body, FeedBodySize, bodyMax);
                OverlayDraw.TextLeftMid(ctx, OverlayDraw.Text(shown, FeedBodySize, FgBrush), x, midY);
            }

            y += FeedLineHeight;
        }
    }

    // A stable per-handle colour from the theme's status hues (not process-random string hashing, so the
    // same handle always gets the same dot across runs and in the render output).
    private static Color AvatarColor(string handle)
    {
        Color[] hues = [RunningColor, MailColor, SubAgentColor, AwaitingColor, AttentionColor];
        int h = 0;
        foreach (char c in handle) h = h * 31 + c;
        return hues[(h & 0x7fffffff) % hues.Length];
    }

    private static string FormatAgo(DateTimeOffset t)
    {
        var d = DateTimeOffset.UtcNow - t;
        if (d < TimeSpan.FromMinutes(1)) return "now";
        if (d < TimeSpan.FromHours(1)) return $"{(int)d.TotalMinutes}m";
        if (d < TimeSpan.FromDays(1)) return $"{(int)d.TotalHours}h";
        return $"{(int)d.TotalDays}d";
    }
}
