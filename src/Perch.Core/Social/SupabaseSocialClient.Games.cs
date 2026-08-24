using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Perch.Games;

namespace Perch.Social;

/// <summary>
/// The networked Connect 4 surface of <see cref="SupabaseSocialClient"/>. Games and moves live in Postgres with
/// RLS scoping every row to the two players; the move itself goes through the <c>drop_disc</c> RPC, which
/// validates it server-side (whose turn, legal column, game not over) — the client only proposes a column and
/// renders the state it gets back. Live turns ride the generic <see cref="SupabaseRealtimeConnection"/> on the
/// game's <c>moves</c> channel; a change just nudges the caller to re-fetch (the DB stays authoritative). See
/// docs/connect4-plan.md.
/// </summary>
public sealed partial class SupabaseSocialClient
{
    public async Task<GameSummary> CreateGameAsync(Guid opponentUserId, CancellationToken ct = default)
    {
        var uid = RequireUser();
        if (opponentUserId == uid) throw new SocialException("You can't play yourself.");
        var token = await ValidAccessTokenAsync(ct);

        using var req = Rest(HttpMethod.Post, "/rest/v1/games", token);
        req.Headers.Add("Prefer", "return=representation");
        req.Content = JsonContent.Create(new { player_red = uid, player_yellow = opponentUserId });
        using var resp = await _http.SendAsync(req, ct);
        // The games_create RLS policy requires an accepted friendship, so a stranger is rejected here.
        await EnsureOkAsync(resp, "start the game", ct);
        var rows = await resp.Content.ReadFromJsonAsync<GameRow[]>(Json, ct) ?? [];
        if (rows.Length == 0) throw new SocialException("The game wasn't created.");

        var profiles = await FetchProfilesAsync([uid, opponentUserId], token, ct);
        return ToSummary(rows[0], profiles);
    }

    public async Task<GameRequest> RequestGameAsync(Guid opponentUserId, int firstColumn, CancellationToken ct = default)
    {
        var uid = RequireUser();
        if (opponentUserId == uid) throw new SocialException("You can't play yourself.");
        if (firstColumn is < 0 or > 6) throw new SocialException("That column is off the board.");
        var token = await ValidAccessTokenAsync(ct);

        using var req = Rest(HttpMethod.Post, "/rest/v1/game_requests", token);
        req.Headers.Add("Prefer", "return=representation");
        req.Content = JsonContent.Create(new { requester = uid, addressee = opponentUserId, first_col = firstColumn });
        using var resp = await _http.SendAsync(req, ct);
        // The unique (requester, addressee) index rejects a duplicate invite; the RLS check requires friendship.
        await EnsureOkAsync(resp, "send the invite", ct);
        var rows = await resp.Content.ReadFromJsonAsync<GameRequestRow[]>(Json, ct) ?? [];
        if (rows.Length == 0) throw new SocialException("The invite wasn't sent.");

        var profiles = await FetchProfilesAsync([uid, opponentUserId], token, ct);
        var request = ToRequest(rows[0], profiles);
        // Best-effort instant delivery: the persisted row is authoritative; this just beats the invitee's poll.
        await BroadcastInboxAsync(opponentUserId,
            new InboxMessage(InboxKind.GameInvite, uid, MyHandle(), RequestId: request.Id), token, ct);
        return request;
    }

    public async Task<IReadOnlyList<GameRequest>> GetGameRequestsAsync(CancellationToken ct = default)
    {
        var uid = RequireUser();
        var token = await ValidAccessTokenAsync(ct);
        using var req = Rest(HttpMethod.Get,
            $"/rest/v1/game_requests?or=(requester.eq.{uid},addressee.eq.{uid})" +
            "&select=id,requester,addressee,created_at&order=created_at.desc", token);
        using var resp = await _http.SendAsync(req, ct);
        await EnsureOkAsync(resp, "load your invites", ct);
        var rows = await resp.Content.ReadFromJsonAsync<GameRequestRow[]>(Json, ct) ?? [];

        var profiles = await FetchProfilesAsync(rows.SelectMany(r => new[] { r.Requester, r.Addressee }), token, ct);
        return rows.Select(r => ToRequest(r, profiles)).ToList();
    }

    public async Task<GameState> AcceptGameRequestAsync(Guid requestId, CancellationToken ct = default)
    {
        RequireUser();
        var token = await ValidAccessTokenAsync(ct);
        using var req = Rest(HttpMethod.Post, "/rest/v1/rpc/accept_game_request", token);
        req.Content = JsonContent.Create(new { p_request = requestId });
        using var resp = await _http.SendAsync(req, ct);
        await EnsureOkAsync(resp, "accept the invite", ct);
        // The RPC returns the freshly created games row (a single composite); re-read the full state.
        var row = await resp.Content.ReadFromJsonAsync<GameRow>(Json, ct)
                  ?? throw new SocialException("The game wasn't created.");
        var state = await GetGameAsync(row.Id, ct);
        // Tell the inviter instantly that their game is live (and it already carries their first move).
        var me = RequireUser();
        var inviter = state.Summary.Opponent(me)?.Id ?? state.Summary.Red.Id;
        await BroadcastInboxAsync(inviter,
            new InboxMessage(InboxKind.GameInviteAccepted, me, MyHandle(), GameId: row.Id), token, ct);
        return state;
    }

    public async Task DeclineGameRequestAsync(Guid requestId, CancellationToken ct = default)
    {
        var uid = RequireUser();
        var token = await ValidAccessTokenAsync(ct);
        // Read the request first (RLS-scoped) so we know who to notify once it's gone; a missing row is a no-op.
        Guid? other = null;
        using (var greq = Rest(HttpMethod.Get, $"/rest/v1/game_requests?id=eq.{requestId}&select=requester,addressee", token))
        using (var gresp = await _http.SendAsync(greq, ct))
        {
            if (gresp.IsSuccessStatusCode)
            {
                var rows = await gresp.Content.ReadFromJsonAsync<GameRequestRow[]>(Json, ct) ?? [];
                if (rows.Length > 0) other = rows[0].Requester == uid ? rows[0].Addressee : rows[0].Requester;
            }
        }

        // The RLS delete policy scopes this to a request you're part of (decline if invitee, cancel if requester).
        using var req = Rest(HttpMethod.Delete, $"/rest/v1/game_requests?id=eq.{requestId}", token);
        using var resp = await _http.SendAsync(req, ct);
        await EnsureOkAsync(resp, "remove the invite", ct);

        if (other is { } them)
            await BroadcastInboxAsync(them,
                new InboxMessage(InboxKind.GameInviteDeclined, uid, MyHandle(), RequestId: requestId), token, ct);
    }

    public async Task<IReadOnlyList<GameSummary>> GetGamesAsync(CancellationToken ct = default)
    {
        var uid = RequireUser();
        var token = await ValidAccessTokenAsync(ct);
        using var req = Rest(HttpMethod.Get,
            $"/rest/v1/games?or=(player_red.eq.{uid},player_yellow.eq.{uid})" +
            "&select=id,player_red,player_yellow,status,turn,move_count,updated_at&order=updated_at.desc", token);
        using var resp = await _http.SendAsync(req, ct);
        await EnsureOkAsync(resp, "load your games", ct);
        var rows = await resp.Content.ReadFromJsonAsync<GameRow[]>(Json, ct) ?? [];

        var profiles = await FetchProfilesAsync(rows.SelectMany(r => new[] { r.PlayerRed, r.PlayerYellow }), token, ct);
        return rows.Select(r => ToSummary(r, profiles)).ToList();
    }

    public async Task<GameState> GetGameAsync(Guid gameId, CancellationToken ct = default)
    {
        RequireUser();
        var token = await ValidAccessTokenAsync(ct);

        using var greq = Rest(HttpMethod.Get,
            $"/rest/v1/games?id=eq.{gameId}&select=id,player_red,player_yellow,status,turn,move_count,updated_at", token);
        using var gresp = await _http.SendAsync(greq, ct);
        await EnsureOkAsync(gresp, "load the game", ct);
        var grows = await gresp.Content.ReadFromJsonAsync<GameRow[]>(Json, ct) ?? [];
        if (grows.Length == 0) throw new SocialException("No such game.");
        var row = grows[0];

        using var mreq = Rest(HttpMethod.Get, $"/rest/v1/moves?game_id=eq.{gameId}&select=ply,col&order=ply.asc", token);
        using var mresp = await _http.SendAsync(mreq, ct);
        await EnsureOkAsync(mresp, "load the moves", ct);
        var mrows = await mresp.Content.ReadFromJsonAsync<MoveRow[]>(Json, ct) ?? [];

        var profiles = await FetchProfilesAsync([row.PlayerRed, row.PlayerYellow], token, ct);
        var moves = mrows.OrderBy(m => m.Ply).Select(m => m.Col).ToList();
        return new GameState(ToSummary(row, profiles), moves);
    }

    public async Task<GameState> DropAsync(Guid gameId, int column, CancellationToken ct = default)
    {
        RequireUser();
        if (column is < 0 or > 6) throw new SocialException("That column is off the board.");
        var token = await ValidAccessTokenAsync(ct);

        using var req = Rest(HttpMethod.Post, "/rest/v1/rpc/drop_disc", token);
        req.Content = JsonContent.Create(new { p_game = gameId, p_col = column });
        using var resp = await _http.SendAsync(req, ct);
        // The RPC validates the move (turn / column / game state) and raises on any violation — surfaced here.
        await EnsureOkAsync(resp, "make your move", ct);
        return await GetGameAsync(gameId, ct);   // re-read the authoritative board + resolved profiles
    }

    public async Task<GameState> ResignGameAsync(Guid gameId, CancellationToken ct = default)
    {
        RequireUser();
        var token = await ValidAccessTokenAsync(ct);
        using var req = Rest(HttpMethod.Post, "/rest/v1/rpc/resign_game", token);
        req.Content = JsonContent.Create(new { p_game = gameId });
        using var resp = await _http.SendAsync(req, ct);
        await EnsureOkAsync(resp, "resign the game", ct);
        return await GetGameAsync(gameId, ct);
    }

    public async Task DeleteGameAsync(Guid gameId, CancellationToken ct = default)
    {
        RequireUser();
        var token = await ValidAccessTokenAsync(ct);
        // The games_delete RLS policy scopes this to a game you're in; moves cascade via the FK.
        using var req = Rest(HttpMethod.Delete, $"/rest/v1/games?id=eq.{gameId}", token);
        using var resp = await _http.SendAsync(req, ct);
        await EnsureOkAsync(resp, "remove the game", ct);
    }

    // Live turns: subscribe to the game's moves channel; each insert just nudges the caller to re-fetch (the DB
    // is authoritative). Unconfigured → a safe no-op; the caller's polling covers it.
    public IDisposable SubscribeGame(Guid gameId, Action onChanged)
    {
        if (!_config.IsConfigured) return new NoopDisposable();
        return new SupabaseRealtimeConnection(BaseUrl, _config.PublishableKey, RealtimeChannel.Moves(gameId),
            ValidAccessTokenAsync, _ => onChanged());
    }

    public IDisposable SubscribeInbox(Action<InboxMessage> onMessage)
    {
        if (!_config.IsConfigured) return new NoopDisposable();
        Guid me;
        try { me = RequireUser(); } catch (SocialException) { return new NoopDisposable(); }
        return new SupabaseRealtimeConnection(BaseUrl, _config.PublishableKey, RealtimeChannel.Inbox(me),
            ValidAccessTokenAsync, frame =>
            {
                if (RealtimeProtocol.TryParseBroadcast(frame, out var name, out var payload) &&
                    name == InboxEvent && TryReadInbox(payload, out var msg))
                    onMessage(msg);
            });
    }

    public async Task SendNudgeAsync(Guid gameId, Guid opponentUserId, CancellationToken ct = default)
    {
        var uid = RequireUser();
        var token = await ValidAccessTokenAsync(ct);
        await BroadcastInboxAsync(opponentUserId,
            new InboxMessage(InboxKind.Nudge, uid, MyHandle(), GameId: gameId), token, ct);
    }

    // ── inbox broadcast (transient) ────────────────────────────────────────────────────────────────
    // The application event name carried inside every Perch inbox broadcast.
    private const string InboxEvent = "inbox";

    private string? MyHandle() { lock (_gate) return _me?.Handle; }

    // Posts one transient broadcast to another user's inbox channel via the Realtime broadcast REST endpoint —
    // no DB write. Best-effort: a failure is swallowed (the persistent row + the recipient's poll still cover it).
    private async Task BroadcastInboxAsync(Guid toUserId, InboxMessage msg, string token, CancellationToken ct)
    {
        try
        {
            var channel = RealtimeChannel.Inbox(toUserId);
            var payload = new System.Text.Json.Nodes.JsonObject
            {
                ["kind"] = (int)msg.Kind,
                ["from"] = msg.FromUserId.ToString(),
                ["from_handle"] = msg.FromHandle,
                ["game_id"] = msg.GameId?.ToString(),
                ["request_id"] = msg.RequestId?.ToString(),
            };
            var body = new System.Text.Json.Nodes.JsonObject
            {
                ["messages"] = new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject
                {
                    ["topic"] = channel.BroadcastName,
                    ["event"] = InboxEvent,
                    ["payload"] = payload,
                    ["private"] = false,
                }),
            };
            using var req = Rest(HttpMethod.Post, "/realtime/v1/api/broadcast", token);
            req.Content = new StringContent(body.ToJsonString(), System.Text.Encoding.UTF8, "application/json");
            using var resp = await _http.SendAsync(req, ct);
            // Ignore the result — this is only an accelerator.
        }
        catch { /* transient: the poll (and persisted state) still deliver */ }
    }

    private static bool TryReadInbox(System.Text.Json.Nodes.JsonObject p, out InboxMessage msg)
    {
        msg = default!;
        try
        {
            if (!Enum.IsDefined(typeof(InboxKind), (int?)p["kind"] ?? -1)) return false;
            var kind = (InboxKind)(int)p["kind"]!;
            Guid.TryParse((string?)p["from"], out var from);
            Guid? game = Guid.TryParse((string?)p["game_id"], out var g) ? g : null;
            Guid? request = Guid.TryParse((string?)p["request_id"], out var r) ? r : null;
            msg = new InboxMessage(kind, from, (string?)p["from_handle"], game, request);
            return true;
        }
        catch { return false; }
    }

    // ── mapping ──────────────────────────────────────────────────────────────────────────────────────

    private GameSummary ToSummary(GameRow r, IReadOnlyDictionary<Guid, Profile> profiles) => new(
        r.Id,
        profiles.GetValueOrDefault(r.PlayerRed) ?? new Profile(r.PlayerRed, "unknown"),
        profiles.GetValueOrDefault(r.PlayerYellow) ?? new Profile(r.PlayerYellow, "unknown"),
        ParseStatus(r.Status), ParseTurn(r.Turn), r.MoveCount, r.UpdatedAt);

    private static GameStatus ParseStatus(string s) => s switch
    {
        "red_won" => GameStatus.RedWon,
        "yellow_won" => GameStatus.YellowWon,
        "draw" => GameStatus.Draw,
        "abandoned" => GameStatus.Abandoned,
        _ => GameStatus.InProgress,
    };

    private static Connect4Disc ParseTurn(string s) => s == "yellow" ? Connect4Disc.Yellow : Connect4Disc.Red;

    private GameRequest ToRequest(GameRequestRow r, IReadOnlyDictionary<Guid, Profile> profiles) => new(
        r.Id,
        profiles.GetValueOrDefault(r.Requester) ?? new Profile(r.Requester, "unknown"),
        profiles.GetValueOrDefault(r.Addressee) ?? new Profile(r.Addressee, "unknown"),
        r.CreatedAt);

    // ── wire DTOs ────────────────────────────────────────────────────────────────────────────────────

    private sealed record GameRow(
        [property: JsonPropertyName("id")] Guid Id,
        [property: JsonPropertyName("player_red")] Guid PlayerRed,
        [property: JsonPropertyName("player_yellow")] Guid PlayerYellow,
        [property: JsonPropertyName("status")] string Status,
        [property: JsonPropertyName("turn")] string Turn,
        [property: JsonPropertyName("move_count")] int MoveCount,
        [property: JsonPropertyName("updated_at")] DateTimeOffset UpdatedAt);

    private sealed record MoveRow(
        [property: JsonPropertyName("ply")] int Ply,
        [property: JsonPropertyName("col")] int Col);

    private sealed record GameRequestRow(
        [property: JsonPropertyName("id")] Guid Id,
        [property: JsonPropertyName("requester")] Guid Requester,
        [property: JsonPropertyName("addressee")] Guid Addressee,
        [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt);
}
