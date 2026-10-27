using Perch.Data;
using Xunit;

namespace Perch.Tests;

/// <summary>
/// Covers the opt-in sign-in refresher: when a sign-in is due (5 minutes out), that the claude CLI is only run for a due
/// one, and how the stored sign-in after the run reads — renewed, signed out by a rejected refresh (login required), or
/// unchanged (retry). The CLI run is a fake that rewrites an in-memory blob, so nothing is launched or sent. Fixtures are
/// synthetic — never a real blob.
/// </summary>
public sealed class SignInRefresherTests
{
    private static readonly DateTime Now = new(2026, 10, 27, 9, 0, 0, DateTimeKind.Utc);

    private static long Ms(DateTime utc) => new DateTimeOffset(utc).ToUnixTimeMilliseconds();

    private static string Blob(DateTime accessEnds, string access = "at-old", string refresh = "rt-old", DateTime? loginEnds = null) =>
        $$"""
        {"claudeAiOauth":{"accessToken":"{{access}}","refreshToken":"{{refresh}}","expiresAt":{{Ms(accessEnds)}},
          {{(loginEnds is { } l ? $"\"refreshTokenExpiresAt\":{Ms(l)}," : "")}}
          "scopes":["user:inference"],"subscriptionType":"max"} }
        """;

    // How Claude Code leaves the blob once its refresh is rejected.
    private static readonly string SignedOut =
        """{"claudeAiOauth":{"accessToken":"","refreshToken":"","expiresAt":0,"scopes":["user:inference"],"subscriptionType":"max"}}""";

    private sealed class Fake
    {
        public string? Json;
        public int Runs;
        public SignInStore Store => new(new ClaudeConfigDir(@"C:\fixtures\signin"), false, () => Json);
    }

    // A CLI run that does `effect` to the stored blob (Claude Code renewing, or signing the account out).
    private static SignInRefresher Runs(Fake fake, Func<string?, string?> effect, string? error = null) =>
        new((_, _) =>
        {
            fake.Runs++;
            if (error is null) fake.Json = effect(fake.Json);
            return Task.FromResult(error);
        });

    private static SignInRefresher.Plan Plan(string? json) =>
        SignInRefresher.PlanFor(SignInRefresher.OAuthBlob.Parse(json), Now);

    // ── PlanFor ──────────────────────────────────────────────────────────────

    [Fact]
    public void Plan_WaitsUntilFiveMinutesBeforeExpiry()
    {
        Assert.Equal(SignInRefresher.Plan.NotDue, Plan(Blob(Now.AddMinutes(6))));
        Assert.Equal(SignInRefresher.Plan.Due, Plan(Blob(Now.AddMinutes(5))));
        Assert.Equal(SignInRefresher.Plan.Due, Plan(Blob(Now.AddHours(-3))));
    }

    [Fact]
    public void Plan_ExpiredLogin_NeedsALogin_EvenWhenTheAccessTokenIsFine()
    {
        Assert.Equal(SignInRefresher.Plan.LoginExpired, Plan(Blob(Now.AddHours(4), loginEnds: Now.AddMinutes(-1))));
    }

    [Fact]
    public void Plan_NoRefreshToken_IsOnlyALoginProblemOnceTheAccessTokenLapses()
    {
        string Without(DateTime ends) => $$"""{"claudeAiOauth":{"accessToken":"a","expiresAt":{{Ms(ends)}} } }""";
        Assert.Equal(SignInRefresher.Plan.NotDue, Plan(Without(Now.AddMinutes(2))));
        Assert.Equal(SignInRefresher.Plan.NoRefreshToken, Plan(Without(Now.AddMinutes(-1))));
    }

    [Fact]
    public void Plan_BlankedTokens_AreSignedOut_ButNoBlockIsNotSignedIn()
    {
        Assert.Equal(SignInRefresher.Plan.SignedOut, Plan(SignedOut));
        Assert.Equal(SignInRefresher.Plan.NotSignedIn, Plan(null));
        Assert.Equal(SignInRefresher.Plan.NotSignedIn, Plan("""{"primaryApiKey":"x"}"""));
        Assert.Equal(SignInRefresher.Plan.NotSignedIn, Plan("not json"));
    }

    // ── RefreshIfDueAsync ────────────────────────────────────────────────────

    [Fact]
    public async Task Due_RunsClaude_AndALaterExpiryIsARenewal()
    {
        var fake = new Fake { Json = Blob(Now.AddMinutes(4)) };
        var refresher = Runs(fake, _ => Blob(Now.AddHours(8), access: "at-new", refresh: "rt-new"));

        var r = await refresher.RefreshIfDueAsync(fake.Store, Now);

        Assert.Equal(RefreshOutcome.Refreshed, r.Outcome);
        Assert.Equal(1, fake.Runs);
    }

    [Fact]
    public async Task NotDue_RunsNothing()
    {
        var fake = new Fake { Json = Blob(Now.AddHours(2)) };

        var r = await Runs(fake, j => j).RefreshIfDueAsync(fake.Store, Now);

        Assert.Equal(RefreshOutcome.NotDue, r.Outcome);
        Assert.Equal(0, fake.Runs);
    }

    [Fact]
    public async Task ExpiredLogin_IsLoginRequired_WithoutRunningClaude()
    {
        var fake = new Fake { Json = Blob(Now.AddMinutes(1), loginEnds: Now.AddHours(-1)) };

        var r = await Runs(fake, j => j).RefreshIfDueAsync(fake.Store, Now);

        Assert.Equal(RefreshOutcome.LoginRequired, r.Outcome);
        Assert.Contains("login expired", r.Reason);
        Assert.Equal(0, fake.Runs);
    }

    [Fact]
    public async Task ARejectedRefresh_SignsTheAccountOut_AndStaysLoginRequired_UntilTheUserLogsInAgain()
    {
        var fake = new Fake { Json = Blob(Now.AddMinutes(3)) };
        var refresher = Runs(fake, _ => SignedOut);

        var first = await refresher.RefreshIfDueAsync(fake.Store, Now);
        var second = await refresher.RefreshIfDueAsync(fake.Store, Now.AddMinutes(1));

        Assert.Equal(RefreshOutcome.LoginRequired, first.Outcome);
        Assert.Contains("rejected", first.Reason);
        Assert.Equal(RefreshOutcome.LoginRequired, second.Outcome);
        Assert.Equal(1, fake.Runs);   // a signed-out account isn't run again

        // `claude auth login` writes a new sign-in: it's healthy again.
        fake.Json = Blob(Now.AddHours(8), refresh: "rt-from-login");
        Assert.Equal(RefreshOutcome.NotDue, (await refresher.RefreshIfDueAsync(fake.Store, Now.AddMinutes(2))).Outcome);
    }

    [Fact]
    public async Task AnUnchangedSignIn_IsARetry_NotADeadLogin()
    {
        var fake = new Fake { Json = Blob(Now.AddMinutes(3)) };
        var refresher = Runs(fake, j => j);   // Claude Code couldn't reach the server, say

        var r = await refresher.RefreshIfDueAsync(fake.Store, Now);

        Assert.Equal(RefreshOutcome.Failed, r.Outcome);
        Assert.Equal(RefreshOutcome.Failed, (await refresher.RefreshIfDueAsync(fake.Store, Now)).Outcome);
        Assert.Equal(2, fake.Runs);
    }

    [Fact]
    public async Task AClaudeThatCanNotRun_IsARetry_WithItsReason()
    {
        var fake = new Fake { Json = Blob(Now.AddMinutes(3)) };

        var r = await Runs(fake, j => j, error: "Couldn't run the claude CLI: not on PATH").RefreshIfDueAsync(fake.Store, Now);

        Assert.Equal(RefreshOutcome.Failed, r.Outcome);
        Assert.Contains("not on PATH", r.Reason);
    }

    // ── The overlay's rule ───────────────────────────────────────────────────

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
