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

    public Task<GameRequest> RequestGameAsync(Guid opponentUserId, int firstColumn, CancellationToken ct = default)
    {
        GameRequest request;
        Guid from;
        string? fromHandle;
        lock (_gate)
        {
            RequireMe();
            if (opponentUserId == _me!.Id) throw new SocialException("You can't play yourself.");
            if (firstColumn is < 0 or > 6) throw new SocialException("That column is off the board.");
            bool friends = _edges.TryGetValue(opponentUserId, out var s)
                           && s == FriendshipState.Accepted && !_blocked.Contains(opponentUserId);
            if (!friends) throw new SocialException("You can only invite an accepted friend.");
            if (_gameRequests.Values.Any(r => r.Requester == _me.Id && r.Addressee == opponentUserId))
                throw new SocialException("You've already invited them.");

            var fr = new FakeRequest { Requester = _me.Id, Addressee = opponentUserId, FirstCol = firstColumn };
            _gameRequests[fr.Id] = fr;
            request = RequestModelLocked(fr);
            from = _me.Id; fromHandle = _me.Handle;
        }
        FakeInboxBus.Deliver(opponentUserId,
            new InboxMessage(InboxKind.GameInvite, from, fromHandle, RequestId: request.Id));
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
        Guid me, inviter;
        string? myHandle;
        lock (_gate)
        {
            RequireMe();
            if (!_gameRequests.TryGetValue(requestId, out var fr))
                throw new SocialException("That invite is no longer available.");
            if (fr.Addressee != _me!.Id) throw new SocialException("Only the invitee can accept.");

            // Seed the inviter's first move so it's immediately the accepter's turn — mirrors accept_game_request.
            var fg = new FakeGame { Red = fr.Requester, Yellow = fr.Addressee };
            fg.Moves.Add(fr.FirstCol);
            _games[fg.Id] = fg;
            _gameRequests.Remove(requestId);
            state = StateLocked(fg);
            me = _me.Id; myHandle = _me.Handle; inviter = fr.Requester;
        }
        FakeInboxBus.Deliver(inviter,
            new InboxMessage(InboxKind.GameInviteAccepted, me, myHandle, GameId: state.Summary.Id));
        return Task.FromResult(state);
    }

    public Task DeclineGameRequestAsync(Guid requestId, CancellationToken ct = default)
    {
        Guid me = Guid.Empty, other = Guid.Empty;
        string? myHandle = null;
        bool removed = false;
        lock (_gate)
        {
            RequireMe();
            // Only remove a request you're part of; a missing one is a no-op (idempotent).
            if (_gameRequests.TryGetValue(requestId, out var fr) && (fr.Requester == _me!.Id || fr.Addressee == _me.Id))
            {
                _gameRequests.Remove(requestId);
                me = _me.Id; myHandle = _me.Handle;
                other = fr.Requester == _me.Id ? fr.Addressee : fr.Requester;
                removed = true;
            }
        }
        if (removed)
            FakeInboxBus.Deliver(other,
                new InboxMessage(InboxKind.GameInviteDeclined, me, myHandle, RequestId: requestId));
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

    public IDisposable SubscribeInbox(Action<InboxMessage> onMessage)
    {
        Guid me;
        lock (_gate) { RequireMe(); me = _me!.Id; }
        return FakeInboxBus.Subscribe(me, onMessage);
    }

    public Task SendNudgeAsync(Guid gameId, Guid opponentUserId, CancellationToken ct = default)
    {
        Guid me; string? myHandle;
        lock (_gate) { RequireMe(); me = _me!.Id; myHandle = _me.Handle; }
        FakeInboxBus.Deliver(opponentUserId, new InboxMessage(InboxKind.Nudge, me, myHandle, GameId: gameId));
        return Task.CompletedTask;
    }

    // ── Test/preview seams (not part of ISocialClient) ──────────────────────────────────────────────

    /// <summary>Simulates <paramref name="fromUserId"/> inviting you to a game (shows as an incoming request),
    /// carrying their opening move in <paramref name="firstCol"/> (seeded on accept, as the real flow does).</summary>
    public GameRequest SimulateIncomingGameRequest(Guid fromUserId, int firstCol = 3)
    {
        lock (_gate)
        {
            RequireMe();
            var fr = new FakeRequest { Requester = fromUserId, Addressee = _me!.Id, FirstCol = firstCol };
            _gameRequests[fr.Id] = fr;
            return RequestModelLocked(fr);
        }
    }

    /// <summary>Delivers a message straight to the signed-in user's inbox — the test/preview seam for the
    /// transient broadcast inbox (mirrors what a friend's client would broadcast).</summary>
    public void SimulateInbox(InboxMessage message)
    {
        Guid me;
        lock (_gate) { RequireMe(); me = _me!.Id; }
        FakeInboxBus.Deliver(me, message);
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
        public int FirstCol { get; init; }             // the requester's first move, seeded into the game on accept
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

    // A tiny in-process stand-in for the Realtime broadcast inbox, keyed by user id, so that two FakeSocialClient
    // instances (as in a two-player test) can exchange invites/nudges just like the real cross-client broadcast.
    // Subscribers are keyed by the (unique) user GUIDs, so there's no cross-test bleed as long as tests dispose.
    private static class FakeInboxBus
    {
        private static readonly object Gate = new();
        private static readonly Dictionary<Guid, List<Action<InboxMessage>>> Subs = new();

        public static IDisposable Subscribe(Guid userId, Action<InboxMessage> cb)
        {
            lock (Gate)
            {
                if (!Subs.TryGetValue(userId, out var list)) Subs[userId] = list = new();
                list.Add(cb);
            }
            return new Subscription(userId, cb);
        }

        public static void Deliver(Guid userId, InboxMessage msg)
        {
            Action<InboxMessage>[] targets;
            lock (Gate)
            {
                if (!Subs.TryGetValue(userId, out var list)) return;
                targets = list.ToArray();
            }
            foreach (var t in targets) { try { t(msg); } catch { } }
        }

        private sealed class Subscription : IDisposable
        {
            private readonly Guid _userId;
            private readonly Action<InboxMessage> _cb;
            public Subscription(Guid userId, Action<InboxMessage> cb) { _userId = userId; _cb = cb; }
            public void Dispose()
            {
                lock (Gate)
                    if (Subs.TryGetValue(_userId, out var list)) { list.Remove(_cb); if (list.Count == 0) Subs.Remove(_userId); }
            }
        }
    }
}
