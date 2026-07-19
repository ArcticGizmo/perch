using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Perch.Avalonia.Rendering;
using Perch.Avalonia.Theming;
using Perch.Data;

namespace Perch.Avalonia.Views;

/// <summary>
/// The owner-drawn "trophy cabinet" — the whole-window achievements view, hosted by
/// <c>AchievementsWindow</c>. A title + "N / M unlocked" tally over the shared <see cref="AchievementGrid"/>
/// in its roomy variant (bigger tiles that carry each badge's criteria, so locked ones tell you how to earn
/// them). One <see cref="Draw"/> routine measures (null context) and paints, so height never drifts from
/// layout.
/// </summary>
internal sealed class AchievementsDashboard : Control
{
    private static readonly Color BodyBg = Color.FromRgb(18, 18, 24);
    private static readonly IBrush TitleBrush  = new SolidColorBrush(Palette.Title);
    private static readonly IBrush MutedBrush  = new SolidColorBrush(Palette.Muted);
    private static readonly IBrush AccentBrush = new SolidColorBrush(Palette.Accent);

    private const double H1Size = 20, BodySize = 13, Pad = 22;

    private IReadOnlyList<Achievement> _badges = [];
    private string? _subtitle;
    private bool _loading = true;

    public void SetLoading()
    {
        _loading = true;
        InvalidateMeasure();
        InvalidateVisual();
    }

    public void SetBadges(IReadOnlyList<Achievement> badges, string? subtitle)
    {
        _badges = badges;
        _subtitle = subtitle;
        _loading = false;
        InvalidateMeasure();
        InvalidateVisual();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        double w = double.IsFinite(availableSize.Width) && availableSize.Width > 0 ? availableSize.Width : 620;
        return new Size(w, Draw(null, w));
    }

    public override void Render(DrawingContext ctx)
    {
        ctx.FillRectangle(new SolidColorBrush(BodyBg), new Rect(Bounds.Size));
        Draw(ctx, Bounds.Width);
    }

    private double Draw(DrawingContext? ctx, double width)
    {
        double x = Pad, y = Pad, innerW = width - Pad * 2;

        Text(ctx, "Achievements", H1Size, TitleBrush, x, y, FontWeight.Bold);
        if (ctx != null && !_loading && _badges.Count > 0)   // tally, right-aligned on the title row
        {
            var tally = OverlayDraw.Text(AchievementGrid.Tally(_badges), BodySize, AccentBrush, FontWeight.SemiBold);
            ctx.DrawText(tally, new Point(x + innerW - tally.Width, y + 6));
        }
        if (_subtitle is { Length: > 0 } sub)
            Text(ctx, sub, BodySize, MutedBrush, x + 4, y + 28);
        y += 58;

        if (_loading)
        {
            Text(ctx, "Loading…", BodySize, MutedBrush, x, y);
            return y + 40;
        }
        if (_badges.Count == 0)
        {
            Text(ctx, "No achievements to show yet.", BodySize, MutedBrush, x, y);
            return y + 40;
        }

        y = AchievementGrid.Draw(ctx, _badges, x, y, innerW, targetTileW: 200, emojiSize: 30, showDescription: true);
        return y + Pad;
    }

    private static void Text(DrawingContext? ctx, string s, double size, IBrush brush, double x, double y,
        FontWeight weight = FontWeight.Normal)
    {
        if (ctx != null) ctx.DrawText(OverlayDraw.Text(s, size, brush, weight), new Point(x, y));
    }
}
