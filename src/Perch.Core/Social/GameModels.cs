using Perch.Games;

namespace Perch.Social;

/// <summary>Where a networked Connect 4 game stands, mirroring the backend's <c>game_status</c> enum.</summary>
public enum GameStatus
{
    /// <summary>Still being played.</summary>
    InProgress,
    /// <summary>Red completed four in a row.</summary>
    RedWon,
    /// <summary>Yellow completed four in a row.</summary>
    YellowWon,
    /// <summary>The board filled with no winner.</summary>
    Draw,
    /// <summary>A player resigned (or the game was otherwise abandoned).</summary>
    Abandoned,
}

/// <summary>
/// A networked Connect 4 game between two friends, as the signed-in client sees it — the pairing, whose turn
/// it is, and the outcome. The creator is always <see cref="Red"/> and moves first. This is the lightweight
/// row used to list games; the full board comes from <see cref="GameState"/>.
/// </summary>
/// <param name="Id">Server id of the game.</param>
/// <param name="Red">The red player (the creator, who moves first).</param>
/// <param name="Yellow">The yellow player (the invitee).</param>
/// <param name="Status">The game's current state.</param>
/// <param name="Turn">Whose disc plays next (meaningful only while <see cref="Status"/> is in progress).</param>
/// <param name="MoveCount">How many discs have been dropped.</param>
/// <param name="UpdatedAt">When the game last changed (server time) — used to order "your turn" lists.</param>
public sealed record GameSummary(
    Guid Id, Profile Red, Profile Yellow, GameStatus Status, Connect4Disc Turn, int MoveCount, DateTimeOffset UpdatedAt)
{
    /// <summary>The disc <paramref name="userId"/> plays as, or <see cref="Connect4Disc.None"/> if they aren't
    /// one of the two players.</summary>
    public Connect4Disc Seat(Guid userId) =>
        userId == Red.Id ? Connect4Disc.Red : userId == Yellow.Id ? Connect4Disc.Yellow : Connect4Disc.None;

    /// <summary>The other player from <paramref name="userId"/>'s point of view (their opponent), or null if
    /// <paramref name="userId"/> isn't in this game.</summary>
    public Profile? Opponent(Guid userId) =>
        userId == Red.Id ? Yellow : userId == Yellow.Id ? Red : null;

    /// <summary>True when it is <paramref name="userId"/>'s move (the game is live and it's their colour's turn).</summary>
    public bool IsTurnOf(Guid userId) => Status == GameStatus.InProgress && Seat(userId) == Turn;
}

/// <summary>
/// The full state of a networked game: its <see cref="Summary"/> plus the ordered list of dropped columns
/// (move 0 = red). The board is reconstructed on demand from the moves via the pure engine, so the client
/// never re-implements the rules — it renders and validates from the authoritative move list the server
/// returned.
/// </summary>
/// <param name="Summary">The pairing / status / turn.</param>
/// <param name="Moves">The dropped columns in play order (each 0–6).</param>
public sealed record GameState(GameSummary Summary, IReadOnlyList<int> Moves)
{
    /// <summary>Replays the moves into a fresh engine, so the UI can draw the board and know the winning line.
    /// The engine's own status/turn will agree with <see cref="Summary"/> for a well-formed game.</summary>
    public Connect4Game ToGame() => Connect4Game.Replay(Moves);
}
