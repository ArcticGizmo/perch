using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Perch.Social;

/// <summary>
/// The Phoenix-channel wire format Supabase Realtime speaks, factored out as pure string/URI helpers so the
/// message building and (defensive) parsing are unit-testable without a socket. Every frame is a JSON object
/// <c>{ topic, event, payload, ref }</c>; we join one channel scoped to <c>public.posts</c> INSERTs, keep it
/// alive with a heartbeat, and re-send the access token as the JWT rotates. RLS still filters what the server
/// pushes — a subscriber only ever receives inserts it is allowed to <c>SELECT</c>.
/// </summary>
internal static class RealtimeProtocol
{
    // The channel topic. Any stable string works; naming it after the table keeps logs legible.
    public const string PostsTopic = "realtime:public:posts";

    /// <summary>Turns the project's REST base URL into the Realtime WebSocket endpoint, carrying the
    /// publishable key (public by design) and the protocol version.</summary>
    public static Uri SocketUri(string baseUrl, string apiKey)
    {
        var b = new UriBuilder(baseUrl.TrimEnd('/'))
        {
            Scheme = baseUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ? "ws" : "wss",
        };
        b.Path = b.Path.TrimEnd('/') + "/realtime/v1/websocket";
        b.Query = $"apikey={Uri.EscapeDataString(apiKey)}&vsn=1.0.0";
        return b.Uri;
    }

    /// <summary>The <c>phx_join</c> frame that subscribes to INSERTs on <c>public.posts</c>. The server applies
    /// the caller's RLS to the change stream, so no author filter is needed here.</summary>
    public static string JoinPosts(int refId, string accessToken) => Frame(PostsTopic, "phx_join", refId, new JsonObject
    {
        ["config"] = new JsonObject
        {
            ["postgres_changes"] = new JsonArray(new JsonObject
            {
                ["event"] = "INSERT",
                ["schema"] = "public",
                ["table"] = "posts",
            }),
        },
        ["access_token"] = accessToken,
    });

    /// <summary>The keep-alive frame (Phoenix drops an idle socket after ~60s).</summary>
    public static string Heartbeat(int refId) => Frame("phoenix", "heartbeat", refId, new JsonObject());

    /// <summary>Pushes a refreshed JWT to the channel so a long-lived socket keeps its authorization as the
    /// access token rotates.</summary>
    public static string AccessToken(int refId, string accessToken) =>
        Frame(PostsTopic, "access_token", refId, new JsonObject { ["access_token"] = accessToken });

    private static string Frame(string topic, string @event, int refId, JsonObject payload) => new JsonObject
    {
        ["topic"] = topic,
        ["event"] = @event,
        ["payload"] = payload,
        ["ref"] = refId.ToString(),
    }.ToJsonString();

    /// <summary>
    /// Best-effort parse of an inbound frame into the new post it announces, or false for anything that isn't a
    /// <c>public.posts</c> INSERT (replies, heartbeats, presence, malformed frames). Never throws — a frame we
    /// can't read is simply ignored, and the poll remains the source of truth.
    /// </summary>
    public static bool TryParseInsert(string json, out RealtimePost post)
    {
        post = default!;
        try
        {
            var root = JsonNode.Parse(json)?.AsObject();
            if (root is null) return false;
            if ((string?)root["event"] != "postgres_changes") return false;

            var data = root["payload"]?["data"]?.AsObject();
            if (data is null) return false;
            if ((string?)data["type"] != "INSERT" || (string?)data["table"] != "posts") return false;

            var rec = data["record"]?.AsObject();
            if (rec is null) return false;

            if (!Guid.TryParse((string?)rec["id"], out var id) ||
                !Guid.TryParse((string?)rec["author"], out var author))
                return false;
            var body = (string?)rec["body"] ?? "";
            var mood = (string?)rec["mood_emoji"];
            var created = DateTimeOffset.TryParse((string?)rec["created_at"], out var ts) ? ts : DateTimeOffset.UtcNow;

            post = new RealtimePost(id, author, body, mood, created);
            return true;
        }
        catch (JsonException) { return false; }
    }
}

/// <summary>A newly inserted post as announced over Realtime — the raw row, before author-profile resolution
/// (the feed poll fills that in). Deliberately minimal; liveness only needs to know "something new landed".</summary>
internal readonly record struct RealtimePost(Guid Id, Guid Author, string Body, string? Mood, DateTimeOffset CreatedAt);

/// <summary>
/// A single Supabase Realtime subscription to <c>public.posts</c> INSERTs. Owns a <see cref="ClientWebSocket"/>,
/// joins the channel, heartbeats, and reconnects with exponential backoff when the socket drops — all on a
/// background loop, so construction never blocks. Each visible insert invokes the callback (off the UI thread);
/// disposal tears the socket down and stops reconnecting.
///
/// <para><b>Fallback is the whole point.</b> If the socket can never establish — a strict proxy, no network,
/// realtime disabled on the project — this just keeps retrying quietly; nothing surfaces to the user, and the
/// feed still updates on its polling cadence. Realtime only makes the poll fire <em>sooner</em>.</para>
/// </summary>
internal sealed class SupabaseRealtimeConnection : IDisposable
{
    private static readonly TimeSpan HeartbeatEvery = TimeSpan.FromSeconds(25);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(2);

    private readonly Uri _socketUri;
    private readonly Func<CancellationToken, Task<string>> _tokenProvider;
    private readonly Action<RealtimePost> _onInsert;
    private readonly CancellationTokenSource _cts = new();
    private int _ref;

    public SupabaseRealtimeConnection(
        string baseUrl, string apiKey, Func<CancellationToken, Task<string>> tokenProvider, Action<RealtimePost> onInsert)
    {
        _socketUri = RealtimeProtocol.SocketUri(baseUrl, apiKey);
        _tokenProvider = tokenProvider;
        _onInsert = onInsert;
        _ = RunAsync(_cts.Token);
    }

    // Connect → join → pump, reconnecting with backoff. Each iteration is one socket lifetime; any failure
    // (including the very first connect) just waits and retries, so a blocked WS degrades to "no live updates"
    // rather than an error. Backoff resets after a socket has stayed up a while.
    private async Task RunAsync(CancellationToken ct)
    {
        var backoff = TimeSpan.FromSeconds(2);
        while (!ct.IsCancellationRequested)
        {
            var connectedAt = DateTimeOffset.UtcNow;
            try
            {
                await SessionAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch { /* swallow — reconnect below */ }

            if (ct.IsCancellationRequested) return;

            // A socket that survived a while is a healthy reconnect, not a flapping one — reset the backoff.
            if (DateTimeOffset.UtcNow - connectedAt > TimeSpan.FromSeconds(30))
                backoff = TimeSpan.FromSeconds(2);

            try { await Task.Delay(backoff, ct); } catch (OperationCanceledException) { return; }
            backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, MaxBackoff.Ticks));
        }
    }

    private async Task SessionAsync(CancellationToken ct)
    {
        var token = await _tokenProvider(ct);   // requires a signed-in session; throws → caught → backoff
        using var ws = new ClientWebSocket();
        await ws.ConnectAsync(_socketUri, ct);
        await SendAsync(ws, RealtimeProtocol.JoinPosts(NextRef(), token), ct);

        using var heartbeat = new CancellationTokenSource();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, heartbeat.Token);
        var beat = HeartbeatLoop(ws, linked.Token);

        try { await ReceiveLoop(ws, linked.Token); }
        finally
        {
            heartbeat.Cancel();
            try { await beat; } catch { }
            if (ws.State == WebSocketState.Open)
                try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None); } catch { }
        }
    }

    private async Task HeartbeatLoop(ClientWebSocket ws, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(HeartbeatEvery, ct); } catch (OperationCanceledException) { return; }
            if (ct.IsCancellationRequested) return;
            await SendAsync(ws, RealtimeProtocol.Heartbeat(NextRef()), ct);
            // Re-assert the (possibly refreshed) token so a long-lived socket doesn't lose authorization.
            try
            {
                var token = await _tokenProvider(ct);
                await SendAsync(ws, RealtimeProtocol.AccessToken(NextRef(), token), ct);
            }
            catch (OperationCanceledException) { return; }
            catch { /* token blip: the heartbeat still kept the socket alive */ }
        }
    }

    private async Task ReceiveLoop(ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        var sb = new StringBuilder();
        while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
        {
            WebSocketReceiveResult res;
            try { res = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct); }
            catch (OperationCanceledException) { return; }

            if (res.MessageType == WebSocketMessageType.Close) return;
            sb.Append(Encoding.UTF8.GetString(buffer, 0, res.Count));
            if (!res.EndOfMessage) continue;

            var frame = sb.ToString();
            sb.Clear();
            if (RealtimeProtocol.TryParseInsert(frame, out var post))
            {
                try { _onInsert(post); } catch { /* never let a callback kill the socket loop */ }
            }
        }
    }

    private async Task SendAsync(ClientWebSocket ws, string json, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, endOfMessage: true, ct);
    }

    private int NextRef() => Interlocked.Increment(ref _ref);

    public void Dispose()
    {
        try { _cts.Cancel(); } catch { }
        _cts.Dispose();
    }
}
