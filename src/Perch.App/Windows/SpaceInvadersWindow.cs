using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Perch.Avalonia.Rendering;
using Perch.Avalonia.Theming;

namespace Perch.Avalonia.Windows;

/// <summary>
/// A completely-secret Space Invaders clone — "Perch Invaders" — that only surfaces after the overlay's
/// brand mark is clicked ten times in quick succession (see <c>OverlayCanvas.RouteClick</c>). Pure
/// owner-drawn Avalonia over the same <see cref="OverlayDraw"/> / <see cref="Palette"/> vocabulary the rest
/// of the app uses, so it reads as part of Perch. No platform APIs, no persistence — it's a toy.
///
/// The window is a thin shell: it owns the chrome and forwards key presses to the <see cref="InvadersField"/>
/// control, which holds all the game state and runs the loop off a <see cref="DispatcherTimer"/> (the same
/// tick-then-<c>InvalidateVisual</c> pattern as <c>AchievementCard</c>).
/// </summary>
public sealed class SpaceInvadersWindow : Window
{
    private readonly InvadersField _field = new();

    public SpaceInvadersWindow()
    {
        Title = "Perch Invaders";
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
        if (_field.HandleKey(e.Key, down: true)) e.Handled = true;
        base.OnKeyDown(e);
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        if (_field.HandleKey(e.Key, down: false)) e.Handled = true;
        base.OnKeyUp(e);
    }
}

/// <summary>
/// The game itself: an owner-drawn, fixed-size playfield running a ~60fps <see cref="DispatcherTimer"/>
/// loop. Invaders march as a block (dropping and reversing at the edges, speeding up as their numbers
/// thin), rain bombs, and the player's cannon slides along the baseline firing one shot at a time.
/// </summary>
internal sealed class InvadersField : Control
{
    // ── Playfield geometry (DIP). The control is fixed to this size, so layout can use the constants. ──
    private const double FieldW = 460, FieldH = 580;
    private const double TopBar = 38;                     // score / lives strip
    private const double MarginX = 18;                    // formation turnaround margin
    private const double GroundGap = 30;                  // cannon baseline above the bottom edge

    private const int Rows = 4, Cols = 8;
    private const double InvW = 26, InvH = 18;
    private const double PitchX = 46, PitchY = 34;
    private const double StepX = 12, DropY = 20;

    private const double PlayerW = 40, PlayerH = 16, PlayerSpeed = 6;
    private const double BulletW = 3, BulletH = 13, BulletSpeed = 9;
    private const double BombW = 4, BombH = 12, BombSpeed = 3.6;

    private const int TickMs = 16;

    private static double PlayerTop => FieldH - GroundGap - PlayerH;

    private enum Phase { Title, Playing, Won, Lost }

    // ── State ──
    private Phase _phase = Phase.Title;
    private readonly bool[,] _alive = new bool[Rows, Cols];
    private double _formX, _formY;
    private int _dir = 1;                                 // +1 marching right, -1 left
    private int _frame;                                  // marching-animation frame (0/1)
    private int _stepCounter;
    private double _playerX = FieldW / 2;
    private double? _bulletX, _bulletY;                  // the single in-flight player shot
    private readonly List<(double X, double Y)> _bombs = new();
    private bool _left, _right;
    private int _score, _lives, _wave;
    private int _hitFlash;                               // ticks of red on the cannon after a hit
    private double _pulse;                               // drives the "press space" prompt shimmer
    private DispatcherTimer? _timer;
    private readonly Random _rng = new();
    private readonly (double X, double Y, double R)[] _stars;

    // Classic 11×8 invader, two marching frames (arms down / arms up).
    private static readonly string[] InvaderA =
    {
        "00100000100",
        "00010001000",
        "00111111100",
        "01101110110",
        "11111111111",
        "10111111101",
        "10100000101",
        "00011011000",
    };
    private static readonly string[] InvaderB =
    {
        "00100000100",
        "10010001001",
        "10111111101",
        "11101110111",
        "11111111111",
        "01111111110",
        "00100000100",
        "01000000010",
    };
    // The player's cannon (13×8).
    private static readonly string[] Cannon =
    {
        "0000001000000",
        "0000011100000",
        "0000011100000",
        "0111111111110",
        "1111111111111",
        "1111111111111",
        "1111111111111",
        "1111111111111",
    };

    public InvadersField()
    {
        Width = FieldW;
        Height = FieldH;
        Focusable = true;

        // A fixed, deterministic starfield behind the action (generated once — the RNG seed doesn't matter).
        _stars = new (double, double, double)[46];
        for (int i = 0; i < _stars.Length; i++)
            _stars[i] = (_rng.NextDouble() * FieldW, TopBar + _rng.NextDouble() * (FieldH - TopBar - GroundGap),
                         0.6 + _rng.NextDouble() * 1.1);

        ResetWave();    // seed a full swarm so the title screen already reads as a game
    }

    protected override Size MeasureOverride(Size availableSize) => new(FieldW, FieldH);

    /// <summary>Freezes the field at a representative mid-play frame for headless snapshots (timers don't
    /// tick under the render harness, so the scene is posed by hand).</summary>
    internal void SnapshotPlaying()
    {
        StartGame();
        _alive[0, 2] = _alive[1, 5] = _alive[3, 0] = false;   // a few already cleared
        _score = 120;
        _bulletX = _playerX;
        _bulletY = FieldH * 0.52;
        _bombs.Add((_formX + 3 * PitchX, FieldH * 0.46));
        _bombs.Add((_formX + 6 * PitchX, FieldH * 0.62));
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

    // ── Input ── Returns true when the key was the game's (so the window can mark it handled).
    public bool HandleKey(Key key, bool down)
    {
        switch (key)
        {
            case Key.Left or Key.A: _left = down; return true;
            case Key.Right or Key.D: _right = down; return true;
            case Key.Space or Key.Up or Key.W or Key.Enter:
                if (down) OnFire();
                return true;
        }
        return false;
    }

    // Space is both "start / restart" and "fire" — from any non-playing screen it (re)starts the game,
    // while playing it launches a shot if none is already in flight (classic one-at-a-time).
    private void OnFire()
    {
        if (_phase != Phase.Playing) { StartGame(); return; }
        if (_bulletY is null)
        {
            _bulletX = _playerX;
            _bulletY = PlayerTop - BulletH;
        }
    }

    private void StartGame()
    {
        _score = 0;
        _lives = 3;
        _wave = 1;
        ResetWave();
        _phase = Phase.Playing;
    }

    private void ResetWave()
    {
        for (int r = 0; r < Rows; r++)
            for (int c = 0; c < Cols; c++)
                _alive[r, c] = true;

        double formationW = (Cols - 1) * PitchX + InvW;
        _formX = (FieldW - formationW) / 2;
        _formY = TopBar + 22;
        _dir = 1;
        _frame = 0;
        _stepCounter = 0;
        _playerX = FieldW / 2;
        _bulletX = _bulletY = null;
        _bombs.Clear();
        _hitFlash = 0;
    }

    // ── The loop ──
    private void Tick()
    {
        _pulse += 0.12;
        if (_hitFlash > 0) _hitFlash--;
        if (_phase == Phase.Playing) UpdatePlaying();
        InvalidateVisual();
    }

    private void UpdatePlaying()
    {
        // Cannon slide.
        if (_left) _playerX -= PlayerSpeed;
        if (_right) _playerX += PlayerSpeed;
        _playerX = Math.Clamp(_playerX, PlayerW / 2 + 4, FieldW - PlayerW / 2 - 4);

        // Player shot.
        if (_bulletY is double by)
        {
            by -= BulletSpeed;
            if (by < TopBar - BulletH) { _bulletX = _bulletY = null; }
            else { _bulletY = by; CheckBulletHits(); }
        }

        // Bombs fall; drop any off the bottom, and end the run if one catches the cannon.
        var cannon = new Rect(_playerX - PlayerW / 2, PlayerTop, PlayerW, PlayerH);
        for (int i = _bombs.Count - 1; i >= 0; i--)
        {
            var b = _bombs[i];
            b.Y += BombSpeed;
            var rect = new Rect(b.X - BombW / 2, b.Y, BombW, BombH);
            if (b.Y > FieldH) { _bombs.RemoveAt(i); continue; }
            if (rect.Intersects(cannon))
            {
                _bombs.RemoveAt(i);
                _hitFlash = 14;
                if (--_lives <= 0) { _phase = Phase.Lost; return; }
                continue;
            }
            _bombs[i] = b;
        }

        // Stepped march — faster as the swarm thins (and per wave).
        if (++_stepCounter >= StepInterval())
        {
            _stepCounter = 0;
            AdvanceInvaders();
        }

        if (AliveCount() == 0) { _phase = Phase.Won; }
    }

    private int StepInterval() => Math.Max(2, 2 + AliveCount() / 4 - _wave);

    private void AdvanceInvaders()
    {
        if (!AliveBounds(out int minCol, out int maxCol, out int maxRow)) return;

        double leftX = _formX + minCol * PitchX;
        double rightX = _formX + maxCol * PitchX + InvW;
        bool hitEdge = (_dir > 0 && rightX + StepX > FieldW - MarginX)
                    || (_dir < 0 && leftX - StepX < MarginX);

        if (hitEdge) { _formY += DropY; _dir = -_dir; }
        else { _formX += _dir * StepX; }

        _frame ^= 1;
        MaybeDropBomb();

        // Landed on the cannon's row → overrun, game over.
        if (_formY + maxRow * PitchY + InvH >= PlayerTop)
            _phase = Phase.Lost;
    }

    private void MaybeDropBomb()
    {
        if (_bombs.Count >= 4 || _rng.NextDouble() > 0.4) return;

        // Bomb from a random column's lowest survivor.
        Span<int> cols = stackalloc int[Cols];
        int n = 0;
        for (int c = 0; c < Cols; c++)
            for (int r = Rows - 1; r >= 0; r--)
                if (_alive[r, c]) { cols[n++] = c; break; }
        if (n == 0) return;

        int col = cols[_rng.Next(n)];
        for (int r = Rows - 1; r >= 0; r--)
            if (_alive[r, col])
            {
                var box = InvRect(r, col);
                _bombs.Add((box.X + box.Width / 2, box.Bottom));
                break;
            }
    }

    private void CheckBulletHits()
    {
        if (_bulletX is not double bx || _bulletY is not double byy) return;
        var shot = new Rect(bx - BulletW / 2, byy, BulletW, BulletH);
        for (int r = 0; r < Rows; r++)
            for (int c = 0; c < Cols; c++)
                if (_alive[r, c] && shot.Intersects(InvRect(r, c)))
                {
                    _alive[r, c] = false;
                    _score += (Rows - r) * 10;             // top rows are worth more
                    _bulletX = _bulletY = null;
                    return;
                }
    }

    private Rect InvRect(int r, int c) => new(_formX + c * PitchX, _formY + r * PitchY, InvW, InvH);

    private int AliveCount()
    {
        int n = 0;
        foreach (var a in _alive) if (a) n++;
        return n;
    }

    private bool AliveBounds(out int minCol, out int maxCol, out int maxRow)
    {
        minCol = Cols; maxCol = -1; maxRow = -1;
        for (int r = 0; r < Rows; r++)
            for (int c = 0; c < Cols; c++)
                if (_alive[r, c])
                {
                    if (c < minCol) minCol = c;
                    if (c > maxCol) maxCol = c;
                    if (r > maxRow) maxRow = r;
                }
        return maxCol >= 0;
    }

    // ── Rendering ──
    public override void Render(DrawingContext ctx)
    {
        double w = Bounds.Width, h = Bounds.Height;
        ctx.FillRectangle(Palette.OverlaySurfaceBrush, new Rect(0, 0, w, h));

        // Starfield + ground line for a bit of arcade atmosphere.
        foreach (var s in _stars)
            ctx.FillRectangle(Palette.MutedBrush, new Rect(s.X, s.Y, s.R, s.R));
        double groundY = FieldH - GroundGap + PlayerH + 4;
        ctx.DrawLine(new Pen(Palette.AccentBrush, 1.5), new Point(0, groundY), new Point(w, groundY));

        DrawTopBar(ctx, w);

        // On every screen but the win card, the swarm is visible (frozen on the title / game-over frame).
        if (_phase != Phase.Won)
            DrawInvaders(ctx, _phase == Phase.Playing ? _frame : 0);

        if (_phase is Phase.Title or Phase.Playing)
            DrawCannon(ctx);

        if (_phase == Phase.Playing)
        {
            if (_bulletX is double bx && _bulletY is double byy)
                ctx.FillRectangle(Palette.FgBrush, new Rect(bx - BulletW / 2, byy, BulletW, BulletH));
            foreach (var b in _bombs)
                ctx.FillRectangle(Palette.ErrorBrush, new Rect(b.X - BombW / 2, b.Y, BombW, BombH));
        }

        switch (_phase)
        {
            case Phase.Title:
                DrawOverlayCard(ctx, w, h, "PERCH INVADERS",
                    "Arrow keys move  ·  Space fires", "Press Space to start");
                break;
            case Phase.Won:
                DrawOverlayCard(ctx, w, h, "SWARM CLEARED",
                    $"Score {_score}", "Space to play again");
                break;
            case Phase.Lost:
                DrawOverlayCard(ctx, w, h, "GAME OVER",
                    $"Score {_score}", "Space to play again");
                break;
        }
    }

    private void DrawTopBar(DrawingContext ctx, double w)
    {
        double midY = TopBar / 2;
        OverlayDraw.TextLeftMid(ctx, OverlayDraw.Text($"SCORE  {_score:0000}", 13, Palette.FgBrush, FontWeight.Bold), 14, midY);

        if (_phase == Phase.Playing)
        {
            // Remaining lives as little cannon glyphs, right-aligned.
            double x = w - 14 - 22;
            for (int i = 0; i < Math.Max(0, _lives); i++)
            {
                DrawBitSprite(ctx, Cannon, new Rect(x, midY - 7, 18, 12), Palette.AccentBrush);
                x -= 24;
            }
        }
    }

    private void DrawInvaders(DrawingContext ctx, int frame)
    {
        var sprite = frame == 0 ? InvaderA : InvaderB;
        for (int r = 0; r < Rows; r++)
        {
            var brush = RowBrush(r);
            for (int c = 0; c < Cols; c++)
                if (_alive[r, c])
                    DrawBitSprite(ctx, sprite, InvRect(r, c), brush);
        }
    }

    // Rows tinted with Perch's fixed status hues, top (most dangerous / most points) to bottom.
    private static IBrush RowBrush(int row) => (row % 4) switch
    {
        0 => Palette.ErrorBrush,
        1 => Palette.AttentionBrush,
        2 => Palette.AwaitingBrush,
        _ => Palette.RunningBrush,
    };

    private void DrawCannon(DrawingContext ctx)
    {
        var brush = _hitFlash > 0 && (_hitFlash / 3) % 2 == 0 ? Palette.ErrorBrush : Palette.AccentBrush;
        DrawBitSprite(ctx, Cannon, new Rect(_playerX - PlayerW / 2, PlayerTop, PlayerW, PlayerH), brush);
    }

    private static void DrawBitSprite(DrawingContext ctx, string[] rows, Rect box, IBrush brush)
    {
        int rN = rows.Length, cN = rows[0].Length;
        double cw = box.Width / cN, ch = box.Height / rN;
        for (int r = 0; r < rN; r++)
            for (int c = 0; c < cN; c++)
                if (rows[r][c] == '1')
                    ctx.FillRectangle(brush, new Rect(box.X + c * cw, box.Y + r * ch, cw + 0.5, ch + 0.5));
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
