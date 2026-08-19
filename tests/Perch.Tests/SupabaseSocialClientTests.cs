using System.Net;
using System.Text;
using Perch.Platform;
using Perch.Social;
using Xunit;

namespace Perch.Tests;

/// <summary>
/// Exercises the REST orchestration of <see cref="SupabaseSocialClient"/> against a stub HTTP handler (no
/// network): session restore from a stored refresh token, loading the profile, claiming a handle (incl. the
/// taken-handle 409), and exact-handle lookup. The browser/loopback half of sign-in isn't covered here —
/// that's the socket-level piece verified live.
/// </summary>
public sealed class SupabaseSocialClientTests
{
    private const string Uid = "11111111-1111-4111-8111-111111111111";
    private const string Other = "22222222-2222-4222-8222-222222222222";
    private const string Other2 = "33333333-3333-4333-8333-333333333333";

    private static SupabaseSocialClient NewClient(StubHandler handler, ISecretStore secrets) =>
        new(new SupabaseConfig("https://demo.supabase.co", "sb_publishable_test"),
            secrets, new NoopUrlOpener(), new HttpClient(handler));

    private static string TokenJson() =>
        """{"access_token":"jwt.access","refresh_token":"rt2","expires_in":3600,"user":{"id":"UID"}}""".Replace("UID", Uid);

    // A profiles-array JSON body with the given handle (display/mood left null), Uid substituted.
    private static string ProfJson(string handle) =>
        ("""[{"id":"UID","handle":"HANDLE","display_name":null,"mood_emoji":null}]""")
            .Replace("UID", Uid).Replace("HANDLE", handle);

    // A posts-array JSON body authored by Uid, with the given body.
    private static string PostsJson(string body) =>
        ("""[{"id":"aaaaaaaa-0000-4000-8000-000000000009","author":"UID","body":"BODY","mood_emoji":null,"created_at":"2026-01-01T00:00:00Z"}]""")
            .Replace("UID", Uid).Replace("BODY", body);

    [Fact]
    public async Task Restore_from_refresh_token_signs_in_and_loads_profile()
    {
        var secrets = new InMemorySecretStore();
        secrets.Set("supabase.refresh_token", "rt1");

        var handler = new StubHandler(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path == "/auth/v1/token") return (HttpStatusCode.OK, TokenJson());
            if (path == "/rest/v1/profiles")
                return (HttpStatusCode.OK, ProfJson("ada"));
            return (HttpStatusCode.NotFound, "[]");
        });

        var client = NewClient(handler, secrets);
        var state = await client.TryRestoreAsync();

        Assert.True(state.SignedIn);
        Assert.Equal("ada", state.Me?.Handle);
        Assert.Equal("rt2", secrets.Get("supabase.refresh_token"));   // rotated refresh token persisted
    }

    [Fact]
    public async Task Restore_with_a_rejected_token_signs_out_and_forgets_it()
    {
        var secrets = new InMemorySecretStore();
        secrets.Set("supabase.refresh_token", "stale");
        var handler = new StubHandler(_ => (HttpStatusCode.BadRequest, """{"error":"invalid_grant"}"""));

        var state = await NewClient(handler, secrets).TryRestoreAsync();

        Assert.False(state.SignedIn);
        Assert.Null(secrets.Get("supabase.refresh_token"));           // forgotten
    }

    [Fact]
    public async Task ClaimHandle_posts_and_returns_the_saved_profile()
    {
        var (client, _) = await SignedInClient(req =>
        {
            if (req.RequestUri!.AbsolutePath == "/rest/v1/profiles" && req.Method == HttpMethod.Post)
                return (HttpStatusCode.Created, ProfJson("ada"));
            return null;   // fall back to the sign-in defaults
        });

        var me = await client.ClaimHandleAsync("Ada", "Ada L.", "🦉");
        Assert.Equal("ada", me.Handle);
        Assert.Equal("ada", client.Current.Me?.Handle);
    }

    [Fact]
    public async Task ClaimHandle_maps_a_conflict_to_a_taken_message()
    {
        var (client, _) = await SignedInClient(req =>
            req.RequestUri!.AbsolutePath == "/rest/v1/profiles" && req.Method == HttpMethod.Post
                ? (HttpStatusCode.Conflict, """{"code":"23505","message":"duplicate key"}""")
                : null);

        var ex = await Assert.ThrowsAsync<SocialException>(() => client.ClaimHandleAsync("taken"));
        Assert.Contains("taken", ex.Message);
    }

    [Fact]
    public async Task FindByHandle_returns_exact_match_or_null()
    {
        var (client, _) = await SignedInClient(req =>
        {
            if (req.RequestUri!.AbsolutePath == "/rest/v1/rpc/find_profile")
            {
                var body = req.Content!.ReadAsStringAsync().Result;
                return body.Contains("\"ghost\"")
                    ? (HttpStatusCode.OK, "[]")
                    : (HttpStatusCode.OK, ProfJson("ada"));
            }
            return null;
        });

        Assert.Equal("ada", (await client.FindByHandleAsync("ada"))?.Handle);
        Assert.Null(await client.FindByHandleAsync("ghost"));
    }

    [Fact]
    public async Task Post_sends_the_body_and_returns_an_id()
    {
        var (client, _) = await SignedInClient(req =>
            req.RequestUri!.AbsolutePath == "/rest/v1/posts" && req.Method == HttpMethod.Post
                ? (HttpStatusCode.Created, PostsJson("hello"))
                : null);
        Assert.NotEqual(default, await client.PostAsync("hello"));
    }

    [Fact]
    public async Task Post_rejects_empty_and_over_long_bodies()
    {
        var (client, _) = await SignedInClient(_ => null);
        await Assert.ThrowsAsync<SocialException>(() => client.PostAsync("   "));
        await Assert.ThrowsAsync<SocialException>(() => client.PostAsync(new string('x', 281)));
    }

    [Fact]
    public async Task Feed_resolves_author_handles()
    {
        var (client, _) = await SignedInClient(req =>
            req.RequestUri!.AbsolutePath == "/rest/v1/posts" && req.Method == HttpMethod.Get
                ? (HttpStatusCode.OK, PostsJson("hi from ada"))
                : null);   // profiles fetch falls through to the default (UID -> ada)

        var feed = await client.GetFeedAsync();
        Assert.Equal("ada", Assert.Single(feed).Author.Handle);
        Assert.Equal("hi from ada", feed[0].Body);
    }

    [Fact]
    public async Task Friends_maps_incoming_and_accepted_with_handles()
    {
        var (client, _) = await SignedInClient(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path == "/rest/v1/friendships")
                return (HttpStatusCode.OK,
                    ("""[{"requester":"OTHER","addressee":"UID","status":"pending"},{"requester":"UID","addressee":"OTHER2","status":"accepted"}]""")
                        .Replace("UID", Uid).Replace("OTHER2", Other2).Replace("OTHER", Other));
            if (path == "/rest/v1/profiles" && req.RequestUri!.Query.Contains("in."))
                return (HttpStatusCode.OK,
                    ("""[{"id":"OTHER","handle":"grace","display_name":null,"mood_emoji":null},{"id":"OTHER2","handle":"linus","display_name":null,"mood_emoji":null}]""")
                        .Replace("OTHER2", Other2).Replace("OTHER", Other));
            return null;
        });

        var friends = await client.GetFriendsAsync();
        Assert.Equal(2, friends.Count);
        Assert.Contains(friends, f => f.Profile.Handle == "grace" && f.State == FriendshipState.Incoming);
        Assert.Contains(friends, f => f.Profile.Handle == "linus" && f.State == FriendshipState.Accepted);
    }

    // Builds a client already signed in via a restore, layering the caller's route overrides on top of the
    // default token + profile responses.
    private static async Task<(SupabaseSocialClient client, InMemorySecretStore secrets)> SignedInClient(
        Func<HttpRequestMessage, (HttpStatusCode, string)?> overrides)
    {
        var secrets = new InMemorySecretStore();
        secrets.Set("supabase.refresh_token", "rt1");
        var handler = new StubHandler(req =>
        {
            if (overrides(req) is { } o) return o;
            var path = req.RequestUri!.AbsolutePath;
            if (path == "/auth/v1/token") return (HttpStatusCode.OK, TokenJson());
            if (path == "/rest/v1/profiles")
                return (HttpStatusCode.OK, ProfJson("ada"));
            return (HttpStatusCode.NotFound, "[]");
        });
        var client = NewClient(handler, secrets);
        await client.TryRestoreAsync();
        return (client, secrets);
    }

    // ── test doubles ────────────────────────────────────────────────────────────────────────────────

    private sealed class StubHandler(Func<HttpRequestMessage, (HttpStatusCode, string)> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var (code, json) = responder(request);
            return Task.FromResult(new HttpResponseMessage(code)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class InMemorySecretStore : ISecretStore
    {
        private readonly Dictionary<string, string> _map = new();
        public void Set(string key, string value) => _map[key] = value;
        public string? Get(string key) => _map.TryGetValue(key, out var v) ? v : null;
        public void Delete(string key) => _map.Remove(key);
    }

    private sealed class NoopUrlOpener : IUrlOpener
    {
        public void Open(string url) { }
        public void OpenInNewWindow(string url) { }
    }
}
