using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Platform;
using Avalonia.Threading;
using Perch.Avalonia.Rendering;
using Perch.Avalonia.Theming;
using Perch.Data;

namespace Perch.Avalonia.Windows;

/// <summary>
/// The pay-off for unlocking achievements: a full-screen, topmost overlay that dims the desktop under a
/// black vignette and reveals the earned badges on cards in the middle of the screen with a coin-flip
/// "coming out of the screen" animation, under an "Achievement Unlocked!" heading, with <b>OK</b> and
/// <b>Don't show again</b> buttons. This replaces the old confetti burst for achievement unlocks (the
/// manual "confetti finish" still uses <see cref="ConfettiWindow"/>). A batch shows up to
/// <see cref="MaxCards"/> cards side by side (shiniest first), plus a "+N more" card for the remainder,
/// each flipping in with a small stagger. Batches that arrive while one is up are shown after it.
/// <b>Don't show again</b> raises <see cref="DoNotShowAgain"/> so the app can persist the opt-out.
/// </summary>
internal sealed class AchievementCardWindow : Window
{
    private const int MaxCards = 3;          // real cards shown; the rest fold into a "+N more" card
    private const double MultiScale = 0.75;  // cards shrink when more than one is shown, so a row fits
    private const int StaggerMs = 110;       // each card flips in a beat after the previous

    private readonly StackPanel _cardsHost = NewCardsHost();
    private readonly Queue<IReadOnlyList<AchievementUnlock>> _batches = new();
    private bool _showing;

    /// <summary>Raised (on the UI thread) when the user clicks "Don't show again", so the app can turn the
    /// celebration off in settings. The window closes itself right after.</summary>
    public event Action? DoNotShowAgain;

    public AchievementCardWindow()
    {
        WindowDecorations = WindowDecorations.None;
        Background = Brushes.Transparent;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        Topmost = true;
        ShowInTaskbar = false;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Content = BuildRoot();
    }

    /// <summary>Erupt the reveal across <paramref name="screen"/> for this batch of freshly-unlocked badges.
    /// Positions to cover the whole screen so the vignette dims it.</summary>
    public void Present(IReadOnlyList<AchievementUnlock> unlocks, Screen screen)
    {
        var b = screen.Bounds;              // physical pixels
        double scale = screen.Scaling;
        Position = b.Position;
        Width = b.Width / scale;            // cover the screen in DIPs
        Height = b.Height / scale;

        if (!IsVisible) Show();
        Activate();
        AddBatch(unlocks);
    }

    /// <summary>Add another batch to a reveal that's already on screen (a later batch caught it still up);
    /// it plays after the current one is dismissed.</summary>
    public void Enqueue(IReadOnlyList<AchievementUnlock> unlocks) => AddBatch(unlocks);

    private void AddBatch(IReadOnlyList<AchievementUnlock> unlocks)
    {
        if (unlocks.Count == 0) return;
        _batches.Enqueue(unlocks);
        if (!_showing) ShowNextBatch();
    }

    private Control BuildRoot()
    {
        var title = new TextBlock
        {
            Text = "Achievement Unlocked!",
            FontSize = 26, FontWeight = FontWeight.Bold,
            Foreground = new SolidColorBrush(Palette.Title),
            HorizontalAlignment = HorizontalAlignment.Center,
        };

        var ok = MakeButton("OK", primary: true);
        ok.Click += (_, _) => Advance();
        var never = MakeButton("Don't show again");
        never.Click += (_, _) => { DoNotShowAgain?.Invoke(); Close(); };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 12,
            HorizontalAlignment = HorizontalAlignment.Center,
            Children = { ok, never },
        };

        var stack = new StackPanel
        {
            Spacing = 18,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { title, _cardsHost, buttons },
        };

        // A radial vignette: the whole desktop dimmed, darkest at the edges, so the cards in the middle
        // read with plenty of contrast. The window itself is transparent, so this blends over the desktop.
        // Dismissal is deliberately only via the buttons (or the keyboard) — a stray backdrop click won't
        // close it, so you always make an explicit OK / Don't-show-again choice.
        return new Grid { Background = Vignette(), Children = { stack } };
    }

    private static StackPanel NewCardsHost() => new()
    {
        Orientation = Orientation.Horizontal, Spacing = 4,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
    };

    private static IBrush Vignette() => new RadialGradientBrush
    {
        GradientOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
        Center = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
        RadiusX = new RelativeScalar(0.75, RelativeUnit.Relative),
        RadiusY = new RelativeScalar(0.75, RelativeUnit.Relative),
        GradientStops =
        {
            new GradientStop(Color.FromArgb(150, 0, 0, 0), 0),
            new GradientStop(Color.FromArgb(205, 0, 0, 0), 0.7),
            new GradientStop(Color.FromArgb(235, 0, 0, 0), 1),
        },
    };

    private static Button MakeButton(string text, bool primary = false) => new()
    {
        Content = text, Height = 40, MinWidth = 132, Padding = new Thickness(16, 0),
        Background = primary ? new SolidColorBrush(Palette.Accent) : new SolidColorBrush(Palette.ButtonBg),
        Foreground = primary ? Brushes.White : new SolidColorBrush(Palette.Fg),
        BorderBrush = new SolidColorBrush(Palette.Border), BorderThickness = new Thickness(1),
        HorizontalContentAlignment = HorizontalAlignment.Center,
        VerticalContentAlignment = VerticalAlignment.Center,
        CornerRadius = new CornerRadius(9), FontSize = 13,
        Cursor = new Cursor(StandardCursorType.Hand),
    };

    // How a batch lays out: the shiniest few shown as cards, the rest folded into a "+N more" tile, and the
    // scale that keeps the row comfortable (a single card stays full-size). Shared by the live window and
    // the headless snapshot so the two can't disagree.
    private static (List<AchievementUnlock> shown, int more, double scale) Plan(IReadOnlyList<AchievementUnlock> unlocks)
    {
        var ordered = unlocks
            .OrderByDescending(u => (int)u.Tier)
            .ThenBy(u => u.Name, StringComparer.Ordinal)
            .ToList();
        var shown = ordered.Take(MaxCards).ToList();
        int more = ordered.Count - shown.Count;
        int tiles = shown.Count + (more > 0 ? 1 : 0);
        return (shown, more, tiles <= 1 ? 1.0 : MultiScale);
    }

    private void ShowNextBatch()
    {
        if (_batches.Count == 0) { _showing = false; return; }
        _showing = true;

        var (shown, more, scale) = Plan(_batches.Dequeue());
        _cardsHost.Children.Clear();
        for (int i = 0; i < shown.Count; i++)
        {
            var card = new AchievementCard(scale);
            card.Reveal(shown[i], delayMs: i * StaggerMs);
            _cardsHost.Children.Add(card);
        }
        if (more > 0)
        {
            var card = new AchievementCard(scale);
            card.RevealMore(more, delayMs: shown.Count * StaggerMs);
            _cardsHost.Children.Add(card);
        }
    }

    // OK / Esc / Enter: play the next queued batch, or close when there are none left.
    private void Advance()
    {
        if (_batches.Count == 0) { Close(); return; }
        ShowNextBatch();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key is Key.Escape or Key.Enter or Key.Space) { Advance(); e.Handled = true; }
        base.OnKeyDown(e);
    }

    /// <summary>Builds a settled (fully-revealed) reveal surface for the headless render harness — the same
    /// vignette + card row + heading + buttons the live window shows, frozen at the end of the flip.</summary>
    internal static Control BuildStaticSurface(IReadOnlyList<AchievementUnlock> unlocks, double width, double height)
    {
        var (shown, more, scale) = Plan(unlocks);
        var host = NewCardsHost();
        foreach (var u in shown)
        {
            var card = new AchievementCard(scale);
            card.SetStatic(u);
            host.Children.Add(card);
        }
        if (more > 0)
        {
            var card = new AchievementCard(scale);
            card.SetStaticMore(more);
            host.Children.Add(card);
        }

        var title = new TextBlock
        {
            Text = "Achievement Unlocked!", FontSize = 26, FontWeight = FontWeight.Bold,
            Foreground = new SolidColorBrush(Palette.Title), HorizontalAlignment = HorizontalAlignment.Center,
        };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 12, HorizontalAlignment = HorizontalAlignment.Center,
            Children = { MakeButton("OK", primary: true), MakeButton("Don't show again") },
        };
        var stack = new StackPanel
        {
            Spacing = 18, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            Children = { title, host, buttons },
        };
        return new Grid { Width = width, Height = height, Background = Vignette(), Children = { stack } };
    }
}

/// <summary>
/// One owner-drawn card and its reveal animation: the tile scales up out of the screen while flipping about
/// its vertical axis like a tossed coin (front → edge → back → edge → front), landing face-up with a small
/// overshoot after an optional start delay (for staggering a row). A card shows either an unlocked badge
/// (tier-tinted, emoji + name + detail) or a "+N more" summary. Driven by a <see cref="DispatcherTimer"/>;
/// call <see cref="Reveal"/>/<see cref="RevealMore"/> to play it or the <c>SetStatic*</c> pair to freeze it.
/// All geometry scales with the constructor's <c>scale</c> so several cards fit one row.
/// </summary>
internal sealed class AchievementCard : Control
{
    private const int TickMs = 16, DurationMs = 950, Spins = 3;   // 3 half-turns → lands face-up

    private static readonly Typeface EmojiFace =
        new(new FontFamily("Segoe UI Emoji, Apple Color Emoji, Noto Color Emoji"));
    private static readonly ImmutableSolidColorBrush ShadowBrush = new(Color.FromArgb(120, 0, 0, 0));

    // The neutral look of the "+N more" card — a dim slate panel, distinct from the tier tints.
    private static readonly Color MoreBg = Color.FromRgb(38, 38, 52), MoreInk = Color.FromRgb(170, 178, 200);

    // Tier panel bg, ink (border/name/emoji tint) and ribbon label — the same tier vocabulary the grid uses.
    private static (Color bg, Color ink, string label) TierFor(AchievementTier t) => t switch
    {
        AchievementTier.Gold   => (Color.FromRgb(64, 54, 26), Color.FromRgb(240, 200, 96), "GOLD"),
        AchievementTier.Silver => (Color.FromRgb(44, 48, 58), Color.FromRgb(200, 208, 222), "SILVER"),
        _                      => (Color.FromRgb(58, 42, 30), Color.FromRgb(214, 158, 110), "BRONZE"),
    };

    private readonly double _scale, _cardW, _cardH, _pad;
    private AchievementUnlock? _unlock;
    private int? _moreCount;
    private DispatcherTimer? _timer;
    private int _delayMs, _ticks;
    private double _progress;   // 0 → 1 across the reveal (after any start delay)

    public AchievementCard(double scale = 1.0)
    {
        _scale = scale;
        _cardW = 300 * scale;
        _cardH = 340 * scale;
        _pad = 32 * scale;      // room for the pop overshoot + shadow (doubles as the gap between cards)
        Width = _cardW + _pad * 2;
        Height = _cardH + _pad * 2;
    }

    public void Reveal(AchievementUnlock u, int delayMs = 0)
    {
        _unlock = u; _moreCount = null;
        Start(delayMs);
    }

    public void RevealMore(int count, int delayMs = 0)
    {
        _moreCount = count; _unlock = null;
        Start(delayMs);
    }

    /// <summary>Freeze at the settled frame (fully revealed, face-up) without animating — for headless snapshots.</summary>
    public void SetStatic(AchievementUnlock u) { _unlock = u; _moreCount = null; _progress = 1; InvalidateVisual(); }
    public void SetStaticMore(int count) { _moreCount = count; _unlock = null; _progress = 1; InvalidateVisual(); }

    private void Start(int delayMs)
    {
        _delayMs = delayMs;
        _ticks = 0;
        _progress = 0;
        _timer ??= CreateTimer();
        if (!_timer.IsEnabled) _timer.Start();
        InvalidateVisual();
    }

    private DispatcherTimer CreateTimer()
    {
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(TickMs) };
        t.Tick += (_, _) => Step();
        return t;
    }

    private void Step()
    {
        _ticks++;
        double elapsed = _ticks * (double)TickMs - _delayMs;   // negative while still in the start delay
        if (elapsed >= 0)
        {
            _progress = Math.Min(1, elapsed / DurationMs);
            if (_progress >= 1) _timer?.Stop();
        }
        InvalidateVisual();
    }

    private static double EaseOutCubic(double t) => 1 - Math.Pow(1 - t, 3);

    // Overshoots past 1 before settling back — the little "pop" as the card lands.
    private static double EaseOutBack(double t)
    {
        const double c1 = 1.70158, c3 = c1 + 1;
        return 1 + c3 * Math.Pow(t - 1, 3) + c1 * Math.Pow(t - 1, 2);
    }

    public override void Render(DrawingContext ctx)
    {
        if (_unlock is null && _moreCount is null) return;

        double p = _progress;
        double cx = Bounds.Width / 2, cy = Bounds.Height / 2;

        // The flip winds down from a few half-turns to face-up; the zoom grows it out of the screen with a
        // small overshoot. Horizontal scale = cos(flip) so the card thins to an edge and shows its back.
        double flip = (1 - EaseOutCubic(p)) * (Spins * Math.PI);
        double zoom = 0.4 + 0.6 * EaseOutBack(p);
        double cos = Math.Cos(flip);
        double opacity = Math.Min(1, p / 0.22);
        var card = new Rect(-_cardW / 2, -_cardH / 2, _cardW, _cardH);

        using (ctx.PushOpacity(opacity))
        {
            // Soft shadow — zoomed but not flipped, so it stays put under the card as it spins.
            using (ctx.PushTransform(Matrix.CreateScale(zoom, zoom) * Matrix.CreateTranslation(cx, cy + 14 * _scale * zoom)))
                OverlayDraw.Panel(ctx, card, ShadowBrush, null, 22 * _scale);

            Color bg, ink;
            if (_moreCount is not null) { bg = MoreBg; ink = MoreInk; }
            else { (bg, ink, _) = TierFor(_unlock!.Tier); }

            using (ctx.PushTransform(Matrix.CreateScale(zoom * cos, zoom) * Matrix.CreateTranslation(cx, cy)))
            {
                if (cos >= 0) DrawFront(ctx, card, bg, ink);
                else DrawBack(ctx, card, bg, ink);
            }
        }
    }

    private void DrawFront(DrawingContext ctx, Rect r, Color bg, Color ink)
    {
        var inkBrush = new SolidColorBrush(ink);
        OverlayDraw.Panel(ctx, r, new SolidColorBrush(bg), new Pen(inkBrush, 2 * _scale), 22 * _scale);
        if (_moreCount is { } m) DrawMoreFace(ctx, m, inkBrush);
        else DrawBadgeFace(ctx, r, _unlock!, inkBrush);
    }

    private void DrawBadgeFace(DrawingContext ctx, Rect r, AchievementUnlock u, IBrush inkBrush)
    {
        var (_, _, tier) = TierFor(u.Tier);
        double cyc = r.Y + 26 * _scale;

        // Tier ribbon ("GOLD"), letter-spaced and tinted to the tier.
        var tierText = OverlayDraw.Text(string.Join(" ", tier.ToCharArray()), 12 * _scale, inkBrush, FontWeight.SemiBold);
        ctx.DrawText(tierText, new Point(-tierText.Width / 2, cyc));
        cyc += tierText.Height + 20 * _scale;

        // Big colour emoji, centred.
        var emoji = new FormattedText(StripVariation(u.Emoji), CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight, EmojiFace, 84 * _scale, inkBrush);
        ctx.DrawText(emoji, new Point(-emoji.Width / 2, cyc));
        cyc += emoji.Height + 22 * _scale;

        // Name, tinted to the tier, wrapped/centred within the card.
        var name = new FormattedText(u.Name, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            OverlayDraw.Face(FontWeight.Bold), 22 * _scale, inkBrush)
        {
            MaxTextWidth = r.Width - 36 * _scale, TextAlignment = TextAlignment.Center,
        };
        ctx.DrawText(name, new Point(r.X + 18 * _scale, cyc));
        cyc += name.Height + 12 * _scale;

        // Detail line ("Tokens · Lvl 3" or the one-off criteria), muted and centred.
        var detail = new FormattedText(u.Detail, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            OverlayDraw.Face(), 13 * _scale, new SolidColorBrush(Palette.Muted))
        {
            MaxTextWidth = r.Width - 36 * _scale, MaxTextHeight = 44 * _scale,
            TextAlignment = TextAlignment.Center, Trimming = TextTrimming.WordEllipsis,
        };
        ctx.DrawText(detail, new Point(r.X + 18 * _scale, cyc));
    }

    // The "+N more" card: a big "+N" over a "more" label, centred as a group.
    private void DrawMoreFace(DrawingContext ctx, int count, IBrush inkBrush)
    {
        var plus = OverlayDraw.Text($"+{count}", 56 * _scale, inkBrush, FontWeight.Bold);
        var label = OverlayDraw.Text("more", 16 * _scale, new SolidColorBrush(Palette.Muted));
        double gap = 6 * _scale;
        double top = -(plus.Height + gap + label.Height) / 2;
        ctx.DrawText(plus, new Point(-plus.Width / 2, top));
        ctx.DrawText(label, new Point(-label.Width / 2, top + plus.Height + gap));
    }

    // The card's reverse: a plain panel with a faint star, glimpsed only mid-flip.
    private void DrawBack(DrawingContext ctx, Rect r, Color bg, Color ink)
    {
        OverlayDraw.Panel(ctx, r, new SolidColorBrush(bg), new Pen(new SolidColorBrush(ink), 2 * _scale), 22 * _scale);
        var star = new FormattedText("★", CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            OverlayDraw.Face(FontWeight.Bold), 96 * _scale, new SolidColorBrush(Color.FromArgb(70, ink.R, ink.G, ink.B)));
        ctx.DrawText(star, new Point(-star.Width / 2, -star.Height / 2));
    }

    private static string StripVariation(string s) =>
        string.Concat(s.Where(ch => ch != '️' && ch != '︎'));

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _timer?.Stop();
        base.OnDetachedFromVisualTree(e);
    }
}
