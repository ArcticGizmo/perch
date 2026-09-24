using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Perch.Games;

namespace Perch.Social;

/// <summary>
/// The "Draw with Perch" surface of <see cref="SupabaseSocialClient"/>. Games, rounds and invites live in
/// Postgres with RLS scoping every row to the two players; state transitions (accept, submit a drawing, guess,
/// give up, resign) go through <c>SECURITY DEFINER</c> RPCs that validate the turn/phase and compute scores, so
/// the client only proposes an action and renders the state it gets back. Live turns ride the generic
/// <see cref="SupabaseRealtimeConnection"/> on the game's <c>draw_rounds</c> channel; a change just nudges the
/// caller to re-fetch. The word rides on the round (secrecy is by convention only); the client masks it from the
/// guesser while a round is still open. See docs/draw-with-perch-plan.md.
/// </summary>
public sealed partial class SupabaseSocialClient
{
    private const string DrawGameSelect =
        "id,player_a,player_b,status,whose_turn,phase,score_a,score_b,round_no,updated_at";
    private const string DrawRoundSelect =
        "id,game_id,round_no,drawer,guesser,difficulty,word,letter_hint,strokes,status,guesses,points_drawer,points_guesser";

    public async Task<DrawRequest> RequestDrawGameAsync(Guid opponentUserId, DrawDifficulty difficulty, string word,
        string letterHint, IReadOnlyList<DrawStroke> strokes, CancellationToken ct = default)
    {
        var uid = RequireUser();
        if (opponentUserId == uid) throw new SocialException("You can't play yourself.");
        if (string.IsNullOrWhiteSpace(word)) throw new SocialException("Pick a word to draw first.");
        var token = await ValidAccessTokenAsync(ct);

        using var req = Rest(HttpMethod.Post, "/rest/v1/draw_requests", token);
        req.Headers.Add("Prefer", "return=representation");
        req.Content = JsonContent.Create(new
        {
            requester = uid, addressee = opponentUserId, difficulty = DiffToWire(difficulty),
            word, letter_hint = letterHint, strokes = DrawStrokeCodec.Encode(strokes),
        });
        using var resp = await _http.SendAsync(req, ct);
        // The draw_requests_create RLS policy requires an accepted friendship; the unique index rejects a dup.
        await EnsureOkAsync(resp, "send the challenge", ct);
        var rows = await resp.Content.ReadFromJsonAsync<DrawRequestRow[]>(Json, ct) ?? [];
        if (rows.Length == 0) throw new SocialException("The challenge wasn't sent.");

        var profiles = await FetchProfilesAsync([uid, opponentUserId], token, ct);
        var request = ToDrawRequest(rows[0], profiles);
        await BroadcastInboxAsync(opponentUserId,
            new InboxMessage(InboxKind.DrawInvite, uid, MyHandle(), RequestId: request.Id), token, ct);
        return request;
    }

    public async Task<IReadOnlyList<DrawRequest>> GetDrawRequestsAsync(CancellationToken ct = default)
    {
        var uid = RequireUser();
        var token = await ValidAccessTokenAsync(ct);
        using var req = Rest(HttpMethod.Get,
            $"/rest/v1/draw_requests?or=(requester.eq.{uid},addressee.eq.{uid})" +
            "&select=id,requester,addressee,difficulty,letter_hint,created_at&order=created_at.desc", token);
        using var resp = await _http.SendAsync(req, ct);
        await EnsureOkAsync(resp, "load your challenges", ct);
        var rows = await resp.Content.ReadFromJsonAsync<DrawRequestRow[]>(Json, ct) ?? [];

        var profiles = await FetchProfilesAsync(rows.SelectMany(r => new[] { r.Requester, r.Addressee }), token, ct);
        return rows.Select(r => ToDrawRequest(r, profiles)).ToList();
    }

    public async Task<DrawGameState> AcceptDrawRequestAsync(Guid requestId, CancellationToken ct = default)
    {
        var me = RequireUser();
        var token = await ValidAccessTokenAsync(ct);
        using var req = Rest(HttpMethod.Post, "/rest/v1/rpc/accept_draw_request", token);
        req.Content = JsonContent.Create(new { p_request = requestId });
        using var resp = await _http.SendAsync(req, ct);
        await EnsureOkAsync(resp, "accept the challenge", ct);
        var row = await resp.Content.ReadFromJsonAsync<DrawGameRow>(Json, ct)
                  ?? throw new SocialException("The game wasn't created.");
        var state = await GetDrawGameAsync(row.Id, ct);
        var inviter = state.Summary.Opponent(me)?.Id ?? state.Summary.PlayerA.Id;
        await BroadcastInboxAsync(inviter,
            new InboxMessage(InboxKind.DrawInviteAccepted, me, MyHandle(), GameId: row.Id), token, ct);
        return state;
    }

    public async Task DeclineDrawRequestAsync(Guid requestId, CancellationToken ct = default)
    {
        var uid = RequireUser();
        var token = await ValidAccessTokenAsync(ct);
        Guid? other = null;
        using (var greq = Rest(HttpMethod.Get, $"/rest/v1/draw_requests?id=eq.{requestId}&select=requester,addressee", token))
        using (var gresp = await _http.SendAsync(greq, ct))
        {
            if (gresp.IsSuccessStatusCode)
            {
                var rows = await gresp.Content.ReadFromJsonAsync<DrawRequestRow[]>(Json, ct) ?? [];
                if (rows.Length > 0) other = rows[0].Requester == uid ? rows[0].Addressee : rows[0].Requester;
            }
        }

        using var req = Rest(HttpMethod.Delete, $"/rest/v1/draw_requests?id=eq.{requestId}", token);
        using var resp = await _http.SendAsync(req, ct);
        await EnsureOkAsync(resp, "remove the challenge", ct);

        if (other is { } them)
            await BroadcastInboxAsync(them,
                new InboxMessage(InboxKind.DrawInviteDeclined, uid, MyHandle(), RequestId: requestId), token, ct);
    }

    public async Task<IReadOnlyList<DrawGameSummary>> GetDrawGamesAsync(CancellationToken ct = default)
    {
        var uid = RequireUser();
        var token = await ValidAccessTokenAsync(ct);
        using var req = Rest(HttpMethod.Get,
            $"/rest/v1/draw_games?or=(player_a.eq.{uid},player_b.eq.{uid})" +
            $"&select={DrawGameSelect}&order=updated_at.desc", token);
        using var resp = await _http.SendAsync(req, ct);
        await EnsureOkAsync(resp, "load your games", ct);
        var rows = await resp.Content.ReadFromJsonAsync<DrawGameRow[]>(Json, ct) ?? [];

        var profiles = await FetchProfilesAsync(rows.SelectMany(r => new[] { r.PlayerA, r.PlayerB }), token, ct);
        return rows.Select(r => ToDrawSummary(r, profiles)).ToList();
    }

    public async Task<DrawGameState> GetDrawGameAsync(Guid gameId, CancellationToken ct = default)
    {
        var me = RequireUser();
        var token = await ValidAccessTokenAsync(ct);

        using var greq = Rest(HttpMethod.Get, $"/rest/v1/draw_games?id=eq.{gameId}&select={DrawGameSelect}", token);
        using var gresp = await _http.SendAsync(greq, ct);
        await EnsureOkAsync(gresp, "load the game", ct);
        var grows = await gresp.Content.ReadFromJsonAsync<DrawGameRow[]>(Json, ct) ?? [];
        if (grows.Length == 0) throw new SocialException("No such game.");
        var row = grows[0];

        using var rreq = Rest(HttpMethod.Get,
            $"/rest/v1/draw_rounds?game_id=eq.{gameId}&select={DrawRoundSelect}&order=round_no.desc&limit=1", token);
        using var rresp = await _http.SendAsync(rreq, ct);
        await EnsureOkAsync(rresp, "load the round", ct);
        var rrows = await rresp.Content.ReadFromJsonAsync<DrawRoundRow[]>(Json, ct) ?? [];

        var profiles = await FetchProfilesAsync([row.PlayerA, row.PlayerB], token, ct);
        var round = rrows.Length > 0 ? ToDrawRound(rrows[0], profiles, me) : null;
        return new DrawGameState(ToDrawSummary(row, profiles), round);
    }

    public async Task<DrawGameState> SubmitDrawRoundAsync(Guid gameId, DrawDifficulty difficulty, string word,
        string letterHint, IReadOnlyList<DrawStroke> strokes, CancellationToken ct = default)
    {
        RequireUser();
        if (string.IsNullOrWhiteSpace(word)) throw new SocialException("Pick a word to draw first.");
        var token = await ValidAccessTokenAsync(ct);
        using var req = Rest(HttpMethod.Post, "/rest/v1/rpc/submit_draw_round", token);
        req.Content = JsonContent.Create(new
        {
            p_game = gameId, p_difficulty = DiffToWire(difficulty), p_word = word,
            p_hint = letterHint, p_strokes = DrawStrokeCodec.Encode(strokes),
        });
        using var resp = await _http.SendAsync(req, ct);
        await EnsureOkAsync(resp, "submit your drawing", ct);
        return await GetDrawGameAsync(gameId, ct);
    }

    public async Task<DrawGameState> SubmitDrawGuessAsync(Guid roundId, string guess, CancellationToken ct = default)
    {
        RequireUser();
        var token = await ValidAccessTokenAsync(ct);
        using var req = Rest(HttpMethod.Post, "/rest/v1/rpc/submit_draw_guess", token);
        req.Content = JsonContent.Create(new { p_round = roundId, p_guess = guess ?? "" });
        using var resp = await _http.SendAsync(req, ct);
        await EnsureOkAsync(resp, "submit your guess", ct);
        var row = await resp.Content.ReadFromJsonAsync<DrawGameRow>(Json, ct)
                  ?? throw new SocialException("The guess wasn't recorded.");
        return await GetDrawGameAsync(row.Id, ct);
    }

    public async Task<DrawGameState> GiveUpDrawRoundAsync(Guid roundId, CancellationToken ct = default)
    {
        RequireUser();
        var token = await ValidAccessTokenAsync(ct);
        using var req = Rest(HttpMethod.Post, "/rest/v1/rpc/give_up_draw_round", token);
        req.Content = JsonContent.Create(new { p_round = roundId });
        using var resp = await _http.SendAsync(req, ct);
        await EnsureOkAsync(resp, "give up the round", ct);
        var row = await resp.Content.ReadFromJsonAsync<DrawGameRow>(Json, ct)
                  ?? throw new SocialException("The round wasn't updated.");
        return await GetDrawGameAsync(row.Id, ct);
    }

    public async Task<DrawGameState> ResignDrawGameAsync(Guid gameId, CancellationToken ct = default)
    {
        RequireUser();
        var token = await ValidAccessTokenAsync(ct);
        using var req = Rest(HttpMethod.Post, "/rest/v1/rpc/resign_draw_game", token);
        req.Content = JsonContent.Create(new { p_game = gameId });
        using var resp = await _http.SendAsync(req, ct);
        await EnsureOkAsync(resp, "resign the game", ct);
        return await GetDrawGameAsync(gameId, ct);
    }

    public async Task DeleteDrawGameAsync(Guid gameId, CancellationToken ct = default)
    {
        RequireUser();
        var token = await ValidAccessTokenAsync(ct);
        using var req = Rest(HttpMethod.Delete, $"/rest/v1/draw_games?id=eq.{gameId}", token);
        using var resp = await _http.SendAsync(req, ct);
        await EnsureOkAsync(resp, "remove the game", ct);
    }

    public IDisposable SubscribeDrawGame(Guid gameId, Action onChanged)
    {
        if (!_config.IsConfigured) return new NoopDisposable();
        return new SupabaseRealtimeConnection(BaseUrl, _config.PublishableKey, RealtimeChannel.DrawRounds(gameId),
            ValidAccessTokenAsync, _ => onChanged());
    }

    // ── mapping ──────────────────────────────────────────────────────────────────────────────────────

    private DrawGameSummary ToDrawSummary(DrawGameRow r, IReadOnlyDictionary<Guid, Profile> profiles) => new(
        r.Id,
        profiles.GetValueOrDefault(r.PlayerA) ?? new Profile(r.PlayerA, "unknown"),
        profiles.GetValueOrDefault(r.PlayerB) ?? new Profile(r.PlayerB, "unknown"),
        r.Status == "abandoned" ? DrawGameStatus.Abandoned : DrawGameStatus.InProgress,
        r.WhoseTurn, r.Phase == "guess" ? DrawPhase.Guess : DrawPhase.Draw,
        r.ScoreA, r.ScoreB, r.RoundNo, r.UpdatedAt);

    private DrawRound ToDrawRound(DrawRoundRow r, IReadOnlyDictionary<Guid, Profile> profiles, Guid meId)
    {
        var status = r.Status switch
        {
            "solved" => DrawRoundStatus.Solved,
            "gave_up" => DrawRoundStatus.GaveUp,
            _ => DrawRoundStatus.Guessing,
        };
        // Convention (not enforced): hide the word from the guesser while the round is still open.
        string? word = r.Word;
        if (status == DrawRoundStatus.Guessing && meId == r.Guesser) word = null;
        return new DrawRound(
            r.Id, r.GameId, r.RoundNo,
            profiles.GetValueOrDefault(r.Drawer) ?? new Profile(r.Drawer, "unknown"),
            profiles.GetValueOrDefault(r.Guesser) ?? new Profile(r.Guesser, "unknown"),
            ParseDiff(r.Difficulty), word, r.LetterHint ?? "",
            DrawStrokeCodec.Decode(r.Strokes), status, r.Guesses ?? [], r.PointsDrawer, r.PointsGuesser);
    }

    private DrawRequest ToDrawRequest(DrawRequestRow r, IReadOnlyDictionary<Guid, Profile> profiles) => new(
        r.Id,
        profiles.GetValueOrDefault(r.Requester) ?? new Profile(r.Requester, "unknown"),
        profiles.GetValueOrDefault(r.Addressee) ?? new Profile(r.Addressee, "unknown"),
        ParseDiff(r.Difficulty), r.LetterHint ?? "", r.CreatedAt);

    private static string DiffToWire(DrawDifficulty d) => d switch
    {
        DrawDifficulty.Easy => "easy",
        DrawDifficulty.Medium => "medium",
        _ => "hard",
    };

    private static DrawDifficulty ParseDiff(string? s) => s switch
    {
        "easy" => DrawDifficulty.Easy,
        "hard" => DrawDifficulty.Hard,
        _ => DrawDifficulty.Medium,
    };

    // ── wire DTOs ────────────────────────────────────────────────────────────────────────────────────

    private sealed record DrawGameRow(
        [property: JsonPropertyName("id")] Guid Id,
        [property: JsonPropertyName("player_a")] Guid PlayerA,
        [property: JsonPropertyName("player_b")] Guid PlayerB,
        [property: JsonPropertyName("status")] string Status,
        [property: JsonPropertyName("whose_turn")] Guid WhoseTurn,
        [property: JsonPropertyName("phase")] string Phase,
        [property: JsonPropertyName("score_a")] int ScoreA,
        [property: JsonPropertyName("score_b")] int ScoreB,
        [property: JsonPropertyName("round_no")] int RoundNo,
        [property: JsonPropertyName("updated_at")] DateTimeOffset UpdatedAt);

    private sealed record DrawRoundRow(
        [property: JsonPropertyName("id")] Guid Id,
        [property: JsonPropertyName("game_id")] Guid GameId,
        [property: JsonPropertyName("round_no")] int RoundNo,
        [property: JsonPropertyName("drawer")] Guid Drawer,
        [property: JsonPropertyName("guesser")] Guid Guesser,
        [property: JsonPropertyName("difficulty")] string Difficulty,
        [property: JsonPropertyName("word")] string? Word,
        [property: JsonPropertyName("letter_hint")] string? LetterHint,
        [property: JsonPropertyName("strokes")] string? Strokes,
        [property: JsonPropertyName("status")] string Status,
        [property: JsonPropertyName("guesses")] string[]? Guesses,
        [property: JsonPropertyName("points_drawer")] int PointsDrawer,
        [property: JsonPropertyName("points_guesser")] int PointsGuesser);

    private sealed record DrawRequestRow(
        [property: JsonPropertyName("id")] Guid Id,
        [property: JsonPropertyName("requester")] Guid Requester,
        [property: JsonPropertyName("addressee")] Guid Addressee,
        [property: JsonPropertyName("difficulty")] string Difficulty,
        [property: JsonPropertyName("letter_hint")] string? LetterHint,
        [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt);
}
