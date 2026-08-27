using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using Perch.Avalonia.Rendering;
using Perch.Avalonia.Theming;
using Perch.Games;
using Perch.Platform;

namespace Perch.Avalonia.Windows;

/// <summary>
/// Desktop basketball (the <c>AppSettings.BasketballEnabled</c> Whimsy toggle): a hoop hung off whichever
/// side of the overlay panel has the most room, and a ball that bounces around the screen under
/// <see cref="BasketballPhysics"/>, eventually coming to rest. Flick the resting ball <em>toward</em> the
/// hoop to shoot (a partial trajectory previews the arc); swishes bump the tally painted under the net;
/// double-clicking the hoop re-tosses the ball from screen centre. See docs/basketball-plan.md.
///
/// <para>Unlike <see cref="ReactionBubbleWindow"/> (transient, deliberately hit-testable) this layer is
/// persistent, so it is fully click-through (<see cref="IWindowChrome.MakeClickThroughNoActivate"/>) and
/// never eats a desktop click. Input arrives instead through two small transparent no-activate windows —
/// <see cref="BallHitWindow"/>, a forgiving halo glued to the ball (parked on it at rest, chasing it in
/// flight — pressing a flying ball catches it dead), and <see cref="RingHitWindow"/> over the hoop (drag
/// to set the hoop height, persisted panel-relative; right-click for reset/hide; double-click to re-toss
/// the ball) — the only pixels the feature claims.</para>
/// </summary>
internal sealed class BasketballWindow : Window
{
    private const double HoopGapDip = 10;      // backboard face → panel edge
    private const double RimBelowPanelTop = 120;

    private readonly BasketballLayer _layer = new();
    private BallHitWindow? _hit;
    private RingHitWindow? _ringHit;
    private double _scale = 1.0;
    private PixelRect _presented;              // the work area last covered, to skip redundant re-covers
    private PixelRect _lastOverlayRect;        // the anchor inputs, kept so a rim drag/reset can re-derive
    private PixelRect _lastWorkArea;
    private bool _anchored;
    private double? _rimOffsetDip;             // user-set hoop height below the panel top; null = default
    private double _dragStartRimY;             // rim height when a ring drag began
    private Rect _overlayRectDip;              // the panel in court coordinates, for the stranded-ball catch

    /// <summary>Raised on each swish, so the App can bump and persist the lifetime tally.</summary>
    public event Action? Scored;

    /// <summary>The user dragged the ring to a new height (an offset below the panel top, in DIPs) or
    /// reset it (null) — the App persists it (<c>AppSettings.BasketballRimOffsetDip</c>).</summary>
    public event Action<double?>? RimOffsetChanged;

    /// <summary>The user picked "Hide desktop basketball" on the ring's right-click menu — the App turns
    /// the Whimsy toggle off and persists.</summary>
    public event Action? HideRequested;

    public BasketballWindow()
    {
        WindowDecorations = WindowDecorations.None;
        Background = Brushes.Transparent;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        Topmost = true;
        ShowInTaskbar = false;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.Manual;

        // Transparent/layered window: ClearType would fringe the tally text — force grayscale AA.
        TextOptions.SetTextRenderingMode(_layer, TextRenderingMode.Antialias);
        Content = _layer;

        _layer.BallCameToRest += OnBallCameToRest;
        _layer.BallMoved += MoveHitWindowWithBall;
        _layer.Scored += () => Scored?.Invoke();
    }

    /// <summary>Seed the lifetime swish tally painted under the net.</summary>
    public void SetTally(int tally) => _layer.SetTally(tally);

    /// <summary>Seed the persisted hoop height (offset below the panel top, DIPs; null = default) before
    /// the first anchor.</summary>
    public void SetRimOffset(double? offsetDip) => _rimOffsetDip = offsetDip;

    /// <summary>Cover the work area (the ball's court, physical pixels — read live from the OS by the App,
    /// since the framework's cached value goes stale across the docked column's edge reservation) in DIPs.
    /// Idempotent, so the per-drag anchor updates can call it freely; a real change re-clamps the ball via
    /// the physics.</summary>
    public void Present(PixelRect workArea, double scale)
    {
        if (workArea == _presented && Math.Abs(scale - _scale) < 0.001)
        {
            if (!IsVisible) Show();
            return;
        }
        _presented = workArea;
        _scale = scale;
        Position = workArea.Position;
        Width = workArea.Width / _scale;
        Height = workArea.Height / _scale;
        // Pin the layer to the same size so its Bounds are the court, not a stretch guess.
        _layer.Width = Width;
        _layer.Height = Height;
        _layer.SetCourt(Width, Height);
        if (!IsVisible) Show();
        if (_layer.IsBallResting) OnBallCameToRest(_layer.BallX, _layer.BallY);
    }

    /// <summary>Hang the hoop off the roomier side of the overlay window (both rects in physical pixels,
    /// like the overlay's own placement maths). The backboard sits <see cref="HoopGapDip"/> off the panel
    /// edge, visually tied to it in floating, docked and dense modes alike.</summary>
    public void SetAnchor(PixelRect overlayRect, PixelRect workArea)
    {
        _lastOverlayRect = overlayRect;
        _lastWorkArea = workArea;
        _anchored = true;

        double left = (overlayRect.X - Position.X) / _scale;
        double right = (overlayRect.Right - Position.X) / _scale;
        double top = (overlayRect.Y - Position.Y) / _scale;
        _overlayRectDip = new Rect(left, top, right - left, overlayRect.Height / _scale);

        int spaceLeft = overlayRect.X - workArea.X;
        int spaceRight = (workArea.X + workArea.Width) - overlayRect.Right;

        int facing = spaceRight >= spaceLeft ? 1 : -1;
        double boardX = facing > 0 ? right + HoopGapDip : left - HoopGapDip;
        _layer.SetHoop(boardX, ClampRim(top + (_rimOffsetDip ?? RimBelowPanelTop)), facing);
        PositionRingHitWindow();

        // The panel may have just moved (or expanded) onto a sleeping ball — don't leave it stranded.
        if (_layer.IsBallResting && BallStranded(_layer.BallX, _layer.BallY))
            _layer.ResetBall();
    }

    // A ball whose centre sits inside the panel/column is unreachable: the panel is topmost, so the ball is
    // invisible there and its grab halo would float over the panel's own UI. Leaning against the panel's
    // edge from outside is fine — only a centre genuinely inside counts.
    private bool BallStranded(double x, double y) =>
        _anchored && _overlayRectDip.Deflate(2).Contains(new Point(x, y));

    // The ball settled: re-toss it if it's somewhere unreachable, otherwise park the grab halo over it.
    private void OnBallCameToRest(double x, double y)
    {
        if (BallStranded(x, y)) _layer.ResetBall();
        else ShowHitWindowOverBall(x, y);
    }

    // Below the panel top, but never higher than a full-power arc can actually reach from the floor
    // (~1800 DIP with the full-court launch cap — beyond any realistic monitor), and never absurdly low.
    private double ClampRim(double rimY)
    {
        double lo = Math.Max(BasketballPhysics.BoardAboveRim + 24, Height - 1800);
        double hi = Math.Max(lo, Height * 0.7);
        return Math.Clamp(rimY, lo, hi);
    }

    // Parks the ring's input window over the drawn hoop (backboard + rim + net + tally). Unlike the ball's
    // halo it's always up: the ring is grabbable (drag = hoop height) and right-clickable at any time.
    private void PositionRingHitWindow()
    {
        _ringHit ??= CreateRingHitWindow();
        double xMin = Math.Min(_layer.HoopBoardX, _layer.HoopRimFarX) - 8;
        double xMax = Math.Max(_layer.HoopBoardX, _layer.HoopRimFarX) + 8;
        double yMin = _layer.HoopRimY - BasketballPhysics.BoardAboveRim - 6;
        double yMax = _layer.HoopRimY + BasketballLayer.NetDepth + 28;   // net depth + the tally pill
        _ringHit.Resize(xMax - xMin, yMax - yMin);
        _ringHit.Position = new PixelPoint(
            Position.X + (int)Math.Round(xMin * _scale),
            Position.Y + (int)Math.Round(yMin * _scale));
        if (!_ringHit.IsVisible) _ringHit.Show();
        PlatformServices.WindowChrome.BringToTopNoActivate(_ringHit.TryGetPlatformHandle()?.Handle ?? 0);
    }

    private RingHitWindow CreateRingHitWindow() => new(
        dragStarted: () => _dragStartRimY = _layer.HoopRimY,
        dragDelta: deltaPx =>
        {
            _layer.MoveHoopTo(ClampRim(_dragStartRimY + deltaPx.Y / _scale));
            PositionRingHitWindow();
        },
        dragEnded: () =>
        {
            // Store the height panel-relative so it keeps riding the panel, and let the App persist it.
            // A press-and-release that didn't actually move (a plain click, the first half of a
            // double-click) changes nothing and saves nothing.
            double top = (_lastOverlayRect.Y - Position.Y) / _scale;
            double offset = _layer.HoopRimY - top;
            if (Math.Abs(offset - (_rimOffsetDip ?? RimBelowPanelTop)) < 0.5) return;
            _rimOffsetDip = offset;
            RimOffsetChanged?.Invoke(_rimOffsetDip);
        },
        rightClicked: ShowRingMenu,
        doubleClicked: () => _layer.ResetBall());

    private void ShowRingMenu()
    {
        if (_ringHit is null) return;
        var reset = new MenuItem { Header = "Reset hoop height" };
        reset.Click += (_, _) =>
        {
            _rimOffsetDip = null;
            RimOffsetChanged?.Invoke(null);
            if (_anchored) SetAnchor(_lastOverlayRect, _lastWorkArea);
        };
        var hide = new MenuItem { Header = "Hide desktop basketball" };
        hide.Click += (_, _) => HideRequested?.Invoke();
        var flyout = new MenuFlyout();
        flyout.Items.Add(reset);
        flyout.Items.Add(hide);
        flyout.ShowAt(_ringHit.Surface, showAtPointer: true);
    }

    private void ShowHitWindowOverBall(double xDip, double yDip)
    {
        _hit ??= new BallHitWindow(
            caught: () => _layer.CatchBall(),
            aimChanged: aimPx => _layer.SetAim(aimPx.X / _scale, aimPx.Y / _scale),
            released: dragPx => _layer.EndAim(dragPx.X / _scale, dragPx.Y / _scale));
        MoveHitWindow(xDip, yDip);
        if (!_hit.IsVisible) _hit.Show();
        PlatformServices.WindowChrome.BringToTopNoActivate(_hit.TryGetPlatformHandle()?.Handle ?? 0);
    }

    // The per-tick chase while the ball flies: just the position write — the show/z-order work happens
    // once in ShowHitWindowOverBall, not sixty times a second.
    private void MoveHitWindowWithBall(double xDip, double yDip)
    {
        if (_hit is not { IsVisible: true }) { ShowHitWindowOverBall(xDip, yDip); return; }
        MoveHitWindow(xDip, yDip);
    }

    private void MoveHitWindow(double xDip, double yDip)
    {
        double half = BallHitWindow.SizeDip / 2;
        _hit!.Position = new PixelPoint(
            Position.X + (int)Math.Round((xDip - half) * _scale),
            Position.Y + (int)Math.Round((yDip - half) * _scale));
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        // Persistent ambient layer: never intercept the mouse, never take focus.
        if (TryGetPlatformHandle() is { } h)
            PlatformServices.WindowChrome.MakeClickThroughNoActivate(h.Handle);
    }

    protected override void OnClosed(EventArgs e)
    {
        _hit?.Close();
        _hit = null;
        _ringHit?.Close();
        _ringHit = null;
        base.OnClosed(e);
    }
}

/// <summary>
/// The owner-drawn court: steps <see cref="BasketballPhysics"/> on the house 16 ms
/// <see cref="DispatcherTimer"/> loop (stopping whenever the ball sleeps and nothing is animating) and
/// paints the backboard + tally, rim, net, ball, the aim rubber-band and the partial trajectory preview,
/// and the "+1" swish flash.
/// </summary>
internal sealed class BasketballLayer : Control
{
    private const int TickMs = 16;
    private const double FlashMs = 900;
    internal const double NetDepth = 34;   // the ring hit window's extent reads this too

    private readonly BasketballPhysics _physics = new();
    private readonly Random _rng = new();   // only for the double-click re-toss variance
    private DispatcherTimer? _timer;
    private long _lastTick;
    private (double Dx, double Dy)? _aim;   // the live drag vector (DIPs), while aiming
    private long? _flashStart;              // TickCount64 of the last swish, for the "+1" flash
    private double _spin;                   // ball roll angle (radians), purely cosmetic
    private int _tally;
    private bool _hoopPlaced;
    private bool _wasResting;

    /// <summary>The ball settled (floor, rim, or a mid-air catch) — park the hit window over it.</summary>
    public event Action<double, double>? BallCameToRest;

    /// <summary>Raised each tick the ball is in flight, so the grab halo can chase it — a moving ball is
    /// catchable (press = freeze it on the spot).</summary>
    public event Action<double, double>? BallMoved;

    /// <summary>A swish landed (the tally here is already bumped; the App persists its copy).</summary>
    public event Action? Scored;

    public bool IsBallResting => _physics.Resting;
    public double BallX => _physics.X;
    public double BallY => _physics.Y;
    public double HoopBoardX => _physics.BoardX;
    public double HoopRimY => _physics.RimY;
    public double HoopRimFarX => _physics.RimFarX;

    public void SetTally(int tally)
    {
        _tally = tally;
        InvalidateVisual();
    }

    public void SetCourt(double width, double height)
    {
        _physics.SetBounds(width, height);
        InvalidateVisual();
    }

    public void SetHoop(double boardX, double rimY, int facing)
    {
        bool moved = _hoopPlaced &&
            (Math.Abs(boardX - _physics.BoardX) > 0.5 || Math.Abs(rimY - _physics.RimY) > 0.5
             || facing != _physics.Facing);
        _physics.SetHoop(boardX, rimY, facing);
        if (!_hoopPlaced)
        {
            _hoopPlaced = true;
            // First appearance: drop the ball in from the top, centre-screen (matching the double-click
            // re-toss's spot, and clear of wherever the panel happens to sit).
            _physics.Drop(_physics.Width / 2);
            EnsureTimer();
        }
        else if (moved && _physics.Resting && _physics.Y < _physics.Height - BasketballPhysics.BallRadius - 1)
        {
            // The ball was asleep balanced on the rim (or caught mid-air) and its support just moved:
            // let it fall — the grab halo chases it via BallMoved until it settles again.
            _physics.Wake();
            _wasResting = false;
            EnsureTimer();
        }
        InvalidateVisual();
    }

    /// <summary>Move just the rim height (a live ring drag); board side and facing stay put.</summary>
    public void MoveHoopTo(double rimY) => SetHoop(_physics.BoardX, rimY, _physics.Facing);

    /// <summary>Re-toss the ball from the centre of the screen with a gentle random lob (the hoop's
    /// double-click reset — for when the ball ends up somewhere annoying).</summary>
    public void ResetBall()
    {
        double vx = (_rng.NextDouble() - 0.5) * 260;        // a little sideways drift either way
        double vy = -(60 + _rng.NextDouble() * 160);        // a gentle upward toss
        _physics.ResetTo(_physics.Width / 2, _physics.Height / 2, vx, vy);
        _wasResting = false;
        EnsureTimer();
        InvalidateVisual();
    }

    /// <summary>The hit window's live drag (DIPs). Repaints the rubber-band + trajectory; no timer needed
    /// while aiming — the ball is asleep and each drag event invalidates.</summary>
    public void SetAim(double dx, double dy)
    {
        _aim = (dx, dy);
        InvalidateVisual();
    }

    /// <summary>The drag ended: launch (toward the flick) or, for a tiny drag, cancel the shot.</summary>
    public void EndAim(double dx, double dy)
    {
        _aim = null;
        if (_physics.Launch(dx, dy))
        {
            _wasResting = false;
            EnsureTimer();
        }
        InvalidateVisual();
    }

    /// <summary>Catch a flying ball: freeze it on the spot (it becomes the normal aimable resting ball,
    /// mid-air included). A no-op when it's already at rest.</summary>
    public void CatchBall()
    {
        if (_physics.Resting) return;
        _physics.Catch();
        _wasResting = true;
        InvalidateVisual();
        BallCameToRest?.Invoke(_physics.X, _physics.Y);
    }

    private void EnsureTimer()
    {
        _timer ??= CreateTimer();
        if (!_timer.IsEnabled)
        {
            _lastTick = Environment.TickCount64;
            _timer.Start();
        }
    }

    private DispatcherTimer CreateTimer()
    {
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(TickMs) };
        t.Tick += (_, _) => Tick();
        return t;
    }

    private void Tick()
    {
        long now = Environment.TickCount64;
        double dt = Math.Min((now - _lastTick) / 1000.0, 0.05);
        _lastTick = now;

        var result = _physics.Step(dt);
        _spin += _physics.Vx * dt / BasketballPhysics.BallRadius;   // roll with horizontal travel
        if (result.Scored)
        {
            _tally++;
            _flashStart = now;
            Scored?.Invoke();
        }
        InvalidateVisual();

        if (!_physics.Resting)
            BallMoved?.Invoke(_physics.X, _physics.Y);   // the grab halo chases the flying ball

        if (_physics.Resting && !_wasResting)
        {
            _wasResting = true;
            BallCameToRest?.Invoke(_physics.X, _physics.Y);
        }
        bool flashing = _flashStart is { } f && now - f < FlashMs;
        if (_physics.Resting && !flashing)
            _timer!.Stop();
    }

    public override void Render(DrawingContext ctx)
    {
        base.Render(ctx);
        // No hit-test fill: the window is click-through, so every pixel stays the desktop's.
        DrawHoop(ctx);
        if (_aim is { } aim && _physics.Resting) DrawAim(ctx, aim.Dx, aim.Dy);
        DrawBall(ctx, _physics.X, _physics.Y, _spin);
        DrawFlash(ctx);
    }

    private void DrawHoop(DrawingContext ctx)
    {
        if (!_hoopPlaced) return;
        double bx = _physics.BoardX, rimY = _physics.RimY;
        int facing = _physics.Facing;

        // Backboard: a slim slab on the panel side of the collision plane, in overlay chrome colours.
        const double thickness = 6;
        var board = new Rect(facing > 0 ? bx - thickness : bx,
            rimY - BasketballPhysics.BoardAboveRim, thickness,
            BasketballPhysics.BoardAboveRim + BasketballPhysics.BoardBelowRim);
        OverlayDraw.Panel(ctx, board, Palette.OverlaySurfaceBrush, new Pen(Palette.BorderBrush, 1), 2);

        // Rim: the brand red-orange, front lip marked.
        double farX = _physics.RimFarX;
        var rimPen = new Pen(Palette.BrandBrush, 3);
        ctx.DrawLine(rimPen, new Point(bx, rimY), new Point(farX, rimY));
        ctx.DrawEllipse(Palette.BrandBrush, null, new Point(farX, rimY), 2.5, 2.5);

        // Net: a diamond mesh between the lips — two tiers of crossing strands tapering toward the
        // bottom — that sways for a moment after a swish, so the catch reads (the engine bleeds the
        // ball's speed at the same instant; together they make the swish feel like cloth, not a hole).
        var netPen = new Pen(new SolidColorBrush(Color.FromArgb(110, Palette.Fg.R, Palette.Fg.G, Palette.Fg.B)), 1);
        double near = _physics.RimNearX;
        double sway = 0;
        if (_flashStart is { } fs)
        {
            double ph = Math.Clamp((Environment.TickCount64 - fs) / FlashMs, 0, 1);
            if (ph < 1) sway = Math.Sin(ph * Math.Tau * 2.2) * 5 * (1 - ph);   // a damped wobble
        }
        double midY = rimY + NetDepth * 0.55, botY = rimY + NetDepth;
        double XAt(double t, double taper, double shift) => near + (farX - near) * (taper / 2 + (1 - taper) * t) + shift;
        const int cols = 5;
        for (int j = 0; j < cols - 1; j++)
        {
            double tl = j / (double)(cols - 1), tr = (j + 1) / (double)(cols - 1);
            var mid = new Point(XAt((j + 0.5) / (cols - 1), 0.16, sway * 0.55), midY);
            ctx.DrawLine(netPen, new Point(XAt(tl, 0, 0), rimY + 1), mid);
            ctx.DrawLine(netPen, new Point(XAt(tr, 0, 0), rimY + 1), mid);
            ctx.DrawLine(netPen, mid, new Point(XAt(tl, 0.34, sway), botY));
            ctx.DrawLine(netPen, mid, new Point(XAt(tr, 0.34, sway), botY));
        }

        // The lifetime tally, on a small scrim pill under the net so it reads over any desktop.
        var ft = OverlayDraw.Text(_tally.ToString(), 10, Palette.MutedBrush);
        double cx = (near + farX) / 2;
        var pill = new Rect(cx - ft.Width / 2 - 5, botY + 4, ft.Width + 10, ft.Height + 4);
        OverlayDraw.Panel(ctx, pill, Palette.OverlayScrimBrush, null, pill.Height / 2);
        ctx.DrawText(ft, new Point(cx - ft.Width / 2, botY + 6));
    }

    private static void DrawBall(DrawingContext ctx, double x, double y, double spin)
    {
        double r = BasketballPhysics.BallRadius;
        var outline = new Pen(new SolidColorBrush(Color.FromArgb(90, 0, 0, 0)), 1.5);
        ctx.DrawEllipse(Palette.BasketballBrush, outline, new Point(x, y), r, r);

        // Seams: a rolled cross (recognisable at 24px; the roll angle tracks horizontal travel).
        var seam = new Pen(new SolidColorBrush(Color.FromArgb(90, 0, 0, 0)), 1.2);
        double c = Math.Cos(spin), s = Math.Sin(spin);
        double ri = r - 0.75;   // keep seam ends inside the outline
        ctx.DrawLine(seam, new Point(x - c * ri, y - s * ri), new Point(x + c * ri, y + s * ri));
        ctx.DrawLine(seam, new Point(x + s * ri, y - c * ri), new Point(x - s * ri, y + c * ri));
    }

    private void DrawAim(DrawingContext ctx, double dx, double dy)
    {
        double x = _physics.X, y = _physics.Y;

        // The aim line: ball → your drag point (the direction of the shot), with a grip dot at the hand.
        var band = new Pen(new SolidColorBrush(Color.FromArgb(150, Palette.Fg.R, Palette.Fg.G, Palette.Fg.B)),
            1.5, DashStyle.Dash);
        ctx.DrawLine(band, new Point(x, y), new Point(x + dx, y + dy));
        ctx.DrawEllipse(new SolidColorBrush(Color.FromArgb(150, Palette.Fg.R, Palette.Fg.G, Palette.Fg.B)),
            null, new Point(x + dx, y + dy), 3, 3);

        // The partial trajectory: the first few dots of the arc, shrinking and fading.
        var points = _physics.TrajectoryPreview(dx, dy);
        for (int i = 0; i < points.Count; i++)
        {
            double t = i / (double)Math.Max(1, points.Count - 1);
            byte a = (byte)(200 * (1 - t) + 30 * t);
            var dot = new SolidColorBrush(Color.FromArgb(a, Palette.Basketball.R, Palette.Basketball.G, Palette.Basketball.B));
            double dr = 3.4 - 1.8 * t;
            ctx.DrawEllipse(dot, null, new Point(points[i].X, points[i].Y), dr, dr);
        }
    }

    private void DrawFlash(DrawingContext ctx)
    {
        if (_flashStart is not { } start) return;
        double t = Math.Clamp((Environment.TickCount64 - start) / FlashMs, 0, 1);
        if (t >= 1) return;

        // An expanding, fading brand ring at the rim centre + a rising "+1".
        double cx = (_physics.RimNearX + _physics.RimFarX) / 2, cy = _physics.RimY;
        byte ringA = (byte)(170 * (1 - t));
        var ring = new Pen(new SolidColorBrush(Color.FromArgb(ringA, Palette.Brand.R, Palette.Brand.G, Palette.Brand.B)), 2.5);
        double rr = BasketballPhysics.RimSpan * (0.5 + t * 0.8);
        ctx.DrawEllipse(null, ring, new Point(cx, cy), rr, rr);

        var ft = OverlayDraw.Text("+1", 14, Palette.FgBrush, FontWeight.SemiBold);
        using (ctx.PushOpacity(1 - t))
            ctx.DrawText(ft, new Point(cx - ft.Width / 2, cy - 26 - t * 22 - ft.Height));
    }

    /// <summary>A posed, non-animating frame for the headless render harness: the hoop anchored beside a
    /// pretend panel edge, the ball resting mid-aim with its rubber-band and partial trajectory, and a
    /// tally on the board — over a dim backdrop so it reads in the snapshot. The timer never starts.</summary>
    internal static Control CreateForRender()
    {
        var layer = new BasketballLayer { Width = 420, Height = 360 };
        layer._physics.SetBounds(420, 360);
        // Seed state directly (not via SetHoop, whose first placement drops the ball and starts the timer).
        layer._physics.SetHoop(340, 120, -1);   // rim opens left, board hugging a panel on the right
        layer._hoopPlaced = true;
        layer._tally = 12;
        // Settle the ball deterministically (no timer headless), then pose a drag.
        layer._physics.Drop(140);
        while (!layer._physics.Resting) layer._physics.Step(0.016);
        layer._aim = (70, -60);                 // flick up-right → a shot arcing toward the hoop
        return new Grid
        {
            Width = 420, Height = 360,
            Background = new SolidColorBrush(Color.FromRgb(24, 24, 32)),
            Children = { layer },
        };
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _timer?.Stop();
        base.OnDetachedFromVisualTree(e);
    }
}

/// <summary>
/// The ball's interactive surface: a small transparent no-activate tool window glued to the
/// ball — parked on it at rest, chasing it in flight. Press catches the ball (a flying ball freezes on
/// the spot) and captures the pointer; the drag is reported in physical pixels (screen space, so it
/// keeps working outside the window's own square); release fires the shot toward the flick. The
/// click-through layer owns every other pixel's honesty.
/// </summary>
internal sealed class BallHitWindow : Window
{
    // Generously larger than the 32-DIP ball: the ball is a small target and the window is invisible, so
    // a forgiving halo makes it grabbable without hunting for exact pixels. Kept modest all the same —
    // desktop clicks inside this square go to the ball, not the desktop, wherever the ball is.
    public const double SizeDip = 88;

    private readonly Action _caught;
    private readonly Action<PixelPoint> _aimChanged;
    private readonly Action<PixelPoint> _released;
    private readonly Control _surface;
    private PixelPoint _dragStart;
    private bool _dragging;

    public BallHitWindow(Action caught, Action<PixelPoint> aimChanged, Action<PixelPoint> released)
    {
        _caught = caught;
        _aimChanged = aimChanged;
        _released = released;

        WindowDecorations = WindowDecorations.None;
        Background = Brushes.Transparent;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        Topmost = true;
        ShowInTaskbar = false;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Width = SizeDip;
        Height = SizeDip;
        Cursor = new Cursor(StandardCursorType.Hand);

        // A transparent fill makes every pixel hit-testable while staying invisible (the ball itself is
        // painted by the layer beneath).
        _surface = new HitSurface(this) { Width = SizeDip, Height = SizeDip };
        Content = _surface;
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        if (TryGetPlatformHandle() is { } h)
            PlatformServices.WindowChrome.MakeToolWindowNoActivate(h.Handle);
    }

    private sealed class HitSurface : Control
    {
        private readonly BallHitWindow _owner;
        public HitSurface(BallHitWindow owner) => _owner = owner;

        public override void Render(DrawingContext ctx) =>
            ctx.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
            _owner._caught();   // a flying ball freezes on the spot; a resting one is untouched
            _owner._dragging = true;
            _owner._dragStart = this.PointToScreen(e.GetPosition(this));
            e.Pointer.Capture(this);
            e.Handled = true;
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);
            if (!_owner._dragging) return;
            var p = this.PointToScreen(e.GetPosition(this));
            _owner._aimChanged(new PixelPoint(p.X - _owner._dragStart.X, p.Y - _owner._dragStart.Y));
            e.Handled = true;
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            base.OnPointerReleased(e);
            if (!_owner._dragging) return;
            _owner._dragging = false;
            e.Pointer.Capture(null);
            var p = this.PointToScreen(e.GetPosition(this));
            _owner._released(new PixelPoint(p.X - _owner._dragStart.X, p.Y - _owner._dragStart.Y));
            e.Handled = true;
        }
    }
}

/// <summary>
/// The ring's input window: a transparent no-activate tool window parked over the drawn hoop, up the
/// whole time the game is (unlike the ball's halo, which hides in flight). Left-drag adjusts the hoop
/// height — reported as cumulative screen-pixel deltas so the drag keeps working outside the window —
/// right-click opens the ring menu (reset height / hide the game), and double-click re-tosses the ball
/// from screen centre (for when it settles somewhere annoying).
/// </summary>
internal sealed class RingHitWindow : Window
{
    private readonly Action _dragStarted;
    private readonly Action<PixelPoint> _dragDelta;
    private readonly Action _dragEnded;
    private readonly Action _rightClicked;
    private readonly Action _doubleClicked;
    private PixelPoint _dragStart;
    private bool _dragging;

    /// <summary>The hit-test surface — also the anchor the ring menu flyout shows at.</summary>
    internal Control Surface { get; }

    public RingHitWindow(Action dragStarted, Action<PixelPoint> dragDelta, Action dragEnded,
        Action rightClicked, Action doubleClicked)
    {
        _dragStarted = dragStarted;
        _dragDelta = dragDelta;
        _dragEnded = dragEnded;
        _rightClicked = rightClicked;
        _doubleClicked = doubleClicked;

        WindowDecorations = WindowDecorations.None;
        Background = Brushes.Transparent;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        Topmost = true;
        ShowInTaskbar = false;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Cursor = new Cursor(StandardCursorType.SizeNorthSouth);

        Surface = new HitSurface(this);
        Content = Surface;
    }

    /// <summary>Match the window (and its pinned hit surface) to the hoop's drawn extent, in DIPs.</summary>
    public void Resize(double widthDip, double heightDip)
    {
        Width = widthDip;
        Height = heightDip;
        Surface.Width = widthDip;
        Surface.Height = heightDip;
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        if (TryGetPlatformHandle() is { } h)
            PlatformServices.WindowChrome.MakeToolWindowNoActivate(h.Handle);
    }

    private sealed class HitSurface : Control
    {
        private readonly RingHitWindow _owner;
        public HitSurface(RingHitWindow owner) => _owner = owner;

        public override void Render(DrawingContext ctx) =>
            ctx.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            var props = e.GetCurrentPoint(this).Properties;
            if (props.IsRightButtonPressed)
            {
                _owner._rightClicked();
                e.Handled = true;
                return;
            }
            if (!props.IsLeftButtonPressed) return;
            if (e.ClickCount >= 2)
            {
                // Double-click resets the ball; don't also start a height drag from the second press.
                _owner._doubleClicked();
                e.Handled = true;
                return;
            }
            _owner._dragging = true;
            _owner._dragStart = this.PointToScreen(e.GetPosition(this));
            _owner._dragStarted();
            e.Pointer.Capture(this);
            e.Handled = true;
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);
            if (!_owner._dragging) return;
            var p = this.PointToScreen(e.GetPosition(this));
            _owner._dragDelta(new PixelPoint(p.X - _owner._dragStart.X, p.Y - _owner._dragStart.Y));
            e.Handled = true;
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            base.OnPointerReleased(e);
            if (!_owner._dragging) return;
            _owner._dragging = false;
            e.Pointer.Capture(null);
            _owner._dragEnded();
            e.Handled = true;
        }
    }
}
