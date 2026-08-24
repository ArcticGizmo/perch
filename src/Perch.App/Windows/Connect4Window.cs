using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Perch.Games;
using Perch.Social;
using Perch.Avalonia.Rendering;
using Perch.Avalonia.Theming;
using Perch.Theming;

namespace Perch.Avalonia.Windows;

/// <summary>
/// The fourth secret toy reached from the arcade chooser (see <c>ArcadeMenuWindow</c>): Connect 4. Three modes:
/// <b>vs Computer</b> (you are red, against the deterministic <see cref="Connect4Ai"/>), <b>2 Player</b>
/// hot-seat on one machine, and — when Social is signed in — <b>Online</b>, playing a friend over the Supabase
/// backend. Online games drive the very same <see cref="Connect4Game"/> engine from moves exchanged through
/// <see cref="ISocialClient"/>; the server is authoritative, so this window only proposes a column and renders
/// the state it gets back (see docs/connect4-plan.md).
///
/// Owner-drawn over the shared <see cref="OverlayDraw"/> / <see cref="Palette"/> vocabulary like its siblings.
/// </summary>
internal sealed class Connect4Window : Window
{
    private readonly Connect4Board _board = new();
    private readonly ISocialClient? _social;
    private readonly Connect4OnlineController? _online;
    private readonly Action<GameSummary>? _onRematch;
    private Guid _meId;
    private Profile? _opponent;
    private bool _rematchInFlight;

    /// <summary>Local play (vs computer / hot-seat). If <paramref name="social"/> is signed in, the board also
    /// offers a "Play a friend" pill that opens the online lobby.</summary>
    public Connect4Window(ISocialClient? social = null)
    {
        _social = social;
        Chrome("Perch Connect 4");

        bool onlineAvailable = social is { Current.SignedIn: true, Current.Me: not null };
        _board.SetOnlineAvailable(onlineAvailable);
        _board.PlayAFriendRequested += OpenLobby;
    }

    /// <summary>Online play: an existing game against a friend. The board starts in Online mode and reconciles
    /// against the authoritative server state via the controller. <paramref name="onRematch"/>, when supplied,
    /// takes over what "Rematch" does with the freshly created game (the debug tester uses it to reopen both
    /// boards); when null, a rematch just opens your own board for the new game.</summary>
    public Connect4Window(ISocialClient social, Guid meId, GameSummary game, Action<GameSummary>? onRematch = null)
    {
        _social = social;
        _meId = meId;
        _opponent = game.Opponent(meId);
        _onRematch = onRematch;
        Chrome(_opponent is null ? "Connect 4 — online" : $"Connect 4 — @{_opponent.Handle}");

        _board.EnterOnlineMode(meId);
        _online = new Connect4OnlineController(social, game.Id);
        _online.Updated += _board.ApplyOnlineState;
        _online.Failed += _board.SetOnlineError;
        _board.OnlineDropRequested += col => _ = _online.DropAsync(col);
        _board.ResignRequested += () => _ = _online.ResignAsync();
        _board.RematchRequested += Rematch;
    }

    // A finished online game's "Rematch" starts a fresh game with the same opponent. The in-flight guard stops
    // a double-click from creating two games. If an onRematch hook was supplied (the debug tester), it decides
    // what to open with the new game (both boards); otherwise this opens your own board. Either way the current
    // window closes.
    private async void Rematch()
    {
        if (_social is null || _opponent is null || _rematchInFlight) return;
        _rematchInFlight = true;
        try
        {
            var next = await _social.CreateGameAsync(_opponent.Id);
            if (_onRematch is not null) _onRematch(next);           // e.g. the tester reopens both boards
            else new Connect4Window(_social, _meId, next).Show();   // real play: your board; opponent opens theirs
            Close();
        }
        catch { _rematchInFlight = false; /* leave the finished game open if the rematch can't be created */ }
    }

    private void Chrome(string title)
    {
        Title = title;
        CanResize = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Palette.OverlaySurfaceBrush;
        Content = _board;
    }

    private void OpenLobby()
    {
        if (_social is null) return;
        new Connect4LobbyWindow(_social, OpenOnlineGame).Show(this);
    }

    private void OpenOnlineGame(GameSummary game)
    {
        if (_social?.Current.Me is not { } me) return;
        new Connect4Window(_social, me.Id, game).Show(this);
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        _board.Focus();
        _board.Begin();
        _online?.Start();
    }

    protected override void OnClosed(EventArgs e)
    {
        _board.Stop();
        _online?.Dispose();
        base.OnClosed(e);
    }

    // Keys handled at the window so input works regardless of focus. Esc closes; the rest is the game's.
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { Close(); e.Handled = true; return; }
        if (_board.HandleKey(e.Key)) e.Handled = true;
        base.OnKeyDown(e);
    }
}

/// <summary>The board itself: the header (title, mode pills / opponent + resign, turn indicator), the owner-drawn
/// 7×6 grid with a falling-disc animation and a hover "ghost" of where the next disc lands, and a footer hint.
/// Local rules and win detection come from <see cref="Connect4Game"/>; Online mode is driven externally — the
/// window feeds it authoritative <see cref="GameState"/> via <see cref="ApplyOnlineState"/> and it raises
/// <see cref="OnlineDropRequested"/> for the window to send.</summary>
internal sealed class Connect4Board : Control
{
    // ── Geometry (DIP), all fixed so layout is a handful of constants ──
    private const double BoardW = 520;
    private const int Cols = Connect4Game.Cols, Rows = Connect4Game.Rows;
    private const double Cell = 62, Gap = 8;
    private const double GridW = Cols * Cell + (Cols - 1) * Gap;
    private const double Pad = (BoardW - GridW) / 2;
    // A gap above the grid reserved for the drop-indicator arrow that marks the selected column.
    private const double GridTop = 172;
    private const double GridH = Rows * Cell + (Rows - 1) * Gap;
    private const double GridBottom = GridTop + GridH;
    private const double BoardH = GridBottom + 58;
    private const double Disc = Cell - 12;               // disc diameter, leaving a rim inside each hole
    private const int TickMs = 16;

    // The human is red in vs-Computer; the computer plays yellow.
    private const Connect4Disc AiDisc = Connect4Disc.Yellow;

    private enum GameMode { VsComputer, TwoPlayer, Online }

    // ── Events (online) ──
    public event Action? PlayAFriendRequested;   // local mode: the "Play a friend" pill
    public event Action<int>? OnlineDropRequested;
    public event Action? ResignRequested;
    public event Action? RematchRequested;       // online mode: the "Rematch" pill on a finished game

    // ── State ──
    private Connect4Game _game = new();
    private GameMode _mode = GameMode.VsComputer;
    private bool _onlineAvailable;
    private int _cursor = Cols / 2;                       // keyboard-selected column
    private int _hoverCol = -1;                           // mouse-hovered column, or -1
    private double _pulse;                                // drives the win-line glow + end prompt shimmer

    // Falling-disc animation: while active, input is blocked and the disc is drawn mid-fall rather than settled.
    private bool _falling;
    private int _fallCol, _fallRow;
    private Connect4Disc _fallDisc;
    private double _fallY, _fallVel;

    // Computer "thinking" delay so its move doesn't land instantly; counts ticks down to 0, then it plays.
    private int _aiThinkTicks;

    // Online state (authoritative, from the server).
    private Guid _onlineMeId;
    private GameSummary? _onlineSummary;
    private bool _onlineBusy;                             // a move is in flight — input disabled until it resolves
    private bool _onlineApplied;                          // have we applied at least one server state?
    private int _lastMoveCount;
    private string? _toast;                               // transient message (e.g. a rejected move)
    private int _toastTicks;

    private DispatcherTimer? _timer;

    // Header hit-rects, laid out from the fixed geometry per mode.
    private Rect _vsCpuPill, _twoPlayerPill, _playFriendPill, _resignPill;

    public Connect4Board()
    {
        Width = BoardW;
        Height = BoardH;
        Focusable = true;
        LayoutHeader();
    }

    protected override Size MeasureOverride(Size availableSize) => new(BoardW, BoardH);

    // ── Online surface (driven by the window) ──
    public void SetOnlineAvailable(bool available) { _onlineAvailable = available; LayoutHeader(); InvalidateVisual(); }

    public void EnterOnlineMode(Guid meId)
    {
        _mode = GameMode.Online;
        _onlineMeId = meId;
        _onlineApplied = false;
        _lastMoveCount = 0;
        LayoutHeader();
        InvalidateVisual();
    }

    /// <summary>Applies an authoritative game state from the server: rebuilds the board from the move list and,
    /// if exactly one move was appended since the last state, animates that disc dropping (so both your own and
    /// the opponent's moves land with the falling animation).</summary>
    public void ApplyOnlineState(GameState state)
    {
        _onlineSummary = state.Summary;
        var g = state.ToGame();
        bool animate = _onlineApplied && g.MoveCount == _lastMoveCount + 1 && state.Moves.Count > 0;
        _game = g;
        _lastMoveCount = g.MoveCount;
        _onlineApplied = true;
        _onlineBusy = false;
        _toast = null;

        if (animate)
        {
            int col = state.Moves[^1];
            int row = TopFilledRow(col);
            if (row >= 0)
            {
                _falling = true; _fallCol = col; _fallRow = row; _fallDisc = _game.CellAt(col, row);
                _fallY = GridTop - Cell; _fallVel = 0;
            }
        }
        InvalidateVisual();
    }

    /// <summary>Surfaces a server rejection (e.g. "not your turn") as a transient message and re-enables input.</summary>
    public void SetOnlineError(string message)
    {
        _onlineBusy = false;
        _toast = message;
        _toastTicks = 220;   // ~3.5s at 16ms
        InvalidateVisual();
    }

    private int TopFilledRow(int col)
    {
        for (int r = Rows - 1; r >= 0; r--)
            if (_game.CellAt(col, r) != Connect4Disc.None) return r;
        return -1;
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
        t.Tick += (_, _) => { Tick(); InvalidateVisual(); };
        return t;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _timer?.Stop();
        base.OnDetachedFromVisualTree(e);
    }

    private void Tick()
    {
        _pulse += 0.12;
        if (_toastTicks > 0 && --_toastTicks == 0) _toast = null;

        if (_falling)
        {
            _fallVel += 2.4;                             // gravity
            _fallY += _fallVel;
            double target = CellY(_fallRow);
            if (_fallY >= target) { _fallY = target; _falling = false; OnLanded(); }
            return;                                      // nothing else moves while a disc is in flight
        }

        // Computer's turn (vs-Computer only): after a short think, play its chosen column.
        if (_aiThinkTicks > 0 && --_aiThinkTicks == 0)
        {
            int col = Connect4Ai.SuggestMove(_game);
            if (col >= 0) DoDrop(col);
        }
    }

    // Called once a dropped disc has settled: in vs-Computer mode, hand the turn to the bot; other modes do
    // nothing (2-player alternates by itself, Online is driven by the server).
    private void OnLanded()
    {
        if (_mode != GameMode.VsComputer || _game.IsOver) return;
        if (_game.Turn == AiDisc) _aiThinkTicks = 28;   // ~0.45s at 16ms
    }

    // ── Input ──
    public bool HandleKey(Key key)
    {
        if (_mode == GameMode.Online) return HandleKeyOnline(key);

        // When a local game is over, any confirm key starts a fresh one.
        if (_game.IsOver && key is Key.Enter or Key.Return or Key.Space or Key.R) { NewGame(); return true; }

        switch (key)
        {
            case Key.M or Key.Tab:
                ToggleMode(); return true;
            case Key.R:
                NewGame(); return true;
            // Arrow keys take over the highlight from the mouse: clear the hover so the cursor drives it.
            case Key.Left or Key.A:
                _hoverCol = -1; _cursor = (_cursor + Cols - 1) % Cols; InvalidateVisual(); return true;
            case Key.Right or Key.D:
                _hoverCol = -1; _cursor = (_cursor + 1) % Cols; InvalidateVisual(); return true;
            case Key.Down or Key.S or Key.Enter or Key.Return or Key.Space:
                TryHumanDrop(_cursor); return true;
        }
        if (key is >= Key.D1 and <= Key.D7) { TryHumanDrop(key - Key.D1); return true; }
        if (key is >= Key.NumPad1 and <= Key.NumPad7) { TryHumanDrop(key - Key.NumPad1); return true; }
        return false;
    }

    private bool HandleKeyOnline(Key key)
    {
        // On a finished game, a confirm key asks for a rematch.
        if (_onlineSummary is { Status: not GameStatus.InProgress } && key is Key.Enter or Key.Return or Key.Space or Key.R)
        {
            RematchRequested?.Invoke();
            return true;
        }
        switch (key)
        {
            case Key.Left or Key.A:
                _hoverCol = -1; _cursor = (_cursor + Cols - 1) % Cols; InvalidateVisual(); return true;
            case Key.Right or Key.D:
                _hoverCol = -1; _cursor = (_cursor + 1) % Cols; InvalidateVisual(); return true;
            case Key.Down or Key.S or Key.Enter or Key.Return or Key.Space:
                TryHumanDrop(_cursor); return true;
        }
        if (key is >= Key.D1 and <= Key.D7) { TryHumanDrop(key - Key.D1); return true; }
        if (key is >= Key.NumPad1 and <= Key.NumPad7) { TryHumanDrop(key - Key.NumPad1); return true; }
        return false;
    }

    private bool HumanCanMoveNow()
    {
        if (_falling) return false;
        if (_mode == GameMode.Online)
            return !_onlineBusy && _onlineSummary is { Status: GameStatus.InProgress } s && s.IsTurnOf(_onlineMeId);
        if (_game.IsOver || _aiThinkTicks != 0) return false;
        return _mode == GameMode.TwoPlayer || _game.Turn != AiDisc;
    }

    private void TryHumanDrop(int col)
    {
        if (!HumanCanMoveNow()) return;
        if (!_game.CanDrop(col)) return;   // full/invalid column: nothing to do
        if (_mode == GameMode.Online)
        {
            _onlineBusy = true;            // lock input until the server confirms/rejects
            _toast = null;
            InvalidateVisual();
            OnlineDropRequested?.Invoke(col);
            return;
        }
        DoDrop(col);
    }

    // The local funnel: validate, apply to the engine, and start the fall animation from above the board.
    private void DoDrop(int col)
    {
        if (!_game.CanDrop(col)) return;
        int row = _game.Drop(col);
        if (row < 0) return;

        _falling = true;
        _fallCol = col;
        _fallRow = row;
        _fallDisc = _game.CellAt(col, row);
        _fallY = GridTop - Cell;
        _fallVel = 0;
        InvalidateVisual();
    }

    private void NewGame()
    {
        _game.Reset();
        _falling = false;
        _aiThinkTicks = 0;
        _cursor = Cols / 2;
        InvalidateVisual();
    }

    private void ToggleMode()
    {
        _mode = _mode == GameMode.VsComputer ? GameMode.TwoPlayer : GameMode.VsComputer;
        LayoutHeader();
        NewGame();                                        // switching modes starts a clean game
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        int col = ColumnAt(e.GetPosition(this));
        if (col != _hoverCol) { _hoverCol = col; InvalidateVisual(); }
        base.OnPointerMoved(e);
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        if (_hoverCol != -1) { _hoverCol = -1; InvalidateVisual(); }
        base.OnPointerExited(e);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        var p = e.GetPosition(this);

        if (_mode == GameMode.Online)
        {
            // Top-right pill: Resign while the game is live, Rematch once it's over.
            if (_onlineSummary is { } s && _resignPill.Contains(p))
            {
                if (s.Status == GameStatus.InProgress) ResignRequested?.Invoke();
                else RematchRequested?.Invoke();
                e.Handled = true; return;
            }
            int oc = ColumnAt(p);
            if (oc >= 0) { TryHumanDrop(oc); e.Handled = true; return; }
            base.OnPointerPressed(e);
            return;
        }

        if (_vsCpuPill.Contains(p)) { if (_mode != GameMode.VsComputer) ToggleMode(); e.Handled = true; return; }
        if (_twoPlayerPill.Contains(p)) { if (_mode != GameMode.TwoPlayer) ToggleMode(); e.Handled = true; return; }
        if (_onlineAvailable && _playFriendPill.Contains(p)) { PlayAFriendRequested?.Invoke(); e.Handled = true; return; }

        if (_game.IsOver) { NewGame(); e.Handled = true; return; }

        int col = ColumnAt(p);
        if (col >= 0) { TryHumanDrop(col); e.Handled = true; return; }
        base.OnPointerPressed(e);
    }

    // The column a point maps to, or -1 if outside the board. Divides the whole grid width by the column pitch
    // so the gaps *between* holes still resolve to a column (the one to their left) — otherwise a pointer in a
    // gap reads as "no column" and the highlight snaps back to the keyboard cursor.
    private static int ColumnAt(Point p)
    {
        if (p.Y < GridTop - Cell || p.Y > GridBottom) return -1;
        if (p.X < Pad || p.X > Pad + GridW) return -1;
        int c = (int)((p.X - Pad) / (Cell + Gap));
        return Math.Clamp(c, 0, Cols - 1);
    }

    // ── Layout ──
    private void LayoutHeader()
    {
        const double ph = 30, y = 58, gap = 12;
        _vsCpuPill = _twoPlayerPill = _playFriendPill = _resignPill = default;

        if (_mode == GameMode.Online)
        {
            _resignPill = new Rect(BoardW - 96, 20, 80, 28);
            return;
        }

        double cpuW = 128, twoW = 104, friendW = 140;
        double totalW = cpuW + gap + twoW + (_onlineAvailable ? gap + friendW : 0);
        double x = (BoardW - totalW) / 2;
        _vsCpuPill = new Rect(x, y, cpuW, ph);
        x += cpuW + gap;
        _twoPlayerPill = new Rect(x, y, twoW, ph);
        x += twoW + gap;
        if (_onlineAvailable) _playFriendPill = new Rect(x, y, friendW, ph);
    }

    private static double CellX(int col) => Pad + col * (Cell + Gap);
    private static double CellY(int row) => GridTop + (Rows - 1 - row) * (Cell + Gap);

    // ── Rendering ──
    public override void Render(DrawingContext ctx)
    {
        ctx.FillRectangle(Palette.OverlaySurfaceBrush, new Rect(0, 0, BoardW, BoardH));

        DrawHeader(ctx);
        DrawBoard(ctx);
        DrawDropPreview(ctx);   // after the board, so the column outline + landing ring aren't painted over
        DrawFooter(ctx);
    }

    private void DrawHeader(DrawingContext ctx)
    {
        var title = OverlayDraw.Text("PERCH CONNECT 4", 26, Palette.AccentBrush, FontWeight.Bold);
        ctx.DrawText(title, new Point((BoardW - title.Width) / 2, 22));

        if (_mode == GameMode.Online)
        {
            var opp = _onlineSummary?.Opponent(_onlineMeId);
            var vs = OverlayDraw.Text(opp is null ? "Online game" : $"vs @{opp.Handle}", 14, Palette.FgBrush, FontWeight.SemiBold);
            ctx.DrawText(vs, new Point((BoardW - vs.Width) / 2, 60));
            if (_onlineSummary is { } s)
                DrawPill(ctx, _resignPill, s.Status == GameStatus.InProgress ? "Resign" : "Rematch", active: false);
        }
        else
        {
            DrawPill(ctx, _vsCpuPill, "vs Computer", _mode == GameMode.VsComputer);
            DrawPill(ctx, _twoPlayerPill, "2 Player", _mode == GameMode.TwoPlayer);
            if (_onlineAvailable) DrawPill(ctx, _playFriendPill, "Play a friend", active: false);
        }

        DrawTurnLine(ctx);
    }

    private void DrawPill(DrawingContext ctx, Rect rect, string label, bool active)
    {
        var fill = active ? Palette.AccentBrush : Palette.ButtonBgBrush;
        var fg = active ? Palette.OnAccentBrush : Palette.MutedBrush;
        OverlayDraw.Panel(ctx, rect, fill, active ? null : new Pen(Palette.BorderBrush, 1), rect.Height / 2);
        var ft = OverlayDraw.Text(label, 13, fg, FontWeight.SemiBold);
        ctx.DrawText(ft, new Point(rect.X + (rect.Width - ft.Width) / 2, rect.Y + (rect.Height - ft.Height) / 2));
    }

    // The line under the header: whose turn it is (with a disc swatch), or the end-of-game verdict.
    private void DrawTurnLine(DrawingContext ctx)
    {
        const double y = 100;
        string text;
        IBrush brush;
        Connect4Disc swatch = Connect4Disc.None;

        if (_mode == GameMode.Online)
        {
            (text, brush, swatch) = OnlineStatusLine();
        }
        else switch (_game.Status)
        {
            case Connect4Status.RedWon:
                (text, brush, swatch) = (WinnerLabel(Connect4Disc.Red), DiscBrush(Connect4Disc.Red), Connect4Disc.Red);
                break;
            case Connect4Status.YellowWon:
                (text, brush, swatch) = (WinnerLabel(Connect4Disc.Yellow), DiscBrush(Connect4Disc.Yellow), Connect4Disc.Yellow);
                break;
            case Connect4Status.Draw:
                (text, brush) = ("It's a draw", Palette.MutedBrush);
                break;
            default:
                swatch = _game.Turn;
                text = TurnLabel(_game.Turn);
                brush = Palette.FgBrush;
                break;
        }

        var ft = OverlayDraw.Text(text, 15, brush, FontWeight.SemiBold);
        double swatchW = swatch == Connect4Disc.None ? 0 : 20;
        double x = (BoardW - (ft.Width + swatchW)) / 2;

        bool ended = IsGameOver();
        double a = ended ? 0.6 + 0.4 * Math.Abs(Math.Sin(_pulse)) : 1.0;
        using (ctx.PushOpacity(a))
        {
            if (swatch != Connect4Disc.None)
            {
                ctx.DrawEllipse(DiscBrush(swatch), null, new Point(x + 7, y + ft.Height / 2), 7, 7);
                x += swatchW;
            }
            ctx.DrawText(ft, new Point(x, y));
        }
    }

    private bool IsGameOver() =>
        _mode == GameMode.Online ? _onlineSummary is { Status: not GameStatus.InProgress } : _game.IsOver;

    // (text, colour, disc swatch) for the online turn/verdict line.
    private (string, IBrush, Connect4Disc) OnlineStatusLine()
    {
        if (_onlineSummary is not { } s) return ("Connecting…", Palette.MutedBrush, Connect4Disc.None);
        var mySeat = s.Seat(_onlineMeId);
        var opp = s.Opponent(_onlineMeId);
        switch (s.Status)
        {
            case GameStatus.RedWon:
            case GameStatus.YellowWon:
                var winner = s.Status == GameStatus.RedWon ? Connect4Disc.Red : Connect4Disc.Yellow;
                bool iWon = winner == mySeat;
                return (iWon ? "You win!" : $"@{opp?.Handle} wins", DiscBrush(winner), winner);
            case GameStatus.Draw:
                return ("It's a draw", Palette.MutedBrush, Connect4Disc.None);
            case GameStatus.Abandoned:
                return ("Game abandoned", Palette.MutedBrush, Connect4Disc.None);
            default:
                return s.IsTurnOf(_onlineMeId)
                    ? ("Your turn", Palette.FgBrush, mySeat)
                    : ($"Waiting for @{opp?.Handle}…", Palette.MutedBrush, Connect4Game.Other(mySeat));
        }
    }

    private string TurnLabel(Connect4Disc turn)
    {
        if (_mode == GameMode.TwoPlayer)
            return turn == Connect4Disc.Red ? "Red's turn" : "Yellow's turn";
        return turn == AiDisc ? "Computer is thinking…" : "Your turn";
    }

    private string WinnerLabel(Connect4Disc winner)
    {
        if (_mode == GameMode.VsComputer)
            return winner == AiDisc ? "Computer wins" : "You win!";
        return winner == Connect4Disc.Red ? "Red wins!" : "Yellow wins!";
    }

    // A drop indicator on the active column — arrow in the lane, player-coloured outline, and a bright ring at
    // the landing cell — so you always know where the next disc goes. Only while the mover (you) can act.
    private void DrawDropPreview(DrawingContext ctx)
    {
        if (!HumanCanMoveNow()) return;
        int col = _hoverCol >= 0 ? _hoverCol : _cursor;
        if (col < 0 || col >= Cols) return;

        double cx = CellX(col);
        int row = _game.LandingRow(col);
        bool full = row < 0;
        var accent = full ? Palette.MutedBrush : DiscBrush(MoverDisc());

        DrawDropArrow(ctx, cx + Cell / 2, accent);

        var band = new Rect(cx - 4, GridTop - 4, Cell + 8, GridH + 8);
        OverlayDraw.Panel(ctx, band, null, new Pen(accent, 2), 10);

        if (!full)
        {
            var center = new Point(cx + Cell / 2, CellY(row) + Cell / 2);
            using (ctx.PushOpacity(0.30))
                ctx.DrawEllipse(accent, null, center, Disc / 2, Disc / 2);
            ctx.DrawEllipse(null, new Pen(accent, 3), center, Disc / 2, Disc / 2);
        }
    }

    // Whose disc the human is about to drop: online = my seat, vs-Computer = red, hot-seat = whoever's turn.
    private Connect4Disc MoverDisc() =>
        _mode == GameMode.Online ? (_onlineSummary?.Seat(_onlineMeId) ?? Connect4Disc.Red) : _game.Turn;

    private static void DrawDropArrow(DrawingContext ctx, double cx, IBrush brush)
    {
        const double halfW = 10, top = 138, bottom = 158;
        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            g.BeginFigure(new Point(cx - halfW, top), true);
            g.LineTo(new Point(cx + halfW, top));
            g.LineTo(new Point(cx, bottom));
            g.EndFigure(true);
        }
        ctx.DrawGeometry(brush, null, geo);
    }

    private void DrawBoard(DrawingContext ctx)
    {
        var frame = new Rect(Pad - 10, GridTop - 10, GridW + 20, GridH + 20);
        OverlayDraw.Panel(ctx, frame, Palette.ButtonBgBrush, new Pen(Palette.BorderBrush, 1), 16);

        for (int c = 0; c < Cols; c++)
            for (int r = 0; r < Rows; r++)
            {
                var center = new Point(CellX(c) + Cell / 2, CellY(r) + Cell / 2);
                var disc = _game.CellAt(c, r);

                ctx.DrawEllipse(Palette.OverlaySurfaceBrush, null, center, Disc / 2 + 3, Disc / 2 + 3);   // hole
                if (disc != Connect4Disc.None && !(_falling && c == _fallCol && r == _fallRow))
                    DrawDisc(ctx, center, disc);
            }

        if (_falling)
            DrawDisc(ctx, new Point(CellX(_fallCol) + Cell / 2, _fallY + Cell / 2), _fallDisc);

        if (_game.WinningLine.Count > 0)
        {
            double glow = 0.5 + 0.5 * Math.Abs(Math.Sin(_pulse));
            var pen = new Pen(new SolidColorBrush(Palette.Active.TextPrimary.ToColor()), 3);
            using (ctx.PushOpacity(glow))
                foreach (var (c, r) in _game.WinningLine)
                    ctx.DrawEllipse(null, pen, new Point(CellX(c) + Cell / 2, CellY(r) + Cell / 2), Disc / 2 + 1, Disc / 2 + 1);
        }
    }

    private static void DrawDisc(DrawingContext ctx, Point center, Connect4Disc disc)
    {
        ctx.DrawEllipse(DiscBrush(disc), null, center, Disc / 2, Disc / 2);
        var ring = new Pen(new SolidColorBrush(Colors.White, 0.18), 2);
        ctx.DrawEllipse(null, ring, center, Disc / 2 - 4, Disc / 2 - 4);
    }

    // Red uses the reliable red status hue, yellow the awaiting hue — both resolve through the active theme.
    private static IBrush DiscBrush(Connect4Disc disc) =>
        disc == Connect4Disc.Red ? Palette.ErrorBrush : Palette.AwaitingBrush;

    private void DrawFooter(DrawingContext ctx)
    {
        // A transient toast (server rejection) takes precedence over the hint line.
        if (_toast is { } toast)
        {
            var tf = OverlayDraw.Text(toast, 12, Palette.WarnBrush, FontWeight.SemiBold);
            ctx.DrawText(tf, new Point((BoardW - tf.Width) / 2, GridBottom + 24));
            return;
        }

        string text;
        bool prompt;
        if (_mode == GameMode.Online)
        {
            if (_onlineSummary is not { Status: GameStatus.InProgress }) { text = "Game over — Rematch to play again"; prompt = true; }
            else if (_onlineBusy) { text = "Sending…"; prompt = false; }
            else if (_onlineSummary.IsTurnOf(_onlineMeId)) { text = "◀ ▶ pick column  ·  ↓ drop  ·  Resign to concede"; prompt = false; }
            else { text = "Waiting for your opponent to move…"; prompt = false; }
        }
        else if (_game.IsOver) { text = "Enter / click to play again"; prompt = true; }
        else { text = "◀ ▶ pick column  ·  ↓ drop  ·  M mode  ·  R restart"; prompt = false; }

        double a = prompt ? 0.55 + 0.45 * Math.Abs(Math.Sin(_pulse)) : 1.0;
        var ft = OverlayDraw.Text(text, 12, prompt ? Palette.FgBrush : Palette.MutedBrush,
            prompt ? FontWeight.SemiBold : FontWeight.Normal);
        using (ctx.PushOpacity(a))
            ctx.DrawText(ft, new Point((BoardW - ft.Width) / 2, GridBottom + 24));
    }

    /// <summary>Poses a representative mid-game board for headless snapshots (timers don't tick under the render
    /// harness): a handful of moves played, no animation in flight, with a column hovered so the drop-preview
    /// ghost and column wash render.</summary>
    internal void SnapshotPlaying()
    {
        _mode = GameMode.VsComputer;
        _onlineAvailable = true;   // show the "Play a friend" pill in the snapshot
        LayoutHeader();
        _game.Reset();
        foreach (int c in new[] { 3, 3, 4, 2, 4, 4, 2, 5 })
            _game.Drop(c);
        _falling = false;
        _aiThinkTicks = 0;
        _hoverCol = -1;
        _cursor = 2;
        InvalidateVisual();
    }

    /// <summary>Poses an in-progress online game (from the caller's point of view) for headless snapshots.</summary>
    internal void SnapshotOnline(GameState state, Guid meId)
    {
        EnterOnlineMode(meId);
        ApplyOnlineState(state);
        _hoverCol = -1;
        _cursor = 3;
    }
}
