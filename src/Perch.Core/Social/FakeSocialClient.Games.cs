using Perch.Games;

namespace Perch.Social;

/// <summary>
/// The in-memory networked-Connect-4 surface of <see cref="FakeSocialClient"/> — the same rules the Supabase
/// backend enforces, so tests prove the contract without a database: you can only start a game with an accepted
/// friend, moves are validated (whose turn, legal column, game not over), and both players see one authoritative
/// move list. State is derived from that move list via the pure <see cref="Connect4Game"/> engine, exactly as
/// the real client does. Adds <c>Simulate*</c> seams so a test can have the opponent move.
/// </summary>
public sealed partial class FakeSocialClient
{
    private readonly Dictionary<Guid, FakeGame> _games = new();
    private readonly Dictionary<Guid, List<Action>> _gameSubs = new();
    private readonly Dictionary<Guid, FakeRequest> _gameRequests = new();

    public Task<GameRequest> RequestGameAsync(Guid opponentUserId, CancellationToken ct = default)
    {
        GameRequest request;
        lock (_gate)
        {
            RequireMe();
            if (opponentUserId == _me!.Id) throw new SocialException("You can't play yourself.");
            bool friends = _edges.TryGetValue(opponentUserId, out var s)
                           && s == FriendshipState.Accepted && !_blocked.Contains(opponentUserId);
            if (!friends) throw new SocialException("You can only invite an accepted friend.");
            if (_gameRequests.Values.Any(r => r.Requester == _me.Id && r.Addressee == opponentUserId))
                throw new SocialException("You've already invited them.");

            var fr = new FakeRequest { Requester = _me.Id, Addressee = opponentUserId };
            _gameRequests[fr.Id] = fr;
            request = RequestModelLocked(fr);
        }
        return Task.FromResult(request);
    }

    public Task<IReadOnlyList<GameRequest>> GetGameRequestsAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            RequireMe();
            var list = _gameRequests.Values
                .Where(r => r.Requester == _me!.Id || r.Addressee == _me.Id)
                .OrderByDescending(r => r.Created)
                .Select(RequestModelLocked)
                .ToList();
            return Task.FromResult<IReadOnlyList<GameRequest>>(list);
        }
    }

    public Task<GameState> AcceptGameRequestAsync(Guid requestId, CancellationToken ct = default)
    {
        GameState state;
        lock (_gate)
        {
            RequireMe();
            if (!_gameRequests.TryGetValue(requestId, out var fr))
                throw new SocialException("That invite is no longer available.");
            if (fr.Addressee != _me!.Id) throw new SocialException("Only the invitee can accept.");

            var fg = new FakeGame { Red = fr.Requester, Yellow = fr.Addressee };
            _games[fg.Id] = fg;
            _gameRequests.Remove(requestId);
            state = StateLocked(fg);
        }
        return Task.FromResult(state);
    }

    public Task DeclineGameRequestAsync(Guid requestId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            RequireMe();
            // Only remove a request you're part of; a missing one is a no-op (idempotent).
            if (_gameRequests.TryGetValue(requestId, out var fr) && (fr.Requester == _me!.Id || fr.Addressee == _me.Id))
                _gameRequests.Remove(requestId);
        }
        return Task.CompletedTask;
    }

    public Task<GameSummary> CreateGameAsync(Guid opponentUserId, CancellationToken ct = default)
    {
        GameSummary summary;
        lock (_gate)
        {
            RequireMe();
            if (opponentUserId == _me!.Id) throw new SocialException("You can't play yourself.");
            bool friends = _edges.TryGetValue(opponentUserId, out var s)
                           && s == FriendshipState.Accepted && !_blocked.Contains(opponentUserId);
            if (!friends) throw new SocialException("You can only play an accepted friend.");

            var fg = new FakeGame { Red = _me.Id, Yellow = opponentUserId };
            _games[fg.Id] = fg;
            summary = SummaryLocked(fg);
        }
        return Task.FromResult(summary);
    }

    public Task<IReadOnlyList<GameSummary>> GetGamesAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            RequireMe();
            var list = _games.Values
                .Where(fg => fg.Red == _me!.Id || fg.Yellow == _me.Id)
                .OrderByDescending(fg => fg.Updated)
                .Select(SummaryLocked)
                .ToList();
            return Task.FromResult<IReadOnlyList<GameSummary>>(list);
        }
    }

    public Task<GameState> GetGameAsync(Guid gameId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var fg = RequireMyGameLocked(gameId);
            return Task.FromResult(StateLocked(fg));
        }
    }

    public Task<GameState> DropAsync(Guid gameId, int column, CancellationToken ct = default)
    {
        GameState state;
        lock (_gate)
        {
            var fg = RequireMyGameLocked(gameId);
            if (fg.Forced is not null) throw new SocialException("The game is over.");

            var g = Connect4Game.Replay(fg.Moves);
            if (g.IsOver) throw new SocialException("The game is over.");
            var seat = _me!.Id == fg.Red ? Connect4Disc.Red : Connect4Disc.Yellow;
            if (g.Turn != seat) throw new SocialException("It's not your turn.");
            if (!g.CanDrop(column)) throw new SocialException("That column is full.");

            fg.Moves.Add(column);
            fg.Updated = DateTimeOffset.UtcNow;
            state = StateLocked(fg);
        }
        NotifyGame(gameId);
        return Task.FromResult(state);
    }

    public Task<GameState> ResignGameAsync(Guid gameId, CancellationToken ct = default)
    {
        GameState state;
        lock (_gate)
        {
            var fg = RequireMyGameLocked(gameId);
            var g = Connect4Game.Replay(fg.Moves);
            if (fg.Forced is null && !g.IsOver)
            {
                // Resigning hands the win to the opponent.
                fg.Forced = _me!.Id == fg.Red ? GameStatus.YellowWon : GameStatus.RedWon;
                fg.Updated = DateTimeOffset.UtcNow;
            }
            state = StateLocked(fg);
        }
        NotifyGame(gameId);
        return Task.FromResult(state);
    }

    public Task DeleteGameAsync(Guid gameId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            RequireMe();
            // Only remove a game you're actually in; a missing/other game is a no-op (idempotent).
            if (_games.TryGetValue(gameId, out var fg) && (fg.Red == _me!.Id || fg.Yellow == _me.Id))
            {
                _games.Remove(gameId);
                _gameSubs.Remove(gameId);
            }
        }
        return Task.CompletedTask;
    }

    public IDisposable SubscribeGame(Guid gameId, Action onChanged)
    {
        lock (_gate)
        {
            if (!_gameSubs.TryGetValue(gameId, out var list)) _gameSubs[gameId] = list = new();
            list.Add(onChanged);
        }
        return new GameSubscription(this, gameId, onChanged);
    }

    // ── Test/preview seams (not part of ISocialClient) ──────────────────────────────────────────────

    /// <summary>Simulates <paramref name="fromUserId"/> inviting you to a game (shows as an incoming request).</summary>
    public GameRequest SimulateIncomingGameRequest(Guid fromUserId)
    {
        lock (_gate)
        {
            RequireMe();
            var fr = new FakeRequest { Requester = fromUserId, Addressee = _me!.Id };
            _gameRequests[fr.Id] = fr;
            return RequestModelLocked(fr);
        }
    }

    /// <summary>Simulates the opponent dropping into <paramref name="column"/> — applies a move for whoever's
    /// turn it currently is (so call it after your own move) and fires the game's subscribers.</summary>
    public void SimulateOpponentDrop(Guid gameId, int column)
    {
        lock (_gate)
        {
            if (!_games.TryGetValue(gameId, out var fg)) throw new SocialException("Unknown game.");
            var g = Connect4Game.Replay(fg.Moves);
            if (g.IsOver || fg.Forced is not null) throw new SocialException("The game is over.");
            if (!g.CanDrop(column)) throw new SocialException("That column is full.");
            fg.Moves.Add(column);
            fg.Updated = DateTimeOffset.UtcNow;
        }
        NotifyGame(gameId);
    }

    // ── internals ──────────────────────────────────────────────────────────────────────────────────

    private FakeGame RequireMyGameLocked(Guid gameId)
    {
        RequireMe();
        if (!_games.TryGetValue(gameId, out var fg) || (fg.Red != _me!.Id && fg.Yellow != _me.Id))
            throw new SocialException("No such game.");
        return fg;
    }

    private GameSummary SummaryLocked(FakeGame fg)
    {
        var g = Connect4Game.Replay(fg.Moves);
        var status = fg.Forced ?? MapStatus(g.Status);
        var red = _profiles.GetValueOrDefault(fg.Red) ?? new Profile(fg.Red, "unknown");
        var yellow = _profiles.GetValueOrDefault(fg.Yellow) ?? new Profile(fg.Yellow, "unknown");
        return new GameSummary(fg.Id, red, yellow, status, g.Turn, g.MoveCount, fg.Updated);
    }

    private GameState StateLocked(FakeGame fg) => new(SummaryLocked(fg), fg.Moves.ToList());

    private GameRequest RequestModelLocked(FakeRequest fr)
    {
        var requester = _profiles.GetValueOrDefault(fr.Requester) ?? new Profile(fr.Requester, "unknown");
        var addressee = _profiles.GetValueOrDefault(fr.Addressee) ?? new Profile(fr.Addressee, "unknown");
        return new GameRequest(fr.Id, requester, addressee, fr.Created);
    }

    private static GameStatus MapStatus(Connect4Status s) => s switch
    {
        Connect4Status.RedWon => GameStatus.RedWon,
        Connect4Status.YellowWon => GameStatus.YellowWon,
        Connect4Status.Draw => GameStatus.Draw,
        _ => GameStatus.InProgress,
    };

    private void NotifyGame(Guid gameId)
    {
        Action[] subs;
        lock (_gate)
        {
            if (!_gameSubs.TryGetValue(gameId, out var list)) return;
            subs = list.ToArray();
        }
        foreach (var s in subs) { try { s(); } catch { } }
    }

    private sealed class FakeGame
    {
        public Guid Id { get; } = Guid.NewGuid();
        public Guid Red { get; init; }
        public Guid Yellow { get; init; }
        public List<int> Moves { get; } = new();
        public GameStatus? Forced { get; set; }        // set by resign; otherwise status is derived from Moves
        public DateTimeOffset Updated { get; set; } = DateTimeOffset.UtcNow;
    }

    private sealed class FakeRequest
    {
        public Guid Id { get; } = Guid.NewGuid();
        public Guid Requester { get; init; }
        public Guid Addressee { get; init; }
        public DateTimeOffset Created { get; } = DateTimeOffset.UtcNow;
    }

    private sealed class GameSubscription : IDisposable
    {
        private readonly FakeSocialClient _owner;
        private readonly Guid _gameId;
        private readonly Action _cb;
        public GameSubscription(FakeSocialClient owner, Guid gameId, Action cb) { _owner = owner; _gameId = gameId; _cb = cb; }
        public void Dispose()
        {
            lock (_owner._gate)
                if (_owner._gameSubs.TryGetValue(_gameId, out var list)) list.Remove(_cb);
        }
    }
}
