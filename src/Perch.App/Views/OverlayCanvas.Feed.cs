using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Perch.Avalonia.Rendering;
using Perch.Avalonia.Theming;
using Perch.Social;

namespace Perch.Avalonia.Views;

/// <summary>
/// The overlay's social region: a collapsible band below the session rows that shows your friends as a roster —
/// one row per accepted friend with their mood emoji, <c>@handle</c>, latest status and time, the reactions on
/// that status, and a "what are you working on?" row to post your own. It has its own expand/collapse (a chevron
/// in the region header), so it can sit quietly as a one-line header or open into the full roster.
///
/// <para>Owner-drawn through <see cref="OverlayDraw"/> on the same measure-or-paint discipline as the rest of
/// <see cref="OverlayCanvas"/>: every height derives from the measured font line height (cached), never a magic
/// pixel value, so the rows survive a DPI/font change without clipping. When a friend's status changes, their
/// row briefly glows (a short, self-stopping fade) so a new status is noticed without being noisy.</para>
/// </summary>
public sealed partial class OverlayCanvas
{
    private const double FeedCaptionSize  = 9;    // the "FRIENDS" caption + chevron row
    private const double FeedHandleSize   = 10;   // @handle
    private const double FeedBodySize     = 10;   // the status text
    private const double FeedTimeSize     = 9;    // relative time on the right
    private const double FeedReactionSize = 10;   // reaction chips + the compose prompt
    private const int    FeedMaxRows      = 6;    // friends shown before a "+N more" overflow line

    // A short menu of reactions the "+" button offers.
    private static readonly string[] ReactionChoices = ["👍", "🔥", "🎉", "😂", "😮", "❤️", "🙌", "👀"];

    private static readonly IBrush FeedTileBrush  = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255));  // avatar tile
    private static readonly IBrush FeedChipBrush   = new SolidColorBrush(Color.FromArgb(28, 255, 255, 255)); // reaction chip
    private static readonly IBrush FeedHoverBrush   = new SolidColorBrush(Color.FromArgb(26, 255, 255, 255)); // row/button hover

    private bool _feedEnabled;
    private bool _regionExpanded = true;
    private RosterSnapshot? _roster;

    // Per-friend status-change glow: tick (Environment.TickCount64) when a friend's latest status last changed.
    // Primed on the first roster so an initial population doesn't glow the whole list.
    private readonly Dictionary<Guid, long> _glowStart = new();
    private readonly Dictionary<Guid, Guid> _rosterSeenLatest = new();
    private bool _rosterPrimed;
    private DispatcherTimer? _glowTimer;
    private const long GlowMs = 1600;

    // Measured once and cached — FormattedText.Height is a constant in DIPs, so caching costs no correctness on
    // a DPI change and saves re-measuring every paint.
    private static double? _feedLineH, _feedCaptionH, _feedReactionH;
    private static double FeedLineHeight
        => _feedLineH ??= OverlayDraw.Text("Xg", FeedBodySize, FgBrush).Height + 8;
    private static double FeedCaptionHeight
        => _feedCaptionH ??= OverlayDraw.Text("Xg", FeedCaptionSize, MutedBrush).Height + 4;
    private static double FeedReactionHeight
        => _feedReactionH ??= OverlayDraw.Text("Xg😀", FeedReactionSize, FgBrush).Height + 8;

    private double SocialHeaderHeight => FeedCaptionHeight + 12;

    /// <summary>Raised when the region header's chevron is clicked to expand or collapse — the App persists it.</summary>
    public event Action<bool>? SocialRegionExpandChanged;

    /// <summary>Raised when a reaction chip or the "+" button is clicked: the post to react to, the emoji, and
    /// whether it should be on (add) or off (remove). The App relays it to the social client.</summary>
    public event Action<Guid, string, bool>? ReactRequested;

    // Shown once Social is on, the region toggle (ShowFeedStrip) is on, and you're signed in with a handle. Its
    // sibling — the sign-in prompt strip — shows in the complementary state, so the two never overlap.
    private bool SocialRegionVisible => _socialEnabled && _feedEnabled && _socialSignedIn && _socialHasHandle;

    private int FriendRowCount => Math.Min(FeedMaxRows, _roster?.Friends.Count ?? 0);
    private bool FriendOverflow => (_roster?.Friends.Count ?? 0) > FeedMaxRows;

    // Height of one friend's row: the status line, plus a reactions line when they have a post to react to.
    private double FriendRowHeight(RosterFriend f) => FeedLineHeight + (f.Latest is not null ? FeedReactionHeight : 0);

    private double SocialRegionHeight
    {
        get
        {
            if (!SocialRegionVisible) return 0;
            double h = SocialHeaderHeight;
            if (!_regionExpanded) return h;

            if (_roster is { Friends.Count: > 0 } r)
                for (int i = 0; i < FriendRowCount; i++) h += FriendRowHeight(r.Friends[i]);

            if (FriendOverflow) h += FeedCaptionHeight;
            h += FeedLineHeight + 6;   // the "you" / compose row
            return h;
        }
    }

    // Kept for the settings gate / measure code that still speaks in terms of the old "feed strip".
    private bool FeedStripVisible => SocialRegionVisible;
    private double FeedStripHeight => SocialRegionHeight;

    /// <summary>Show/hide the whole social region (the ShowFeedStrip setting). Toggling changes the panel
    /// height, so relayout when the visibility actually flips.</summary>
    public void SetShowFeedStrip(bool enabled)
    {
        if (_feedEnabled == enabled) return;
        bool before = SocialRegionVisible;
        _feedEnabled = enabled;
        if (SocialRegionVisible != before) RemeasurePanel();
    }

    /// <summary>Sets the region's initial expand/collapse state (from AppSettings) without raising the change
    /// event. Call once at wire-up.</summary>
    public void SetSocialRegionExpanded(bool expanded)
    {
        if (_regionExpanded == expanded) return;
        _regionExpanded = expanded;
        if (SocialRegionVisible) RemeasurePanel();
    }

    /// <summary>Feeds the latest friends roster (on the UI thread), or null to clear. Detects per-friend status
    /// changes to trigger the glow, then relayouts if the height changed or repaints otherwise.</summary>
    public void UpdateRoster(RosterSnapshot? roster)
    {
        bool before = SocialRegionVisible;
        double beforeH = SocialRegionHeight;

        DetectStatusChanges(roster);
        _roster = roster;

        if (SocialRegionVisible != before || SocialRegionHeight != beforeH) RemeasurePanel();
        else if (_feedEnabled) InvalidateVisual();
    }

    // Marks a glow for any friend whose latest-status id changed to a new value. The first roster only primes
    // the baseline (an initial load shouldn't glow everyone), mirroring the notification priming.
    private void DetectStatusChanges(RosterSnapshot? roster)
    {
        if (roster is null) return;
        bool wasPrimed = _rosterPrimed;
        foreach (var f in roster.Friends)
        {
            if (f.Latest is not { } latest) continue;
            bool changed = !_rosterSeenLatest.TryGetValue(f.Profile.Id, out var prev) || prev != latest.Id;
            _rosterSeenLatest[f.Profile.Id] = latest.Id;
            if (changed && wasPrimed)
            {
                _glowStart[f.Profile.Id] = Environment.TickCount64;
                EnsureGlowTimer();
            }
        }
        _rosterPrimed = true;
    }

    // 1→0 ease-out over GlowMs; 0 once elapsed (the timer prunes it).
    private double GlowIntensity(Guid friendId)
    {
        if (!_glowStart.TryGetValue(friendId, out var tick)) return 0;
        double e = Environment.TickCount64 - tick;
        if (e >= GlowMs) return 0;
        double t = 1 - e / (double)GlowMs;
        return t * t;
    }

    private void EnsureGlowTimer()
    {
        _glowTimer ??= CreateGlowTimer();
        if (!_glowTimer.IsEnabled) _glowTimer.Start();
    }

    private DispatcherTimer CreateGlowTimer()
    {
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        t.Tick += (_, _) =>
        {
            long now = Environment.TickCount64;
            foreach (var id in _glowStart.Where(kv => now - kv.Value >= GlowMs).Select(kv => kv.Key).ToList())
                _glowStart.Remove(id);
            if (_glowStart.Count == 0) _glowTimer!.Stop();
            if (SocialRegionVisible) InvalidateVisual();
        };
        return t;
    }

    // Routed from RouteClick: the header toggles expand/collapse.
    private void OnSocialHeaderClicked()
    {
        _regionExpanded = !_regionExpanded;
        SocialRegionExpandChanged?.Invoke(_regionExpanded);
        RemeasurePanel();
    }

    // ── painting ──────────────────────────────────────────────────────────────────────────────────────

    // Paints the region at y=top (its height is already reserved in Draw): the header (caption + chevron +
    // count), then — when expanded — a row per friend, an optional overflow line, and the compose row.
    private void DrawSocialRegion(DrawingContext ctx, double width, double top)
    {
        ClearSocialRegionHitRects();
        ctx.DrawLine(SepPen, new Point(HorizPad, top + 0.5), new Point(width - HorizPad, top + 0.5));

        DrawRegionHeader(ctx, width, top);
        double y = top + SocialHeaderHeight;
        if (!_regionExpanded) return;

        if (_roster is { Friends.Count: > 0 } r)
        {
            for (int i = 0; i < FriendRowCount; i++)
            {
                double rowH = FriendRowHeight(r.Friends[i]);
                DrawFriendRow(ctx, width, y, rowH, r.Friends[i], i);
                y += rowH;
            }
            if (FriendOverflow)
            {
                int more = r.Friends.Count - FriendRowCount;
                var ft = OverlayDraw.Text($"+{more} more · manage friends", FeedCaptionSize, MutedBrush);
                OverlayDraw.TextLeftMid(ctx, ft, HorizPad + 2, y + FeedCaptionHeight / 2);
                _socialMoreRect = new Rect(0, y, width, FeedCaptionHeight);
                y += FeedCaptionHeight;
            }
        }

        DrawComposeRow(ctx, width, y);
    }

    private void DrawRegionHeader(DrawingContext ctx, double width, double top)
    {
        double midY = top + 6 + FeedCaptionHeight / 2;
        if (_hoveredSocialHeader)
            OverlayDraw.Panel(ctx, new Rect(HorizPad - 4, top + 3, width - 2 * (HorizPad - 4), SocialHeaderHeight - 6),
                FeedHoverBrush, null, 6);

        // Chevron (▸ collapsed / ▾ expanded), then the caption.
        DrawChevron(ctx, HorizPad + 4, midY, _regionExpanded);
        double x = HorizPad + 14;
        OverlayDraw.TextLeftMid(ctx, OverlayDraw.Text("FRIENDS", FeedCaptionSize, MutedBrush), x, midY);

        // Far right: a "+" button to add / manage friends (opens the Friends window).
        const double addBox = 18;
        double addCx = width - HorizPad - addBox / 2 + 2;
        var addRect = new Rect(addCx - addBox / 2, midY - addBox / 2, addBox, addBox);
        if (_hoveredSocialAdd) OverlayDraw.Panel(ctx, addRect, FeedHoverBrush, null, 5);
        DrawPlusGlyph(ctx, _hoveredSocialAdd ? FgBrush : MutedBrush, addCx, midY);
        _socialAddRect = addRect;

        // Left of the "+": a live dot + count of friends with a current status.
        int online = _roster?.Friends.Count(f => f.Latest is not null) ?? 0;
        if (online > 0)
        {
            var ft = OverlayDraw.Text($"{online} active", FeedCaptionSize, MutedBrush);
            double cx = addRect.Left - 10 - ft.Width;
            OverlayDraw.TextLeftMid(ctx, ft, cx, midY);
            ctx.DrawEllipse(new SolidColorBrush(RunningColor), null, new Point(cx - 8, midY), 3, 3);
        }

        _socialHeaderRect = new Rect(0, top, width, SocialHeaderHeight);
    }

    private void DrawFriendRow(DrawingContext ctx, double width, double top, double rowH, RosterFriend f, int index)
    {
        // The status-change glow: a soft accent wash fading out behind the row.
        double glow = GlowIntensity(f.Profile.Id);
        if (glow > 0.01)
        {
            var g = Palette.Accent;
            var wash = new SolidColorBrush(Color.FromArgb((byte)(46 * glow), g.R, g.G, g.B));
            OverlayDraw.Panel(ctx, new Rect(HorizPad - 4, top + 1, width - 2 * (HorizPad - 4), rowH - 2), wash, null, 6);
        }

        double statusMid = top + FeedLineHeight / 2;

        // Avatar tile: the mood emoji if set, else a stable colour dot from the handle.
        const double tile = 20;
        DrawAvatarTile(ctx, HorizPad + tile / 2, statusMid, tile, f.Profile.MoodEmoji, f.Profile.Handle);
        double x = HorizPad + tile + 8;

        // Relative time (right), then @handle (accent), then the status filling the middle.
        double agoX = width - HorizPad;
        if (f.Latest is { } latest)
        {
            var agoFt = OverlayDraw.Text(FormatAgo(latest.CreatedAt), FeedTimeSize, MutedBrush);
            agoX = width - HorizPad - agoFt.Width;
            OverlayDraw.TextLeftMid(ctx, agoFt, agoX, statusMid);
        }

        var handleFt = OverlayDraw.Text("@" + f.Profile.Handle, FeedHandleSize, Palette.AccentBrush, FontWeight.SemiBold);
        OverlayDraw.TextLeftMid(ctx, handleFt, x, statusMid);
        double bodyX = x + handleFt.Width + 8;

        double bodyMax = agoX - 8 - bodyX;
        if (bodyMax > 12)
        {
            if (f.Latest is { } latest2)
            {
                string shown = OverlayDraw.Truncate(latest2.Body, FeedBodySize, bodyMax);
                OverlayDraw.TextLeftMid(ctx, OverlayDraw.Text(shown, FeedBodySize, FgBrush), bodyX, statusMid);
            }
            else
            {
                OverlayDraw.TextLeftMid(ctx, OverlayDraw.Text("(no status yet)", FeedBodySize, MutedBrush), bodyX, statusMid);
            }
        }

        // Reactions line (only when there's a post to react to): existing reactions as chips + a "+" button.
        if (f.Latest is { } post)
            DrawReactionsLine(ctx, width, top + FeedLineHeight, x, post.Id, f.Reactions, index);
    }

    // Draws the reaction chips and the trailing "+" react button, capturing each hit-rect for RouteClick.
    private void DrawReactionsLine(DrawingContext ctx, double width, double top, double x, Guid postId,
        IReadOnlyList<ReactionGroup> reactions, int index)
    {
        double midY = top + FeedReactionHeight / 2;
        double cx = x;
        var rowRects = new List<(Rect Rect, string Emoji)>();

        var mineFill = new SolidColorBrush(Color.FromArgb(40, Palette.Accent.R, Palette.Accent.G, Palette.Accent.B));
        foreach (var g in reactions)
        {
            var emojiFt = OverlayDraw.Emoji(g.Emoji, FeedReactionSize, FgBrush);
            var countFt = g.Count > 1 ? OverlayDraw.Text(g.Count.ToString(), FeedReactionSize,
                g.Mine ? Palette.AccentBrush : FgBrush) : null;
            double chipW = emojiFt.Width + (countFt is null ? 0 : countFt.Width + 4) + 14;
            if (cx + chipW > width - HorizPad - 28) break;   // leave room for the "+" button
            var chip = new Rect(cx, top + 2, chipW, FeedReactionHeight - 4);
            OverlayDraw.Panel(ctx, chip, g.Mine ? mineFill : FeedChipBrush,
                g.Mine ? new Pen(Palette.AccentBrush, 1) : null, 8);
            OverlayDraw.TextLeftMid(ctx, emojiFt, cx + 7, midY);
            if (countFt is not null) OverlayDraw.TextLeftMid(ctx, countFt, cx + 7 + emojiFt.Width + 4, midY);
            rowRects.Add((chip, g.Emoji));
            cx += chipW + 5;
        }

        // The "+" react button (a hand-drawn plus, so it never falls to tofu).
        var plus = new Rect(cx, top + 2, 22, FeedReactionHeight - 4);
        bool hoverPlus = _hoveredReactAdd == index;
        if (hoverPlus) OverlayDraw.Panel(ctx, plus, FeedHoverBrush, null, 8);
        DrawPlusGlyph(ctx, MutedBrush, plus.Center.X, plus.Center.Y);
        _reactAddRects.Add((plus, postId, index));
        foreach (var (rect, emoji) in rowRects) _reactChipRects.Add((rect, postId, emoji));
    }

    // The "you" row: your own avatar + current status (or the "what are you working on?" prompt when you have
    // none), with a right-hand affordance to post/update. Clicking anywhere on it opens the composer.
    private void DrawComposeRow(DrawingContext ctx, double width, double top)
    {
        double midY = top + 3 + FeedLineHeight / 2;
        if (_hoveredSocialCompose)
            OverlayDraw.Panel(ctx, new Rect(HorizPad - 4, top + 2, width - 2 * (HorizPad - 4), FeedLineHeight),
                FeedHoverBrush, null, 6);

        const double tile = 20;
        DrawAvatarTile(ctx, HorizPad + tile / 2, midY, tile, _roster?.Me?.MoodEmoji, _roster?.Me?.Handle ?? "you");
        double x = HorizPad + tile + 8;

        bool hasStatus = _roster?.MyLatest is not null;
        // Right affordance: "Update" when you already have a status, "Post" otherwise.
        var actionFt = OverlayDraw.Text(hasStatus ? "Update" : "Post", FeedReactionSize, Palette.AccentBrush, FontWeight.SemiBold);
        double actionX = width - HorizPad - actionFt.Width;
        OverlayDraw.TextLeftMid(ctx, actionFt, actionX, midY);
        double rightEdge = actionX;

        // A muted "you" label so the row reads as yours, then your status (or the prompt).
        var youFt = OverlayDraw.Text("you", FeedHandleSize, MutedBrush, FontWeight.SemiBold);
        OverlayDraw.TextLeftMid(ctx, youFt, x, midY);
        double bodyX = x + youFt.Width + 8;

        if (_roster?.MyLatest is { } mine)
        {
            var agoFt = OverlayDraw.Text(FormatAgo(mine.CreatedAt), FeedTimeSize, MutedBrush);
            double agoX = rightEdge - 8 - agoFt.Width;
            OverlayDraw.TextLeftMid(ctx, agoFt, agoX, midY);
            double bodyMax = agoX - 8 - bodyX;
            if (bodyMax > 12)
                OverlayDraw.TextLeftMid(ctx, OverlayDraw.Text(OverlayDraw.Truncate(mine.Body, FeedBodySize, bodyMax),
                    FeedBodySize, FgBrush), bodyX, midY);
        }
        else
        {
            OverlayDraw.TextLeftMid(ctx, OverlayDraw.Text("what are you working on?", FeedBodySize, MutedBrush),
                bodyX, midY);
        }

        _socialComposeRect = new Rect(0, top, width, FeedLineHeight + 6);
    }

    // A small rounded avatar tile: the mood emoji centred if set, else a stable colour dot from the handle.
    private void DrawAvatarTile(DrawingContext ctx, double cx, double cy, double size, string? emoji, string handle)
    {
        var rect = new Rect(cx - size / 2, cy - size / 2, size, size);
        OverlayDraw.Panel(ctx, rect, FeedTileBrush, null, 6);
        if (!string.IsNullOrWhiteSpace(emoji))
        {
            var ft = OverlayDraw.Emoji(emoji, size * 0.62, FgBrush);
            ctx.DrawText(ft, new Point(cx - ft.Width / 2, cy - ft.Height / 2));
        }
        else
        {
            ctx.DrawEllipse(new SolidColorBrush(AvatarColor(handle)), null, new Point(cx, cy), 3.5, 3.5);
        }
    }

    // A small "+" for the react button (drawn, not a glyph, so it can't fall to tofu).
    private static void DrawPlusGlyph(DrawingContext ctx, IBrush b, double cx, double cy)
    {
        var pen = new Pen(b, 1.4);
        ctx.DrawLine(pen, new Point(cx - 3.5, cy), new Point(cx + 3.5, cy));
        ctx.DrawLine(pen, new Point(cx, cy - 3.5), new Point(cx, cy + 3.5));
    }

    // A small chevron: ▸ when collapsed, ▾ when expanded.
    private void DrawChevron(DrawingContext ctx, double cx, double cy, bool expanded)
    {
        var pen = new Pen(MutedBrush, 1.4);
        if (expanded)
        {
            ctx.DrawLine(pen, new Point(cx - 3, cy - 1.5), new Point(cx, cy + 2));
            ctx.DrawLine(pen, new Point(cx, cy + 2), new Point(cx + 3, cy - 1.5));
        }
        else
        {
            ctx.DrawLine(pen, new Point(cx - 1.5, cy - 3), new Point(cx + 2, cy));
            ctx.DrawLine(pen, new Point(cx + 2, cy), new Point(cx - 1.5, cy + 3));
        }
    }

    // ── hit-testing ─────────────────────────────────────────────────────────────────────────────────────

    private Rect _socialHeaderRect, _socialComposeRect, _socialMoreRect, _socialAddRect;
    private readonly List<(Rect Rect, Guid PostId, string Emoji)> _reactChipRects = new();
    private readonly List<(Rect Rect, Guid PostId, int Index)> _reactAddRects = new();
    private int _hoveredReactAdd = -1;
    private bool _hoveredSocialHeader, _hoveredSocialCompose, _hoveredSocialAdd;

    // Called from OnPointerMoved to refresh the region's hover state; returns true if anything changed.
    private bool UpdateSocialRegionHover(Point p)
    {
        bool add = _socialAddRect.Width > 0 && _socialAddRect.Contains(p);
        // The "+" sits inside the header band, so don't also light the header when hovering it.
        bool header = !add && _socialHeaderRect.Width > 0 && _socialHeaderRect.Contains(p);
        bool compose = _socialComposeRect.Width > 0 && _socialComposeRect.Contains(p);
        int reactAdd = HitTestReactAdd(p);
        bool changed = add != _hoveredSocialAdd || header != _hoveredSocialHeader
                       || compose != _hoveredSocialCompose || reactAdd != _hoveredReactAdd;
        _hoveredSocialAdd = add;
        _hoveredSocialHeader = header;
        _hoveredSocialCompose = compose;
        _hoveredReactAdd = reactAdd;
        return changed;
    }

    private void ClearSocialRegionHitRects()
    {
        _socialHeaderRect = _socialComposeRect = _socialMoreRect = _socialAddRect = default;
        _reactChipRects.Clear();
        _reactAddRects.Clear();
    }

    // Returns the react "+" button index under p, or -1.
    private int HitTestReactAdd(Point p)
    {
        foreach (var (rect, _, index) in _reactAddRects) if (rect.Contains(p)) return index;
        return -1;
    }

    // Routes a click inside the region. Returns true if it consumed the click.
    private bool RouteSocialRegionClick(Point p)
    {
        foreach (var (rect, postId, emoji) in _reactChipRects)
            if (rect.Contains(p)) { ToggleReaction(postId, emoji); return true; }

        foreach (var (rect, postId, _) in _reactAddRects)
            if (rect.Contains(p)) { ShowReactionPicker(postId); return true; }

        if (_socialAddRect.Width > 0 && _socialAddRect.Contains(p)) { FriendsRequested?.Invoke(); return true; }
        if (_socialComposeRect.Width > 0 && _socialComposeRect.Contains(p)) { PostStatusRequested?.Invoke(); return true; }
        if (_socialMoreRect.Width > 0 && _socialMoreRect.Contains(p)) { FriendsRequested?.Invoke(); return true; }
        if (_socialHeaderRect.Width > 0 && _socialHeaderRect.Contains(p)) { OnSocialHeaderClicked(); return true; }
        return false;
    }

    // Toggles the signed-in user's reaction on a post: off if it's already yours, on otherwise. The App relays
    // it and re-polls, so the chip settles to the server truth within a beat.
    private void ToggleReaction(Guid postId, string emoji)
    {
        bool mine = _roster?.Friends
            .FirstOrDefault(f => f.Latest?.Id == postId)?.Reactions
            .FirstOrDefault(r => r.Emoji == emoji)?.Mine ?? false;
        ReactRequested?.Invoke(postId, emoji, !mine);
    }

    // Pops a small emoji menu for the "+" button; picking one adds that reaction.
    private void ShowReactionPicker(Guid postId)
    {
        var items = new List<Control>();
        foreach (var emoji in ReactionChoices)
        {
            var mi = new MenuItem { Header = emoji };
            mi.Click += (_, _) => ReactRequested?.Invoke(postId, emoji, true);
            items.Add(mi);
        }
        ShowFlyout(items);
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────────────────────

    // A stable per-handle colour from the theme's status hues (only used when a friend has no mood emoji).
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
