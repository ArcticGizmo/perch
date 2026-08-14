using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Perch.Avalonia.Rendering;
using Perch.Avalonia.Theming;

namespace Perch.Avalonia.Windows;

/// <summary>
/// A second completely-secret toy — "Perch Crossing", a bird-themed Frogger clone — reached from the same
/// arcade chooser as <see cref="SpaceInvadersWindow"/> (see <c>ArcadeMenuWindow</c>). Hop a little bird up
/// across a busy road, then ride drifting logs over a river, and land it safely in the perches at the top.
/// Pure owner-drawn Avalonia over the shared <see cref="OverlayDraw"/> / <see cref="Palette"/> vocabulary,
/// no platform APIs and no persistence — a toy, like its sibling.
///
/// The window is a thin shell: it owns the chrome and forwards key presses to the <see cref="FroggerField"/>
/// control, which holds all the game state and runs the loop off a <see cref="DispatcherTimer"/> (the same
/// tick-then-<c>InvalidateVisual</c> pattern as <c>InvadersField</c>).
/// </summary>
public sealed class FroggerWindow : Window
{
    private readonly FroggerField _field = new();

    public FroggerWindow()
    {
        Title = "Perch Crossing";
        CanResize = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Palette.OverlaySurfaceBrush;
        Content = _field;
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        _field.Focus();     // so the field takes keys directly; the window forwards too, as a backstop
        _field.Begin();
    }

    protected override void OnClosed(EventArgs e)
    {
        _field.Stop();
        base.OnClosed(e);
    }

    // Keys are handled at the window so input works regardless of which child holds focus. Esc closes;
    // everything else is the game's.
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { Close(); e.Handled = true; return; }
        if (_field.HandleKey(e.Key)) e.Handled = true;
        base.OnKeyDown(e);
    }
}

/// <summary>
/// The game itself: an owner-drawn, fixed-size playfield running a ~60fps <see cref="DispatcherTimer"/>
/// loop. Lanes of cars and drifting logs (and the occasional crocodile) scroll horizontally; the bird hops
/// one cell at a time (discrete Frogger hops) and, while it sits on a river lane, drifts with whatever
/// carries it — miss it and it drowns, and landing on a crocodile's jaws is just as fatal. Fill all the
/// perches at the top to advance a level; everything just gets faster.
/// </summary>
internal sealed class FroggerField : Control
{
    // ── Playfield geometry (DIP). The control is fixed to this size, so layout can use the constants. ──
    private const double FieldW = 520, FieldH = 662;
    private const double TopBar = 38;                     // score / level / lives strip
    private const int Lanes = 13;                         // home + 5 river + median + 5 road + start
    private const double LaneH = (FieldH - TopBar) / Lanes;

    private const double HopX = 44;                       // horizontal hop distance
    private const double BirdW = 34, BirdH = 34;
    private const int Nests = 5;
    private const double NestCatch = 28;                  // half-width of a perch's catch zone

    private const int TickMs = 16;
    private const int StartLives = 3;
    private const int TimeTicks = 110 * 60 / 16;          // ~110s of ticks per crossing

    private enum LaneKind { Safe, Road, River, Home }

    // A single scrolling lane of identical items (cars or logs). Item positions are derived from a single
    // scrolling <see cref="Offset"/> so drawing and collision can never disagree; the modulo domain is
    // shifted by one item width so wrap-arounds always happen off-screen on both edges.
    private sealed class Lane
    {
        public LaneKind Kind;
        public double Speed;        // px/tick, sign = direction
        public double Offset;       // accumulated scroll
        public double Spacing;      // gap between item starts
        public double ItemW;
        public int Count;
        public IBrush Brush = Palette.MutedBrush;
        public bool[]? Croc;        // river only: which items are crocodiles (index-aligned with ItemXs())

        public double[] ItemXs()
        {
            double p = Count * Spacing;
            var xs = new double[Count];
            for (int i = 0; i < Count; i++)
                xs[i] = Mod(Offset + i * Spacing, p) - ItemW;
            return xs;
        }
    }

    // Base config per moving lane (before the per-level speed multiplier). Directions alternate so the
    // board reads as traffic; widths/spacings vary so no two lanes feel the same.
    private readonly record struct LaneSpec(double Speed, double ItemW, double Spacing);

    private static readonly LaneSpec[] RoadSpecs =
    {
        new(1.3,  60, 220),
        new(-1.7, 48, 200),
        new(2.0,  76, 260),
        new(-1.4, 54, 210),
        new(1.8,  66, 240),
    };
    private static readonly LaneSpec[] RiverSpecs =
    {
        new(1.1, 130, 230),
        new(-1.5, 110, 210),
        new(1.9, 160, 270),
        new(-1.2, 116, 220),
        new(1.5, 140, 250),
    };

    private enum Phase { Title, Playing, Lost }

    // ── State ──
    private Phase _phase = Phase.Title;
    private readonly Lane?[] _lanes = new Lane?[Lanes];   // null on safe/home lanes
    private readonly bool[] _nests = new bool[Nests];
    private int _birdLane = Lanes - 1;                    // start row (bottom)
    private double _birdX = FieldW / 2;
    private int _facing;                                  // 0 up · 1 right · 2 down · 3 left
    private int _score, _lives, _level;
    private int _timeLeft;
    private int _hopFlash;                                // little squash after a hop
    private int _deathFlash;                              // red flash on the bird after a hit
    private string? _banner;                              // transient centred message (level / splat)
    private int _bannerTicks;
    private double _pulse;                                // drives the "press space" prompt shimmer
    private DispatcherTimer? _timer;
    private readonly Random _rng = new();

    // The bird, from above: beak up, wings spread, forked tail. Square so it rotates cleanly to face a hop.
    private static readonly string[] Bird =
    {
        "00000100000",
        "00001110000",
        "00011111000",
        "01011111010",
        "11111111111",
        "11111111111",
        "01111111110",
        "00111111100",
        "00111111100",
        "00110101100",
        "00100000100",
    };

    private static bool IsRoad(int lane) => lane is >= 7 and <= 11;
    private static bool IsRiver(int lane) => lane is >= 1 and <= 5;
    private static bool IsHome(int lane) => lane == 0;
    private static double LaneTop(int lane) => TopBar + lane * LaneH;
    private static double LaneMidY(int lane) => LaneTop(lane) + LaneH / 2;
    private static double NestCenterX(int k) => FieldW * (k + 0.5) / Nests;
    private static double Mod(double a, double m) => ((a % m) + m) % m;

    public FroggerField()
    {
        Width = FieldW;
        Height = FieldH;
        Focusable = true;
        BuildLanes(1);      // seed a live-looking board behind the title card
    }

    protected override Size MeasureOverride(Size availableSize) => new(FieldW, FieldH);

    /// <summary>Freezes the field at a representative mid-play frame for headless snapshots (timers don't
    /// tick under the render harness, so the scene is posed by hand).</summary>
    internal void SnapshotPlaying()
    {
        StartGame();
        _nests[0] = _nests[3] = true;                 // a couple already home
        _score = 340;
        _birdLane = 3;                                 // riding a river log
        _birdX = FieldW / 2;
        _facing = 0;
        // Slide the carrying lane so a crocodile sits exactly under the bird for the pose (riding its back).
        if (_lanes[3] is Lane l)
        {
            l.Offset = _birdX - l.ItemW / 2 + l.ItemW;
            if (l.Croc is { Length: > 0 } croc) croc[0] = true;
        }
        InvalidateVisual();
    }

    // ── Lifecycle ──
    public void Begin()
    {
        _timer ??= CreateTimer();
        if (!_timer.IsEnabled) _timer.Start();
    }

    public void Stop() => _timer?.Stop();

    private DispatcherTimer CreateTimer()
    {
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(TickMs) };
        t.Tick += (_, _) => Tick();
        return t;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _timer?.Stop();
        base.OnDetachedFromVisualTree(e);
    }

    // ── Input ── Discrete hops on key-down. Returns true when the key was the game's.
    public bool HandleKey(Key key)
    {
        switch (key)
        {
            case Key.Left or Key.A: Hop(-1, 0, 3); return true;
            case Key.Right or Key.D: Hop(1, 0, 1); return true;
            case Key.Up or Key.W: Hop(0, -1, 0); return true;
            case Key.Down or Key.S: Hop(0, 1, 2); return true;
            case Key.Space or Key.Enter:
                if (_phase != Phase.Playing) StartGame();
                return true;
        }
        return false;
    }

    private void Hop(int dx, int dy, int facing)
    {
        if (_phase != Phase.Playing) { if (dx != 0 || dy != 0) StartGame(); return; }

        _facing = facing;
        if (dx != 0)
            _birdX = Math.Clamp(_birdX + dx * HopX, BirdW / 2, FieldW - BirdW / 2);

        if (dy != 0)
        {
            int next = Math.Clamp(_birdLane + dy, 0, Lanes - 1);
            if (next != _birdLane)
            {
                _birdLane = next;
                _hopFlash = 6;
                if (IsHome(_birdLane)) ReachHome();
            }
        }
    }

    private void StartGame()
    {
        _score = 0;
        _lives = StartLives;
        _level = 1;
        Array.Clear(_nests);
        BuildLanes(1);
        ResetBird();
        _banner = null;
        _bannerTicks = 0;
        _phase = Phase.Playing;
    }

    private void ResetBird()
    {
        _birdLane = Lanes - 1;
        _birdX = FieldW / 2;
        _facing = 0;
        _timeLeft = TimeTicks;
    }

    private void BuildLanes(int level)
    {
        double mult = 1 + 0.16 * (level - 1);
        for (int i = 0; i < Lanes; i++) _lanes[i] = null;

        for (int r = 0; r < RoadSpecs.Length; r++)
            _lanes[7 + r] = MakeLane(LaneKind.Road, RoadSpecs[r], mult, CarBrush(r));
        for (int r = 0; r < RiverSpecs.Length; r++)
            _lanes[1 + r] = MakeLane(LaneKind.River, RiverSpecs[r], mult, Palette.BurnBrush);
    }

    private Lane MakeLane(LaneKind kind, LaneSpec spec, double mult, IBrush brush)
    {
        int count = (int)Math.Ceiling((FieldW + 2 * spec.ItemW) / spec.Spacing) + 1;
        var lane = new Lane
        {
            Kind = kind,
            Speed = spec.Speed * mult,
            ItemW = spec.ItemW,
            Spacing = spec.Spacing,
            Count = count,
            Offset = _rng.NextDouble() * count * spec.Spacing,   // random phase so lanes don't line up
            Brush = brush,
        };

        // Sprinkle crocodiles through the river lanes: you can ride their backs, but their jaws (the head at
        // the leading edge) will chomp the bird. Never two crocs adjacent, so a safe log is always in reach.
        if (kind == LaneKind.River)
        {
            var croc = new bool[count];
            for (int i = 0; i < count; i++)
                croc[i] = !(i > 0 && croc[i - 1]) && _rng.NextDouble() < 0.4;
            lane.Croc = croc;
        }
        return lane;
    }

    private static IBrush CarBrush(int roadRow) => (roadRow % 4) switch
    {
        0 => Palette.ErrorBrush,
        1 => Palette.AttentionBrush,
        2 => Palette.AwaitingBrush,
        _ => Palette.WarnBrush,
    };

    // ── The loop ──
    private void Tick()
    {
        _pulse += 0.12;
        if (_hopFlash > 0) _hopFlash--;
        if (_deathFlash > 0) _deathFlash--;
        if (_bannerTicks > 0 && --_bannerTicks == 0) _banner = null;

        foreach (var l in _lanes) if (l is not null) l.Offset += l.Speed;

        if (_phase == Phase.Playing) UpdatePlaying();
        InvalidateVisual();
    }

    private void UpdatePlaying()
    {
        if (--_timeLeft <= 0) { LoseLife(); return; }

        if (IsRiver(_birdLane))
        {
            // Ride a log (or a crocodile's back); land on open water — or a croc's jaws — and it's over.
            switch (RiverRide(out double speed))
            {
                case RideResult.Ride:
                    _birdX += speed;
                    if (_birdX < BirdW / 2 || _birdX > FieldW - BirdW / 2) { LoseLife(); return; }
                    break;
                case RideResult.Chomp:
                case RideResult.Drown:
                    LoseLife();
                    return;
            }
        }
        else if (IsRoad(_birdLane) && HitByCar())
        {
            LoseLife();
        }
    }

    private enum RideResult { Drown, Ride, Chomp }

    private static double CrocHeadW(Lane l) => Math.Min(l.ItemW * 0.30, 42);

    // What the bird is standing on in its current river lane: open water (drown), a rideable back
    // (ride, carrying the drift speed out), or a crocodile's jaws (chomp).
    private RideResult RiverRide(out double speed)
    {
        speed = 0;
        if (_lanes[_birdLane] is not Lane l) return RideResult.Drown;
        double[] xs = l.ItemXs();
        for (int i = 0; i < xs.Length; i++)
        {
            double x = xs[i];
            if (_birdX < x || _birdX > x + l.ItemW) continue;
            speed = l.Speed;
            if (l.Croc is { } croc && croc[i] && InCrocHead(x, l)) return RideResult.Chomp;
            return RideResult.Ride;
        }
        return RideResult.Drown;
    }

    // The jaws sit at the leading edge — right end when the croc swims right, left end when it swims left.
    private bool InCrocHead(double x, Lane l)
    {
        double hw = CrocHeadW(l);
        return l.Speed >= 0 ? _birdX >= x + l.ItemW - hw : _birdX <= x + hw;
    }

    private bool HitByCar()
    {
        if (_lanes[_birdLane] is not Lane l) return false;
        double carH = LaneH * 0.62;
        double top = LaneTop(_birdLane) + (LaneH - carH) / 2;
        var bird = BirdRect();
        foreach (double x in l.ItemXs())
            if (bird.Intersects(new Rect(x, top, l.ItemW, carH))) return true;
        return false;
    }

    private void ReachHome()
    {
        for (int k = 0; k < Nests; k++)
        {
            if (Math.Abs(_birdX - NestCenterX(k)) <= NestCatch)
            {
                if (_nests[k]) break;                 // already occupied → collision
                _nests[k] = true;
                _score += 50 + _timeLeft / 20;        // faster crossings score more
                if (Array.TrueForAll(_nests, n => n)) LevelUp();
                else { Banner("SAFE!", 60); ResetBird(); }
                return;
            }
        }
        LoseLife();                                    // landed on a hedge or an occupied perch
    }

    private void LevelUp()
    {
        _score += 200;
        _level++;
        Array.Clear(_nests);
        BuildLanes(_level);
        ResetBird();
        Banner($"LEVEL {_level}", 90);
    }

    private void LoseLife()
    {
        _deathFlash = 16;
        if (--_lives <= 0) { _phase = Phase.Lost; return; }
        Banner("SPLAT!", 50);
        ResetBird();
    }

    private void Banner(string text, int ticks) { _banner = text; _bannerTicks = ticks; }

    private Rect BirdRect() =>
        new(_birdX - BirdW / 2, LaneMidY(_birdLane) - BirdH / 2, BirdW, BirdH);

    // ── Rendering ──
    public override void Render(DrawingContext ctx)
    {
        double w = Bounds.Width, h = Bounds.Height;
        ctx.FillRectangle(Palette.OverlaySurfaceBrush, new Rect(0, 0, w, h));

        DrawLanes(ctx);
        DrawHome(ctx);

        if (_phase != Phase.Lost || (_deathFlash / 3) % 2 == 0)
            DrawBird(ctx);

        DrawTopBar(ctx, w);
        DrawTimeBar(ctx);

        if (_banner is not null && _phase == Phase.Playing)
            DrawBanner(ctx, w);

        switch (_phase)
        {
            case Phase.Title:
                DrawOverlayCard(ctx, w, h, "PERCH CROSSING",
                    "Ride logs  ·  mind the crocs", "Press Space to start");
                break;
            case Phase.Lost:
                DrawOverlayCard(ctx, w, h, "GAME OVER",
                    $"Score {_score}  ·  Level {_level}", "Space to play again");
                break;
        }
    }

    private void DrawLanes(DrawingContext ctx)
    {
        for (int lane = 0; lane < Lanes; lane++)
        {
            var rect = new Rect(0, LaneTop(lane), FieldW, LaneH);

            // Lane bed.
            if (IsRiver(lane))
                using (ctx.PushOpacity(0.30)) ctx.FillRectangle(Palette.TealBrush, rect);
            else if (IsRoad(lane))
                ctx.FillRectangle(Palette.SurfaceSunkenBrush, rect);
            else if (!IsHome(lane))     // the two grassy safe strips (median + start)
                using (ctx.PushOpacity(0.22)) ctx.FillRectangle(Palette.RunningBrush, rect);

            // Lane cargo.
            if (_lanes[lane] is Lane l)
            {
                if (l.Kind == LaneKind.Road)
                {
                    double carH = LaneH * 0.62, top = LaneTop(lane) + (LaneH - carH) / 2;
                    foreach (double x in l.ItemXs())
                        OverlayDraw.Panel(ctx, new Rect(x, top, l.ItemW, carH), l.Brush, null, 5);
                }
                else // river: logs and the occasional crocodile
                {
                    double itemH = LaneH * 0.58, top = LaneTop(lane) + (LaneH - itemH) / 2;
                    double[] xs = l.ItemXs();
                    for (int i = 0; i < xs.Length; i++)
                    {
                        var box = new Rect(xs[i], top, l.ItemW, itemH);
                        if (l.Croc is { } croc && croc[i])
                            DrawCroc(ctx, box, l.Speed >= 0, CrocHeadW(l));
                        else
                            OverlayDraw.Panel(ctx, box, l.Brush, null, itemH / 2);
                    }
                }
            }
        }
    }

    private void DrawHome(DrawingContext ctx)
    {
        var rect = new Rect(0, LaneTop(0), FieldW, LaneH);
        using (ctx.PushOpacity(0.35)) ctx.FillRectangle(Palette.RunningBrush, rect);

        double slotW = 40, slotH = LaneH * 0.7, midY = LaneMidY(0);
        for (int k = 0; k < Nests; k++)
        {
            double cx = NestCenterX(k);
            var slot = new Rect(cx - slotW / 2, midY - slotH / 2, slotW, slotH);
            if (_nests[k])
            {
                OverlayDraw.Panel(ctx, slot, Palette.RunningBrush, null, 8);
                DrawBirdSprite(ctx, new Rect(cx - 11, midY - 11, 22, 22), Palette.OnAccentBrush, 0);
            }
            else
            {
                OverlayDraw.Panel(ctx, slot, null, new Pen(Palette.AccentBrush, 1.5), 8);
            }
        }
    }

    private void DrawBird(DrawingContext ctx)
    {
        double squash = _hopFlash > 0 ? 3 : 0;   // a tiny landing squash
        var box = new Rect(_birdX - BirdW / 2, LaneMidY(_birdLane) - BirdH / 2 + squash / 2, BirdW, BirdH - squash);
        var brush = _deathFlash > 0 && (_deathFlash / 3) % 2 == 0 ? Palette.ErrorBrush : Palette.AccentBrush;
        DrawBirdSprite(ctx, box, brush, _facing);
    }

    // Draws the square bird bitmap rotated to face its last hop (0 up · 1 right · 2 down · 3 left).
    private static void DrawBirdSprite(DrawingContext ctx, Rect box, IBrush brush, int facing)
    {
        int n = Bird.Length;    // square
        double cw = box.Width / n, ch = box.Height / n;
        for (int r = 0; r < n; r++)
            for (int c = 0; c < n; c++)
                if (Bird[r][c] == '1')
                {
                    (int dr, int dc) = facing switch
                    {
                        1 => (c, n - 1 - r),
                        2 => (n - 1 - r, n - 1 - c),
                        3 => (n - 1 - c, r),
                        _ => (r, c),
                    };
                    ctx.FillRectangle(brush, new Rect(box.X + dc * cw, box.Y + dr * ch, cw + 0.5, ch + 0.5));
                }
    }

    // A top-down crocodile: a green back (safe to ride) with two eyes and a set of open red jaws at the
    // leading edge — the head the bird must never land on. <paramref name="headW"/> matches the deadly zone
    // used by <see cref="InCrocHead"/> so the picture and the collision agree.
    private static void DrawCroc(DrawingContext ctx, Rect box, bool headRight, double headW)
    {
        double h = box.Height;

        OverlayDraw.Panel(ctx, box, Palette.RunningBrush, null, h / 2);   // the rideable green back

        // The head is the deadly zone. It's drawn solid red across its whole width (headW) so the picture
        // and the hitbox agree exactly — see InCrocHead, which uses the same CrocHeadW. Land on green only.
        var head = headRight
            ? new Rect(box.Right - headW, box.Y, headW, h)
            : new Rect(box.X, box.Y, headW, h);
        OverlayDraw.Panel(ctx, head, Palette.ErrorBrush, null, h / 2);

        // Teeth: a few white triangles along the mouth line (the head/back boundary), pointing to the tip.
        double mouthX = headRight ? head.X : head.Right;
        double dir = headRight ? 1 : -1;
        double toothLen = Math.Min(6, headW * 0.5);
        for (int i = 0; i < 3; i++)
        {
            double ty = box.Y + h * (i + 0.5) / 3;
            FillTriangle(ctx, Palette.FgBrush,
                new Point(mouthX, ty - h * 0.12), new Point(mouthX, ty + h * 0.12),
                new Point(mouthX + dir * toothLen, ty));
        }

        // Two eyes on top of the head.
        double eyeR = Math.Max(1.5, h * 0.11);
        double eyeX = headRight ? box.Right - headW * 0.45 : box.X + headW * 0.45;
        ctx.DrawEllipse(Palette.FgBrush, null, new Point(eyeX, box.Y + h * 0.27), eyeR, eyeR);
        ctx.DrawEllipse(Palette.FgBrush, null, new Point(eyeX, box.Y + h * 0.73), eyeR, eyeR);
    }

    private static void FillTriangle(DrawingContext ctx, IBrush brush, Point a, Point b, Point c)
    {
        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            g.BeginFigure(a, isFilled: true);
            g.LineTo(b);
            g.LineTo(c);
            g.EndFigure(true);
        }
        ctx.DrawGeometry(brush, null, geo);
    }

    private void DrawTopBar(DrawingContext ctx, double w)
    {
        ctx.FillRectangle(Palette.OverlaySurfaceBrush, new Rect(0, 0, w, TopBar));
        double midY = TopBar / 2;
        OverlayDraw.TextLeftMid(ctx, OverlayDraw.Text($"SCORE  {_score:0000}", 13, Palette.FgBrush, FontWeight.Bold), 14, midY);

        var lvl = OverlayDraw.Text($"LEVEL {Math.Max(1, _level)}", 12, Palette.MutedBrush, FontWeight.Bold);
        ctx.DrawText(lvl, new Point((w - lvl.Width) / 2, midY - lvl.Height / 2));

        if (_phase == Phase.Playing)
        {
            double x = w - 14 - 18;
            for (int i = 0; i < Math.Max(0, _lives); i++)
            {
                DrawBirdSprite(ctx, new Rect(x, midY - 9, 18, 18), Palette.AccentBrush, 0);
                x -= 22;
            }
        }
    }

    private void DrawTimeBar(DrawingContext ctx)
    {
        if (_phase != Phase.Playing) return;
        double frac = Math.Clamp((double)_timeLeft / TimeTicks, 0, 1);
        var track = new Rect(0, FieldH - 4, FieldW, 4);
        ctx.FillRectangle(Palette.TrackBrush, track);
        var brush = frac < 0.25 ? Palette.ErrorBrush : Palette.RunningBrush;
        ctx.FillRectangle(brush, new Rect(0, FieldH - 4, FieldW * frac, 4));
    }

    private void DrawBanner(DrawingContext ctx, double w)
    {
        var t = OverlayDraw.Text(_banner!, 22, Palette.AccentBrush, FontWeight.Bold);
        double a = 0.5 + 0.5 * Math.Abs(Math.Sin(_pulse));
        using (ctx.PushOpacity(a))
            ctx.DrawText(t, new Point((w - t.Width) / 2, LaneMidY(6) - t.Height / 2));
    }

    private void DrawOverlayCard(DrawingContext ctx, double w, double h, string title, string sub, string hint)
    {
        ctx.FillRectangle(new SolidColorBrush(Color.FromArgb(180, 10, 12, 18)), new Rect(0, 0, w, h));
        double cx = w / 2, cy = h / 2;

        var t = OverlayDraw.Text(title, 30, Palette.AccentBrush, FontWeight.Bold);
        ctx.DrawText(t, new Point(cx - t.Width / 2, cy - 78));

        var s = OverlayDraw.Text(sub, 14, Palette.FgBrush);
        ctx.DrawText(s, new Point(cx - s.Width / 2, cy - 26));

        // The prompt shimmers so it reads as "waiting for you".
        double a = 0.45 + 0.55 * Math.Abs(Math.Sin(_pulse));
        using (ctx.PushOpacity(a))
        {
            var hnt = OverlayDraw.Text(hint, 15, Palette.FgBrush, FontWeight.Bold);
            ctx.DrawText(hnt, new Point(cx - hnt.Width / 2, cy + 20));
        }

        var esc = OverlayDraw.Text("Esc to close", 11, Palette.MutedBrush);
        ctx.DrawText(esc, new Point(cx - esc.Width / 2, cy + 70));
    }
}
