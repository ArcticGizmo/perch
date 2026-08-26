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
/// <see cref="BasketballPhysics"/>, eventually coming to rest. Drag the resting ball <em>away</em> from the
/// hoop to slingshot it (a partial trajectory previews the arc); swishes bump the tally painted under the
/// net. See docs/basketball-plan.md.
///
/// <para>Unlike <see cref="ReactionBubbleWindow"/> (transient, deliberately hit-testable) this layer is
/// persistent, so it is fully click-through (<see cref="IWindowChrome.MakeClickThroughNoActivate"/>) and
/// never eats a desktop click. Input arrives instead through <see cref="BallHitWindow"/> — a tiny
/// transparent no-activate window parked exactly over the <em>resting</em> ball, the only pixels the
/// feature ever claims. It captures the slingshot drag and hides while the ball flies.</para>
/// </summary>
internal sealed class BasketballWindow : Window
{
    private const double HoopGapDip = 10;      // backboard face → panel edge
    private const double RimBelowPanelTop = 120;

    private readonly BasketballLayer _layer = new();
    private BallHitWindow? _hit;
    private double _scale = 1.0;
    private PixelRect _presented;              // the work area last covered, to skip redundant re-covers

    /// <summary>Raised on each swish, so the App can bump and persist the lifetime tally.</summary>
    public event Action? Scored;

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

        _layer.BallCameToRest += ShowHitWindowOverBall;
        _layer.BallLaunched += () => _hit?.Hide();
        _layer.Scored += () => Scored?.Invoke();
    }

    /// <summary>Seed the lifetime swish tally painted under the net.</summary>
    public void SetTally(int tally) => _layer.SetTally(tally);

    /// <summary>Cover <paramref name="screen"/>'s work area (the ball's court) in DIPs. Idempotent, so the
    /// per-drag anchor updates can call it freely; a real change re-clamps the ball via the physics.</summary>
    public void Present(Screen screen)
    {
        var wa = screen.WorkingArea;            // physical pixels
        if (wa == _presented && Math.Abs(screen.Scaling - _scale) < 0.001)
        {
            if (!IsVisible) Show();
            return;
        }
        _presented = wa;
        _scale = screen.Scaling;
        Position = wa.Position;
        Width = wa.Width / _scale;
        Height = wa.Height / _scale;
        // Pin the layer to the same size so its Bounds are the court, not a stretch guess.
        _layer.Width = Width;
        _layer.Height = Height;
        _layer.SetCourt(Width, Height);
        if (!IsVisible) Show();
        if (_layer.IsBallResting) ShowHitWindowOverBall(_layer.BallX, _layer.BallY);
    }

    /// <summary>Hang the hoop off the roomier side of the overlay window (both rects in physical pixels,
    /// like the overlay's own placement maths). The backboard sits <see cref="HoopGapDip"/> off the panel
    /// edge, visually tied to it in floating, docked and dense modes alike.</summary>
    public void SetAnchor(PixelRect overlayRect, PixelRect workArea)
    {
        double left = (overlayRect.X - Position.X) / _scale;
        double right = (overlayRect.Right - Position.X) / _scale;
        double top = (overlayRect.Y - Position.Y) / _scale;

        int spaceLeft = overlayRect.X - workArea.X;
        int spaceRight = (workArea.X + workArea.Width) - overlayRect.Right;

        int facing = spaceRight >= spaceLeft ? 1 : -1;
        double boardX = facing > 0 ? right + HoopGapDip : left - HoopGapDip;
        // Below the panel top, but never higher than a full-power arc can actually reach from the floor
        // (~640 DIP with the engine's launch cap), and never absurdly low.
        double lo = Math.Max(BasketballPhysics.BoardAboveRim + 24, Height - 640);
        double hi = Math.Max(lo, Height * 0.7);
        _layer.SetHoop(boardX, Math.Clamp(top + RimBelowPanelTop, lo, hi), facing);
    }

    private void ShowHitWindowOverBall(double xDip, double yDip)
    {
        _hit ??= new BallHitWindow(
            aimPx => _layer.SetAim(aimPx.X / _scale, aimPx.Y / _scale),
            dragPx => _layer.EndAim(dragPx.X / _scale, dragPx.Y / _scale));
        double half = BallHitWindow.SizeDip / 2;
        _hit.Position = new PixelPoint(
            Position.X + (int)Math.Round((xDip - half) * _scale),
            Position.Y + (int)Math.Round((yDip - half) * _scale));
        if (!_hit.IsVisible) _hit.Show();
        PlatformServices.WindowChrome.BringToTopNoActivate(_hit.TryGetPlatformHandle()?.Handle ?? 0);
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
    private const double NetDepth = 26;

    private readonly BasketballPhysics _physics = new();
    private DispatcherTimer? _timer;
    private long _lastTick;
    private (double Dx, double Dy)? _aim;   // the live drag vector (DIPs), while aiming
    private long? _flashStart;              // TickCount64 of the last swish, for the "+1" flash
    private double _spin;                   // ball roll angle (radians), purely cosmetic
    private int _tally;
    private bool _hoopPlaced;
    private bool _wasResting;

    /// <summary>The ball settled (floor or balanced on the rim) — park the hit window over it.</summary>
    public event Action<double, double>? BallCameToRest;

    /// <summary>A slingshot fired — hide the hit window until the ball settles again.</summary>
    public event Action? BallLaunched;

    /// <summary>A swish landed (the tally here is already bumped; the App persists its copy).</summary>
    public event Action? Scored;

    public bool IsBallResting => _physics.Resting;
    public double BallX => _physics.X;
    public double BallY => _physics.Y;

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
        _physics.SetHoop(boardX, rimY, facing);
        if (!_hoopPlaced)
        {
            _hoopPlaced = true;
            // First appearance: drop the ball in from the top, just in front of the hoop.
            _physics.Drop(boardX + facing * (BasketballPhysics.RimSpan + 70));
            EnsureTimer();
        }
        InvalidateVisual();
    }

    /// <summary>The hit window's live drag (DIPs). Repaints the rubber-band + trajectory; no timer needed
    /// while aiming — the ball is asleep and each drag event invalidates.</summary>
    public void SetAim(double dx, double dy)
    {
        _aim = (dx, dy);
        InvalidateVisual();
    }

    /// <summary>The drag ended: launch (opposite the drag) or, for a tiny drag, cancel the shot.</summary>
    public void EndAim(double dx, double dy)
    {
        _aim = null;
        if (_physics.Launch(dx, dy))
        {
            _wasResting = false;
            BallLaunched?.Invoke();
            EnsureTimer();
        }
        InvalidateVisual();
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

        // Net: tapering strands with one weave line, in faint foreground.
        var netPen = new Pen(new SolidColorBrush(Color.FromArgb(110, Palette.Fg.R, Palette.Fg.G, Palette.Fg.B)), 1);
        double near = _physics.RimNearX;
        double baseY = rimY + NetDepth;
        const int strands = 5;
        for (int i = 0; i < strands; i++)
        {
            double t = i / (double)(strands - 1);
            double topX = near + (farX - near) * t;
            double botX = near + (farX - near) * (0.18 + 0.64 * t);   // taper ~30%
            ctx.DrawLine(netPen, new Point(topX, rimY + 1), new Point(botX, baseY));
        }
        double midY = rimY + NetDepth * 0.5;
        ctx.DrawLine(netPen, new Point(near + (farX - near) * 0.09, midY), new Point(near + (farX - near) * 0.91, midY));

        // The lifetime tally, on a small scrim pill under the net so it reads over any desktop.
        var ft = OverlayDraw.Text(_tally.ToString(), 10, Palette.MutedBrush);
        double cx = (near + farX) / 2;
        var pill = new Rect(cx - ft.Width / 2 - 5, baseY + 4, ft.Width + 10, ft.Height + 4);
        OverlayDraw.Panel(ctx, pill, Palette.OverlayScrimBrush, null, pill.Height / 2);
        ctx.DrawText(ft, new Point(cx - ft.Width / 2, baseY + 6));
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

        // The rubber-band: ball → your drag point (behind the shot), with a grip dot at the hand.
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
        layer._aim = (-70, -60);                // pull up-left (vertical mirrors down) → a shot arcing to the hoop
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
/// The one interactive surface of desktop basketball: a tiny transparent no-activate tool window parked
/// exactly over the resting ball. Press captures the pointer; the drag is reported in physical pixels
/// (screen space, so it keeps working outside the window's own 40 DIPs); release fires the slingshot.
/// Hidden while the ball is in flight — the click-through layer owns every other pixel's honesty.
/// </summary>
internal sealed class BallHitWindow : Window
{
    public const double SizeDip = 40;

    private readonly Action<PixelPoint> _aimChanged;
    private readonly Action<PixelPoint> _released;
    private readonly Control _surface;
    private PixelPoint _dragStart;
    private bool _dragging;

    public BallHitWindow(Action<PixelPoint> aimChanged, Action<PixelPoint> released)
    {
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
