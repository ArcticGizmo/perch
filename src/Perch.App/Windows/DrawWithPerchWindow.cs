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
/// The sixth secret toy reached from the arcade chooser (see <c>ArcadeMenuWindow</c>): Draw with Perch, a slow,
/// async draw-and-guess game against a friend over the Supabase backend. You're offered three words (easy →
/// hard), draw one with a limited palette, and your friend guesses by typing; then the roles swap. Online-only
/// (a bot can neither draw nor guess a freehand sketch). The server is authoritative — this window only proposes
/// a drawing/guess and renders the <see cref="DrawGameState"/> it gets back (see docs/draw-with-perch-plan.md).
///
/// Owner-drawn over the shared <see cref="OverlayDraw"/> / <see cref="Palette"/> vocabulary like its siblings.
/// </summary>
internal sealed class DrawWithPerchWindow : Window
{
    private readonly DrawBoard _board = new();
    private readonly ISocialClient _social;
    private DrawOnlineController? _online;
    private Guid _meId;
    private Guid _gameId;
    private Profile? _opponent;

    // Compose (challenge) lifecycle, mirroring Connect4Window: after the first drawing is submitted, an inbox
    // subscription (instant) and a fallback poll (robust) both watch for the game to appear, then GoLive.
    private readonly bool _composing;
    private Guid _composeRequestId;
    private HashSet<Guid> _preSendGameIds = new();
    private IDisposable? _inboxSub;
    private DispatcherTimer? _composePoll;
    private bool _wentLive;

    /// <summary>The online game this window is showing (empty for a challenge not yet accepted) — lets the App
    /// find the right board to float a nudge bubble beside.</summary>
    public Guid CurrentGameId => _wentLive || !_composing ? _gameId : Guid.Empty;

    /// <summary>Online play: an existing game against a friend.</summary>
    public DrawWithPerchWindow(ISocialClient social, Guid meId, DrawGameSummary game)
    {
        _social = social;
        _meId = meId;
        _gameId = game.Id;
        _opponent = game.Opponent(meId);
        Chrome(_opponent is null ? "Draw with Perch" : $"Draw with Perch — @{_opponent.Handle}");

        _board.EnterOnline(meId);
        _online = new DrawOnlineController(social, game.Id);
        WireLiveBoard();
    }

    /// <summary>Compose a challenge: pick a word, draw it, then the invite is sent carrying that drawing. Once the
    /// friend accepts, this same window becomes the live game (they're immediately on their turn to guess).</summary>
    public DrawWithPerchWindow(ISocialClient social, Guid meId, Profile opponent)
    {
        _social = social;
        _meId = meId;
        _opponent = opponent;
        _composing = true;
        Chrome($"Draw with Perch — challenge @{opponent.Handle}");

        _board.EnterCompose(meId, opponent);
        _board.ComposeSubmitRequested += SendChallenge;
        _board.CancelComposeRequested += CancelCompose;
    }

    private void WireLiveBoard()
    {
        if (_online is null) return;
        _online.Updated += _board.ApplyState;
        _online.Failed += _board.SetError;
        _board.RoundSubmitRequested += (d, w, h, s) => _ = _online!.SubmitRoundAsync(d, w, h, s);
        _board.GuessSubmitRequested += (rid, g) => _ = _online!.SubmitGuessAsync(rid, g);
        _board.GiveUpRequested += rid => _ = _online!.GiveUpAsync(rid);
        _board.ResignRequested += () => _ = _online!.ResignAsync();
        _board.NudgeRequested += Nudge;
    }

    // The first drawing is in: persist + broadcast the invite carrying it, then wait for acceptance.
    private async void SendChallenge(DrawDifficulty diff, string word, string hint, IReadOnlyList<DrawStroke> strokes)
    {
        if (_opponent is null) return;
        try
        {
            var existing = await _social.GetDrawGamesAsync();
            _preSendGameIds = existing.Where(g => g.Opponent(_meId)?.Id == _opponent.Id).Select(g => g.Id).ToHashSet();

            var req = await _social.RequestDrawGameAsync(_opponent.Id, diff, word, hint, strokes);
            _composeRequestId = req.Id;
            _board.MarkComposeSent();

            _inboxSub = _social.SubscribeInbox(OnInbox);
            _composePoll = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            _composePoll.Tick += (_, _) => _ = PollForAcceptedGame();
            _composePoll.Start();
        }
        catch (SocialException ex) { _board.MarkComposeDeclined(ex.Message); }
        catch { _board.MarkComposeDeclined("Couldn't send the challenge — try again."); }
    }

    private void OnInbox(InboxMessage m) => Dispatcher.UIThread.Post(() =>
    {
        if (_wentLive) return;
        if (m.Kind == InboxKind.DrawInviteAccepted && m.GameId is { } g) GoLive(g);
        else if (m.Kind == InboxKind.DrawInviteDeclined && m.RequestId == _composeRequestId)
            _board.MarkComposeDeclined($"@{_opponent?.Handle} declined the challenge.");
    });

    private async Task PollForAcceptedGame()
    {
        if (_wentLive || _opponent is null) return;
        try
        {
            var games = await _social.GetDrawGamesAsync();
            var fresh = games.FirstOrDefault(g =>
                g.Status == DrawGameStatus.InProgress && g.Opponent(_meId)?.Id == _opponent.Id && !_preSendGameIds.Contains(g.Id));
            if (fresh is not null) GoLive(fresh.Id);
        }
        catch { /* transient — the next tick / the inbox will catch it */ }
    }

    private void GoLive(Guid gameId)
    {
        if (_wentLive) return;
        _wentLive = true;
        _gameId = gameId;
        _composePoll?.Stop();
        _inboxSub?.Dispose();
        _inboxSub = null;

        _online = new DrawOnlineController(_social, gameId);
        WireLiveBoard();
        _online.Start();
    }

    private void CancelCompose()
    {
        if (_composeRequestId != Guid.Empty)
            _ = _social.DeclineDrawRequestAsync(_composeRequestId);
        Close();
    }

    private void Nudge()
    {
        if (_opponent is null || _gameId == Guid.Empty) return;
        _ = _social.SendNudgeAsync(_gameId, _opponent.Id);
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
        _composePoll?.Stop();
        _inboxSub?.Dispose();
        base.OnClosed(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { Close(); e.Handled = true; return; }
        if (_board.HandleKey(e.Key)) e.Handled = true;
        base.OnKeyDown(e);
    }
}

/// <summary>The board: a multi-screen owner-drawn control that walks a player through picking a word, drawing it,
/// guessing the opponent's drawing, and reviewing a resolved round. The drawing canvas captures freehand pointer
/// strokes into <see cref="DrawStroke"/>s (serialised by <see cref="DrawStrokeCodec"/> for the wire); guessing is
/// typed into an owner-drawn field. State transitions are driven by the authoritative <see cref="DrawGameState"/>
/// fed in via <see cref="ApplyState"/>; the board raises events for the window to send.</summary>
internal sealed class DrawBoard : Control
{
    // ── Geometry (DIP) ──
    private const double BoardW = 560, BoardH = 660;
    private const double CanvasPx = 400;
    private const double CanvasX = (BoardW - CanvasPx) / 2;
    private const double CanvasTop = 112;
    private const double CanvasBottom = CanvasTop + CanvasPx;
    private const double Scale = CanvasPx / DrawStrokeCodec.CanvasSize;   // 0..1000 space → screen px
    private const int TickMs = 16;

    private enum Screen { WordPick, Draw, Guess, Review, Waiting, Over }

    // ── Events (board → window) ──
    public event Action<DrawDifficulty, string, string, IReadOnlyList<DrawStroke>>? ComposeSubmitRequested;
    public event Action<DrawDifficulty, string, string, IReadOnlyList<DrawStroke>>? RoundSubmitRequested;
    public event Action<Guid, string>? GuessSubmitRequested;
    public event Action<Guid>? GiveUpRequested;
    public event Action? ResignRequested;
    public event Action? CancelComposeRequested;
    public event Action? NudgeRequested;

    // ── State ──
    private Screen _screen = Screen.WordPick;
    private Guid _meId;
    private Profile? _opponent;
    private bool _composeMode;
    private bool _composeSent;
    private DrawGameState? _state;
    private string? _toast;
    private int _toastTicks;
    private double _pulse;
    private bool _busy;                       // an action is in flight; input disabled until the next state
    private int _nudgeCooldownTicks;

    // Word pick.
    private DrawWords.Offer _offer;
    private DrawDifficulty _chosenDiff;
    private string _chosenWord = "";

    // Drawing buffer.
    private readonly List<DrawStroke> _strokes = new();
    private List<DrawPoint>? _current;        // the stroke being traced
    private int _color;                       // palette index
    private int _size = 1;                    // brush size index
    private bool _eraser;

    // Guessing.
    private Guid _guessRoundId;
    private string _guessInput = "";

    // Review: the round id already dismissed via "Draw next", so we don't loop back to Review.
    private Guid _dismissedReviewRound;

    private DispatcherTimer? _timer;

    // Hit-rects, laid out per screen.
    private readonly Rect[] _wordChips = new Rect[3];
    private readonly Rect[] _swatches = new Rect[12];
    private readonly Rect[] _sizes = new Rect[3];
    private Rect _eraserBtn, _undoBtn, _clearBtn, _submitBtn, _guessBtn, _giveUpBtn, _nextBtn, _topRightBtn, _nudgeBtn;

    public DrawBoard()
    {
        Width = BoardW;
        Height = BoardH;
        Focusable = true;
    }

    protected override Size MeasureOverride(Size availableSize) => new(BoardW, BoardH);

    // ── Window-driven surface ──
    public void EnterOnline(Guid meId)
    {
        _meId = meId;
        _composeMode = false;
        _screen = Screen.Waiting;   // until the first state lands
        LayoutScreen();
        InvalidateVisual();
    }

    public void EnterCompose(Guid meId, Profile opponent)
    {
        _meId = meId;
        _opponent = opponent;
        _composeMode = true;
        _composeSent = false;
        NewOffer();
        _screen = Screen.WordPick;
        LayoutScreen();
        InvalidateVisual();
    }

    public void MarkComposeSent()
    {
        _composeSent = true;
        _busy = false;
        _toast = null;
        _screen = Screen.Waiting;
        LayoutScreen();
        InvalidateVisual();
    }

    public void MarkComposeDeclined(string message)
    {
        _busy = false;
        _toast = message;
        _toastTicks = 400;
        InvalidateVisual();
    }

    /// <summary>Applies an authoritative game state and reconciles the screen: my turn to draw → review the last
    /// round then pick a word; my turn to guess → the guess screen; otherwise → waiting.</summary>
    public void ApplyState(DrawGameState state)
    {
        _state = state;
        _opponent = state.Summary.Opponent(_meId);
        _composeMode = false;
        _composeSent = false;
        _busy = false;
        _toast = null;

        var s = state.Summary;
        if (s.Status == DrawGameStatus.Abandoned) { _screen = Screen.Over; }
        else if (s.IsTurnOf(_meId))
        {
            if (s.Phase == DrawPhase.Guess && state.Current is { } gr)
            {
                _guessRoundId = gr.Id;
                _guessInput = "";
                _screen = Screen.Guess;
            }
            else // my turn to draw the next round
            {
                var cur = state.Current;
                bool reviewable = cur is { Status: DrawRoundStatus.Solved or DrawRoundStatus.GaveUp }
                                  && cur.Guesser.Id == _meId && cur.Id != _dismissedReviewRound;
                if (reviewable) _screen = Screen.Review;
                else { NewOffer(); _screen = Screen.WordPick; }
            }
        }
        else _screen = Screen.Waiting;

        LayoutScreen();
        InvalidateVisual();
    }

    public void SetError(string message)
    {
        _busy = false;
        _toast = message;
        _toastTicks = 220;
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
        if (_nudgeCooldownTicks > 0) _nudgeCooldownTicks--;
    }

    private void NewOffer()
    {
        _offer = DrawWords.OfferWords(new Random());
        _chosenWord = "";
        _strokes.Clear();
        _current = null;
        _color = 0;
        _size = 1;
        _eraser = false;
    }

    // ── Keyboard (special keys; text arrives via OnTextInput) ──
    public bool HandleKey(Key key)
    {
        if (_screen == Screen.Guess)
        {
            switch (key)
            {
                case Key.Enter or Key.Return: SubmitGuess(); return true;
                case Key.Back:
                    if (_guessInput.Length > 0) { _guessInput = _guessInput[..^1]; InvalidateVisual(); }
                    return true;
            }
            return false;   // let letters/space fall through to OnTextInput
        }
        if (_screen == Screen.Draw && key is Key.Enter or Key.Return) { SubmitDrawing(); return true; }
        if (_screen == Screen.Review && key is Key.Enter or Key.Return or Key.Space) { DrawNext(); return true; }
        if (_screen == Screen.Draw && key == Key.Z) { Undo(); return true; }
        return false;
    }

    protected override void OnTextInput(TextInputEventArgs e)
    {
        if (_screen == Screen.Guess && !string.IsNullOrEmpty(e.Text))
        {
            foreach (char c in e.Text)
                if (_guessInput.Length < 40 && (char.IsLetterOrDigit(c) || c is ' ' or '-' or '\''))
                    _guessInput += c;
            InvalidateVisual();
            e.Handled = true;
        }
        base.OnTextInput(e);
    }

    // ── Pointer ──
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        var p = e.GetPosition(this);

        // Screen-agnostic top-right button (Resign / Cancel).
        if (_topRightBtn.Contains(p))
        {
            if (_composeMode) CancelComposeRequested?.Invoke();
            else if (_state is { Summary.Status: DrawGameStatus.InProgress }) ResignRequested?.Invoke();
            e.Handled = true; return;
        }

        switch (_screen)
        {
            case Screen.WordPick:
                for (int i = 0; i < 3; i++)
                    if (_wordChips[i].Contains(p)) { ChooseWord((DrawDifficulty)i); e.Handled = true; return; }
                break;

            case Screen.Draw:
                if (HandleDrawToolClick(p)) { e.Handled = true; return; }
                if (InCanvas(p) && !_busy)
                {
                    _current = new List<DrawPoint> { ToCanvas(p) };
                    e.Pointer.Capture(this);
                    InvalidateVisual();
                    e.Handled = true; return;
                }
                break;

            case Screen.Guess:
                if (_guessBtn.Contains(p)) { SubmitGuess(); e.Handled = true; return; }
                if (_giveUpBtn.Contains(p)) { GiveUp(); e.Handled = true; return; }
                break;

            case Screen.Review:
                if (_nextBtn.Contains(p)) { DrawNext(); e.Handled = true; return; }
                break;

            case Screen.Waiting:
                if (CanNudgeNow() && _nudgeBtn.Contains(p)) { TryNudge(); e.Handled = true; return; }
                break;
        }
        base.OnPointerPressed(e);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        if (_current is not null)
        {
            var p = e.GetPosition(this);
            _current.Add(ToCanvas(p));
            InvalidateVisual();
        }
        base.OnPointerMoved(e);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        if (_current is not null)
        {
            if (_current.Count > 0)
                _strokes.Add(new DrawStroke((byte)(_eraser ? DrawPalette.PaperIndex : _color), (byte)_size, _current));
            _current = null;
            e.Pointer.Capture(null);
            InvalidateVisual();
        }
        base.OnPointerReleased(e);
    }

    private bool HandleDrawToolClick(Point p)
    {
        for (int i = 0; i < _swatches.Length; i++)
            if (_swatches[i].Contains(p)) { _color = i; _eraser = false; InvalidateVisual(); return true; }
        for (int i = 0; i < _sizes.Length; i++)
            if (_sizes[i].Contains(p)) { _size = i; InvalidateVisual(); return true; }
        if (_eraserBtn.Contains(p)) { _eraser = !_eraser; InvalidateVisual(); return true; }
        if (_undoBtn.Contains(p)) { Undo(); return true; }
        if (_clearBtn.Contains(p)) { _strokes.Clear(); InvalidateVisual(); return true; }
        if (_submitBtn.Contains(p) && _strokes.Count > 0 && !_busy) { SubmitDrawing(); return true; }
        return false;
    }

    private void Undo()
    {
        if (_strokes.Count > 0) { _strokes.RemoveAt(_strokes.Count - 1); InvalidateVisual(); }
    }

    // ── Actions ──
    private void ChooseWord(DrawDifficulty diff)
    {
        _chosenDiff = diff;
        _chosenWord = _offer.For(diff);
        _strokes.Clear();
        _screen = Screen.Draw;
        LayoutScreen();
        InvalidateVisual();
    }

    private void SubmitDrawing()
    {
        if (_strokes.Count == 0 || string.IsNullOrEmpty(_chosenWord)) return;
        _busy = true;
        string hint = DrawGuessing.LetterHint(_chosenWord);
        var strokes = _strokes.ToList();
        if (_composeMode) ComposeSubmitRequested?.Invoke(_chosenDiff, _chosenWord, hint, strokes);
        else { _dismissedReviewRound = Guid.Empty; RoundSubmitRequested?.Invoke(_chosenDiff, _chosenWord, hint, strokes); }
        _screen = Screen.Waiting;
        LayoutScreen();
        InvalidateVisual();
    }

    private void SubmitGuess()
    {
        var g = _guessInput.Trim();
        if (g.Length == 0 || _busy) return;
        _busy = true;
        GuessSubmitRequested?.Invoke(_guessRoundId, g);
        InvalidateVisual();
    }

    private void GiveUp()
    {
        if (_busy) return;
        _busy = true;
        GiveUpRequested?.Invoke(_guessRoundId);
        InvalidateVisual();
    }

    private void DrawNext()
    {
        if (_state?.Current is { } cur) _dismissedReviewRound = cur.Id;
        NewOffer();
        _screen = Screen.WordPick;
        LayoutScreen();
        InvalidateVisual();
    }

    private bool CanNudgeNow() =>
        !_composeMode && _state is { Summary.Status: DrawGameStatus.InProgress } s && !s.Summary.IsTurnOf(_meId);

    private void TryNudge()
    {
        if (!CanNudgeNow() || _nudgeCooldownTicks > 0) return;
        _nudgeCooldownTicks = 625;   // ~10s
        NudgeRequested?.Invoke();
        InvalidateVisual();
    }

    // ── Canvas mapping ──
    private static bool InCanvas(Point p) =>
        p.X >= CanvasX && p.X <= CanvasX + CanvasPx && p.Y >= CanvasTop && p.Y <= CanvasBottom;

    private static DrawPoint ToCanvas(Point p)
    {
        double nx = (p.X - CanvasX) / CanvasPx * DrawStrokeCodec.CanvasSize;
        double ny = (p.Y - CanvasTop) / CanvasPx * DrawStrokeCodec.CanvasSize;
        return new DrawPoint(
            (short)Math.Clamp(nx, 0, DrawStrokeCodec.CanvasSize),
            (short)Math.Clamp(ny, 0, DrawStrokeCodec.CanvasSize));
    }

    private static Point FromCanvas(DrawPoint p) => new(CanvasX + p.X * Scale, CanvasTop + p.Y * Scale);

    // ── Layout ──
    private void LayoutScreen()
    {
        _wordChips.AsSpan().Clear();
        _swatches.AsSpan().Clear();
        _sizes.AsSpan().Clear();
        _eraserBtn = _undoBtn = _clearBtn = _submitBtn = _guessBtn = _giveUpBtn = _nextBtn = _topRightBtn = _nudgeBtn = default;

        // The top-right Resign/Cancel button shows on every in-game screen.
        if (_composeMode || _state is { Summary.Status: DrawGameStatus.InProgress })
            _topRightBtn = new Rect(BoardW - 96, 18, 80, 28);

        switch (_screen)
        {
            case Screen.WordPick:
            {
                const double w = 360, h = 64, gap = 16;
                double x = (BoardW - w) / 2, y = 190;
                for (int i = 0; i < 3; i++) { _wordChips[i] = new Rect(x, y, w, h); y += h + gap; }
                break;
            }
            case Screen.Draw:
            {
                double toolY = CanvasBottom + 18;
                double sw = 28, sgap = 6;
                double rowW = _swatches.Length * sw + (_swatches.Length - 1) * sgap;
                double x = (BoardW - rowW) / 2;
                for (int i = 0; i < _swatches.Length; i++) { _swatches[i] = new Rect(x, toolY, sw, sw); x += sw + sgap; }

                double row2 = toolY + sw + 12;
                double bx = CanvasX;
                for (int i = 0; i < _sizes.Length; i++) { _sizes[i] = new Rect(bx, row2, 34, 30); bx += 40; }
                _eraserBtn = new Rect(bx, row2, 56, 30); bx += 56 + 8;
                _undoBtn = new Rect(bx, row2, 52, 30); bx += 58;
                _clearBtn = new Rect(bx, row2, 52, 30);
                _submitBtn = new Rect(CanvasX + CanvasPx - 84, row2, 84, 30);
                break;
            }
            case Screen.Guess:
            {
                double y = CanvasBottom + 64;
                _giveUpBtn = new Rect(CanvasX, y, 96, 34);
                _guessBtn = new Rect(CanvasX + CanvasPx - 110, y, 110, 34);
                break;
            }
            case Screen.Review:
                _nextBtn = new Rect((BoardW - 160) / 2, 430, 160, 40);
                break;
            case Screen.Waiting:
                if (CanNudgeNow()) _nudgeBtn = new Rect((BoardW - 120) / 2, 380, 120, 34);
                break;
        }
    }

    // ── Rendering ──
    public override void Render(DrawingContext ctx)
    {
        ctx.FillRectangle(Palette.OverlaySurfaceBrush, new Rect(0, 0, BoardW, BoardH));
        DrawHeader(ctx);

        switch (_screen)
        {
            case Screen.WordPick: DrawWordPick(ctx); break;
            case Screen.Draw: DrawCanvas(ctx, _strokes, live: true); DrawTools(ctx); break;
            case Screen.Guess: DrawGuess(ctx); break;
            case Screen.Review: DrawReview(ctx); break;
            case Screen.Waiting: DrawWaiting(ctx); break;
            case Screen.Over: DrawOver(ctx); break;
        }

        DrawToast(ctx);
    }

    private void DrawHeader(DrawingContext ctx)
    {
        var title = OverlayDraw.Text("DRAW WITH PERCH", 24, Palette.AccentBrush, FontWeight.Bold);
        ctx.DrawText(title, new Point((BoardW - title.Width) / 2, 20));

        string sub = StatusLine();
        var st = OverlayDraw.Text(sub, 14, Palette.FgBrush, FontWeight.SemiBold);
        ctx.DrawText(st, new Point((BoardW - st.Width) / 2, 56));

        // Running score line (once a live game exists).
        if (_state is { } s && !_composeMode)
        {
            var opp = s.Summary.Opponent(_meId);
            string score = $"You {s.Summary.MyScore(_meId)}   ·   @{opp?.Handle ?? "friend"} {s.Summary.TheirScore(_meId)}";
            var sc = OverlayDraw.Text(score, 12, Palette.MutedBrush);
            ctx.DrawText(sc, new Point((BoardW - sc.Width) / 2, 80));
        }

        if (_topRightBtn != default)
            DrawButton(ctx, _topRightBtn, _composeMode ? "Cancel" : "Resign", accent: false);
    }

    private string StatusLine()
    {
        string opp = _opponent is { } o ? $"@{o.Handle}" : "your friend";
        return _screen switch
        {
            Screen.WordPick => "Pick a word to draw",
            Screen.Draw => $"Draw: {_chosenWord}",
            Screen.Guess => "Guess what they drew",
            Screen.Review => "Round over",
            Screen.Over => "Game abandoned",
            Screen.Waiting when _composeMode && !_composeSent => $"Challenging {opp}…",
            Screen.Waiting when _composeMode => $"Waiting for {opp} to accept…",
            Screen.Waiting => WaitingSubtitle(opp),
            _ => "",
        };
    }

    private string WaitingSubtitle(string opp)
    {
        // If the last round was mine and it just resolved, celebrate/commiserate before "waiting".
        if (_state?.Current is { } c && c.Drawer.Id == _meId)
        {
            if (c.Status == DrawRoundStatus.Solved) return $"{opp} guessed it! Waiting for their drawing…";
            if (c.Status == DrawRoundStatus.GaveUp) return $"{opp} gave up. Waiting for their drawing…";
        }
        return _state?.Summary.Phase == DrawPhase.Guess
            ? $"Waiting for {opp} to guess…"
            : $"Waiting for {opp} to draw…";
    }

    private void DrawWordPick(DrawingContext ctx)
    {
        for (int i = 0; i < 3; i++)
        {
            var d = (DrawDifficulty)i;
            var chip = _wordChips[i];
            var accent = DifficultyBrush(d);
            OverlayDraw.Panel(ctx, chip, Palette.ButtonBgBrush, new Pen(accent, 2), 14);

            var tier = OverlayDraw.Text(d.ToString().ToUpperInvariant(), 12, accent, FontWeight.Bold);
            ctx.DrawText(tier, new Point(chip.X + 18, chip.Y + 12));

            var word = OverlayDraw.Text(_offer.For(d), 22, Palette.FgBrush, FontWeight.SemiBold);
            ctx.DrawText(word, new Point(chip.X + 18, chip.Y + 30));

            int pts = DrawScoring.Base(d);
            var val = OverlayDraw.Text($"{pts} pts", 13, Palette.MutedBrush);
            ctx.DrawText(val, new Point(chip.Right - val.Width - 18, chip.Y + (chip.Height - val.Height) / 2));
        }
    }

    private void DrawCanvas(DrawingContext ctx, IReadOnlyList<DrawStroke> strokes, bool live)
    {
        var rect = new Rect(CanvasX, CanvasTop, CanvasPx, CanvasPx);
        // The paper is always white so a drawing reads the same for both players.
        OverlayDraw.Panel(ctx, rect, Palette.DrawSwatch(DrawPalette.PaperIndex), new Pen(Palette.BorderBrush, 1), 12);

        using (ctx.PushClip(rect))
        {
            foreach (var s in strokes) PaintStroke(ctx, s);
            if (live && _current is { Count: > 0 })
                PaintStroke(ctx, new DrawStroke((byte)(_eraser ? DrawPalette.PaperIndex : _color), (byte)_size, _current));
        }
    }

    private static void PaintStroke(DrawingContext ctx, DrawStroke s)
    {
        var brush = Palette.DrawSwatch(s.Color);
        double width = Math.Max(1, DrawPalette.Sizes[Math.Clamp(s.Size, 0, DrawPalette.Sizes.Count - 1)] * Scale);
        if (s.Points.Count == 1)
        {
            var p = FromCanvas(s.Points[0]);
            ctx.DrawEllipse(brush, null, p, width / 2, width / 2);
            return;
        }
        var pen = new Pen(brush, width) { LineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            g.BeginFigure(FromCanvas(s.Points[0]), false);
            for (int i = 1; i < s.Points.Count; i++) g.LineTo(FromCanvas(s.Points[i]));
            g.EndFigure(false);
        }
        ctx.DrawGeometry(null, pen, geo);
    }

    private void DrawTools(DrawingContext ctx)
    {
        for (int i = 0; i < _swatches.Length; i++)
        {
            bool sel = !_eraser && _color == i;
            OverlayDraw.Panel(ctx, _swatches[i], Palette.DrawSwatch(i),
                new Pen(sel ? Palette.AccentBrush : Palette.BorderBrush, sel ? 3 : 1), 6);
        }

        for (int i = 0; i < _sizes.Length; i++)
        {
            bool sel = _size == i;
            OverlayDraw.Panel(ctx, _sizes[i], sel ? Palette.AccentBrush : Palette.ButtonBgBrush,
                new Pen(Palette.BorderBrush, 1), 8);
            double dot = DrawPalette.Sizes[i] * Scale;
            ctx.DrawEllipse(sel ? Palette.OnAccentBrush : Palette.FgBrush, null, _sizes[i].Center,
                Math.Min(dot, 11) / 2, Math.Min(dot, 11) / 2);
        }

        DrawButton(ctx, _eraserBtn, "Eraser", accent: _eraser);
        DrawButton(ctx, _undoBtn, "Undo", accent: false);
        DrawButton(ctx, _clearBtn, "Clear", accent: false);
        DrawButton(ctx, _submitBtn, _composeMode ? "Send" : "Submit", accent: _strokes.Count > 0, enabled: _strokes.Count > 0 && !_busy);
    }

    private void DrawGuess(DrawingContext ctx)
    {
        var round = _state?.Current;
        DrawCanvas(ctx, round?.Strokes ?? [], live: false);

        // Letter blanks from the hint.
        DrawBlanks(ctx, round?.LetterHint ?? "", CanvasBottom + 26);

        // The typed-guess field (owner-drawn), spanning between the Give up and Guess buttons.
        var field = new Rect(_giveUpBtn.Right + 8, _giveUpBtn.Y, _guessBtn.Left - _giveUpBtn.Right - 16, 34);
        OverlayDraw.Panel(ctx, field, Palette.SurfaceSunkenBrush, new Pen(Palette.BorderBrush, 1), 8);
        bool empty = _guessInput.Length == 0;
        var ft = OverlayDraw.Text(empty ? "type your guess" : _guessInput, 15,
            empty ? Palette.MutedBrush : Palette.FgBrush);
        OverlayDraw.TextLeftMid(ctx, ft, field.X + 12, field.Center.Y);
        if (!empty && Math.Abs(Math.Sin(_pulse)) > 0.5)   // blinking caret
        {
            double cx = field.X + 12 + ft.Width + 2;
            ctx.DrawLine(new Pen(Palette.FgBrush, 1.5), new Point(cx, field.Y + 8), new Point(cx, field.Bottom - 8));
        }

        DrawButton(ctx, _guessBtn, "Guess", accent: true, enabled: _guessInput.Trim().Length > 0 && !_busy);
        DrawButton(ctx, _giveUpBtn, "Give up", accent: false);

        // Prior wrong guesses.
        if (round is { Guesses.Count: > 0 })
        {
            var wrong = string.Join(", ", round.Guesses.Where(g => !DrawGuessing.IsCorrect(g, round.Word ?? "")));
            if (wrong.Length > 0)
            {
                var gt = OverlayDraw.Text($"Tried: {OverlayDraw.Truncate(wrong, 12, CanvasPx)}", 12, Palette.MutedBrush);
                ctx.DrawText(gt, new Point(CanvasX, CanvasBottom + 108));
            }
        }
    }

    private void DrawBlanks(DrawingContext ctx, string hint, double y)
    {
        var groups = hint.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(t => int.TryParse(t, out int n) ? n : 0).Where(n => n > 0).ToList();
        if (groups.Count == 0) return;

        const double bw = 16, bgap = 6, wordGap = 18;
        double total = groups.Sum(n => n * bw + (n - 1) * bgap) + (groups.Count - 1) * wordGap;
        double x = (BoardW - total) / 2;
        var pen = new Pen(Palette.MutedBrush, 2);
        foreach (int n in groups)
        {
            for (int i = 0; i < n; i++)
            {
                ctx.DrawLine(pen, new Point(x, y), new Point(x + bw, y));
                x += bw + bgap;
            }
            x += wordGap - bgap;
        }
    }

    private void DrawReview(DrawingContext ctx)
    {
        var round = _state?.Current;
        if (round is null) return;
        bool solved = round.Status == DrawRoundStatus.Solved;

        var verdict = OverlayDraw.Text(solved ? "You got it!" : "Gave up", 26,
            solved ? Palette.RunningBrush : Palette.MutedBrush, FontWeight.Bold);
        ctx.DrawText(verdict, new Point((BoardW - verdict.Width) / 2, 150));

        var word = OverlayDraw.Text((round.Word ?? "").ToUpperInvariant(), 30, Palette.FgBrush, FontWeight.Bold);
        ctx.DrawText(word, new Point((BoardW - word.Width) / 2, 200));

        if (solved)
        {
            var pts = OverlayDraw.Text($"+{round.PointsGuesser} points", 18, Palette.AccentBrush, FontWeight.SemiBold);
            ctx.DrawText(pts, new Point((BoardW - pts.Width) / 2, 258));
        }

        // A thumbnail of the drawing they made.
        DrawThumb(ctx, round.Strokes, new Rect((BoardW - 180) / 2, 300, 180, 100));

        DrawButton(ctx, _nextBtn, "Draw next ▸", accent: true);
    }

    private void DrawWaiting(DrawingContext ctx)
    {
        // Show the drawing that's in play (mine, awaiting a guess) as a reminder.
        if (_state?.Current is { } c && c.Drawer.Id == _meId && c.Status == DrawRoundStatus.Guessing)
            DrawCanvas(ctx, c.Strokes, live: false);
        else
        {
            var msg = OverlayDraw.Text(_composeMode ? "🪶" : "⏳", 40, Palette.MutedBrush);
            ctx.DrawText(msg, new Point((BoardW - msg.Width) / 2, 240));
        }

        if (CanNudgeNow())
        {
            bool ready = _nudgeCooldownTicks == 0;
            DrawButton(ctx, _nudgeBtn, ready ? "Nudge" : "Nudged", accent: ready, enabled: ready);
        }
    }

    private void DrawOver(DrawingContext ctx)
    {
        var msg = OverlayDraw.Text("Game abandoned", 22, Palette.MutedBrush, FontWeight.SemiBold);
        ctx.DrawText(msg, new Point((BoardW - msg.Width) / 2, 260));
    }

    private void DrawThumb(DrawingContext ctx, IReadOnlyList<DrawStroke> strokes, Rect rect)
    {
        OverlayDraw.Panel(ctx, rect, Palette.DrawSwatch(DrawPalette.PaperIndex), new Pen(Palette.BorderBrush, 1), 8);
        using (ctx.PushClip(rect))
        {
            double sx = rect.Width / DrawStrokeCodec.CanvasSize, sy = rect.Height / DrawStrokeCodec.CanvasSize;
            foreach (var s in strokes)
            {
                if (s.Points.Count < 2) continue;
                var pen = new Pen(Palette.DrawSwatch(s.Color), Math.Max(1, DrawPalette.Sizes[Math.Clamp(s.Size, 0, DrawPalette.Sizes.Count - 1)] * sx))
                { LineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
                var geo = new StreamGeometry();
                using (var g = geo.Open())
                {
                    g.BeginFigure(new Point(rect.X + s.Points[0].X * sx, rect.Y + s.Points[0].Y * sy), false);
                    for (int i = 1; i < s.Points.Count; i++) g.LineTo(new Point(rect.X + s.Points[i].X * sx, rect.Y + s.Points[i].Y * sy));
                    g.EndFigure(false);
                }
                ctx.DrawGeometry(null, pen, geo);
            }
        }
    }

    private void DrawButton(DrawingContext ctx, Rect rect, string label, bool accent, bool enabled = true)
    {
        if (rect == default) return;
        var fill = accent ? Palette.AccentBrush : Palette.ButtonBgBrush;
        var fg = accent ? Palette.OnAccentBrush : Palette.MutedBrush;
        using (ctx.PushOpacity(enabled ? 1.0 : 0.5))
        {
            OverlayDraw.Panel(ctx, rect, fill, accent ? null : new Pen(Palette.BorderBrush, 1), rect.Height / 2);
            var ft = OverlayDraw.Text(label, 13, fg, FontWeight.SemiBold);
            ctx.DrawText(ft, new Point(rect.X + (rect.Width - ft.Width) / 2, rect.Y + (rect.Height - ft.Height) / 2));
        }
    }

    private void DrawToast(DrawingContext ctx)
    {
        if (_toast is not { } toast) return;
        var tf = OverlayDraw.Text(toast, 12, Palette.WarnBrush, FontWeight.SemiBold);
        ctx.DrawText(tf, new Point((BoardW - tf.Width) / 2, BoardH - 28));
    }

    private static IBrush DifficultyBrush(DrawDifficulty d) => d switch
    {
        DrawDifficulty.Easy => Palette.RunningBrush,
        DrawDifficulty.Medium => Palette.AwaitingBrush,
        _ => Palette.ErrorBrush,
    };

    // ── Headless snapshot poses (timers don't tick under the render harness) ──
    internal void SnapshotWordPick(Guid meId, Profile opponent)
    {
        EnterCompose(meId, opponent);
        _offer = new DrawWords.Offer("cat", "ice cream", "gravity");
        _screen = Screen.WordPick;
        LayoutScreen();
    }

    internal void SnapshotDraw(Guid meId, Profile opponent)
    {
        EnterCompose(meId, opponent);
        _chosenDiff = DrawDifficulty.Medium;
        _chosenWord = "ice cream";
        _color = 8; _size = 1;
        _strokes.Clear();
        _strokes.Add(new DrawStroke(0, 1, Trace((200, 300), (300, 250), (420, 300), (500, 420))));
        _strokes.Add(new DrawStroke(8, 2, Trace((250, 500), (500, 520), (750, 500))));
        _strokes.Add(new DrawStroke(3, 0, Trace((600, 200), (650, 260), (700, 200))));
        _screen = Screen.Draw;
        LayoutScreen();
    }

    internal void SnapshotGuess(DrawGameState state, Guid meId)
    {
        EnterOnline(meId);
        ApplyState(state);
        _guessInput = "ice cr";
    }

    internal void SnapshotReview(DrawGameState state, Guid meId)
    {
        EnterOnline(meId);
        ApplyState(state);
        _screen = Screen.Review;
        LayoutScreen();
    }

    private static List<DrawPoint> Trace(params (short x, short y)[] pts) =>
        pts.Select(p => new DrawPoint(p.x, p.y)).ToList();
}
