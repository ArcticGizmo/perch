using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Perch.Avalonia.Rendering;
using Perch.Avalonia.Theming;
using Perch.Avalonia.Windows;
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

    private int _maxFriends = 3;   // friends shown before a "+N more" overflow line (AppSettings.MaxFriendsShown)

    // The reaction presets the picker offers as a grid, with search keywords. The picker also accepts any
    // system emoji you type or paste, so these are a fast path, not a limit.
    private static readonly (string Emoji, string Keywords)[] ReactionPresets =
    [
        ("👍", "up like yes good approve thumbs"), ("🔥", "fire lit hot streak on a roll"),
        ("🎉", "party celebrate tada ship"),       ("😂", "laugh lol funny haha"),
        ("😮", "wow surprised whoa"),               ("❤️", "love heart red"),
        ("🙌", "praise hooray raised hands"),        ("👀", "eyes looking watching reviewing"),
        ("😢", "sad cry tear"),                      ("😔", "down disappointed sad"),
        ("🚀", "rocket ship launch fast"),          ("💯", "hundred perfect score nailed it"),
        ("🤯", "mind blown wow"),                    ("🫡", "salute respect o7"),
        ("😅", "phew nervous close sweat"),          ("💀", "dead dying rip lol"),
    ];

    private static readonly IBrush FeedTileBrush  = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255));  // avatar tile
    private static readonly IBrush FeedChipBrush   = new SolidColorBrush(Color.FromArgb(28, 255, 255, 255)); // reaction chip
    private static readonly IBrush FeedHoverBrush   = new SolidColorBrush(Color.FromArgb(26, 255, 255, 255)); // row/button hover

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
    private static double? _feedLineH, _feedCaptionH, _feedReactionH, _feedHandleH, _feedRowH;
    private static double FeedLineHeight
        => _feedLineH ??= OverlayDraw.Text("Xg", FeedBodySize, FgBrush).Height + 8;
    private static double FeedCaptionHeight
        => _feedCaptionH ??= OverlayDraw.Text("Xg", FeedCaptionSize, MutedBrush).Height + 4;
    private static double FeedReactionHeight
        => _feedReactionH ??= OverlayDraw.Text("Xg😀", FeedReactionSize, FgBrush).Height + 8;
    private static double FeedHandleLineHeight
        => _feedHandleH ??= OverlayDraw.Text("Xg", FeedHandleSize, FgBrush).Height;
    // A friend/you row is two lines: handle + time on top, status + reactions below, with padding.
    private static double FeedRowHeight
        => _feedRowH ??= FeedHandleLineHeight + OverlayDraw.Text("Xg", FeedBodySize, FgBrush).Height + 14;

    private double SocialHeaderHeight => FeedCaptionHeight + 12;

    /// <summary>Raised when the region header's chevron is clicked to expand or collapse — the App persists it.</summary>
    public event Action<bool>? SocialRegionExpandChanged;

    /// <summary>Raised when a reaction chip or the "+" button is clicked: the post to react to, the emoji, and
    /// whether it should be on (add) or off (remove). The App relays it to the social client.</summary>
    public event Action<Guid, string, bool>? ReactRequested;

    // Shown once Social is on and you're signed in with a handle (there's just the one "Social feed" switch now
    // — the region and the feature are the same thing). Its sibling — the sign-in prompt strip — shows in the
    // complementary state, so the two never overlap.
    private bool SocialRegionVisible => _socialEnabled && _socialSignedIn && _socialHasHandle;

    private int FriendRowCount => Math.Min(_maxFriends, _roster?.Friends.Count ?? 0);
    private bool FriendOverflow => (_roster?.Friends.Count ?? 0) > _maxFriends;

    /// <summary>Sets how many friends the roster shows before the "+N more" overflow (AppSettings.MaxFriendsShown,
    /// clamped to a sane range). Changing it can change the region height, so relayout when it actually differs.</summary>
    public void SetMaxFriendsShown(int count)
    {
        count = Math.Clamp(count, 1, 20);
        if (_maxFriends == count) return;
        _maxFriends = count;
        if (SocialRegionVisible && _regionExpanded) RemeasurePanel();
    }

    private double SocialRegionHeight
    {
        get
        {
            if (!SocialRegionVisible) return 0;
            double h = SocialHeaderHeight;
            if (!_regionExpanded) return h;

            // Two lines per friend/you row: handle + time on top, status + reactions below — the overlay is
            // narrow, so the status needs its own line to breathe.
            h += FriendRowCount * FeedRowHeight;
            if (FriendOverflow) h += FeedCaptionHeight;
            h += FeedRowHeight;   // the "you" / compose row
            return h;
        }
    }

    // Kept for the measure/paint code in OverlayCanvas.cs that still speaks in terms of the old "feed strip".
    private bool FeedStripVisible => SocialRegionVisible;
    private double FeedStripHeight => SocialRegionHeight;

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
        else if (_socialEnabled) InvalidateVisual();
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
                DrawFriendRow(ctx, width, y, r.Friends[i], i);
                y += FeedRowHeight;
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
        var capFt = OverlayDraw.Text("FRIENDS", FeedCaptionSize, MutedBrush);
        OverlayDraw.TextLeftMid(ctx, capFt, x, midY);
        x += capFt.Width + 8;

        // Invite badge: a small attention pill with the pending-request count. Clicking it opens Friends.
        int invites = _roster?.IncomingRequests ?? 0;
        if (invites > 0)
        {
            var nFt = OverlayDraw.Text(invites.ToString(), FeedCaptionSize, BgBrush, FontWeight.SemiBold);
            double pillW = Math.Max(16, nFt.Width + 10);
            var pill = new Rect(x, midY - FeedCaptionHeight / 2, pillW, FeedCaptionHeight);
            OverlayDraw.Pill(ctx, new SolidColorBrush(AttentionColor), pill);
            OverlayDraw.TextLeftMid(ctx, nFt, x + (pillW - nFt.Width) / 2, midY);
            _socialInviteRect = pill;
        }

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

    // One friend, on two lines: the mood avatar spans both; the top line is @handle (left) + time (right); the
    // bottom line is the status (left, truncated) with reaction chips and the on-hover "+" tucked right. Giving
    // the status its own line keeps it readable in the narrow overlay. A truncated status is a dwell target.
    private void DrawFriendRow(DrawingContext ctx, double width, double top, RosterFriend f, int index)
    {
        double rowH = FeedRowHeight;
        _friendRowRects.Add((new Rect(0, top, width, rowH), index));

        // The status-change glow: a soft accent wash fading out behind the whole row.
        double glow = GlowIntensity(f.Profile.Id);
        if (glow > 0.01)
        {
            var g = Palette.Accent;
            var wash = new SolidColorBrush(Color.FromArgb((byte)(46 * glow), g.R, g.G, g.B));
            OverlayDraw.Panel(ctx, new Rect(HorizPad - 4, top + 1, width - 2 * (HorizPad - 4), rowH - 2), wash, null, 6);
        }

        // Avatar (mood from the latest status, then profile mood, then a colour dot), centred over both lines.
        const double tile = 28;
        string? mood = f.Latest?.MoodEmoji ?? f.Profile.MoodEmoji;
        DrawAvatarTile(ctx, HorizPad + tile / 2, top + rowH / 2, tile, mood, f.Profile.Handle);
        double x = HorizPad + tile + 10;

        double topMid = top + 6 + FeedHandleLineHeight / 2;
        double botMid = top + rowH - 6 - OverlayDraw.Text("Xg", FeedBodySize, FgBrush).Height / 2;

        // Top line: @handle (left), time (right).
        OverlayDraw.TextLeftMid(ctx, OverlayDraw.Text("@" + f.Profile.Handle, FeedHandleSize, Palette.AccentBrush, FontWeight.SemiBold), x, topMid);
        if (f.Latest is { } head)
        {
            var agoFt = OverlayDraw.Text(FormatAgo(head.CreatedAt), FeedTimeSize, MutedBrush);
            OverlayDraw.TextLeftMid(ctx, agoFt, width - HorizPad - agoFt.Width, topMid);
        }

        // Bottom line: build the right cluster (reactions, then the hover "+") from the right edge inward.
        double cursor = width - HorizPad;
        if (f.Latest is { } latest)
        {
            var chips = ChipsFor(f.Reactions);
            double chipsW = chips.Sum(ChipWidth) + Math.Max(0, chips.Count - 1) * 5;
            if (chips.Count > 0 && chipsW < width * 0.7)
            {
                double cx = cursor - chipsW;
                foreach (var c in chips) cx = DrawChip(ctx, cx, botMid, c, latest.Id, _hoveredFriendRow == index) + 5;
                cursor -= chipsW + 6;
            }

            if (_hoveredFriendRow == index)
            {
                var plus = new Rect(cursor - 22, botMid - 9, 22, 18);
                if (_hoveredReactAdd == index) OverlayDraw.Panel(ctx, plus, FeedHoverBrush, null, 6);
                DrawPlusGlyph(ctx, _hoveredReactAdd == index ? FgBrush : MutedBrush, plus.Center.X, plus.Center.Y);
                _reactAddRects.Add((plus, latest.Id, index));
                cursor -= 22 + 4;
            }
        }

        double bodyMax = cursor - 8 - x;   // from the handle column to just before the right cluster
        if (bodyMax > 12)
        {
            if (f.Latest is { } latest2)
            {
                string shown = OverlayDraw.Truncate(latest2.Body, FeedBodySize, bodyMax);
                OverlayDraw.TextLeftMid(ctx, OverlayDraw.Text(shown, FeedBodySize, FgBrush), x, botMid);
                if (shown != latest2.Body)
                    _socialStatusTips.Add((new Rect(x, botMid - FeedBodySize, bodyMax, FeedBodySize * 2), latest2.Body));
            }
            else
            {
                OverlayDraw.TextLeftMid(ctx, OverlayDraw.Text("(no status yet)", FeedBodySize, MutedBrush), x, botMid);
            }
        }
    }

    // A chip ready to draw: either one reaction group, or a "combined" summary standing in for >2 distinct
    // emojis (Emoji = the top one, Count = the total, Tooltip = the per-emoji breakdown).
    private readonly record struct DrawableChip(string Emoji, int Count, bool Mine, bool Combined, string? Tooltip);

    // Turns the reaction groups into the chips to draw: individually up to two distinct emojis, else a single
    // combined count chip (so a post can't sprawl into a long row of chips).
    private static List<DrawableChip> ChipsFor(IReadOnlyList<ReactionGroup> groups)
    {
        if (groups.Count == 0) return [];
        if (groups.Count <= 2)
            return groups.Select(g => new DrawableChip(g.Emoji, g.Count, g.Mine, false, null)).ToList();
        int total = groups.Sum(g => g.Count);
        string breakdown = string.Join("   ", groups.Select(g => $"{g.Emoji} {g.Count}"));
        return [new DrawableChip(groups[0].Emoji, total, groups.Any(g => g.Mine), true, breakdown)];
    }

    // A chip shows its count when it's >1 or it's the combined summary.
    private static bool ShowsCount(DrawableChip c) => c.Count > 1 || c.Combined;

    private static double ChipWidth(DrawableChip c)
    {
        double emojiW = OverlayDraw.Emoji(c.Emoji, FeedReactionSize, Brushes.White).Width;
        double countW = ShowsCount(c) ? OverlayDraw.MeasureWidth(c.Count.ToString(), FeedReactionSize) + 4 : 0;
        return emojiW + countW + 14;
    }

    // Draws one chip at x, centred on midY; returns the x just past it. Captures its hit-rect (empty emoji marks
    // the combined chip → clicking opens the picker) and, for the combined chip, a tooltip target. The "mine"
    // accent outline is drawn only while <paramref name="rowHovered"/> — at rest the chips sit quietly with no
    // border, and the outline appears when you hover the row that owns them.
    private double DrawChip(DrawingContext ctx, double x, double midY, DrawableChip c, Guid postId, bool rowHovered,
        bool interactive = true)
    {
        double chipH = FeedReactionHeight - 4;
        double w = ChipWidth(c);
        var chip = new Rect(x, midY - chipH / 2, w, chipH);
        var mineFill = new SolidColorBrush(Color.FromArgb(40, Palette.Accent.R, Palette.Accent.G, Palette.Accent.B));
        OverlayDraw.Panel(ctx, chip, c.Mine ? mineFill : FeedChipBrush,
            c.Mine && rowHovered ? new Pen(Palette.AccentBrush, 1) : null, 8);
        var emojiFt = OverlayDraw.Emoji(c.Emoji, FeedReactionSize, FgBrush);
        OverlayDraw.TextLeftMid(ctx, emojiFt, x + 7, midY);
        if (ShowsCount(c))
            OverlayDraw.TextLeftMid(ctx, OverlayDraw.Text(c.Count.ToString(), FeedReactionSize,
                c.Mine ? Palette.AccentBrush : FgBrush), x + 7 + emojiFt.Width + 4, midY);
        // Own-post chips are display-only (you can't react to yourself) — skip the click hit-rect, but keep the
        // combined-summary tooltip so you can still see who reacted with what.
        if (interactive) _reactChipRects.Add((chip, postId, c.Combined ? "" : c.Emoji));
        if (c.Combined && c.Tooltip is { } tip) _reactSummaryTips.Add((chip, tip));
        return chip.Right;
    }

    // The "you" row: same two-line shape as a friend — your avatar, "you" + time on top, your current status (or
    // the "what are you working on?" prompt) on the bottom, with an Update/Post affordance. Clicking opens the composer.
    private void DrawComposeRow(DrawingContext ctx, double width, double top)
    {
        double rowH = FeedRowHeight;
        if (_hoveredSocialCompose)
            OverlayDraw.Panel(ctx, new Rect(HorizPad - 4, top + 1, width - 2 * (HorizPad - 4), rowH - 2),
                FeedHoverBrush, null, 6);

        const double tile = 28;
        string? myMood = _roster?.MyLatest?.MoodEmoji ?? _roster?.Me?.MoodEmoji;
        DrawAvatarTile(ctx, HorizPad + tile / 2, top + rowH / 2, tile, myMood, _roster?.Me?.Handle ?? "you");
        double x = HorizPad + tile + 10;

        double topMid = top + 6 + FeedHandleLineHeight / 2;
        double botMid = top + rowH - 6 - OverlayDraw.Text("Xg", FeedBodySize, FgBrush).Height / 2;

        // Top line: "you" (left) + time (right, when you have a status).
        OverlayDraw.TextLeftMid(ctx, OverlayDraw.Text("you", FeedHandleSize, MutedBrush, FontWeight.SemiBold), x, topMid);
        if (_roster?.MyLatest is { } head)
        {
            var agoFt = OverlayDraw.Text(FormatAgo(head.CreatedAt), FeedTimeSize, MutedBrush);
            OverlayDraw.TextLeftMid(ctx, agoFt, width - HorizPad - agoFt.Width, topMid);
        }

        // Bottom line, right cluster built from the right edge inward: Update/Post affordance, then the
        // reactions friends left on your status (display-only — you can't react to your own post).
        double cursor = width - HorizPad;
        var actionFt = OverlayDraw.Text(_roster?.MyLatest is not null ? "Update" : "Post",
            FeedReactionSize, Palette.AccentBrush, FontWeight.SemiBold);
        OverlayDraw.TextLeftMid(ctx, actionFt, cursor - actionFt.Width, botMid);
        cursor -= actionFt.Width + 8;

        if (_roster is { MyLatest: { } myPost, MyReactions.Count: > 0 } r2)
        {
            var chips = ChipsFor(r2.MyReactions);
            double chipsW = chips.Sum(ChipWidth) + Math.Max(0, chips.Count - 1) * 5;
            if (chipsW < width * 0.7)
            {
                double cx = cursor - chipsW;
                foreach (var c in chips) cx = DrawChip(ctx, cx, botMid, c, myPost.Id, _hoveredSocialCompose, interactive: false) + 5;
                cursor -= chipsW + 6;
            }
        }

        double bodyMax = cursor - 8 - x;
        if (bodyMax > 12)
        {
            if (_roster?.MyLatest is { } mine)
            {
                string shown = OverlayDraw.Truncate(mine.Body, FeedBodySize, bodyMax);
                OverlayDraw.TextLeftMid(ctx, OverlayDraw.Text(shown, FeedBodySize, FgBrush), x, botMid);
                if (shown != mine.Body)
                    _socialStatusTips.Add((new Rect(x, botMid - FeedBodySize, bodyMax, FeedBodySize * 2), mine.Body));
            }
            else
            {
                OverlayDraw.TextLeftMid(ctx, OverlayDraw.Text("what are you working on?", FeedBodySize, MutedBrush), x, botMid);
            }
        }

        _socialComposeRect = new Rect(0, top, width, rowH);
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

    private Rect _socialHeaderRect, _socialComposeRect, _socialMoreRect, _socialAddRect, _socialInviteRect;
    private readonly List<(Rect Rect, Guid PostId, string Emoji)> _reactChipRects = new();
    private readonly List<(Rect Rect, Guid PostId, int Index)> _reactAddRects = new();
    private readonly List<(Rect Rect, int Index)> _friendRowRects = new();
    private readonly List<(Rect Rect, string Full)> _socialStatusTips = new();
    private readonly List<(Rect Rect, string Breakdown)> _reactSummaryTips = new();
    private int _hoveredReactAdd = -1;
    private int _hoveredFriendRow = -1;
    private bool _hoveredSocialHeader, _hoveredSocialCompose, _hoveredSocialAdd;

    // Called from OnPointerMoved to refresh the region's hover state; returns true if anything changed. Note
    // the friend-row hover is computed before the "+" (which only exists while its row is hovered), so a fresh
    // move re-derives both together.
    private bool UpdateSocialRegionHover(Point p)
    {
        bool add = _socialAddRect.Width > 0 && _socialAddRect.Contains(p);
        // The "+" sits inside the header band, so don't also light the header when hovering it.
        bool header = !add && _socialHeaderRect.Width > 0 && _socialHeaderRect.Contains(p);
        bool compose = _socialComposeRect.Width > 0 && _socialComposeRect.Contains(p);
        int friendRow = HitTestFriendRow(p);
        int reactAdd = HitTestReactAdd(p);
        bool changed = add != _hoveredSocialAdd || header != _hoveredSocialHeader
                       || compose != _hoveredSocialCompose || reactAdd != _hoveredReactAdd
                       || friendRow != _hoveredFriendRow;
        _hoveredSocialAdd = add;
        _hoveredSocialHeader = header;
        _hoveredSocialCompose = compose;
        _hoveredFriendRow = friendRow;
        _hoveredReactAdd = reactAdd;
        return changed;
    }

    private int HitTestFriendRow(Point p)
    {
        foreach (var (rect, index) in _friendRowRects) if (rect.Contains(p)) return index;
        return -1;
    }

    // Index of the truncated status under p, or -1 — the dwell-tooltip source (status over the line).
    private int HitTestSocialStatus(Point p)
    {
        for (int i = 0; i < _socialStatusTips.Count; i++) if (_socialStatusTips[i].Rect.Contains(p)) return i;
        return -1;
    }

    // Index of the combined-reaction summary chip under p, or -1 — its dwell tooltip lists the per-emoji counts.
    private int HitTestReactionSummary(Point p)
    {
        for (int i = 0; i < _reactSummaryTips.Count; i++) if (_reactSummaryTips[i].Rect.Contains(p)) return i;
        return -1;
    }

    /// <summary>Clears the region's hover state (called on pointer-exit so a row doesn't keep its highlight
    /// after the cursor leaves the overlay).</summary>
    private bool ClearSocialRegionHover()
    {
        bool any = _hoveredSocialAdd || _hoveredSocialHeader || _hoveredSocialCompose
                   || _hoveredReactAdd >= 0 || _hoveredFriendRow >= 0;
        _hoveredSocialAdd = _hoveredSocialHeader = _hoveredSocialCompose = false;
        _hoveredReactAdd = _hoveredFriendRow = -1;
        return any;
    }

    private void ClearSocialRegionHitRects()
    {
        _socialHeaderRect = _socialComposeRect = _socialMoreRect = _socialAddRect = _socialInviteRect = default;
        _reactChipRects.Clear();
        _reactAddRects.Clear();
        _friendRowRects.Clear();
        _socialStatusTips.Clear();
        _reactSummaryTips.Clear();
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
            if (rect.Contains(p))
            {
                if (emoji.Length == 0) ShowReactionPicker(postId, ToScreen(rect.Left, rect.Bottom + 2));   // combined summary chip → pick/change
                else ToggleReaction(postId, emoji);
                return true;
            }

        foreach (var (rect, postId, _) in _reactAddRects)
            if (rect.Contains(p)) { ShowReactionPicker(postId, ToScreen(rect.Left, rect.Bottom + 2)); return true; }

        if (_socialInviteRect.Width > 0 && _socialInviteRect.Contains(p)) { FriendsRequested?.Invoke(); return true; }
        if (_socialAddRect.Width > 0 && _socialAddRect.Contains(p)) { FriendsRequested?.Invoke(); return true; }
        if (_socialComposeRect.Width > 0 && _socialComposeRect.Contains(p)) { PostStatusRequested?.Invoke(); return true; }
        if (_socialMoreRect.Width > 0 && _socialMoreRect.Contains(p)) { FriendsRequested?.Invoke(); return true; }
        if (_socialHeaderRect.Width > 0 && _socialHeaderRect.Contains(p)) { OnSocialHeaderClicked(); return true; }
        return false;
    }

    // True when p is anywhere in the social region (or the sign-in strip) — the Social right-click menu lives
    // here now, not on the overlay header.
    private bool HitTestSocialArea(Point p) =>
        (_socialSignInRect.Width > 0 && _socialSignInRect.Contains(p))
        || (_socialHeaderRect.Width > 0 && _socialHeaderRect.Contains(p))
        || (_socialComposeRect.Width > 0 && _socialComposeRect.Contains(p))
        || (_socialMoreRect.Width > 0 && _socialMoreRect.Contains(p))
        || HitTestFriendRow(p) >= 0;

    // The Social context menu, shown on a right-click within the region. Adapts to the sign-in state, and adds a
    // per-friend "React…" when a specific friend row was clicked.
    private void ShowSocialMenu(Point p)
    {
        var items = new List<Control>();
        if (!_socialSignedIn)
        {
            items.Add(MenuItem("Sign in to Social", () => SignInRequested?.Invoke()));
            items.Add(MenuItem("Social settings…", () => SocialManageRequested?.Invoke()));
            ShowFlyout(items);
            return;
        }
        if (!_socialHasHandle)
        {
            items.Add(MenuItem("Finish setup — claim a handle", () => SocialManageRequested?.Invoke()));
            items.Add(MenuItem("Sign out of Social", () => SignOutRequested?.Invoke()));
            ShowFlyout(items);
            return;
        }

        int fr = HitTestFriendRow(p);
        if (fr >= 0 && _roster is { } r && fr < r.Friends.Count && r.Friends[fr].Latest is { } post)
        {
            items.Add(MenuItem($"React to @{r.Friends[fr].Profile.Handle}…", () => ShowReactionPicker(post.Id, ToScreen(p.X, p.Y))));
            items.Add(new Separator());
        }
        items.Add(MenuItem("Post a status…", () => PostStatusRequested?.Invoke()));
        items.Add(MenuItem("Friends…", () => FriendsRequested?.Invoke()));
        items.Add(MenuItem("Social settings…", () => SocialManageRequested?.Invoke()));
        items.Add(MenuItem("Sign out of Social", () => SignOutRequested?.Invoke()));
        ShowFlyout(items);
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

    // Pops the reaction picker at the given screen anchor: a grid of preset emojis plus an entry that accepts
    // any system emoji you type or paste. Picking one adds that reaction to the post.
    private void ShowReactionPicker(Guid postId, PixelPoint anchor)
    {
        var picker = new EmojiPickerWindow("React", ReactionPresets,
            emoji => { if (!string.IsNullOrWhiteSpace(emoji)) ReactRequested?.Invoke(postId, emoji, true); },
            anchor);
        picker.Show();
        picker.Activate();
    }

    // Shows the full text of a truncated status as a dwell tooltip (wired via TipKind.SocialStatus).
    private void ShowSocialStatusTooltip(int index)
    {
        if (index < 0 || index >= _socialStatusTips.Count) return;
        var (rect, full) = _socialStatusTips[index];
        Tooltip().ShowLines([new(full, OverlayTooltip.FgColor, false)], ToScreen(rect.Left, rect.Bottom + 4));
    }

    // Shows the per-emoji breakdown behind a combined reaction chip (wired via TipKind.ReactionSummary).
    private void ShowReactionSummaryTooltip(int index)
    {
        if (index < 0 || index >= _reactSummaryTips.Count) return;
        var (rect, breakdown) = _reactSummaryTips[index];
        var lines = breakdown.Split("   ")
            .Select(s => new OverlayTooltip.Line(s, OverlayTooltip.FgColor, false)).ToList();
        Tooltip().ShowLines(lines, ToScreen(rect.Left, rect.Bottom + 4));
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
