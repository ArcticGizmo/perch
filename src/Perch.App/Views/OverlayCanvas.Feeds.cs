using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Perch.Avalonia.Rendering;
using Perch.Avalonia.Theming;
using Perch.Data;

namespace Perch.Avalonia.Views;

/// <summary>
/// The overlay's feeds row (docs/feeds-plan.md §1): one line of story-style heads, one per subscribed feed. Each
/// head is the feed's icon (or its initials) in a circle with a ring:
/// <list type="bullet">
///   <item><b>Unread:</b> an accent → brand gradient ring and a count pill — the "story" cue.</item>
///   <item><b>Seen:</b> a thin muted ring.</item>
///   <item><b>Failing:</b> a small yellow badge (the tooltip says why).</item>
///   <item><b>Just arrived:</b> a short self-stopping pop + glow, skipped under reduced motion. Never a perpetual
///   animation — the lit ring is the steady cue.</item>
/// </list>
/// Heads that don't fit the width fold into a "+N" chip; a trailing "+" adds a feed (with no feeds yet, the whole
/// row reads "Add a feed"). Click a head to play its story, right-click for its menu, dwell for a summary.
///
/// <para>Fed a toolkit-neutral list of <see cref="FeedHeadView"/>s by the app (icons pre-decoded off the UI
/// thread), so painting touches no files. Its height derives from the head size and the measured count text, and
/// the measure and paint passes share <see cref="FeedsRowHeight"/>. Not collapsible: it's already one line.</para>
/// </summary>
public sealed partial class OverlayCanvas
{
    /// <summary>What the row draws for one feed. Title / latest title are already cleaned by the feeds engine.</summary>
    internal sealed record FeedHeadView(
        string SubId, string Title, Bitmap? Icon, int Unread, string? LatestTitle, DateTime? LatestUtc,
        string? Error, string? SiteUrl);

    private const double FeedHeadR = 12;      // the ring's outer radius
    private const double FeedHeadGap = 8;     // between heads
    private const double FeedRingW = 2;       // an unread ring's stroke
    private const double FeedRingInset = 1.5; // the gap between ring and icon
    private const double FeedAddBox = 18;     // the "+" hit box (the collapsible headers' idiom)
    private const double FeedCountSize = 8.5;
    private const long FeedPopMs = 1200;

    private bool _feedsEnabled;
    private IReadOnlyList<FeedHeadView> _feedHeads = [];
    private readonly Dictionary<string, int> _feedUnreadSeen = new(StringComparer.Ordinal);
    private bool _feedsPrimed;

    private readonly Dictionary<string, long> _feedPopStart = new(StringComparer.Ordinal);
    private DispatcherTimer? _feedPopTimer;

    private readonly List<Rect> _feedHeadRects = [];
    private Rect _feedMoreRect, _feedAddRect, _feedsRowRect;
    private int _feedOverflowFrom = -1;   // index of the first head folded into "+N"
    private int _hoveredFeedHead = -1;
    private bool _hoveredFeedMore, _hoveredFeedAdd;


    // The unread ring's gradient, rebuilt when the theme's accent (or the brand hue) changes.
    private (Color A, Color B) _feedRingColors;
    private IPen? _feedRingPen;

    /// <summary>A head was clicked (its sub id), or the "+N" chip (null): play the story.</summary>
    public event Action<string?>? FeedStoryRequested;
    public event Action? FeedAddRequested;
    public event Action<string>? FeedRefreshRequested;
    public event Action<string>? FeedMarkAllReadRequested;
    public event Action<string>? FeedEditRequested;
    public event Action<string>? FeedOpenSiteRequested;
    public event Action? FeedSettingsRequested;

    private bool FeedsRowVisible => _feedsEnabled;

    private static double? _feedsRowH;
    // The head diameter, plus room for the count pill that hangs off a head's lower edge, plus padding.
    private static double FeedsRowHeightCore =>
        _feedsRowH ??= FeedHeadR * 2 + Math.Max(10, OverlayDraw.Text("9+", FeedCountSize, FgBrush).Height / 2 + 6);
    private double FeedsRowHeight => FeedsRowVisible ? FeedsRowHeightCore : 0;

    /// <summary>Show/hide the row (the "Feeds" setting). Changes the panel height, so relayout.</summary>
    public void SetShowFeeds(bool enabled)
    {
        if (_feedsEnabled == enabled) return;
        _feedsEnabled = enabled;
        if (!enabled) { _hoveredFeedHead = -1; _hoveredFeedMore = _hoveredFeedAdd = false; }
        RemeasurePanel();
    }

    /// <summary>Replaces the heads (UI thread). A head whose unread count rose since the last call pops; the very
    /// first call only primes, so launching Perch doesn't set every ring popping.</summary>
    internal void SetFeedsRow(IReadOnlyList<FeedHeadView> heads)
    {
        long now = Environment.TickCount64;
        bool popped = false;
        foreach (var h in heads)
        {
            int before = _feedUnreadSeen.GetValueOrDefault(h.SubId, -1);
            if (_feedsPrimed && h.Unread > Math.Max(0, before) && !Pulse.ReduceMotion)
            {
                _feedPopStart[h.SubId] = now;
                popped = true;
            }
            _feedUnreadSeen[h.SubId] = h.Unread;
        }
        _feedsPrimed = true;
        _feedHeads = heads;
        if (_hoveredFeedHead >= heads.Count) _hoveredFeedHead = -1;
        if (popped) EnsureFeedPopTimer();
        if (FeedsRowVisible) InvalidateVisual();
    }

    // 1 → 0 ease-out over FeedPopMs; 0 when not popping.
    private double FeedPop(string subId)
    {
        if (!_feedPopStart.TryGetValue(subId, out var start)) return 0;
        double e = Environment.TickCount64 - start;
        if (e >= FeedPopMs) return 0;
        double t = 1 - e / FeedPopMs;
        return t * t;
    }

    private void EnsureFeedPopTimer()
    {
        _feedPopTimer ??= CreateFeedPopTimer();
        if (!_feedPopTimer.IsEnabled) _feedPopTimer.Start();
    }

    private DispatcherTimer CreateFeedPopTimer()
    {
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        t.Tick += (_, _) =>
        {
            long now = Environment.TickCount64;
            foreach (var id in _feedPopStart.Where(kv => now - kv.Value >= FeedPopMs).Select(kv => kv.Key).ToList())
                _feedPopStart.Remove(id);
            if (_feedPopStart.Count == 0 || !OnScreen()) { _feedPopTimer!.Stop(); _feedPopStart.Clear(); }
            if (FeedsRowVisible) InvalidateVisual();
        };
        return t;
    }

    private IPen FeedRingPen()
    {
        var colors = (Palette.Accent, Palette.Brand);
        if (_feedRingPen is null || colors != _feedRingColors)
        {
            _feedRingColors = colors;
            var brush = new ImmutableLinearGradientBrush(
                [new ImmutableGradientStop(0, colors.Item1), new ImmutableGradientStop(1, colors.Item2)],
                startPoint: new RelativePoint(0, 0, RelativeUnit.Relative),
                endPoint: new RelativePoint(1, 1, RelativeUnit.Relative));
            _feedRingPen = new ImmutablePen(brush, FeedRingW);
        }
        return _feedRingPen;
    }

    // ── Paint ───────────────────────────────────────────────────────────────────────────────────────────────

    private void DrawFeedsRow(DrawingContext ctx, double width, double top)
    {
        double h = FeedsRowHeight;
        double cy = top + h / 2;
        _feedsRowRect = new Rect(0, top, width, h);
        _feedHeadRects.Clear();
        _feedMoreRect = default;
        _feedOverflowFrom = -1;

        double left = HorizPad, right = width - HorizPad;

        if (_feedHeads.Count == 0)
        {
            // No feeds yet: the whole band is the "Add a feed" control.
            var band = new Rect(HorizPad - 4, top + 3, Math.Max(0, width - 2 * (HorizPad - 4)), h - 6);
            _feedAddRect = band;
            if (_hoveredFeedAdd) OverlayDraw.Panel(ctx, band, FeedHoverBrush, null, 6);
            var brush = _hoveredFeedAdd ? Palette.AccentBrush : MutedBrush;
            DrawPlusGlyph(ctx, brush, left + 4, cy);
            OverlayDraw.TextLeftMid(ctx, OverlayDraw.Text("Add a feed", 11, brush), left + 16, cy);
            return;
        }

        // The "+" box at the far right.
        double boxCx = right - FeedAddBox / 2 + 2;
        _feedAddRect = new Rect(boxCx - FeedAddBox / 2, cy - FeedAddBox / 2, FeedAddBox, FeedAddBox);
        if (_hoveredFeedAdd) OverlayDraw.Panel(ctx, _feedAddRect, FeedHoverBrush, null, 5);
        DrawPlusGlyph(ctx, _hoveredFeedAdd ? Palette.AccentBrush : MutedBrush, boxCx, cy);

        // How many heads fit; the rest fold into "+N".
        double avail = _feedAddRect.Left - 6 - left;
        double slot = FeedHeadR * 2 + FeedHeadGap;
        int n = _feedHeads.Count;
        int fit = Math.Max(0, (int)Math.Floor((avail + FeedHeadGap) / slot));
        int shown = n;
        FormattedText? moreFt = null;
        double moreW = 0;
        if (n > fit)
        {
            moreFt = OverlayDraw.Text("+" + n, FeedCountSize + 1.5, MutedBrush, FontWeight.SemiBold);
            moreW = moreFt.Width + 12;
            shown = Math.Max(0, (int)Math.Floor((avail - moreW) / slot));
            moreFt = OverlayDraw.Text("+" + (n - shown), FeedCountSize + 1.5,
                _hoveredFeedMore ? FgBrush : MutedBrush, FontWeight.SemiBold);
            moreW = moreFt.Width + 12;
        }

        for (int i = 0; i < shown; i++)
            DrawFeedHead(ctx, _feedHeads[i], left + FeedHeadR + i * slot, cy, i);

        if (shown < n && moreFt is not null)
        {
            _feedOverflowFrom = shown;
            double x = left + shown * slot;
            double ph = moreFt.Height + 6;
            _feedMoreRect = new Rect(x, cy - ph / 2, moreW, ph);
            OverlayDraw.Pill(ctx, _hoveredFeedMore ? FeedHoverBrush : Palette.ButtonBgBrush, _feedMoreRect);
            // Any unread among the folded heads: a small accent dot on the chip, so overflow can't hide news.
            if (_feedHeads.Skip(shown).Any(f => f.Unread > 0))
                DrawGlyphBadge(ctx, new Point(_feedMoreRect.Right - 2, _feedMoreRect.Top + 2), Palette.AccentBrush);
            OverlayDraw.TextLeftMid(ctx, moreFt, x + 6, cy);
        }
    }

    private void DrawFeedHead(DrawingContext ctx, FeedHeadView head, double cx, double cy, int index)
    {
        double pop = FeedPop(head.SubId);
        bool hovered = _hoveredFeedHead == index;
        double r = FeedHeadR + pop * 2;
        var center = new Point(cx, cy);
        _feedHeadRects.Add(new Rect(cx - FeedHeadR - 3, cy - FeedHeadR - 3, FeedHeadR * 2 + 6, FeedHeadR * 2 + 6));

        if (pop > 0) ctx.DrawEllipse(OverlayDraw.Brush(Palette.Accent, 0.35 * pop), null, center, r + 4, r + 4);
        if (hovered) ctx.DrawEllipse(FeedHoverBrush, null, center, FeedHeadR + 3, FeedHeadR + 3);

        // The icon (or initials), clipped round, inside the ring.
        double ri = FeedHeadR - FeedRingW - FeedRingInset;
        if (head.Icon is { } icon)
        {
            // The icon sits inscribed in a raised disc (a logo in a ring) rather than clipped round: its square never
            // reaches past the circle, so no clip is needed. (A clip around DrawImage also mis-restored the
            // transform under the 1.5× headless render, painting the rest of the panel at double scale.)
            ctx.DrawEllipse(Palette.ButtonBgBrush, null, center, ri, ri);
            double side = ri * Math.Sqrt(2);
            ctx.DrawImage(icon, new Rect(cx - side / 2, cy - side / 2, side, side));
        }
        else
        {
            var tint = FallbackColor(head.Title);
            ctx.DrawEllipse(OverlayDraw.Brush(tint, 0.22), null, center, ri, ri);
            var ft = OverlayDraw.Text(Initials(head.Title), 8, OverlayDraw.Brush(tint), FontWeight.Bold);
            ctx.DrawText(ft, new Point(cx - ft.Width / 2, cy - ft.Height / 2));
        }

        // The ring: the story cue when unread, a quiet outline otherwise.
        if (head.Unread > 0)
            ctx.DrawEllipse(null, FeedRingPen(), center, r - FeedRingW / 2, r - FeedRingW / 2);
        else
            ctx.DrawEllipse(null, OverlayDraw.Pen(OverlayDraw.Brush(Palette.Muted, hovered ? 0.8 : 0.45), 1),
                center, r - 0.5, r - 0.5);

        if (head.Error is not null)
            DrawGlyphBadge(ctx, new Point(cx + r * 0.72, cy - r * 0.72), OverlayDraw.Brush(Palette.Yellow));

        if (head.Unread > 0)
        {
            var count = OverlayDraw.Text(head.Unread > 9 ? "9+" : head.Unread.ToString(), FeedCountSize,
                OverlayDraw.Brush(Palette.OnAccent), FontWeight.Bold);
            double ph = count.Height;
            double pw = Math.Max(ph, count.Width + 5);
            var pill = new Rect(cx + r * 0.75 - pw / 2, cy + r * 0.75 - ph / 2, pw, ph);
            OverlayDraw.Pill(ctx, Palette.FormBgBrush, pill.Inflate(1.5));   // cut-out so it reads on the ring
            OverlayDraw.Pill(ctx, Palette.AccentBrush, pill);
            ctx.DrawText(count, new Point(pill.Center.X - count.Width / 2, pill.Center.Y - count.Height / 2));
        }
    }

    // ── Hit-testing ─────────────────────────────────────────────────────────────────────────────────────────

    private int HitTestFeedHead(Point p)
    {
        if (!ShowFullPanel || !FeedsRowVisible) return -1;
        for (int i = 0; i < _feedHeadRects.Count; i++)
            if (_feedHeadRects[i].Contains(p)) return i;
        return -1;
    }

    private bool HitTestFeedMore(Point p) => ShowFullPanel && FeedsRowVisible && _feedMoreRect.Width > 0 && _feedMoreRect.Contains(p);
    private bool HitTestFeedAdd(Point p) => ShowFullPanel && FeedsRowVisible && _feedAddRect.Width > 0 && _feedAddRect.Contains(p);
    private bool InFeedsRow(Point p) => ShowFullPanel && FeedsRowVisible && _feedsRowRect.Contains(p);

    /// <summary>Hover bookkeeping from OnPointerMoved. Returns whether anything clickable is under the cursor.</summary>
    private bool UpdateFeedsHover(Point p)
    {
        int head = HitTestFeedHead(p);
        bool more = HitTestFeedMore(p), add = HitTestFeedAdd(p);
        if (head != _hoveredFeedHead || more != _hoveredFeedMore || add != _hoveredFeedAdd)
        {
            _hoveredFeedHead = head;
            _hoveredFeedMore = more;
            _hoveredFeedAdd = add;
            InvalidateVisual();
        }
        return head >= 0 || more || add;
    }

    private bool ClearFeedsHover()
    {
        bool changed = _hoveredFeedHead >= 0 || _hoveredFeedMore || _hoveredFeedAdd;
        _hoveredFeedHead = -1;
        _hoveredFeedMore = _hoveredFeedAdd = false;
        return changed;
    }

    /// <summary>Left click routing. True when the click landed on the row.</summary>
    private bool RouteFeedsClick(Point p)
    {
        if (!InFeedsRow(p)) return false;
        int head = HitTestFeedHead(p);
        if (head >= 0 && head < _feedHeads.Count) FeedStoryRequested?.Invoke(_feedHeads[head].SubId);
        else if (HitTestFeedMore(p)) FeedStoryRequested?.Invoke(null);
        else if (HitTestFeedAdd(p)) FeedAddRequested?.Invoke();
        return true;   // the row's gaps are inert
    }

    /// <summary>Right-click menu: per head, or the row's own.</summary>
    private bool ShowFeedsMenu(Point p)
    {
        if (!InFeedsRow(p)) return false;
        var items = new List<Control>();
        int i = HitTestFeedHead(p);
        if (i >= 0 && i < _feedHeads.Count)
        {
            var head = _feedHeads[i];
            items.Add(MenuItem(head.Unread > 0 ? "Play new entries" : "Replay recent", () => FeedStoryRequested?.Invoke(head.SubId)));
            if (head.Unread > 0) items.Add(MenuItem("Mark all read", () => FeedMarkAllReadRequested?.Invoke(head.SubId)));
            items.Add(MenuItem("Check now", () => FeedRefreshRequested?.Invoke(head.SubId)));
            if (head.SiteUrl is not null) items.Add(MenuItem("Open website", () => FeedOpenSiteRequested?.Invoke(head.SubId)));
            items.Add(new Separator());
            items.Add(MenuItem("Edit feed…", () => FeedEditRequested?.Invoke(head.SubId)));
        }
        items.Add(MenuItem("Add a feed…", () => FeedAddRequested?.Invoke()));
        items.Add(MenuItem("Feeds settings…", () => FeedSettingsRequested?.Invoke()));
        ShowFlyout(items);
        return true;
    }

    // ── Tooltips ────────────────────────────────────────────────────────────────────────────────────────────

    private void ShowFeedHeadTooltip(int index)
    {
        if (index < 0 || index >= _feedHeads.Count || index >= _feedHeadRects.Count) return;
        var head = _feedHeads[index];
        var lines = new List<OverlayTooltip.Line> { new(OverlayDraw.Truncate(head.Title, 11, 280), OverlayTooltip.FgColor, true) };
        if (head.Error is { } err) lines.Add(new("⚠ " + OverlayDraw.Truncate(err, 10, 280), Palette.Yellow, false));
        if (head.Unread > 0) lines.Add(new(head.Unread == 1 ? "1 new entry" : $"{head.Unread} new entries", Palette.Accent, false));
        if (head.LatestTitle is { } latest)
        {
            string when = head.LatestUtc is { } at ? " · " + RelativeTime.Ago(DateTime.UtcNow, at) : "";
            lines.Add(new(OverlayDraw.Truncate("Latest: " + latest, 10, 280) + when, OverlayTooltip.MutedColor, false));
        }
        lines.Add(new(head.Unread > 0 ? "Click to play" : "Click to replay", OverlayTooltip.MutedColor, false));
        var r = _feedHeadRects[index];
        Tooltip().ShowLines(lines, ToScreen(r.Left, r.Bottom + 4));
    }

    private void ShowFeedMoreTooltip()
    {
        if (_feedOverflowFrom < 0 || _feedMoreRect.Width <= 0) return;
        var hidden = _feedHeads.Skip(_feedOverflowFrom).ToList();
        var lines = new List<OverlayTooltip.Line> { new($"{hidden.Count} more feeds", OverlayTooltip.FgColor, true) };
        foreach (var h in hidden.Take(8))
            lines.Add(new(OverlayDraw.Truncate(h.Title, 10, 240) + (h.Unread > 0 ? $"  · {h.Unread} new" : ""),
                h.Unread > 0 ? Palette.Accent : OverlayTooltip.MutedColor, false));
        if (hidden.Count > 8) lines.Add(new($"…and {hidden.Count - 8} more", OverlayTooltip.MutedColor, false));
        Tooltip().ShowLines(lines, ToScreen(_feedMoreRect.Left, _feedMoreRect.Bottom + 4));
    }
}
