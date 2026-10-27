using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Perch.Data;
using Perch.Platform;
using Xunit;

namespace Perch.Tests;

/// <summary>
/// Covers the opt-in sign-in refresher: when a sign-in is due (5 minutes out), the token exchange it sends, how the
/// reply is merged back into the blob, Claude Code's refresh lock, and that a rejected refresh token turns into "login
/// required" without ever being retried or logging in. Fixtures are synthetic — never a real blob — and the token
/// endpoint is a fake handler, so nothing touches the network.
/// </summary>
public sealed class SignInRefresherTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 10, 27, 9, 0, 0, DateTimeKind.Utc);
    private readonly string _root = FeedTestSupport.TempDir();

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static long Ms(DateTime utc) => new DateTimeOffset(utc).ToUnixTimeMilliseconds();

    private static string Blob(DateTime accessEnds, string refresh = "rt-old", DateTime? loginEnds = null,
        string scopes = """["user:inference","user:profile"]""") =>
        $$"""
        {"claudeAiOauth":{"accessToken":"at-old","refreshToken":"{{refresh}}","expiresAt":{{Ms(accessEnds)}},
          {{(loginEnds is { } l ? $"\"refreshTokenExpiresAt\":{Ms(l)}," : "")}}
          "scopes":{{scopes}},"subscriptionType":"max"},
         "mcpOAuth":{"server":{"accessToken":"mcp-fixture"} } }
        """;

    private sealed class FakeCredentials(string? json) : IClaudeCredentials
    {
        public string? Json = json;
        public bool FailWrites;
        public int Writes;
        public string? ReadCredentialsJson() => Json;

        public bool WriteCredentialsJson(string json)
        {
            if (FailWrites) return false;
            Writes++;
            Json = json;
            return true;
        }
    }

    private SignInStore Store(FakeCredentials creds) => new(new ClaudeConfigDir(_root), creds);

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static RouteHandler TokenEndpoint(Func<HttpRequestMessage, HttpResponseMessage> responder) =>
        new RouteHandler().On(SignInRefresher.TokenUrl, responder);

    private static RouteHandler Grants(string accessToken = "at-new", string? refreshToken = "rt-new", int expiresIn = 28800,
        string? extra = null) =>
        TokenEndpoint(_ => Json(HttpStatusCode.OK,
            $$"""{"access_token":"{{accessToken}}",{{(refreshToken is null ? "" : $"\"refresh_token\":\"{refreshToken}\",")}}"expires_in":{{expiresIn}}{{extra}}}"""));

    private static SignInRefresher.OAuthBlob? Parse(string? json) => SignInRefresher.OAuthBlob.Parse(json);

    // ── PlanFor ──────────────────────────────────────────────────────────────

    [Fact]
    public void Plan_WaitsUntilFiveMinutesBeforeExpiry()
    {
        Assert.Equal(SignInRefresher.Plan.NotDue,
            SignInRefresher.PlanFor(Parse(Blob(Now.AddMinutes(6))), Now, _ => false));
        Assert.Equal(SignInRefresher.Plan.Due,
            SignInRefresher.PlanFor(Parse(Blob(Now.AddMinutes(5))), Now, _ => false));
        Assert.Equal(SignInRefresher.Plan.Due,
            SignInRefresher.PlanFor(Parse(Blob(Now.AddHours(-3))), Now, _ => false));
    }

    [Fact]
    public void Plan_ExpiredLogin_NeedsALogin_EvenWhenTheAccessTokenIsFine()
    {
        var blob = Parse(Blob(Now.AddHours(4), loginEnds: Now.AddMinutes(-1)));
        Assert.Equal(SignInRefresher.Plan.LoginExpired, SignInRefresher.PlanFor(blob, Now, _ => false));
    }

    [Fact]
    public void Plan_NoRefreshToken_IsOnlyALoginProblemOnceTheAccessTokenLapses()
    {
        string Without(DateTime ends) => $$"""{"claudeAiOauth":{"accessToken":"a","expiresAt":{{Ms(ends)}} } }""";
        Assert.Equal(SignInRefresher.Plan.NotDue, SignInRefresher.PlanFor(Parse(Without(Now.AddMinutes(2))), Now, _ => false));
        Assert.Equal(SignInRefresher.Plan.NoRefreshToken, SignInRefresher.PlanFor(Parse(Without(Now.AddMinutes(-1))), Now, _ => false));
    }

    [Fact]
    public void Plan_NoBlob_OrApiKeySetup_IsNotSignedIn()
    {
        Assert.Equal(SignInRefresher.Plan.NotSignedIn, SignInRefresher.PlanFor(Parse(null), Now, _ => false));
        Assert.Equal(SignInRefresher.Plan.NotSignedIn, SignInRefresher.PlanFor(Parse("""{"primaryApiKey":"x"}"""), Now, _ => false));
        Assert.Equal(SignInRefresher.Plan.NotSignedIn, SignInRefresher.PlanFor(Parse("not json"), Now, _ => false));
    }

    // ── ApplyRefresh ─────────────────────────────────────────────────────────

    [Fact]
    public void ApplyRefresh_RotatesTheTokens_AndKeepsEverythingElse()
    {
        var reply = (JsonObject)JsonNode.Parse(
            """{"access_token":"at-new","refresh_token":"rt-new","expires_in":28800,"refresh_token_expires_in":2592000,"scope":"user:inference user:profile user:sessions"}""")!;

        var updated = JsonNode.Parse(SignInRefresher.ApplyRefresh(Blob(Now.AddMinutes(2)), reply, Now)!)!;
        var o = updated["claudeAiOauth"]!;

        Assert.Equal("at-new", o["accessToken"]!.ToString());
        Assert.Equal("rt-new", o["refreshToken"]!.ToString());
        Assert.Equal(Ms(Now.AddHours(8)), (long)o["expiresAt"]!);
        Assert.Equal(Ms(Now.AddDays(30)), (long)o["refreshTokenExpiresAt"]!);
        Assert.Equal(new[] { "user:inference", "user:profile", "user:sessions" }, o["scopes"]!.AsArray().Select(n => n!.ToString()));
        Assert.Equal("max", o["subscriptionType"]!.ToString());
        Assert.Equal("mcp-fixture", updated["mcpOAuth"]!["server"]!["accessToken"]!.ToString());
    }

    [Fact]
    public void ApplyRefresh_KeepsTheRefreshTokenAndLoginClock_WhenTheReplyDoesNotRotateThem()
    {
        var login = Now.AddDays(12);
        var reply = (JsonObject)JsonNode.Parse("""{"access_token":"at-new","expires_in":3600}""")!;

        var o = JsonNode.Parse(SignInRefresher.ApplyRefresh(Blob(Now, loginEnds: login), reply, Now)!)!["claudeAiOauth"]!;

        Assert.Equal("rt-old", o["refreshToken"]!.ToString());
        Assert.Equal(Ms(login), (long)o["refreshTokenExpiresAt"]!);
        Assert.Equal(2, o["scopes"]!.AsArray().Count);
    }

    [Fact]
    public void ApplyRefresh_RefusesAReplyWithoutAnAccessTokenOrLifetime()
    {
        Assert.Null(SignInRefresher.ApplyRefresh(Blob(Now), (JsonObject)JsonNode.Parse("""{"expires_in":3600}""")!, Now));
        Assert.Null(SignInRefresher.ApplyRefresh(Blob(Now), (JsonObject)JsonNode.Parse("""{"access_token":"a"}""")!, Now));
    }

    // ── RefreshIfDueAsync ────────────────────────────────────────────────────

    [Fact]
    public async Task Due_ExchangesTheRefreshToken_LikeClaudeCode_AndSavesTheReply()
    {
        string? sent = null;
        var handler = TokenEndpoint(req =>
        {
            sent = req.Content!.ReadAsStringAsync().Result;
            return Json(HttpStatusCode.OK, """{"access_token":"at-new","refresh_token":"rt-new","expires_in":28800}""");
        });
        var creds = new FakeCredentials(Blob(Now.AddMinutes(4)));

        var r = await new SignInRefresher(handler).RefreshIfDueAsync(Store(creds), Now);

        Assert.Equal(RefreshOutcome.Refreshed, r.Outcome);
        var body = JsonNode.Parse(sent!)!;
        Assert.Equal("refresh_token", body["grant_type"]!.ToString());
        Assert.Equal("rt-old", body["refresh_token"]!.ToString());
        Assert.Equal(SignInRefresher.DefaultClientId, body["client_id"]!.ToString());
        Assert.Equal("user:inference user:profile", body["scope"]!.ToString());
        Assert.Equal(1, creds.Writes);
        Assert.Equal("rt-new", Parse(creds.Json)!.RefreshToken);
        Assert.False(Directory.Exists(Path.Combine(_root, OAuthRefreshLock.Name)));   // released
    }

    [Fact]
    public async Task NotDue_SendsNothing()
    {
        var handler = Grants();
        var creds = new FakeCredentials(Blob(Now.AddHours(2)));

        var r = await new SignInRefresher(handler).RefreshIfDueAsync(Store(creds), Now);

        Assert.Equal(RefreshOutcome.NotDue, r.Outcome);
        Assert.Empty(handler.Requests);
        Assert.Equal(0, creds.Writes);
    }

    [Fact]
    public async Task ExpiredLogin_IsLoginRequired_WithoutTryingTheServer()
    {
        var handler = Grants();
        var creds = new FakeCredentials(Blob(Now.AddMinutes(1), loginEnds: Now.AddHours(-1)));

        var r = await new SignInRefresher(handler).RefreshIfDueAsync(Store(creds), Now);

        Assert.Equal(RefreshOutcome.LoginRequired, r.Outcome);
        Assert.Contains("login expired", r.Reason);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task RejectedRefreshToken_IsLoginRequired_AndNeverRetried_UntilTheUserLogsInAgain()
    {
        var handler = TokenEndpoint(_ => Json(HttpStatusCode.BadRequest,
            """{"error":"invalid_grant","error_description":"Refresh token not found or invalid"}"""));
        var creds = new FakeCredentials(Blob(Now.AddMinutes(3)));
        var refresher = new SignInRefresher(handler);

        var first = await refresher.RefreshIfDueAsync(Store(creds), Now);
        var second = await refresher.RefreshIfDueAsync(Store(creds), Now.AddMinutes(1));

        Assert.Equal(RefreshOutcome.LoginRequired, first.Outcome);
        Assert.Contains("invalid_grant", first.Reason);
        Assert.Equal(RefreshOutcome.LoginRequired, second.Outcome);
        Assert.Equal(first.Reason, second.Reason);
        Assert.Single(handler.Requests);
        Assert.Equal(0, creds.Writes);

        // `claude auth login` writes a new sign-in: it's healthy again.
        creds.Json = Blob(Now.AddHours(8), refresh: "rt-from-login");
        Assert.Equal(RefreshOutcome.NotDue, (await refresher.RefreshIfDueAsync(Store(creds), Now.AddMinutes(2))).Outcome);
    }

    [Fact]
    public async Task Rejection_AfterAnotherProcessRotatedTheToken_IsNotALoginProblem()
    {
        var creds = new FakeCredentials(Blob(Now.AddMinutes(3)));
        var handler = TokenEndpoint(_ =>
        {
            // Claude Code renewed (and saved) while our request was in flight, spending the token we sent.
            creds.Json = Blob(Now.AddHours(8), refresh: "rt-claude");
            return Json(HttpStatusCode.BadRequest, """{"error":"invalid_grant"}""");
        });

        var r = await new SignInRefresher(handler).RefreshIfDueAsync(Store(creds), Now);

        Assert.Equal(RefreshOutcome.NotDue, r.Outcome);
        Assert.Equal("rt-claude", Parse(creds.Json)!.RefreshToken);
    }

    [Fact]
    public async Task OtherRefusals_AndServerErrors_AreRetried_NotTreatedAsADeadLogin()
    {
        int calls = 0;
        var handler = TokenEndpoint(_ => ++calls == 1
            ? Json(HttpStatusCode.BadRequest, """{"error":"invalid_scope"}""")
            : Json(HttpStatusCode.ServiceUnavailable, "{}"));
        var creds = new FakeCredentials(Blob(Now.AddMinutes(3)));
        var refresher = new SignInRefresher(handler);

        Assert.Equal(RefreshOutcome.Failed, (await refresher.RefreshIfDueAsync(Store(creds), Now)).Outcome);
        Assert.Equal(RefreshOutcome.Failed, (await refresher.RefreshIfDueAsync(Store(creds), Now)).Outcome);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task ClaudeCodeHoldingTheLock_DefersThePass()
    {
        var handler = Grants();
        Directory.CreateDirectory(Path.Combine(_root, OAuthRefreshLock.Name));

        var r = await new SignInRefresher(handler).RefreshIfDueAsync(Store(new FakeCredentials(Blob(Now.AddMinutes(1)))), Now);

        Assert.Equal(RefreshOutcome.Busy, r.Outcome);
        Assert.Empty(handler.Requests);
        Assert.True(Directory.Exists(Path.Combine(_root, OAuthRefreshLock.Name)));   // theirs, left alone
    }

    [Fact]
    public async Task AStaleLock_IsTakenOver()
    {
        var lockDir = Path.Combine(_root, OAuthRefreshLock.Name);
        Directory.CreateDirectory(lockDir);
        Directory.SetLastWriteTimeUtc(lockDir, DateTime.UtcNow.AddMinutes(-5));

        var r = await new SignInRefresher(Grants()).RefreshIfDueAsync(Store(new FakeCredentials(Blob(Now.AddMinutes(1)))), Now);

        Assert.Equal(RefreshOutcome.Refreshed, r.Outcome);
        Assert.False(Directory.Exists(lockDir));
    }

    [Fact]
    public async Task ARenewalThatCanNotBeSaved_NeedsALogin()
    {
        var creds = new FakeCredentials(Blob(Now.AddMinutes(1))) { FailWrites = true };

        var r = await new SignInRefresher(Grants()).RefreshIfDueAsync(Store(creds), Now);

        Assert.Equal(RefreshOutcome.LoginRequired, r.Outcome);
        Assert.Contains("couldn't save", r.Reason);
    }

    // ── File write-back + the overlay's rule ────────────────────────────────

    [Fact]
    public void WriteFile_ReplacesTheBlob_AndLeavesNoTempBehind()
    {
        var path = Path.Combine(_root, ".credentials.json");
        File.WriteAllText(path, Blob(Now));

        Assert.True(FileClaudeCredentials.WriteFile(path, "{\"claudeAiOauth\":{}}"));

        Assert.Equal("{\"claudeAiOauth\":{}}", new FileClaudeCredentials(path).ReadCredentialsJson());
        Assert.Equal(new[] { path }, Directory.GetFiles(_root));
    }

    [Fact]
    public void LoginRequired_PrefersTheRefresherReason_ThenTheLoginClock()
    {
        var local = new DateTime(2026, 10, 27, 9, 0, 0, DateTimeKind.Local);
        var lapsed = new AuthExpiry(local.AddHours(1), local.AddMinutes(-1));
        var fine = new AuthExpiry(local.AddHours(1), local.AddDays(3));

        Assert.Equal("rejected", AuthExpiryRules.LoginRequired(fine, "rejected", local));
        Assert.Contains("login expired", AuthExpiryRules.LoginRequired(lapsed, null, local));
        Assert.Null(AuthExpiryRules.LoginRequired(fine, null, local));
        Assert.Null(AuthExpiryRules.LoginRequired(AuthExpiry.None, null, local));
    }
}
