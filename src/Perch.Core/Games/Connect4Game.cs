namespace Perch.Games;

/// <summary>Which disc occupies a board cell (also names the two players). <see cref="None"/> is an empty cell.</summary>
public enum Connect4Disc { None, Red, Yellow }

/// <summary>The game's state: still being played, won by one colour, or drawn (board full, no line of four).</summary>
public enum Connect4Status { InProgress, RedWon, YellowWon, Draw }

/// <summary>
/// The pure, UI-free engine behind the secret Connect 4 toy (see <c>Connect4Window</c> in the app head): a
/// classic seven-column, six-row board on which two players alternate dropping discs, first to align four in
/// a row — horizontally, vertically or diagonally — wins. Kept in <c>Perch.Core</c> so it's testable and
/// head-agnostic (nothing here touches Avalonia, the filesystem or the network), and so the eventual
/// networked "play a friend" mode can drive the very same rules from move records. Unlike the stateless
/// <see cref="WordleGame"/> this is a small stateful object — the board mutates as discs land — but every
/// method is deterministic.
/// </summary>
public sealed class Connect4Game
{
    public const int Cols = 7;
    public const int Rows = 6;

    // [col, row]; row 0 is the bottom of the board — the first cell a disc dropped into an empty column fills.
    private readonly Connect4Disc[,] _cells = new Connect4Disc[Cols, Rows];
    private readonly List<(int Col, int Row)> _winningLine = new();

    /// <summary>Whose disc plays next. Red always moves first.</summary>
    public Connect4Disc Turn { get; private set; } = Connect4Disc.Red;

    /// <summary>Where the game stands after the last move.</summary>
    public Connect4Status Status { get; private set; } = Connect4Status.InProgress;

    /// <summary>How many discs have been dropped in total (0..<see cref="Cols"/>*<see cref="Rows"/>).</summary>
    public int MoveCount { get; private set; }

    /// <summary>True once the game is decided (a win or a draw) — no further drops are accepted.</summary>
    public bool IsOver => Status != Connect4Status.InProgress;

    /// <summary>The aligned cells that formed the winning four (bottom-/left-most first); empty until a win.
    /// A run longer than four — e.g. five in a row — is reported in full.</summary>
    public IReadOnlyList<(int Col, int Row)> WinningLine => _winningLine;

    /// <summary>The disc at <paramref name="col"/>,<paramref name="row"/> (row 0 = bottom).</summary>
    public Connect4Disc CellAt(int col, int row) => _cells[col, row];

    /// <summary>True when a disc can still be dropped into <paramref name="col"/> — the game is live, the
    /// column index is in range, and its top cell is empty.</summary>
    public bool CanDrop(int col) =>
        !IsOver && col >= 0 && col < Cols && _cells[col, Rows - 1] == Connect4Disc.None;

    /// <summary>The row a disc would settle in if dropped into <paramref name="col"/> right now, or -1 if the
    /// column is full or the index is out of range. A pure query — it does not mutate the board.</summary>
    public int LandingRow(int col)
    {
        if (col < 0 || col >= Cols) return -1;
        for (int r = 0; r < Rows; r++)
            if (_cells[col, r] == Connect4Disc.None) return r;
        return -1;
    }

    /// <summary>Drops the current player's disc into <paramref name="col"/>. Returns the row it settled in, or
    /// -1 if the move was illegal (game already over, bad column, or column full). On a legal move it records
    /// the disc, then either declares a win (updating <see cref="Status"/> and <see cref="WinningLine"/>),
    /// declares a draw if the board is now full, or advances <see cref="Turn"/> to the other player.</summary>
    public int Drop(int col)
    {
        if (IsOver) return -1;
        int row = LandingRow(col);
        if (row < 0) return -1;

        var disc = Turn;
        _cells[col, row] = disc;
        MoveCount++;

        if (FormsWin(col, row, disc))
            Status = disc == Connect4Disc.Red ? Connect4Status.RedWon : Connect4Status.YellowWon;
        else if (MoveCount >= Cols * Rows)
            Status = Connect4Status.Draw;
        else
            Turn = Other(disc);

        return row;
    }

    /// <summary>Would dropping <paramref name="disc"/> into <paramref name="col"/> immediately complete a four?
    /// A non-mutating hypothetical (it places and un-places internally) that ignores whose turn it actually is
    /// — used by <see cref="Connect4Ai"/> to spot wins and blocks. Returns false for a full/invalid column.</summary>
    public bool WouldWin(int col, Connect4Disc disc)
    {
        int row = LandingRow(col);
        if (row < 0) return false;
        _cells[col, row] = disc;
        bool win = WinLineThrough(col, row, disc) is not null;
        _cells[col, row] = Connect4Disc.None;
        return win;
    }

    /// <summary>Clears the board back to an empty, Red-to-move opening.</summary>
    public void Reset()
    {
        Array.Clear(_cells);
        _winningLine.Clear();
        Turn = Connect4Disc.Red;
        Status = Connect4Status.InProgress;
        MoveCount = 0;
    }

    /// <summary>The opposing colour.</summary>
    public static Connect4Disc Other(Connect4Disc d) =>
        d == Connect4Disc.Red ? Connect4Disc.Yellow : Connect4Disc.Red;

    /// <summary>Rebuilds a game by replaying a sequence of dropped columns in order (move 0 = Red, then
    /// alternating). Stops once the game is decided; a column that isn't a legal move throws
    /// <see cref="ArgumentException"/> (the networked backend guarantees legality, so a throw means the move
    /// list is corrupt). Pure — the networked "play a friend" mode uses this to reconstruct authoritative
    /// state from a game's move list.</summary>
    public static Connect4Game Replay(IEnumerable<int> columns)
    {
        var g = new Connect4Game();
        foreach (int col in columns)
        {
            if (g.IsOver) break;
            if (g.Drop(col) < 0) throw new ArgumentException($"illegal move replaying column {col}", nameof(columns));
        }
        return g;
    }

    // The four axes to test through a freshly placed disc: horizontal, vertical, and the two diagonals. Each
    // is walked in both directions, so testing one representative per axis covers all four line orientations.
    private static readonly (int dc, int dr)[] Axes = { (1, 0), (0, 1), (1, 1), (1, -1) };

    // Records a win through (col,row) if there is one, then reports whether there was.
    private bool FormsWin(int col, int row, Connect4Disc disc)
    {
        var line = WinLineThrough(col, row, disc);
        if (line is null) return false;
        _winningLine.Clear();
        _winningLine.AddRange(line);
        return true;
    }

    // The full run of same-disc cells through (col,row) along whichever axis reaches four or more, or null if
    // none does. Walks to the far end in the negative direction, then counts forward, so the returned list is
    // ordered along the axis. Does not mutate any field (safe for the WouldWin hypothetical).
    private List<(int, int)>? WinLineThrough(int col, int row, Connect4Disc disc)
    {
        foreach (var (dc, dr) in Axes)
        {
            var line = new List<(int, int)> { (col, row) };
            for (int s = 1; InBounds(col - dc * s, row - dr * s) && _cells[col - dc * s, row - dr * s] == disc; s++)
                line.Insert(0, (col - dc * s, row - dr * s));
            for (int s = 1; InBounds(col + dc * s, row + dr * s) && _cells[col + dc * s, row + dr * s] == disc; s++)
                line.Add((col + dc * s, row + dr * s));

            if (line.Count >= 4) return line;
        }
        return null;
    }

    private static bool InBounds(int c, int r) => c >= 0 && c < Cols && r >= 0 && r < Rows;
}

/// <summary>
/// A small, deterministic Connect 4 opponent for local "vs computer" play. Pure and testable — no randomness,
/// so a given position always yields the same move. The strategy, in order: take an immediate winning move;
/// otherwise block the opponent's immediate winning move; otherwise play the most central legal column (the
/// centre file is the strongest square, and preferring it makes the bot a passable, if beatable, sparring
/// partner without a full search).
/// </summary>
public static class Connect4Ai
{
    // Columns ranked by how central they are: the middle first, then outward symmetrically.
    private static readonly int[] CenterOut = { 3, 2, 4, 1, 5, 0, 6 };

    /// <summary>Chooses a column for <see cref="Connect4Game.Turn"/> to play, or -1 if the game is over or the
    /// board is full.</summary>
    public static int SuggestMove(Connect4Game game)
    {
        if (game.IsOver) return -1;
        var me = game.Turn;
        var foe = Connect4Game.Other(me);

        int win = FindImmediateWin(game, me);
        if (win >= 0) return win;

        int block = FindImmediateWin(game, foe);
        if (block >= 0) return block;

        foreach (int c in CenterOut)
            if (game.CanDrop(c)) return c;
        return -1;
    }

    // The lowest-indexed column where dropping `disc` would win outright, or -1 if there is none.
    private static int FindImmediateWin(Connect4Game game, Connect4Disc disc)
    {
        for (int c = 0; c < Connect4Game.Cols; c++)
            if (game.CanDrop(c) && game.WouldWin(c, disc)) return c;
        return -1;
    }
}
