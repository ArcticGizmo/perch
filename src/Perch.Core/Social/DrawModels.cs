using Perch.Games;

namespace Perch.Social;

/// <summary>Where a networked "Draw with Perch" game stands, mirroring the backend's <c>draw_status</c> enum.</summary>
public enum DrawGameStatus
{
    /// <summary>Still being played — an endless back-and-forth of rounds.</summary>
    InProgress,
    /// <summary>A player resigned (or the game was otherwise abandoned).</summary>
    Abandoned,
}

/// <summary>Which half of a turn the player-to-act is on. Roles swap each round: the guesser of one round draws
/// the next, so a single player is "up" for a guess and then a draw before it passes back.</summary>
public enum DrawPhase
{
    /// <summary>The player-to-act must pick a word and draw the next round.</summary>
    Draw,
    /// <summary>The player-to-act must guess the drawing that's waiting for them.</summary>
    Guess,
}

/// <summary>How the current round ended (or that it's still open).</summary>
public enum DrawRoundStatus
{
    /// <summary>The drawing is in, the guesser is still guessing.</summary>
    Guessing,
    /// <summary>The guesser got it — points were awarded.</summary>
    Solved,
    /// <summary>The guesser gave up — the word was revealed, no points.</summary>
    GaveUp,
}

/// <summary>
/// A networked Draw-with-Perch game between two friends, as the signed-in client sees it — the pairing, the
/// running scores, whose turn it is and whether they draw or guess next. The lightweight row used to list games;
/// the drawing/word for the active round comes from <see cref="DrawGameState"/>.
/// </summary>
/// <param name="Id">Server id of the game.</param>
/// <param name="PlayerA">The player who started it (drew the very first round).</param>
/// <param name="PlayerB">The player who was invited.</param>
/// <param name="Status">The game's current state.</param>
/// <param name="WhoseTurn">The user who must act next (draw or guess, per <see cref="Phase"/>).</param>
/// <param name="Phase">Whether <see cref="WhoseTurn"/> draws or guesses next.</param>
/// <param name="ScoreA">Player A's running total.</param>
/// <param name="ScoreB">Player B's running total.</param>
/// <param name="RoundNo">How many rounds have been drawn (1-based once the game is live).</param>
/// <param name="UpdatedAt">When the game last changed (server time) — used to order "your turn" lists.</param>
public sealed record DrawGameSummary(
    Guid Id, Profile PlayerA, Profile PlayerB, DrawGameStatus Status, Guid WhoseTurn, DrawPhase Phase,
    int ScoreA, int ScoreB, int RoundNo, DateTimeOffset UpdatedAt)
{
    /// <summary>The other player from <paramref name="userId"/>'s point of view, or null if they aren't in it.</summary>
    public Profile? Opponent(Guid userId) =>
        userId == PlayerA.Id ? PlayerB : userId == PlayerB.Id ? PlayerA : null;

    /// <summary>True when it's <paramref name="userId"/>'s turn (the game is live and they are the one to act).</summary>
    public bool IsTurnOf(Guid userId) => Status == DrawGameStatus.InProgress && WhoseTurn == userId;

    /// <summary><paramref name="userId"/>'s running score (0 if they aren't in the game).</summary>
    public int MyScore(Guid userId) =>
        userId == PlayerA.Id ? ScoreA : userId == PlayerB.Id ? ScoreB : 0;

    /// <summary>The opponent's running score from <paramref name="userId"/>'s point of view.</summary>
    public int TheirScore(Guid userId) =>
        userId == PlayerA.Id ? ScoreB : userId == PlayerB.Id ? ScoreA : 0;
}

/// <summary>
/// One round of a game: who's drawing for whom, the difficulty and letter-count hint, the drawing itself, and —
/// once resolved — the guesses, outcome and points. The <see cref="Word"/> is only populated for the drawer, or
/// for the guesser once the round is over (the guess view shows blanks from <see cref="LetterHint"/> until then;
/// secrecy is by convention, not enforced — the game is for fun).
/// </summary>
public sealed record DrawRound(
    Guid Id, Guid GameId, int RoundNo, Profile Drawer, Profile Guesser, DrawDifficulty Difficulty,
    string? Word, string LetterHint, IReadOnlyList<DrawStroke> Strokes, DrawRoundStatus Status,
    IReadOnlyList<string> Guesses, int PointsDrawer, int PointsGuesser);

/// <summary>
/// The full state the acting player interacts with: the game <see cref="Summary"/> plus the <see cref="Current"/>
/// round (the one to draw for, or to guess). Null <see cref="Current"/> means no round exists yet (shouldn't
/// happen once a game is live, since accepting an invite seeds round 1).
/// </summary>
public sealed record DrawGameState(DrawGameSummary Summary, DrawRound? Current);

/// <summary>
/// A pending Draw-with-Perch invite — it carries the challenger's <em>first drawing</em> (drawn before sending,
/// like Connect 4's opening move), so the invitee lands straight on a drawing to guess. Exists only until
/// accepted (which creates the game + round 1) or declined/cancelled (which deletes it).
/// </summary>
/// <param name="Id">Server id of the request.</param>
/// <param name="Requester">Who sent the invite (drew the first round, plays player A).</param>
/// <param name="Addressee">Who must accept it (guesses the first round, plays player B).</param>
/// <param name="Difficulty">The tier the challenger chose to draw.</param>
/// <param name="LetterHint">The letter-count hint for the first drawing.</param>
/// <param name="CreatedAt">When the invite was sent (server time).</param>
public sealed record DrawRequest(
    Guid Id, Profile Requester, Profile Addressee, DrawDifficulty Difficulty, string LetterHint,
    DateTimeOffset CreatedAt)
{
    /// <summary>True when <paramref name="userId"/> is the invitee — i.e. this invite is waiting on them.</summary>
    public bool IsIncoming(Guid userId) => Addressee.Id == userId;

    /// <summary>The other party from <paramref name="userId"/>'s point of view.</summary>
    public Profile Other(Guid userId) => Requester.Id == userId ? Addressee : Requester;
}
