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
        await LoadMeAsync(ct);
        return Raise();
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
            await LoadMeAsync(ct);
            return Raise();
        }
        catch
        {
            _secrets.Delete(RefreshTokenKey);   // token no longer valid — forget it
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
    private static SocialException NotYet() => new("This part of Social isn't wired up yet (M3).");
    public Task SendRequestAsync(Guid addresseeId, CancellationToken ct = default) => throw NotYet();
    public Task RespondAsync(Guid requesterId, bool accept, CancellationToken ct = default) => throw NotYet();
    public Task<IReadOnlyList<Friend>> GetFriendsAsync(CancellationToken ct = default) => throw NotYet();
    public Task<PostId> PostAsync(string body, string? moodEmoji = null, CancellationToken ct = default) => throw NotYet();
    public Task<IReadOnlyList<FeedItem>> GetFeedAsync(int limit = 50, CancellationToken ct = default) => throw NotYet();
    public IDisposable SubscribeFeed(Action<FeedItem> onPost) => throw NotYet();

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
}
