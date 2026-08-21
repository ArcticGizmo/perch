using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Perch.Data;
using Perch.Games;
using Perch.Avalonia.Rendering;
using Perch.Avalonia.Theming;
using Perch.Theming;

namespace Perch.Avalonia.Windows;

/// <summary>
/// The third secret toy reached from the arcade chooser (see <c>ArcadeMenuWindow</c>): a daily Wordle. One
/// five-letter word per calendar day (deterministic, from <see cref="WordleGame"/>), six guesses, the usual
/// green/yellow/grey scoring and a live on-screen keyboard that colours as you learn letters. The day's
/// guesses persist in <see cref="AppSettings.WordleState"/> so closing and reopening resumes the same board;
/// crossing midnight starts the next day's puzzle. Type on the physical keyboard or click the on-screen one.
///
/// Owner-drawn over the shared <see cref="OverlayDraw"/> / <see cref="Palette"/> vocabulary like its siblings.
/// Unlike them it isn't a pure toy — it reads/writes settings — so the window hands the board the
/// <see cref="AppSettings"/> to load from and save to.
/// </summary>
internal sealed class WordleWindow : Window
{
    private readonly WordleBoard _board;

    public WordleWindow(AppSettings settings)
    {
        _board = new WordleBoard(settings);
        Title = "Perch Wordle";
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
        _board.LoadToday();     // re-resolve the puzzle each open, so reopening after midnight rolls the day
        _board.Begin();
    }

    protected override void OnClosed(EventArgs e)
    {
        _board.Stop();
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

/// <summary>The board itself: the six-row grid, the message line, and the on-screen keyboard, all owner-drawn
/// at a fixed size. Game state lives here; <see cref="WordleGame"/> supplies the word, the scoring and the
/// persistence codec.</summary>
internal sealed class WordleBoard : Control
{
    // ── Geometry (DIP), all fixed so layout is a handful of constants ──
    private const double BoardW = 480, BoardH = 744;
    private const int Cols = WordleGame.WordLength, Rows = WordleGame.MaxGuesses;
    private const double Tile = 60, TileGap = 8;
    private const double GridTop = 108;
    private const double GridW = Cols * Tile + (Cols - 1) * TileGap;
    private const double GridLeft = (BoardW - GridW) / 2;
    private const double MsgY = GridTop + Rows * Tile + (Rows - 1) * TileGap + 16;
    private const double KeyH = 54, KeyGap = 6, LetterKeyW = 40, WideKeyW = 60;
    private const double KbTop = MsgY + 30;
    private const int TickMs = 16;

    private static readonly string[] KbRows = { "QWERTYUIOP", "ASDFGHJKL", "ZXCVBNM" };
    // Text labels rather than the ↵/⌫ glyphs, which fall to tofu in Inter (the overlay's typeface).
    private const string EnterLabel = "ENTER";
    private const string BackLabel = "DEL";

    private enum Phase { Playing, Won, Lost }

    private readonly AppSettings _settings;

    // ── State ──
    private string _answer = "";
    private DateOnly _today;
    private readonly List<string> _guesses = new();
    private string _current = "";
    private Phase _phase = Phase.Playing;
    private string? _message;
    private int _messageTicks;
    private int _shake;                 // >0 while the active row shudders after an invalid entry
    private double _pulse;              // drives the end-of-game prompt shimmer
    private DispatcherTimer? _timer;

    // On-screen keyboard hit-rects, laid out once from the fixed geometry (never captured from a paint pass).
    private readonly Dictionary<char, Rect> _keyRects = new();
    private Rect _enterRect, _backRect;

    public WordleBoard(AppSettings settings)
    {
        _settings = settings;
        Width = BoardW;
        Height = BoardH;
        Focusable = true;
        LayoutKeyboard();
        LoadToday();
    }

    protected override Size MeasureOverride(Size availableSize) => new(BoardW, BoardH);

    /// <summary>Resolves today's puzzle and rehydrates any guesses already made today from settings. Safe to
    /// call repeatedly (each window open does), so a session left open past midnight rolls to the new word.</summary>
    public void LoadToday()
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        _today = today;
        _answer = WordleGame.AnswerFor(today);

        _guesses.Clear();
        _guesses.AddRange(WordleGame.ParseStateForToday(_settings.WordleState, today));
        _current = "";
        _message = null;
        _messageTicks = 0;
        _shake = 0;
        RecomputePhase();
        InvalidateVisual();
    }

    private void RecomputePhase()
    {
        if (_guesses.Count > 0 && _guesses[^1] == _answer) _phase = Phase.Won;
        else if (_guesses.Count >= Rows) _phase = Phase.Lost;
        else _phase = Phase.Playing;
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
        t.Tick += (_, _) =>
        {
            _pulse += 0.12;
            if (_shake > 0) _shake--;
            if (_messageTicks > 0 && --_messageTicks == 0) _message = null;
            InvalidateVisual();
        };
        return t;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _timer?.Stop();
        base.OnDetachedFromVisualTree(e);
    }

    // ── Input ──
    public bool HandleKey(Key key)
    {
        if (_phase != Phase.Playing) return false;

        if (key is >= Key.A and <= Key.Z)
        {
            AddLetter((char)('A' + (key - Key.A)));
            return true;
        }
        switch (key)
        {
            case Key.Back or Key.Delete: Backspace(); return true;
            case Key.Enter or Key.Return: Submit(); return true;
        }
        return false;
    }

    private void AddLetter(char c)
    {
        if (_current.Length >= Cols) return;
        _current += char.ToLowerInvariant(c);
        InvalidateVisual();
    }

    private void Backspace()
    {
        if (_current.Length == 0) return;
        _current = _current[..^1];
        InvalidateVisual();
    }

    private void Submit()
    {
        if (_current.Length < Cols) { Flash("Not enough letters"); return; }
        if (!WordleGame.IsAcceptedGuess(_current)) { Flash("Not in word list"); return; }

        _guesses.Add(_current);
        _current = "";
        _settings.WordleState = WordleGame.FormatState(_today, _guesses);
        _settings.Save();

        RecomputePhase();
        if (_phase == Phase.Won) Flash(WinWord(_guesses.Count), persistent: true);
        else if (_phase == Phase.Lost) Flash(_answer.ToUpperInvariant(), persistent: true);
        InvalidateVisual();
    }

    private void Flash(string text, bool persistent = false)
    {
        _message = text;
        _messageTicks = persistent ? 0 : 140;   // ~2.3s at 16ms, or sticky for the end-of-game verdict
        if (!persistent) _shake = 30;
        InvalidateVisual();
    }

    private static string WinWord(int guesses) => guesses switch
    {
        1 => "Genius",
        2 => "Magnificent",
        3 => "Impressive",
        4 => "Splendid",
        5 => "Great",
        _ => "Phew",
    };

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        if (_phase != Phase.Playing) { base.OnPointerPressed(e); return; }
        var p = e.GetPosition(this);

        if (_enterRect.Contains(p)) { Submit(); e.Handled = true; return; }
        if (_backRect.Contains(p)) { Backspace(); e.Handled = true; return; }
        foreach (var (c, r) in _keyRects)
            if (r.Contains(p)) { AddLetter(c); e.Handled = true; return; }

        base.OnPointerPressed(e);
    }

    // ── Layout ──
    private void LayoutKeyboard()
    {
        for (int row = 0; row < KbRows.Length; row++)
        {
            string keys = KbRows[row];
            double y = KbTop + row * (KeyH + KeyGap);

            // The bottom row is bracketed by the wide Enter / Back keys; the middle rows are plain letters.
            bool bottom = row == KbRows.Length - 1;
            double rowW = keys.Length * LetterKeyW + (keys.Length - 1) * KeyGap
                          + (bottom ? 2 * (WideKeyW + KeyGap) : 0);
            double x = (BoardW - rowW) / 2;

            if (bottom)
            {
                _enterRect = new Rect(x, y, WideKeyW, KeyH);
                x += WideKeyW + KeyGap;
            }
            foreach (char c in keys)
            {
                _keyRects[c] = new Rect(x, y, LetterKeyW, KeyH);
                x += LetterKeyW + KeyGap;
            }
            if (bottom)
                _backRect = new Rect(x, y, WideKeyW, KeyH);
        }
    }

    // ── Rendering ──
    public override void Render(DrawingContext ctx)
    {
        double w = Bounds.Width;
        ctx.FillRectangle(Palette.OverlaySurfaceBrush, new Rect(0, 0, w, Bounds.Height));

        DrawHeader(ctx, w);
        DrawGrid(ctx);
        DrawMessage(ctx, w);
        DrawKeyboard(ctx);
    }

    private void DrawHeader(DrawingContext ctx, double w)
    {
        var title = OverlayDraw.Text("PERCH WORDLE", 26, Palette.AccentBrush, FontWeight.Bold);
        ctx.DrawText(title, new Point((w - title.Width) / 2, 30));

        var sub = OverlayDraw.Text($"Daily · {_today:MMM d, yyyy}", 12, Palette.MutedBrush);
        ctx.DrawText(sub, new Point((w - sub.Width) / 2, 66));

        ctx.DrawLine(new Pen(Palette.SeparatorBrush, 1), new Point(0, 96), new Point(w, 96));
    }

    private void DrawGrid(DrawingContext ctx)
    {
        int activeRow = _guesses.Count;
        for (int r = 0; r < Rows; r++)
        {
            // A submitted row is scored; the active row shows the in-progress typing; the rest are empty.
            string word = r < _guesses.Count ? _guesses[r]
                        : r == activeRow ? _current
                        : "";
            WordleMark[]? marks = r < _guesses.Count ? WordleGame.Score(_guesses[r], _answer) : null;

            double shakeDx = (r == activeRow && _shake > 0) ? 4 * Math.Sin(_shake * 1.1) : 0;
            for (int c = 0; c < Cols; c++)
            {
                var rect = new Rect(
                    GridLeft + c * (Tile + TileGap) + shakeDx,
                    GridTop + r * (Tile + TileGap),
                    Tile, Tile);
                char? letter = c < word.Length ? char.ToUpperInvariant(word[c]) : null;
                DrawTile(ctx, rect, letter, marks?[c], typed: r == activeRow && letter is not null);
            }
        }
    }

    private static void DrawTile(DrawingContext ctx, Rect rect, char? letter, WordleMark? mark, bool typed)
    {
        if (mark is { } m)
        {
            var (fill, fg) = MarkColors(m);
            OverlayDraw.Panel(ctx, rect, fill, null, 6);
            if (letter is { } lc) DrawTileLetter(ctx, rect, lc, fg);
            return;
        }

        // Unscored tile: a faint outline, brighter once a letter has been typed into it.
        var border = new Pen(typed ? Palette.MutedBrush : Palette.BorderBrush, typed ? 2 : 1.5);
        OverlayDraw.Panel(ctx, rect, null, border, 6);
        if (letter is { } l) DrawTileLetter(ctx, rect, l, Palette.FgBrush);
    }

    private static void DrawTileLetter(DrawingContext ctx, Rect rect, char letter, IBrush brush)
    {
        var ft = OverlayDraw.Text(letter.ToString(), 30, brush, FontWeight.Bold);
        ctx.DrawText(ft, new Point(rect.X + (rect.Width - ft.Width) / 2, rect.Y + (rect.Height - ft.Height) / 2));
    }

    private void DrawMessage(DrawingContext ctx, double w)
    {
        if (_message is null) return;
        bool ended = _phase != Phase.Playing;
        double size = ended ? 22 : 14;
        var brush = _phase == Phase.Lost ? Palette.ErrorBrush : ended ? Palette.RunningBrush : Palette.FgBrush;
        var ft = OverlayDraw.Text(_message, size, brush, FontWeight.Bold);

        double a = ended ? 0.6 + 0.4 * Math.Abs(Math.Sin(_pulse)) : 1.0;
        using (ctx.PushOpacity(a))
            ctx.DrawText(ft, new Point((w - ft.Width) / 2, MsgY - ft.Height / 2));
    }

    private void DrawKeyboard(DrawingContext ctx)
    {
        var kb = WordleGame.KeyboardState(_guesses, _answer);

        foreach (var (c, rect) in _keyRects)
        {
            IBrush fill; IBrush fg;
            if (kb.TryGetValue(c, out var m)) (fill, fg) = MarkColors(m);
            else { fill = Palette.ButtonBgBrush; fg = Palette.FgBrush; }
            DrawKey(ctx, rect, c.ToString(), fill, fg, 16);
        }

        // The two wide action keys always read as neutral chrome (smaller type so the words fit their box).
        DrawKey(ctx, _enterRect, EnterLabel, Palette.ButtonBgBrush, Palette.FgBrush, 13);
        DrawKey(ctx, _backRect, BackLabel, Palette.ButtonBgBrush, Palette.FgBrush, 13);
    }

    private static void DrawKey(DrawingContext ctx, Rect rect, string label, IBrush fill, IBrush fg, double size)
    {
        OverlayDraw.Panel(ctx, rect, fill, null, 6);
        var ft = OverlayDraw.Text(label, size, fg, FontWeight.Bold);
        ctx.DrawText(ft, new Point(rect.X + (rect.Width - ft.Width) / 2, rect.Y + (rect.Height - ft.Height) / 2));
    }

    // The tile/key fill and its best-contrast foreground for each mark, resolved through the active theme so a
    // theme swap recolours them. Correct → green, Present → yellow, Absent → neutral grey.
    private static (IBrush fill, IBrush fg) MarkColors(WordleMark m)
    {
        Rgb bg = m switch
        {
            WordleMark.Correct => Palette.Active.StatusRunning,
            WordleMark.Present => Palette.Active.StatusAwaiting,
            _ => Palette.Active.TeamGray,
        };
        IBrush fill = m switch
        {
            WordleMark.Correct => Palette.RunningBrush,
            WordleMark.Present => Palette.AwaitingBrush,
            _ => Palette.TeamGrayBrush,
        };
        return (fill, new SolidColorBrush(Contrast.BestForeground(bg).ToColor()));
    }

    /// <summary>Poses a representative mid-play board for headless snapshots (timers don't tick under the
    /// render harness). A couple of scored guesses plus a partially-typed active row.</summary>
    internal void SnapshotPlaying()
    {
        _answer = "crane";
        _today = new DateOnly(2026, 8, 21);
        _guesses.Clear();
        _guesses.Add("slate");
        _guesses.Add("brace");
        _current = "cr";
        _phase = Phase.Playing;
        _message = null;
        InvalidateVisual();
    }
}
