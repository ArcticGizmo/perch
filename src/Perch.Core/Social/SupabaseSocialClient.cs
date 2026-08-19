using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Perch.Platform;

namespace Perch.Social;

/// <summary>
/// The real <see cref="ISocialClient"/> backed by Supabase (GoTrue auth + PostgREST). M2 implements the
/// auth + profile surface: GitHub sign-in over the desktop PKCE loopback flow, refresh-token session restore
/// (token kept in <see cref="ISecretStore"/>, never plaintext), claiming a handle, and exact-handle lookup.
/// The friend-graph / posting / feed methods are wired in M3; until then they throw so a miswire is loud.
///
/// <para>Sign-in uses authorization-code-with-PKCE (no client secret in the app): we open Supabase's
/// <c>/authorize?provider=github</c> in the browser with a code challenge, catch the redirect on a loopback
/// socket (<see cref="LoopbackListener"/>), then exchange the code for a session. All PostgREST calls carry
/// the publishable key as <c>apikey</c> and the user's access token as the bearer, so row-level security
/// scopes every read/write to the signed-in user.</para>
/// </summary>
public sealed class SupabaseSocialClient : ISocialClient
{
    // Where the refresh token lives (via ISecretStore → DPAPI / Keychain).
    private const string RefreshTokenKey = "supabase.refresh_token";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly SupabaseConfig _config;
    private readonly ISecretStore _secrets;
    private readonly IUrlOpener _urls;
    private readonly HttpClient _http;
    private readonly object _gate = new();

    private string? _accessToken;
    private DateTimeOffset _accessExpiry;
    private Guid _userId;
    private Profile? _me;
    private bool _signedIn;

    public SupabaseSocialClient(SupabaseConfig config, ISecretStore secrets, IUrlOpener urls, HttpClient? http = null)
    {
        _config = config;
        _secrets = secrets;
        _urls = urls;
        _http = http ?? new HttpClient();
    }

    public AuthState Current
    {
        get { lock (_gate) return new AuthState(_signedIn, _me); }
    }

    public event Action<AuthState>? AuthChanged;

    // ── sign-in / restore / sign-out ───────────────────────────────────────────────────────────────

    public async Task<AuthState> SignInAsync(CancellationToken ct = default)
    {
        if (!_config.IsConfigured)
            throw new SocialException("Social isn't configured yet (no Supabase URL / key).");

        var pkce = Pkce.Create();
        using var loopback = LoopbackListener.Start();

        var authorizeUrl =
            $"{BaseUrl}/auth/v1/authorize?provider=github" +
            $"&redirect_to={Uri.EscapeDataString(loopback.RedirectUri)}" +
            $"&code_challenge={pkce.Challenge}&code_challenge_method=S256";
        _urls.Open(authorizeUrl);

        // Give the user a couple of minutes to complete the browser dance.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(3));

        IReadOnlyDictionary<string, string> cb;
        try { cb = await loopback.WaitForCallbackAsync(timeout.Token); }
        catch (OperationCanceledException) { throw new SocialException("Sign-in timed out or was cancelled."); }

        if (cb.TryGetValue("error", out var err))
            throw new SocialException($"GitHub sign-in failed: {cb.GetValueOrDefault("error_description", err)}");
        if (!cb.TryGetValue("code", out var code) || string.IsNullOrEmpty(code))
            throw new SocialException("Sign-in didn't return an authorization code.");

        var token = await ExchangeAsync("pkce", new { auth_code = code, code_verifier = pkce.Verifier }, ct);
        ApplySession(token);
        Raise();                          // signed in now — propagate even if the profile load hiccups
        await LoadProfileOrExplainAsync(ct);
        return Current;
    }

    // Loads the profile after a session is established. A missing row is fine (you just haven't claimed a
    // handle yet); a failed *call* means the profiles table isn't reachable — almost always because the DB
    // migrations haven't been applied — so surface that plainly rather than a raw HTTP code.
    private async Task LoadProfileOrExplainAsync(CancellationToken ct)
    {
        try { await LoadMeAsync(ct); Raise(); }
        catch (SocialException)
        {
            throw new SocialException(
                "Signed in, but couldn't load your profile — check the database migrations have been applied (backend/supabase).");
        }
    }

    /// <summary>Restores a session from the stored refresh token, if any. Returns the (possibly signed-out)
    /// state. Call at startup; never throws — a stale/rejected token just leaves you signed out.</summary>
    public async Task<AuthState> TryRestoreAsync(CancellationToken ct = default)
    {
        var refresh = _secrets.Get(RefreshTokenKey);
        if (string.IsNullOrEmpty(refresh) || !_config.IsConfigured)
            return Current;
        try
        {
            var token = await ExchangeAsync("refresh_token", new { refresh_token = refresh }, ct);
            ApplySession(token);
            Raise();
            // A profile-load failure here is best-effort: the session is valid; the profile can load later.
            // Only a rejected *refresh* (below) means the stored token is dead and should be forgotten.
            try { await LoadMeAsync(ct); Raise(); } catch (SocialException) { }
            return Current;
        }
        catch (SocialException)
        {
            _secrets.Delete(RefreshTokenKey);   // the refresh token was rejected — forget it
            return Current;
        }
    }

    public Task SignOutAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            _accessToken = null;
            _userId = default;
            _me = null;
            _signedIn = false;
        }
        _secrets.Delete(RefreshTokenKey);
        AuthChanged?.Invoke(AuthState.SignedOut);
        return Task.CompletedTask;
    }

    // ── profile ────────────────────────────────────────────────────────────────────────────────────

    public async Task<Profile?> GetMeAsync(CancellationToken ct = default)
    {
        lock (_gate) { if (_me is not null) return _me; if (!_signedIn) return null; }
        await LoadMeAsync(ct);
        return Current.Me;
    }

    public async Task<Profile> ClaimHandleAsync(string handle, string? displayName = null, string? moodEmoji = null,
        CancellationToken ct = default)
    {
        var uid = RequireUser();
        var token = await ValidAccessTokenAsync(ct);

        // Upsert the caller's own profile row (RLS permits id = auth.uid()). A handle already taken by
        // someone else violates the unique constraint → 409, surfaced as a friendly message.
        using var req = Rest(HttpMethod.Post, "/rest/v1/profiles", token);
        req.Headers.Add("Prefer", "resolution=merge-duplicates,return=representation");
        req.Content = JsonContent.Create(new
        {
            id = uid,
            handle = handle.Trim().ToLowerInvariant(),
            display_name = string.IsNullOrWhiteSpace(displayName) ? null : displayName.Trim(),
            mood_emoji = moodEmoji,
        });

        using var resp = await _http.SendAsync(req, ct);
        if (resp.StatusCode is HttpStatusCode.Conflict)
            throw new SocialException($"@{handle} is already taken.");
        if (resp.StatusCode is HttpStatusCode.BadRequest)
            throw new SocialException("That handle isn't allowed — use 3–20 of a–z, 0–9 or _.");
        await EnsureOkAsync(resp, "claim handle", ct);

        var rows = await resp.Content.ReadFromJsonAsync<ProfileRow[]>(Json, ct);
        var row = rows is { Length: > 0 } ? rows[0] : throw new SocialException("The server didn't return the saved profile.");
        var me = row.ToProfile();
        lock (_gate) _me = me;
        Raise();
        return me;
    }

    public async Task<Profile?> FindByHandleAsync(string handle, CancellationToken ct = default)
    {
        var token = await ValidAccessTokenAsync(ct);
        using var req = Rest(HttpMethod.Post, "/rest/v1/rpc/find_profile", token);
        req.Content = JsonContent.Create(new { q = handle.Trim().ToLowerInvariant() });

        using var resp = await _http.SendAsync(req, ct);
        await EnsureOkAsync(resp, "find profile", ct);
        var rows = await resp.Content.ReadFromJsonAsync<ProfileRow[]>(Json, ct);
        return rows is { Length: > 0 } ? rows[0].ToProfile() : null;
    }

    // ── friend graph / posting / feed (M3) ───────────────────────────────────────────────────────────

    public async Task SendRequestAsync(Guid addresseeId, CancellationToken ct = default)
    {
        var uid = RequireUser();
        var token = await ValidAccessTokenAsync(ct);
        using var req = Rest(HttpMethod.Post, "/rest/v1/friendships", token);
        req.Headers.Add("Prefer", "resolution=merge-duplicates");   // re-sending is a no-op
        req.Content = JsonContent.Create(new { requester = uid, addressee = addresseeId, status = "pending" });
        using var resp = await _http.SendAsync(req, ct);
        await EnsureOkAsync(resp, "send the friend request", ct);
    }

    public async Task RespondAsync(Guid requesterId, bool accept, CancellationToken ct = default)
    {
        var uid = RequireUser();
        var token = await ValidAccessTokenAsync(ct);
        string filter = $"?requester=eq.{requesterId}&addressee=eq.{uid}";   // only the addressee (me) may respond
        using var req = accept
            ? Rest(HttpMethod.Patch, "/rest/v1/friendships" + filter, token)
            : Rest(HttpMethod.Delete, "/rest/v1/friendships" + filter, token);
        if (accept) req.Content = JsonContent.Create(new { status = "accepted" });
        using var resp = await _http.SendAsync(req, ct);
        await EnsureOkAsync(resp, accept ? "accept the request" : "decline the request", ct);
    }

    public async Task<IReadOnlyList<Friend>> GetFriendsAsync(CancellationToken ct = default)
    {
        var uid = RequireUser();
        var token = await ValidAccessTokenAsync(ct);
        using var req = Rest(HttpMethod.Get,
            $"/rest/v1/friendships?or=(requester.eq.{uid},addressee.eq.{uid})&select=requester,addressee,status", token);
        using var resp = await _http.SendAsync(req, ct);
        await EnsureOkAsync(resp, "load friends", ct);
        var rows = await resp.Content.ReadFromJsonAsync<FriendshipRow[]>(Json, ct) ?? [];

        var profiles = await FetchProfilesAsync(rows.Select(r => r.Requester == uid ? r.Addressee : r.Requester), token, ct);
        var list = new List<Friend>();
        foreach (var r in rows)
        {
            var otherId = r.Requester == uid ? r.Addressee : r.Requester;
            var profile = profiles.GetValueOrDefault(otherId) ?? new Profile(otherId, "unknown");
            list.Add(new Friend(profile, MapState(r.Status, amRequester: r.Requester == uid)));
        }
        return list;
    }

    public async Task<PostId> PostAsync(string body, string? moodEmoji = null, CancellationToken ct = default)
    {
        var uid = RequireUser();
        body = body?.Trim() ?? "";
        if (body.Length == 0) throw new SocialException("A status can't be empty.");
        if (body.Length > 280) throw new SocialException("A status can't be longer than 280 characters.");

        var token = await ValidAccessTokenAsync(ct);
        using var req = Rest(HttpMethod.Post, "/rest/v1/posts", token);
        req.Headers.Add("Prefer", "return=representation");
        req.Content = JsonContent.Create(new { author = uid, body, mood_emoji = moodEmoji });
        using var resp = await _http.SendAsync(req, ct);
        await EnsureOkAsync(resp, "post your status", ct);
        var rows = await resp.Content.ReadFromJsonAsync<PostRow[]>(Json, ct);
        return new PostId(rows is { Length: > 0 } ? rows[0].Id : Guid.Empty);
    }

    public async Task<IReadOnlyList<FeedItem>> GetFeedAsync(int limit = 50, CancellationToken ct = default)
    {
        RequireUser();
        var token = await ValidAccessTokenAsync(ct);
        limit = Math.Clamp(limit, 1, 200);
        using var req = Rest(HttpMethod.Get,
            $"/rest/v1/posts?select=id,author,body,mood_emoji,created_at&order=created_at.desc&limit={limit}", token);
        using var resp = await _http.SendAsync(req, ct);
        await EnsureOkAsync(resp, "load the feed", ct);
        var rows = await resp.Content.ReadFromJsonAsync<PostRow[]>(Json, ct) ?? [];

        var profiles = await FetchProfilesAsync(rows.Select(r => r.Author), token, ct);
        return rows.Select(r => new FeedItem(
            r.Id, profiles.GetValueOrDefault(r.Author) ?? new Profile(r.Author, "unknown"),
            r.Body, r.MoodEmoji, r.CreatedAt)).ToList();
    }

    // ── roster + reactions ──────────────────────────────────────────────────────────────────────────────

    public async Task<RosterSnapshot> GetRosterAsync(CancellationToken ct = default)
    {
        var uid = RequireUser();
        var token = await ValidAccessTokenAsync(ct);

        // Accepted friends only — the roster is who you can actually see.
        var friends = (await GetFriendsAsync(ct)).Where(f => f.State == FriendshipState.Accepted).ToList();

        // Latest post per author (the feed is newest-first, so the first hit per author is their latest).
        var feed = await GetFeedAsync(200, ct);
        var latestByAuthor = new Dictionary<Guid, FeedItem>();
        foreach (var item in feed) latestByAuthor.TryAdd(item.Author.Id, item);

        // Reactions for just those latest posts, grouped by emoji.
        var latestIds = friends
            .Select(f => latestByAuthor.GetValueOrDefault(f.Profile.Id)?.Id)
            .Where(id => id is not null).Select(id => id!.Value).ToList();
        var reactions = await FetchReactionsAsync(latestIds, uid, token, ct);

        var entries = friends
            .Select(f =>
            {
                var latest = latestByAuthor.GetValueOrDefault(f.Profile.Id);
                var rx = latest is not null ? reactions.GetValueOrDefault(latest.Id, []) : [];
                return new RosterFriend(f.Profile, latest, rx);
            })
            // Most-recently-active first; friends who haven't posted fall to the bottom, alphabetically.
            .OrderByDescending(e => e.Latest?.CreatedAt ?? DateTimeOffset.MinValue)
            .ThenBy(e => e.Profile.Handle, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new RosterSnapshot(_me, entries);
    }

    public async Task ReactAsync(Guid postId, string emoji, bool on, CancellationToken ct = default)
    {
        var uid = RequireUser();
        emoji = emoji?.Trim() ?? "";
        if (emoji.Length == 0) return;
        var token = await ValidAccessTokenAsync(ct);

        if (on)
        {
            using var req = Rest(HttpMethod.Post, "/rest/v1/reactions", token);
            req.Headers.Add("Prefer", "resolution=merge-duplicates");   // re-reacting is a no-op
            req.Content = JsonContent.Create(new { post_id = postId, reactor = uid, emoji });
            using var resp = await _http.SendAsync(req, ct);
            await EnsureOkAsync(resp, "add your reaction", ct);
        }
        else
        {
            using var req = Rest(HttpMethod.Delete,
                $"/rest/v1/reactions?post_id=eq.{postId}&reactor=eq.{uid}&emoji=eq.{Uri.EscapeDataString(emoji)}", token);
            using var resp = await _http.SendAsync(req, ct);
            await EnsureOkAsync(resp, "remove your reaction", ct);
        }
    }

    // Batch-fetches reactions for the given post ids, grouped per post into (emoji → count, mine). RLS returns
    // only reactions on posts you can see, plus your own.
    private async Task<Dictionary<Guid, IReadOnlyList<ReactionGroup>>> FetchReactionsAsync(
        IReadOnlyList<Guid> postIds, Guid uid, string token, CancellationToken ct)
    {
        var result = new Dictionary<Guid, IReadOnlyList<ReactionGroup>>();
        if (postIds.Count == 0) return result;

        using var req = Rest(HttpMethod.Get,
            $"/rest/v1/reactions?post_id=in.({string.Join(",", postIds)})&select=post_id,reactor,emoji", token);
        using var resp = await _http.SendAsync(req, ct);
        await EnsureOkAsync(resp, "load reactions", ct);
        var rows = await resp.Content.ReadFromJsonAsync<ReactionRow[]>(Json, ct) ?? [];

        foreach (var byPost in rows.GroupBy(r => r.PostId))
        {
            var groups = byPost
                .GroupBy(r => r.Emoji)
                .Select(g => new ReactionGroup(g.Key, g.Count(), g.Any(r => r.Reactor == uid)))
                .OrderByDescending(g => g.Count).ThenBy(g => g.Emoji, StringComparer.Ordinal)
                .ToList();
            result[byPost.Key] = groups;
        }
        return result;
    }

    // ── block / report (M6) ───────────────────────────────────────────────────────────────────────────

    public async Task BlockAsync(Guid userId, CancellationToken ct = default)
    {
        var uid = RequireUser();
        var token = await ValidAccessTokenAsync(ct);
        using var req = Rest(HttpMethod.Post, "/rest/v1/blocks", token);
        req.Headers.Add("Prefer", "resolution=merge-duplicates");   // re-blocking is a no-op
        req.Content = JsonContent.Create(new { blocker = uid, blocked = userId });
        using var resp = await _http.SendAsync(req, ct);
        await EnsureOkAsync(resp, "block this user", ct);
    }

    public async Task UnblockAsync(Guid userId, CancellationToken ct = default)
    {
        var uid = RequireUser();
        var token = await ValidAccessTokenAsync(ct);
        using var req = Rest(HttpMethod.Delete, $"/rest/v1/blocks?blocker=eq.{uid}&blocked=eq.{userId}", token);
        using var resp = await _http.SendAsync(req, ct);
        await EnsureOkAsync(resp, "unblock this user", ct);
    }

    public async Task<IReadOnlyList<Profile>> GetBlockedAsync(CancellationToken ct = default)
    {
        var token = await ValidAccessTokenAsync(ct);
        // A SECURITY DEFINER RPC returns just the caller's blocked profiles (id/handle/name) — a blocked
        // stranger has no friendship edge, so the base-table profile policy wouldn't expose their handle.
        using var req = Rest(HttpMethod.Post, "/rest/v1/rpc/list_blocked", token);
        req.Content = JsonContent.Create(new { });
        using var resp = await _http.SendAsync(req, ct);
        await EnsureOkAsync(resp, "load your block list", ct);
        var rows = await resp.Content.ReadFromJsonAsync<ProfileRow[]>(Json, ct) ?? [];
        return rows.Select(r => r.ToProfile()).ToList();
    }

    public async Task ReportAsync(Guid userId, string? reason = null, CancellationToken ct = default)
    {
        var uid = RequireUser();
        if (reason is { Length: > 500 }) reason = reason[..500];
        var token = await ValidAccessTokenAsync(ct);
        using var req = Rest(HttpMethod.Post, "/rest/v1/reports", token);
        req.Content = JsonContent.Create(new { reporter = uid, reported = userId, reason });
        using var resp = await _http.SendAsync(req, ct);
        await EnsureOkAsync(resp, "submit the report", ct);
    }

    // Realtime (M5): open a WebSocket subscription to public.posts INSERTs. RLS scopes the change stream to
    // what the signed-in user may see, exactly like the feed poll. Each insert is delivered as a FeedItem with
    // a placeholder author (liveness only needs "something new landed"; the feed poll resolves the profile),
    // so the caller can treat this purely as a nudge to re-fetch. Unconfigured → a safe no-op; the poll covers
    // it. A blocked/unavailable socket degrades to the poll transparently (see SupabaseRealtimeConnection).
    public IDisposable SubscribeFeed(Action<FeedItem> onPost)
    {
        if (!_config.IsConfigured) return new NoopDisposable();
        return new SupabaseRealtimeConnection(BaseUrl, _config.PublishableKey, ValidAccessTokenAsync,
            post => onPost(new FeedItem(post.Id, new Profile(post.Author, "…"), post.Body, post.Mood, post.CreatedAt)));
    }

    // Batch-fetches profiles by id (RLS returns only those you may see: your own + friendship-edge shared).
    private async Task<Dictionary<Guid, Profile>> FetchProfilesAsync(IEnumerable<Guid> ids, string token, CancellationToken ct)
    {
        var idList = ids.Where(i => i != Guid.Empty).Distinct().ToList();
        if (idList.Count == 0) return new Dictionary<Guid, Profile>();

        using var req = Rest(HttpMethod.Get,
            $"/rest/v1/profiles?id=in.({string.Join(",", idList)})&select=id,handle,display_name,mood_emoji", token);
        using var resp = await _http.SendAsync(req, ct);
        await EnsureOkAsync(resp, "load profiles", ct);
        var rows = await resp.Content.ReadFromJsonAsync<ProfileRow[]>(Json, ct) ?? [];
        return rows.ToDictionary(r => r.Id, r => r.ToProfile());
    }

    private static FriendshipState MapState(string status, bool amRequester) => status switch
    {
        "accepted" => FriendshipState.Accepted,
        "blocked" => FriendshipState.Blocked,
        _ => amRequester ? FriendshipState.Pending : FriendshipState.Incoming,
    };

    private sealed class NoopDisposable : IDisposable { public void Dispose() { } }

    // ── internals ──────────────────────────────────────────────────────────────────────────────────

    private string BaseUrl => _config.Url.TrimEnd('/');

    // Exchanges an auth code (grant "pkce") or a refresh token (grant "refresh_token") for a session.
    private async Task<TokenResponse> ExchangeAsync(string grant, object body, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/auth/v1/token?grant_type={grant}");
        req.Headers.Add("apikey", _config.PublishableKey);
        req.Content = JsonContent.Create(body);

        using var resp = await _http.SendAsync(req, ct);
        await EnsureOkAsync(resp, "sign in", ct);
        return await resp.Content.ReadFromJsonAsync<TokenResponse>(Json, ct)
               ?? throw new SocialException("The sign-in response was empty.");
    }

    private void ApplySession(TokenResponse token)
    {
        if (string.IsNullOrEmpty(token.AccessToken) || token.User is null)
            throw new SocialException("Sign-in didn't return a valid session.");

        lock (_gate)
        {
            _accessToken = token.AccessToken;
            _accessExpiry = DateTimeOffset.UtcNow.AddSeconds(Math.Max(60, token.ExpiresIn) - 30);
            _userId = token.User.Id;
            _signedIn = true;
        }
        if (!string.IsNullOrEmpty(token.RefreshToken))
            _secrets.Set(RefreshTokenKey, token.RefreshToken);
    }

    // Returns a non-expired access token, refreshing via the stored refresh token if needed.
    private async Task<string> ValidAccessTokenAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            if (_accessToken is { } tok && DateTimeOffset.UtcNow < _accessExpiry) return tok;
        }
        var refresh = _secrets.Get(RefreshTokenKey)
            ?? throw new SocialException("You're signed out. Sign in again.");
        var token = await ExchangeAsync("refresh_token", new { refresh_token = refresh }, ct);
        ApplySession(token);
        return _accessToken!;
    }

    private async Task LoadMeAsync(CancellationToken ct)
    {
        var uid = RequireUser();
        var token = await ValidAccessTokenAsync(ct);
        using var req = Rest(HttpMethod.Get, $"/rest/v1/profiles?id=eq.{uid}&select=id,handle,display_name,mood_emoji", token);
        using var resp = await _http.SendAsync(req, ct);
        await EnsureOkAsync(resp, "load profile", ct);
        var rows = await resp.Content.ReadFromJsonAsync<ProfileRow[]>(Json, ct);
        lock (_gate) _me = rows is { Length: > 0 } ? rows[0].ToProfile() : null;   // null = handle not claimed yet
    }

    private HttpRequestMessage Rest(HttpMethod method, string path, string accessToken)
    {
        var req = new HttpRequestMessage(method, $"{BaseUrl}{path}");
        req.Headers.Add("apikey", _config.PublishableKey);
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
        return req;
    }

    private Guid RequireUser()
    {
        lock (_gate)
        {
            if (!_signedIn || _userId == default) throw new SocialException("You're not signed in.");
            return _userId;
        }
    }

    private AuthState Raise()
    {
        var state = Current;
        AuthChanged?.Invoke(state);
        return state;
    }

    private static async Task EnsureOkAsync(HttpResponseMessage resp, string what, CancellationToken ct)
    {
        if (resp.IsSuccessStatusCode) return;
        string detail = "";
        try { detail = await resp.Content.ReadAsStringAsync(ct); } catch { }
        throw new SocialException($"Couldn't {what} ({(int)resp.StatusCode}). {Trim(detail)}".Trim());
    }

    private static string Trim(string s) => s.Length <= 200 ? s : s[..200];

    // ── wire DTOs ────────────────────────────────────────────────────────────────────────────────────

    private sealed record TokenResponse(
        [property: JsonPropertyName("access_token")] string? AccessToken,
        [property: JsonPropertyName("refresh_token")] string? RefreshToken,
        [property: JsonPropertyName("expires_in")] int ExpiresIn,
        [property: JsonPropertyName("user")] GotrueUser? User);

    private sealed record GotrueUser([property: JsonPropertyName("id")] Guid Id);

    private sealed record ProfileRow(
        [property: JsonPropertyName("id")] Guid Id,
        [property: JsonPropertyName("handle")] string Handle,
        [property: JsonPropertyName("display_name")] string? DisplayName,
        [property: JsonPropertyName("mood_emoji")] string? MoodEmoji)
    {
        public Profile ToProfile() => new(Id, Handle, DisplayName, MoodEmoji);
    }

    private sealed record FriendshipRow(
        [property: JsonPropertyName("requester")] Guid Requester,
        [property: JsonPropertyName("addressee")] Guid Addressee,
        [property: JsonPropertyName("status")] string Status);

    private sealed record PostRow(
        [property: JsonPropertyName("id")] Guid Id,
        [property: JsonPropertyName("author")] Guid Author,
        [property: JsonPropertyName("body")] string Body,
        [property: JsonPropertyName("mood_emoji")] string? MoodEmoji,
        [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt);

    private sealed record ReactionRow(
        [property: JsonPropertyName("post_id")] Guid PostId,
        [property: JsonPropertyName("reactor")] Guid Reactor,
        [property: JsonPropertyName("emoji")] string Emoji);
}
