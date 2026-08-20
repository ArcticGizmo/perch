using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using Perch.Avalonia.Theming;
using Perch.Platform;

namespace Perch.Avalonia.Windows;

/// <summary>
/// The "big reactions" layer: a full-screen, transparent, topmost, no-activate window over which the
/// reactions friends leave on <em>your</em> status float up as large emoji bubbles that wobble side to side
/// and can be popped with a click. Driven by <see cref="AppSettings.ShowLargeReactions"/>; the App spawns a
/// bubble per newly-seen reaction (see <c>SocialFeedMonitorHost.NotifyReactionsToMe</c>).
///
/// <para>It's a no-activate tool window (<see cref="IWindowChrome.MakeToolWindowNoActivate"/>), so a reaction
/// arriving while you're typing never steals focus. It is deliberately <b>not</b> click-through: it has to
/// catch the click that pops a bubble. To keep it from swallowing desktop clicks for longer than the effect
/// lasts, a click on empty space dismisses the whole layer at once, and the layer closes itself the moment
/// the last bubble is gone. A small "Turn off big reactions" pill sits at the bottom the whole time it's up
/// (the user's explicit ask), flipping the setting off via <see cref="TurnOff"/>.</para>
/// </summary>
internal sealed class ReactionBubbleWindow : Window
{
    private readonly ReactionBubbleLayer _layer = new();

    /// <summary>Raised (on the UI thread) when the user clicks the "Turn off big reactions" pill, so the App
    /// can persist the opt-out. The window closes itself right after.</summary>
    public event Action? TurnOff;

    public ReactionBubbleWindow()
    {
        WindowDecorations = WindowDecorations.None;
        Background = Brushes.Transparent;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        Topmost = true;
        ShowInTaskbar = false;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.Manual;

        var pill = new Button
        {
            Content = "Turn off big reactions",
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 40),
            Padding = new Thickness(14, 7),
            Background = new SolidColorBrush(Color.FromArgb(200, Palette.ButtonBg.R, Palette.ButtonBg.G, Palette.ButtonBg.B)),
            Foreground = new SolidColorBrush(Palette.Fg),
            BorderBrush = new SolidColorBrush(Palette.Border), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(16), FontSize = 12,
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        pill.Click += (_, _) => { TurnOff?.Invoke(); Close(); };

        _layer.Dismissed += Close;          // a click on empty space closes the whole layer
        _layer.AllGone += Close;            // nothing left to show

        // The layer fills the window and catches clicks (its background is a transparent, hit-testable brush);
        // the pill sits above it at the bottom.
        Content = new Grid { Children = { _layer, pill } };
    }

    /// <summary>Sizes and shows the layer across <paramref name="screen"/> (the one holding the overlay).</summary>
    public void Present(Screen screen)
    {
        var b = screen.Bounds;              // physical pixels
        double scale = screen.Scaling;
        Position = b.Position;
        Width = b.Width / scale;            // cover the screen in DIPs
        Height = b.Height / scale;
        if (!IsVisible) Show();
    }

    /// <summary>Float a new reaction bubble with <paramref name="emoji"/> up the screen.</summary>
    public void Spawn(string emoji) => _layer.Add(emoji);

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        // No-activate so a reaction never yanks focus from whatever you're typing in.
        if (TryGetPlatformHandle() is { } h)
            PlatformServices.WindowChrome.MakeToolWindowNoActivate(h.Handle);
    }
}

/// <summary>
/// The owner-drawn bubble field: emoji rise from the bottom edge to the top with a gentle sine wobble,
/// fading as they near the top, and pop (a short expanding burst) when clicked. A single
/// <see cref="DispatcherTimer"/> animates while any bubble is alive and stops when the field empties. Clicking
/// empty space raises <see cref="Dismissed"/>; running dry raises <see cref="AllGone"/>.
/// </summary>
internal sealed class ReactionBubbleLayer : Control
{
    private const int TickMs = 16;
    private const double RiseMs = 5200;     // bottom → top travel time
    private const double PopMs = 260;       // burst duration once popped
    private const int MaxBubbles = 16;      // don't let a reaction storm swarm the screen

    private static readonly Typeface EmojiFace =
        new(new FontFamily("Segoe UI Emoji, Apple Color Emoji, Noto Color Emoji"));

    private sealed class Bubble
    {
        public required string Emoji;
        public double BaseX;        // horizontal centre as a fraction of width (0..1)
        public double Amp;          // wobble amplitude in DIPs
        public double Freq;         // wobble angular frequency
        public double Phase;        // wobble phase offset
        public double Size;         // emoji font size
        public long Born;           // TickCount64 at spawn
        public long? Popped;        // TickCount64 when popped, else null
    }

    private readonly List<Bubble> _bubbles = new();
    private readonly Random _rng = new();
    private DispatcherTimer? _timer;

    /// <summary>Raised when a click lands on empty space (not a bubble) — the App closes the window.</summary>
    public event Action? Dismissed;

    /// <summary>Raised when the last bubble has floated off or popped — the App closes the window.</summary>
    public event Action? AllGone;

    public void Add(string emoji)
    {
        if (string.IsNullOrWhiteSpace(emoji)) return;
        if (_bubbles.Count >= MaxBubbles) return;   // ignore beyond the cap rather than swarm

        _bubbles.Add(new Bubble
        {
            Emoji = emoji,
            BaseX = 0.12 + _rng.NextDouble() * 0.76,     // keep clear of the very edges
            Amp = 22 + _rng.NextDouble() * 34,
            Freq = 1.6 + _rng.NextDouble() * 1.4,
            Phase = _rng.NextDouble() * Math.Tau,
            Size = 48 + _rng.NextDouble() * 28,
            Born = Environment.TickCount64,
        });
        EnsureTimer();
        InvalidateVisual();
    }

    private void EnsureTimer()
    {
        _timer ??= CreateTimer();
        if (!_timer.IsEnabled) _timer.Start();
    }

    private DispatcherTimer CreateTimer()
    {
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(TickMs) };
        t.Tick += (_, _) =>
        {
            long now = Environment.TickCount64;
            // Retire bubbles that reached the top, or whose pop burst has finished.
            _bubbles.RemoveAll(b =>
                (b.Popped is { } p && now - p >= PopMs) || (b.Popped is null && now - b.Born >= RiseMs));
            InvalidateVisual();
            if (_bubbles.Count == 0)
            {
                _timer!.Stop();
                AllGone?.Invoke();
            }
        };
        return t;
    }

    // The current centre + drawn size of a bubble, so render and hit-testing agree.
    private (double cx, double cy, double size, double opacity) Geometry(Bubble b, long now)
    {
        double t = Math.Clamp((now - b.Born) / RiseMs, 0, 1);
        double cy = Bounds.Height + b.Size - t * (Bounds.Height + b.Size * 2);   // bottom → top
        double cx = b.BaseX * Bounds.Width + b.Amp * Math.Sin(b.Freq * t * Math.Tau + b.Phase);
        double opacity = 1;
        if (t > 0.15) opacity = Math.Clamp(1 - (t - 0.15) / 0.85, 0.15, 1);      // fade as it climbs
        return (cx, cy, b.Size, opacity);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var p = e.GetPosition(this);
        long now = Environment.TickCount64;
        // Topmost (last-drawn) bubble first, so a click pops what you see on top.
        for (int i = _bubbles.Count - 1; i >= 0; i--)
        {
            var b = _bubbles[i];
            if (b.Popped is not null) continue;
            var (cx, cy, size, _) = Geometry(b, now);
            double r = size * 0.62;                       // a forgiving hit radius around the glyph
            if ((p.X - cx) * (p.X - cx) + (p.Y - cy) * (p.Y - cy) <= r * r)
            {
                b.Popped = now;
                e.Handled = true;
                InvalidateVisual();
                return;
            }
        }
        // Empty space: dismiss the whole layer so we don't keep eating desktop clicks.
        Dismissed?.Invoke();
    }

    public override void Render(DrawingContext ctx)
    {
        base.Render(ctx);
        // A transparent fill over the whole surface makes every pixel hit-testable (so a click on empty space
        // reaches OnPointerPressed and dismisses the layer) while staying visually invisible.
        ctx.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));

        long now = Environment.TickCount64;
        foreach (var b in _bubbles)
        {
            var (cx, cy, size, opacity) = Geometry(b, now);

            if (b.Popped is { } popTick)
            {
                // Pop: a quick expanding, fading ring in the accent colour + the emoji scaling up and away.
                double pt = Math.Clamp((now - popTick) / PopMs, 0, 1);
                double ringR = size * (0.5 + pt * 0.9);
                byte ringA = (byte)(160 * (1 - pt));
                var ringPen = new Pen(new SolidColorBrush(Color.FromArgb(ringA, Palette.Accent.R, Palette.Accent.G, Palette.Accent.B)), 3);
                ctx.DrawEllipse(null, ringPen, new Point(cx, cy), ringR, ringR);
                DrawEmoji(ctx, b.Emoji, cx, cy, size * (1 + pt * 0.5), (1 - pt) * opacity);
                continue;
            }

            // The bubble: a soft translucent disc behind the emoji, then the glyph.
            var disc = new SolidColorBrush(Color.FromArgb((byte)(38 * opacity), 255, 255, 255));
            ctx.DrawEllipse(disc, null, new Point(cx, cy), size * 0.72, size * 0.72);
            DrawEmoji(ctx, b.Emoji, cx, cy, size, opacity);
        }
    }

    private static void DrawEmoji(DrawingContext ctx, string emoji, double cx, double cy, double size, double opacity)
    {
        if (opacity <= 0) return;
        var ft = new FormattedText(StripVariation(emoji), CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            EmojiFace, size, Brushes.White);
        using (ctx.PushOpacity(Math.Clamp(opacity, 0, 1)))
            ctx.DrawText(ft, new Point(cx - ft.Width / 2, cy - ft.Height / 2));
    }

    // Drop the variation selectors so the color-emoji glyph is picked (matches AchievementCard).
    private static string StripVariation(string s) => string.Concat(s.Where(ch => ch != '️' && ch != '︎'));

    /// <summary>A settled, non-animating frame for the headless render harness: a handful of bubbles caught
    /// mid-rise plus one mid-pop, over a dim backdrop so they read in the snapshot. The timer never starts.</summary>
    internal static Control CreateForRender()
    {
        var layer = new ReactionBubbleLayer { Width = 360, Height = 520 };
        long now = Environment.TickCount64;
        void Seed(string emoji, double baseX, double size, long ageMs, bool popped = false)
        {
            var b = new Bubble { Emoji = emoji, BaseX = baseX, Amp = 20, Freq = 2, Phase = 0.5, Size = size, Born = now - ageMs };
            if (popped) b.Popped = now - 120;
            layer._bubbles.Add(b);
        }
        Seed("🎉", 0.28, 60, 1400);
        Seed("❤️", 0.60, 52, 2600);
        Seed("😂", 0.80, 46, 3600);
        Seed("🔥", 0.45, 64, 900, popped: true);
        return new Grid { Width = 360, Height = 520, Background = new SolidColorBrush(Color.FromRgb(24, 24, 32)), Children = { layer } };
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _timer?.Stop();
        base.OnDetachedFromVisualTree(e);
    }
}
