using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Perch.Platform;

namespace Perch.Data;

/// <summary>Where one config dir's sign-in is stored, as the refresher reads and rewrites it: the primary through the
/// OS credential store behind <see cref="IClaudeCredentials"/>, any other dir through its own <c>.credentials.json</c>
/// (the same split the usage poll uses).</summary>
internal sealed record SignInStore(ClaudeConfigDir Dir, IClaudeCredentials Credentials)
{
    public static SignInStore For(ClaudeConfigDir dir, IClaudeCredentials primary) =>
        new(dir, dir.Equals(ClaudeConfigSet.Instance.Primary) ? primary : new FileClaudeCredentials(dir.CredentialsFile));
}

/// <summary>What one refresh pass did for a dir. <see cref="LoginRequired"/> means the sign-in can't be renewed
/// without <c>claude auth login</c>; <see cref="Busy"/> and <see cref="Failed"/> are worth retrying later.</summary>
internal enum RefreshOutcome { NotSignedIn, NotDue, Refreshed, Busy, Failed, LoginRequired }

internal sealed record RefreshResult(RefreshOutcome Outcome, string? Reason = null);

/// <summary>
/// The opt-in sign-in refresher: renews a config dir's OAuth access token <see cref="Lead"/> before it expires,
/// exactly as Claude Code does on a request near expiry, so the usage poll keeps working and a session started or
/// restarted later picks up a live token even when no interactive Claude Code is running.
///
/// <para>It never logs in. When the refresh token is past its expiry, missing, or rejected by the server, the outcome
/// is <see cref="RefreshOutcome.LoginRequired"/> with a reason, and a rejected token is remembered so it isn't retried
/// until the stored sign-in changes (the user ran <c>claude auth login</c>).</para>
///
/// <para>A refresh rotates the refresh token, so it must not race a Claude Code process doing the same. Claude Code
/// serialises its own refreshes with a proper-lockfile lock directory, <c>&lt;config dir&gt;/.oauth_refresh.lock</c>
/// (stale after 60 s), and after taking it re-reads storage and adopts any token another process wrote. Perch takes
/// the same lock and re-reads under it, so whichever side refreshes second sees the first one's tokens instead of
/// spending a dead refresh token. The request mirrors Claude Code's own (<see cref="TokenUrl"/>, its public client
/// id, the stored scopes).</para>
/// </summary>
internal sealed class SignInRefresher
{
    /// <summary>How far ahead of expiry the access token is renewed — Claude Code's own refresh point.</summary>
    public static readonly TimeSpan Lead = TimeSpan.FromMinutes(5);

    internal const string TokenUrl = "https://platform.claude.com/v1/oauth/token";
    internal const string DefaultClientId = "9d1c250a-e61b-44d9-88ed-5944d1962f5e";

    private static readonly JsonSerializerOptions WriteOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly HttpClient _http;

    // Refresh tokens the server rejected, as hashes so the secret isn't kept around. A login that's already dead
    // isn't re-sent every tick; a new login writes a different token and clears it.
    private readonly Dictionary<string, string> _dead = new(StringComparer.Ordinal);
    private readonly Lock _deadGate = new();

    public SignInRefresher(HttpMessageHandler? handler = null) =>
        _http = new HttpClient(handler ?? new HttpClientHandler()) { Timeout = TimeSpan.FromSeconds(20) };

    /// <summary>The <c>claudeAiOauth</c> fields the refresher needs. Times are Unix epoch milliseconds.</summary>
    internal sealed record OAuthBlob(
        string? AccessToken, string? RefreshToken, long? ExpiresAtMs, long? RefreshExpiresAtMs, string[] Scopes, string? ClientId)
    {
        public static OAuthBlob? Parse(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try
            {
                if (JsonNode.Parse(json)?["claudeAiOauth"] is not JsonObject o) return null;
                return new OAuthBlob(Str(o["accessToken"]), Str(o["refreshToken"]), Ms(o["expiresAt"]),
                    Ms(o["refreshTokenExpiresAt"]),
                    o["scopes"] is JsonArray a ? a.Select(Str).OfType<string>().ToArray() : [],
                    Str(o["clientId"]));
            }
            catch
            {
                return null;
            }
        }

        private static string? Str(JsonNode? n) =>
            n is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0 ? s : null;

        private static long? Ms(JsonNode? n) =>
            n is not null && long.TryParse(n.ToString(), out var ms) && ms > 0 ? ms : null;
    }

    internal enum Plan { NotSignedIn, NotDue, Due, LoginExpired, NoRefreshToken, KnownDead }

    /// <summary>What a pass should do with <paramref name="blob"/> at <paramref name="utcNow"/>. Pure, for the tests.</summary>
    internal static Plan PlanFor(OAuthBlob? blob, DateTime utcNow, Func<string, bool> isDead)
    {
        if (blob is null || (blob.AccessToken is null && blob.RefreshToken is null)) return Plan.NotSignedIn;
        long now = new DateTimeOffset(utcNow).ToUnixTimeMilliseconds();
        if (blob.RefreshExpiresAtMs is { } loginEnds && loginEnds <= now) return Plan.LoginExpired;
        if (blob.ExpiresAtMs is not { } accessEnds) return Plan.NotDue;
        if (accessEnds - now > (long)Lead.TotalMilliseconds) return Plan.NotDue;
        // Without a refresh token a lapsing access token can't be renewed — but it's only a login problem once it lapses.
        if (blob.RefreshToken is null) return accessEnds <= now ? Plan.NoRefreshToken : Plan.NotDue;
        return isDead(blob.RefreshToken) ? Plan.KnownDead : Plan.Due;
    }

    /// <summary>Renews <paramref name="store"/>'s sign-in if it's within <see cref="Lead"/> of expiring. Blocking file IO
    /// and a network call — run it off the UI thread. Never throws.</summary>
    public async Task<RefreshResult> RefreshIfDueAsync(SignInStore store, DateTime utcNow, CancellationToken ct = default)
    {
        try
        {
            var json = store.Credentials.ReadCredentialsJson();
            var blob = OAuthBlob.Parse(json);
            var plan = PlanFor(blob, utcNow, IsDead);
            if (plan != Plan.Due) return Settled(plan, blob);

            using var held = OAuthRefreshLock.TryAcquire(store.Dir.Root);
            if (held is null) return new(RefreshOutcome.Busy, "Claude Code is refreshing this sign-in");

            // Another process may have refreshed between the first read and the lock: only its tokens count now.
            json = store.Credentials.ReadCredentialsJson();
            blob = OAuthBlob.Parse(json);
            plan = PlanFor(blob, utcNow, IsDead);
            if (plan != Plan.Due) return Settled(plan, blob);

            return await ExchangeAsync(store, json!, blob!, utcNow, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return new(RefreshOutcome.Failed, ex.Message);
        }
    }

    private RefreshResult Settled(Plan plan, OAuthBlob? blob) => plan switch
    {
        Plan.NotSignedIn => new(RefreshOutcome.NotSignedIn),
        Plan.LoginExpired => new(RefreshOutcome.LoginRequired, LoginExpiredReason(blob!.RefreshExpiresAtMs!.Value)),
        Plan.NoRefreshToken => new(RefreshOutcome.LoginRequired, "The sign-in expired and has no refresh token to renew it with."),
        Plan.KnownDead => new(RefreshOutcome.LoginRequired, DeadReason(blob!.RefreshToken!)),
        _ => new(RefreshOutcome.NotDue),
    };

    private static string LoginExpiredReason(long ms)
    {
        var at = DateTimeOffset.FromUnixTimeMilliseconds(ms).LocalDateTime;
        return $"The login expired on {at:ddd d MMM} at {at:t}, so the sign-in can't be renewed.";
    }

    private async Task<RefreshResult> ExchangeAsync(
        SignInStore store, string json, OAuthBlob blob, DateTime utcNow, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = blob.RefreshToken,
            ["client_id"] = blob.ClientId ?? DefaultClientId,
        };
        if (blob.Scopes.Length > 0) body["scope"] = string.Join(' ', blob.Scopes);

        using var req = new HttpRequestMessage(HttpMethod.Post, TokenUrl)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        req.Headers.UserAgent.ParseAdd($"claude-code/{UsageMonitor.ReadCliVersion()}");
        req.Headers.Accept.ParseAdd("application/json");

        HttpResponseMessage resp;
        try { resp = await _http.SendAsync(req, ct).ConfigureAwait(false); }
        catch (Exception ex) { return new(RefreshOutcome.Failed, "Couldn't reach the sign-in server: " + ex.Message); }

        using (resp)
        {
            var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (resp.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized)
                return Rejected(store, blob, text, (int)resp.StatusCode);
            if (!resp.IsSuccessStatusCode)
                return new(RefreshOutcome.Failed, $"The sign-in server returned {(int)resp.StatusCode}");

            var updated = JsonNode.Parse(text) is JsonObject r ? ApplyRefresh(json, r, utcNow) : null;
            if (updated is null)
                return new(RefreshOutcome.Failed, "The sign-in server's reply had no new token");

            // The old refresh token is spent now, so failing to save the new one loses the sign-in.
            if (!store.Credentials.WriteCredentialsJson(updated))
            {
                MarkDead(blob.RefreshToken!, "Perch renewed the sign-in but couldn't save it, so the old one no longer works.");
                return new(RefreshOutcome.LoginRequired, DeadReason(blob.RefreshToken!));
            }
            return new(RefreshOutcome.Refreshed);
        }
    }

    // A 400/401 from the token endpoint. invalid_grant (and any 401) means the refresh token itself is no good —
    // expired, revoked, or already rotated by someone who didn't save it — so only a login fixes it. Anything else
    // (a malformed request, a scope the server won't grant) is retried later rather than condemning the login.
    private RefreshResult Rejected(SignInStore store, OAuthBlob blob, string text, int status)
    {
        string? error = null, description = null;
        try
        {
            if (JsonNode.Parse(text) is JsonObject e)
            {
                error = e["error"]?.ToString();
                description = e["error_description"]?.ToString();
            }
        }
        catch { }

        // Another process may have rotated the token under us (its lock outlived ours, or it doesn't take one): its
        // save is the live sign-in, not a dead one.
        if (OAuthBlob.Parse(store.Credentials.ReadCredentialsJson())?.RefreshToken is { } current
            && current != blob.RefreshToken)
            return new(RefreshOutcome.NotDue);

        string detail = description is { Length: > 0 } ? $"{error}: {description}" : error ?? $"HTTP {status}";
        if (status == 401 || error == "invalid_grant")
        {
            MarkDead(blob.RefreshToken!, $"The sign-in server rejected the refresh token ({detail}).");
            return new(RefreshOutcome.LoginRequired, DeadReason(blob.RefreshToken!));
        }
        return new(RefreshOutcome.Failed, $"The sign-in server refused the refresh ({detail})");
    }

    /// <summary>
    /// The credentials blob with <paramref name="response"/>'s tokens applied, or null when the reply lacks an access
    /// token or lifetime. Only the <c>claudeAiOauth</c> fields a refresh changes are touched; everything else in the blob
    /// (other OAuth entries, subscription fields) is kept. The refresh token and its expiry are kept when the reply
    /// doesn't rotate them. Pure, for the tests.
    /// </summary>
    internal static string? ApplyRefresh(string blobJson, JsonObject response, DateTime utcNow)
    {
        if (JsonNode.Parse(blobJson) is not JsonObject root || root["claudeAiOauth"] is not JsonObject oauth) return null;
        if (response["access_token"] is not JsonValue a || !a.TryGetValue<string>(out var access) || access.Length == 0)
            return null;
        if (Seconds(response["expires_in"]) is not { } lifetime) return null;

        long now = new DateTimeOffset(utcNow).ToUnixTimeMilliseconds();
        oauth["accessToken"] = access;
        oauth["expiresAt"] = now + (long)(lifetime * 1000);
        if (response["refresh_token"] is JsonValue rv && rv.TryGetValue<string>(out var refresh) && refresh.Length > 0)
            oauth["refreshToken"] = refresh;
        if (Seconds(response["refresh_token_expires_in"]) is { } loginLifetime)
            oauth["refreshTokenExpiresAt"] = now + (long)(loginLifetime * 1000);
        if (response["scope"] is JsonValue sv && sv.TryGetValue<string>(out var scope)
            && scope.Split(' ', StringSplitOptions.RemoveEmptyEntries) is { Length: > 0 } scopes)
            oauth["scopes"] = new JsonArray(scopes.Select(s => (JsonNode)s).ToArray());
        return root.ToJsonString(WriteOptions);
    }

    private static double? Seconds(JsonNode? n) =>
        n is JsonValue v && v.TryGetValue<double>(out var d) && d > 0 ? d : null;

    private static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private bool IsDead(string refreshToken)
    {
        lock (_deadGate) return _dead.ContainsKey(Hash(refreshToken));
    }

    private void MarkDead(string refreshToken, string reason)
    {
        lock (_deadGate) _dead[Hash(refreshToken)] = reason;
    }

    private string DeadReason(string refreshToken)
    {
        lock (_deadGate) return _dead.GetValueOrDefault(Hash(refreshToken)) ?? "The sign-in server rejected the refresh token.";
    }
}

/// <summary>
/// Claude Code's OAuth refresh lock: the directory <c>&lt;config dir&gt;/.oauth_refresh.lock</c>, created by
/// proper-lockfile while a process refreshes and kept fresh by touching its mtime every 5 s; one untouched for 60 s
/// is stale and may be taken over. Perch holds it only for the length of one token request (well under the stale
/// window), so it never needs touching.
/// </summary>
internal static class OAuthRefreshLock
{
    internal const string Name = ".oauth_refresh.lock";
    private static readonly TimeSpan Stale = TimeSpan.FromSeconds(60);

    /// <summary>Takes the lock in <paramref name="configRoot"/>, or returns null while someone else holds it (or the
    /// dir can't be written). Dispose to release.</summary>
    public static IDisposable? TryAcquire(string configRoot)
    {
        var path = Path.Combine(configRoot, Name);
        string? staging = null;
        try
        {
            if (Directory.Exists(path))
            {
                if (DateTime.UtcNow - Directory.GetLastWriteTimeUtc(path) < Stale) return null;
                Directory.Delete(path);
            }
            // Renaming a fresh directory into place fails if the lock appeared meanwhile, where a plain create would
            // silently succeed and leave two holders.
            staging = path + "." + Guid.NewGuid().ToString("N");
            Directory.CreateDirectory(staging);
            Directory.Move(staging, path);
            staging = null;
            return new Held(path);
        }
        catch
        {
            return null;
        }
        finally
        {
            if (staging is not null)
                try { Directory.Delete(staging); } catch { }
        }
    }

    private sealed class Held(string path) : IDisposable
    {
        public void Dispose()
        {
            try { Directory.Delete(path); } catch { }
        }
    }
}
