using Perch.Games;

namespace Perch.Social;

/// <summary>
/// The in-memory "Draw with Perch" surface of <see cref="FakeSocialClient"/> — the same rules the Supabase
/// backend enforces, so tests prove the contract without a database: you can only challenge an accepted friend,
/// the invite carries the challenger's first drawing (seeded as round 1 on accept), turns and phases are
/// validated (draw vs guess, whose turn), guesses are checked with <see cref="DrawGuessing"/> and scored with
/// <see cref="DrawScoring"/>, and the roles swap every round. Adds <c>Simulate*</c> seams so a test (or the
/// render preview) can have the opponent draw or guess.
/// </summary>
public sealed partial class FakeSocialClient
{
    private readonly Dictionary<Guid, FakeDrawGame> _drawGames = new();
    private readonly Dictionary<Guid, List<Action>> _drawSubs = new();
    private readonly Dictionary<Guid, FakeDrawRequest> _drawRequests = new();

    public Task<DrawRequest> RequestDrawGameAsync(Guid opponentUserId, DrawDifficulty difficulty, string word,
        string letterHint, IReadOnlyList<DrawStroke> strokes, CancellationToken ct = default)
    {
        DrawRequest request;
        Guid from; string? fromHandle;
        lock (_gate)
        {
            RequireMe();
            if (opponentUserId == _me!.Id) throw new SocialException("You can't play yourself.");
            if (string.IsNullOrWhiteSpace(word)) throw new SocialException("Pick a word to draw first.");
            if (!AreFriendsLocked(opponentUserId)) throw new SocialException("You can only challenge an accepted friend.");
            if (_drawRequests.Values.Any(r => r.Requester == _me.Id && r.Addressee == opponentUserId))
                throw new SocialException("You've already challenged them.");

            var fr = new FakeDrawRequest
            {
                Requester = _me.Id, Addressee = opponentUserId, Difficulty = difficulty,
                Word = word, LetterHint = letterHint, Strokes = DrawStrokeCodec.Encode(strokes),
            };
            _drawRequests[fr.Id] = fr;
            request = DrawRequestModelLocked(fr);
            from = _me.Id; fromHandle = _me.Handle;
        }
        FakeInboxBus.Deliver(opponentUserId,
            new InboxMessage(InboxKind.DrawInvite, from, fromHandle, RequestId: request.Id));
        return Task.FromResult(request);
    }

    public Task<IReadOnlyList<DrawRequest>> GetDrawRequestsAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            RequireMe();
            var list = _drawRequests.Values
                .Where(r => r.Requester == _me!.Id || r.Addressee == _me.Id)
                .OrderByDescending(r => r.Created)
                .Select(DrawRequestModelLocked)
                .ToList();
            return Task.FromResult<IReadOnlyList<DrawRequest>>(list);
        }
    }

    public Task<DrawGameState> AcceptDrawRequestAsync(Guid requestId, CancellationToken ct = default)
    {
        DrawGameState state;
        Guid me, inviter; string? myHandle;
        lock (_gate)
        {
            RequireMe();
            if (!_drawRequests.TryGetValue(requestId, out var fr))
                throw new SocialException("That invite is no longer available.");
            if (fr.Addressee != _me!.Id) throw new SocialException("Only the invitee can accept.");

            var fg = new FakeDrawGame { PlayerA = fr.Requester, PlayerB = fr.Addressee };
            fg.Rounds.Add(new FakeDrawRound
            {
                RoundNo = 1, Drawer = fr.Requester, Guesser = fr.Addressee, Difficulty = fr.Difficulty,
                Word = fr.Word, LetterHint = fr.LetterHint, Strokes = fr.Strokes,
            });
            _drawGames[fg.Id] = fg;
            _drawRequests.Remove(requestId);
            state = StateLocked(fg, _me.Id);
            me = _me.Id; myHandle = _me.Handle; inviter = fr.Requester;
        }
        FakeInboxBus.Deliver(inviter,
            new InboxMessage(InboxKind.DrawInviteAccepted, me, myHandle, GameId: state.Summary.Id));
        return Task.FromResult(state);
    }

    public Task DeclineDrawRequestAsync(Guid requestId, CancellationToken ct = default)
    {
        Guid me = Guid.Empty, other = Guid.Empty; string? myHandle = null; bool removed = false;
        lock (_gate)
        {
            RequireMe();
            if (_drawRequests.TryGetValue(requestId, out var fr) && (fr.Requester == _me!.Id || fr.Addressee == _me.Id))
            {
                _drawRequests.Remove(requestId);
                me = _me.Id; myHandle = _me.Handle;
                other = fr.Requester == _me.Id ? fr.Addressee : fr.Requester;
                removed = true;
            }
        }
        if (removed)
            FakeInboxBus.Deliver(other,
                new InboxMessage(InboxKind.DrawInviteDeclined, me, myHandle, RequestId: requestId));
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<DrawGameSummary>> GetDrawGamesAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            RequireMe();
            var list = _drawGames.Values
                .Where(fg => fg.PlayerA == _me!.Id || fg.PlayerB == _me.Id)
                .OrderByDescending(fg => fg.Updated)
                .Select(SummaryLocked)
                .ToList();
            return Task.FromResult<IReadOnlyList<DrawGameSummary>>(list);
        }
    }

    public Task<DrawGameState> GetDrawGameAsync(Guid gameId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var fg = RequireMyDrawGameLocked(gameId);
            return Task.FromResult(StateLocked(fg, _me!.Id));
        }
    }

    public Task<DrawGameState> SubmitDrawRoundAsync(Guid gameId, DrawDifficulty difficulty, string word,
        string letterHint, IReadOnlyList<DrawStroke> strokes, CancellationToken ct = default)
    {
        DrawGameState state;
        lock (_gate)
        {
            var fg = RequireMyDrawGameLocked(gameId);
            if (fg.Status != DrawGameStatus.InProgress) throw new SocialException("The game is over.");
            if (string.IsNullOrWhiteSpace(word)) throw new SocialException("Pick a word to draw first.");
            var last = fg.Rounds[^1];
            if (last.Status == DrawRoundStatus.Guessing) throw new SocialException("Wait for your opponent's guess.");
            // The ex-guesser draws the next round.
            if (last.Guesser != _me!.Id) throw new SocialException("It's not your turn to draw.");

            fg.Rounds.Add(new FakeDrawRound
            {
                RoundNo = last.RoundNo + 1, Drawer = _me.Id, Guesser = OpponentLocked(fg, _me.Id),
                Difficulty = difficulty, Word = word, LetterHint = letterHint, Strokes = DrawStrokeCodec.Encode(strokes),
            });
            fg.Updated = DateTimeOffset.UtcNow;
            state = StateLocked(fg, _me.Id);
        }
        NotifyDraw(gameId);
        return Task.FromResult(state);
    }

    public Task<DrawGameState> SubmitDrawGuessAsync(Guid roundId, string guess, CancellationToken ct = default)
    {
        DrawGameState state; Guid gameId;
        lock (_gate)
        {
            var (fg, round) = RequireMyDrawRoundLocked(roundId);
            if (round.Status != DrawRoundStatus.Guessing) throw new SocialException("That round is already over.");
            if (round.Guesser != _me!.Id) throw new SocialException("It's not your round to guess.");

            round.Guesses.Add(guess ?? "");
            if (DrawGuessing.IsCorrect(guess, round.Word))
            {
                round.Status = DrawRoundStatus.Solved;
                var (gp, dp) = DrawScoring.Points(round.Difficulty, round.Guesses.Count);
                round.PointsGuesser = gp; round.PointsDrawer = dp;
                AddScoreLocked(fg, round.Guesser, gp);
                AddScoreLocked(fg, round.Drawer, dp);
            }
            fg.Updated = DateTimeOffset.UtcNow;
            state = StateLocked(fg, _me.Id); gameId = fg.Id;
        }
        NotifyDraw(gameId);
        return Task.FromResult(state);
    }

    public Task<DrawGameState> GiveUpDrawRoundAsync(Guid roundId, CancellationToken ct = default)
    {
        DrawGameState state; Guid gameId;
        lock (_gate)
        {
            var (fg, round) = RequireMyDrawRoundLocked(roundId);
            if (round.Guesser != _me!.Id) throw new SocialException("It's not your round to guess.");
            if (round.Status == DrawRoundStatus.Guessing) round.Status = DrawRoundStatus.GaveUp;
            fg.Updated = DateTimeOffset.UtcNow;
            state = StateLocked(fg, _me.Id); gameId = fg.Id;
        }
        NotifyDraw(gameId);
        return Task.FromResult(state);
    }

    public Task<DrawGameState> ResignDrawGameAsync(Guid gameId, CancellationToken ct = default)
    {
        DrawGameState state;
        lock (_gate)
        {
            var fg = RequireMyDrawGameLocked(gameId);
            if (fg.Status == DrawGameStatus.InProgress) fg.Status = DrawGameStatus.Abandoned;
            fg.Updated = DateTimeOffset.UtcNow;
            state = StateLocked(fg, _me!.Id);
        }
        NotifyDraw(gameId);
        return Task.FromResult(state);
    }

    public Task DeleteDrawGameAsync(Guid gameId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            RequireMe();
            if (_drawGames.TryGetValue(gameId, out var fg) && (fg.PlayerA == _me!.Id || fg.PlayerB == _me.Id))
            {
                _drawGames.Remove(gameId);
                _drawSubs.Remove(gameId);
            }
        }
        return Task.CompletedTask;
    }

    public IDisposable SubscribeDrawGame(Guid gameId, Action onChanged)
    {
        lock (_gate)
        {
            if (!_drawSubs.TryGetValue(gameId, out var list)) _drawSubs[gameId] = list = new();
            list.Add(onChanged);
        }
        return new DrawSubscription(this, gameId, onChanged);
    }

    // ── Test/preview seams (not part of ISocialClient) ──────────────────────────────────────────────

    /// <summary>Simulates <paramref name="fromUserId"/> challenging you to a Draw game (shows as an incoming
    /// request), carrying their first drawing (seeded as round 1 on accept, as the real flow does).</summary>
    public DrawRequest SimulateIncomingDrawRequest(Guid fromUserId, DrawDifficulty difficulty, string word,
        IReadOnlyList<DrawStroke>? strokes = null)
    {
        lock (_gate)
        {
            RequireMe();
            var fr = new FakeDrawRequest
            {
                Requester = fromUserId, Addressee = _me!.Id, Difficulty = difficulty,
                Word = word, LetterHint = DrawGuessing.LetterHint(word), Strokes = DrawStrokeCodec.Encode(strokes),
            };
            _drawRequests[fr.Id] = fr;
            return DrawRequestModelLocked(fr);
        }
    }

    /// <summary>Simulates the current guesser submitting <paramref name="guess"/> — applies it for whoever must
    /// guess the latest round (so call it after a drawing is submitted) and fires the game's subscribers.</summary>
    public void SimulateOpponentGuess(Guid gameId, string guess)
    {
        lock (_gate)
        {
            if (!_drawGames.TryGetValue(gameId, out var fg)) throw new SocialException("Unknown game.");
            var round = fg.Rounds[^1];
            if (round.Status != DrawRoundStatus.Guessing) throw new SocialException("Nothing to guess.");
            round.Guesses.Add(guess ?? "");
            if (DrawGuessing.IsCorrect(guess, round.Word))
            {
                round.Status = DrawRoundStatus.Solved;
                var (gp, dp) = DrawScoring.Points(round.Difficulty, round.Guesses.Count);
                round.PointsGuesser = gp; round.PointsDrawer = dp;
                AddScoreLocked(fg, round.Guesser, gp);
                AddScoreLocked(fg, round.Drawer, dp);
            }
            fg.Updated = DateTimeOffset.UtcNow;
        }
        NotifyDraw(gameId);
    }

    /// <summary>Simulates the current drawer submitting a new round's drawing — applies it for whoever must draw
    /// next (so call it after the previous round resolved) and fires the game's subscribers.</summary>
    public void SimulateOpponentDrawRound(Guid gameId, DrawDifficulty difficulty, string word,
        IReadOnlyList<DrawStroke>? strokes = null)
    {
        lock (_gate)
        {
            if (!_drawGames.TryGetValue(gameId, out var fg)) throw new SocialException("Unknown game.");
            var last = fg.Rounds[^1];
            if (last.Status == DrawRoundStatus.Guessing) throw new SocialException("The previous round isn't resolved.");
            fg.Rounds.Add(new FakeDrawRound
            {
                RoundNo = last.RoundNo + 1, Drawer = last.Guesser, Guesser = last.Drawer,
                Difficulty = difficulty, Word = word, LetterHint = DrawGuessing.LetterHint(word),
                Strokes = DrawStrokeCodec.Encode(strokes),
            });
            fg.Updated = DateTimeOffset.UtcNow;
        }
        NotifyDraw(gameId);
    }

    // ── internals ──────────────────────────────────────────────────────────────────────────────────

    private bool AreFriendsLocked(Guid other) =>
        _edges.TryGetValue(other, out var s) && s == FriendshipState.Accepted && !_blocked.Contains(other);

    private FakeDrawGame RequireMyDrawGameLocked(Guid gameId)
    {
        RequireMe();
        if (!_drawGames.TryGetValue(gameId, out var fg) || (fg.PlayerA != _me!.Id && fg.PlayerB != _me.Id))
            throw new SocialException("No such game.");
        return fg;
    }

    private (FakeDrawGame Game, FakeDrawRound Round) RequireMyDrawRoundLocked(Guid roundId)
    {
        RequireMe();
        foreach (var fg in _drawGames.Values)
        {
            if (fg.PlayerA != _me!.Id && fg.PlayerB != _me.Id) continue;
            var round = fg.Rounds.FirstOrDefault(r => r.Id == roundId);
            if (round is not null) return (fg, round);
        }
        throw new SocialException("No such round.");
    }

    private Guid OpponentLocked(FakeDrawGame fg, Guid me) => me == fg.PlayerA ? fg.PlayerB : fg.PlayerA;

    private static void AddScoreLocked(FakeDrawGame fg, Guid player, int points)
    {
        if (player == fg.PlayerA) fg.ScoreA += points; else if (player == fg.PlayerB) fg.ScoreB += points;
    }

    private DrawGameSummary SummaryLocked(FakeDrawGame fg)
    {
        var last = fg.Rounds[^1];
        var phase = last.Status == DrawRoundStatus.Guessing ? DrawPhase.Guess : DrawPhase.Draw;
        var whoseTurn = last.Guesser;   // the guesser guesses this round, then draws the next
        var a = _profiles.GetValueOrDefault(fg.PlayerA) ?? new Profile(fg.PlayerA, "unknown");
        var b = _profiles.GetValueOrDefault(fg.PlayerB) ?? new Profile(fg.PlayerB, "unknown");
        return new DrawGameSummary(fg.Id, a, b, fg.Status, whoseTurn, phase, fg.ScoreA, fg.ScoreB, fg.Rounds.Count, fg.Updated);
    }

    private DrawGameState StateLocked(FakeDrawGame fg, Guid meId)
    {
        var last = fg.Rounds[^1];
        var drawer = _profiles.GetValueOrDefault(last.Drawer) ?? new Profile(last.Drawer, "unknown");
        var guesser = _profiles.GetValueOrDefault(last.Guesser) ?? new Profile(last.Guesser, "unknown");
        // Convention (not enforced): hide the word from the guesser while the round is still open.
        string? word = last.Word;
        if (last.Status == DrawRoundStatus.Guessing && meId == last.Guesser) word = null;
        var round = new DrawRound(last.Id, fg.Id, last.RoundNo, drawer, guesser, last.Difficulty, word,
            last.LetterHint, DrawStrokeCodec.Decode(last.Strokes), last.Status, last.Guesses.ToList(),
            last.PointsDrawer, last.PointsGuesser);
        return new DrawGameState(SummaryLocked(fg), round);
    }

    private DrawRequest DrawRequestModelLocked(FakeDrawRequest fr)
    {
        var requester = _profiles.GetValueOrDefault(fr.Requester) ?? new Profile(fr.Requester, "unknown");
        var addressee = _profiles.GetValueOrDefault(fr.Addressee) ?? new Profile(fr.Addressee, "unknown");
        return new DrawRequest(fr.Id, requester, addressee, fr.Difficulty, fr.LetterHint, fr.Created);
    }

    private void NotifyDraw(Guid gameId)
    {
        Action[] subs;
        lock (_gate)
        {
            if (!_drawSubs.TryGetValue(gameId, out var list)) return;
            subs = list.ToArray();
        }
        foreach (var s in subs) { try { s(); } catch { } }
    }

    private sealed class FakeDrawGame
    {
        public Guid Id { get; } = Guid.NewGuid();
        public Guid PlayerA { get; init; }        // started the game, drew round 1
        public Guid PlayerB { get; init; }
        public int ScoreA { get; set; }
        public int ScoreB { get; set; }
        public DrawGameStatus Status { get; set; } = DrawGameStatus.InProgress;
        public List<FakeDrawRound> Rounds { get; } = new();
        public DateTimeOffset Updated { get; set; } = DateTimeOffset.UtcNow;
    }

    private sealed class FakeDrawRound
    {
        public Guid Id { get; } = Guid.NewGuid();
        public int RoundNo { get; init; }
        public Guid Drawer { get; init; }
        public Guid Guesser { get; init; }
        public DrawDifficulty Difficulty { get; init; }
        public string Word { get; init; } = "";
        public string LetterHint { get; init; } = "";
        public string Strokes { get; init; } = "";
        public DrawRoundStatus Status { get; set; } = DrawRoundStatus.Guessing;
        public List<string> Guesses { get; } = new();
        public int PointsDrawer { get; set; }
        public int PointsGuesser { get; set; }
    }

    private sealed class FakeDrawRequest
    {
        public Guid Id { get; } = Guid.NewGuid();
        public Guid Requester { get; init; }
        public Guid Addressee { get; init; }
        public DrawDifficulty Difficulty { get; init; }
        public string Word { get; init; } = "";
        public string LetterHint { get; init; } = "";
        public string Strokes { get; init; } = "";
        public DateTimeOffset Created { get; } = DateTimeOffset.UtcNow;
    }

    private sealed class DrawSubscription : IDisposable
    {
        private readonly FakeSocialClient _owner;
        private readonly Guid _gameId;
        private readonly Action _cb;
        public DrawSubscription(FakeSocialClient owner, Guid gameId, Action cb) { _owner = owner; _gameId = gameId; _cb = cb; }
        public void Dispose()
        {
            lock (_owner._gate)
                if (_owner._drawSubs.TryGetValue(_gameId, out var list)) list.Remove(_cb);
        }
    }
}
