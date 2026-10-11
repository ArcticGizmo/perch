using Perch.Data;
using Xunit;

namespace Perch.Tests;

/// <summary>
/// Covers the sign-in clocks: reading <c>expiresAt</c>/<c>refreshTokenExpiresAt</c> out of a credentials blob, the
/// resting/close/lapsed levels, the compact countdown labels the usage strip paints, and the session banner's
/// choice of clock (the process's access token, the account's login). Fixtures are synthetic — never a real blob.
/// </summary>
public class AuthExpiryTests
{
    private static readonly DateTime Now = new(2026, 10, 11, 9, 0, 0, DateTimeKind.Local);

    private static long Ms(DateTime local) => new DateTimeOffset(local).ToUnixTimeMilliseconds();

    [Fact]
    public void FromJson_ReadsBothClocks_AsLocalTimes()
    {
        var access = Now.AddHours(6);
        var login = Now.AddDays(27);
        var json = $$"""
            { "mcpOAuth": { "x": { "expiresAt": 1 } },
              "claudeAiOauth": { "accessToken": "sk-ant-oat-fixture", "refreshToken": "sk-ant-ort-fixture",
                                 "expiresAt": {{Ms(access)}}, "refreshTokenExpiresAt": {{Ms(login)}} } }
            """;

        var auth = AuthExpiry.FromJson(json);

        Assert.Equal(access, auth.AccessExpiresAt);
        Assert.Equal(login, auth.RefreshExpiresAt);
        Assert.True(auth.IsKnown);
    }

    [Fact]
    public void FromJson_OlderBlobWithoutRefreshExpiry_KeepsTheAccessClock()
    {
        var json = $$"""{ "claudeAiOauth": { "accessToken": "t", "expiresAt": {{Ms(Now)}} } }""";

        var auth = AuthExpiry.FromJson(json);

        Assert.Equal(Now, auth.AccessExpiresAt);
        Assert.Null(auth.RefreshExpiresAt);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("""{ "mcpOAuth": {} }""")]
    [InlineData("""{ "claudeAiOauth": { "expiresAt": "soon", "refreshTokenExpiresAt": -5 } }""")]
    public void FromJson_MissingOrGarbage_IsNone(string? json)
    {
        var auth = AuthExpiry.FromJson(json);

        Assert.False(auth.IsKnown);
    }

    [Fact]
    public void Level_RestsThenWarnsThenLapses()
    {
        var soon = TimeSpan.FromMinutes(30);

        Assert.Equal(AuthExpiryLevel.Normal, AuthExpiryRules.Level(Now.AddMinutes(31), Now, soon));
        Assert.Equal(AuthExpiryLevel.Soon, AuthExpiryRules.Level(Now.AddMinutes(29), Now, soon));
        Assert.Equal(AuthExpiryLevel.Expired, AuthExpiryRules.Level(Now, Now, soon));
        Assert.Equal(AuthExpiryLevel.Expired, AuthExpiryRules.Level(Now.AddHours(-1), Now, soon));
    }

    [Fact]
    public void Thresholds_SplitRestingFromClose()
    {
        // Relative to the constants, so the tests hold whatever they're tuned to.
        foreach (var soon in new[] { AuthExpiryRules.AccountAccessSoon, AuthExpiryRules.SessionAccessSoon, AuthExpiryRules.RefreshSoon })
        {
            Assert.Equal(AuthExpiryLevel.Normal, AuthExpiryRules.Level(Now + soon + TimeSpan.FromMinutes(1), Now, soon));
            Assert.Equal(AuthExpiryLevel.Soon, AuthExpiryRules.Level(Now + soon - TimeSpan.FromMinutes(1), Now, soon));
        }
        // A headless session can't refresh, so it warns no later than the account strip does.
        Assert.True(AuthExpiryRules.SessionAccessSoon >= AuthExpiryRules.AccountAccessSoon);
    }

    [Theory]
    [InlineData(-5, "exp")]
    [InlineData(0, "exp")]
    [InlineData(0.4, "1m")]          // under a minute still reads as time left, never "0m"
    [InlineData(45, "45m")]
    [InlineData(59.9, "59m")]        // rounds down — never overstates
    [InlineData(60, "1h")]
    [InlineData(6.5 * 60, "6h")]
    [InlineData(47.9 * 60, "47h")]   // hours right up to two days, so a close login reads precisely
    [InlineData(48 * 60, "2d")]
    [InlineData(27.2 * 24 * 60, "27d")]
    public void Short_PicksTheCoarsestHonestUnit(double minutes, string expected)
    {
        Assert.Equal(expected, AuthExpiryRules.Short(TimeSpan.FromMinutes(minutes)));
    }

    [Fact]
    public void Short_ReusesTheSameStringForTheSameLabel()
    {
        // The overlay paints these every frame; a cached instance means no per-paint allocation.
        Assert.Same(AuthExpiryRules.Short(TimeSpan.FromHours(6.2)), AuthExpiryRules.Short(TimeSpan.FromHours(6.7)));
    }

    [Fact]
    public void Banner_NothingWhileBothClocksRest()
    {
        var auth = new AuthExpiry(Now + AuthExpiryRules.SessionAccessSoon + TimeSpan.FromHours(1),
                                  Now + AuthExpiryRules.RefreshSoon + TimeSpan.FromDays(1));

        var (text, _) = AuthExpiryRules.Banner(auth, auth, running: true, dormant: false, Now);

        Assert.Null(text);
    }

    [Fact]
    public void Banner_UsesTheProcessAccessToken_NotTheAccountsRefreshedOne()
    {
        // A terminal session refreshed the account's token, but this process still holds the one it started with.
        var process = new AuthExpiry(Now.AddMinutes(12), Now.AddDays(20));
        var account = new AuthExpiry(Now.AddHours(8), Now.AddDays(20));

        var (text, level) = AuthExpiryRules.Banner(process, account, running: true, dormant: false, Now);

        Assert.Equal(AuthExpiryLevel.Soon, level);
        Assert.Contains("expires in 12 min", text);
    }

    [Fact]
    public void Banner_LapsedAccessToken_IsAnErrorThatSaysToRestart()
    {
        var process = new AuthExpiry(Now.AddMinutes(-40), Now.AddDays(20));

        var (text, level) = AuthExpiryRules.Banner(process, AuthExpiry.None, running: true, dormant: false, Now);

        Assert.Equal(AuthExpiryLevel.Expired, level);
        Assert.Contains("Resume", text);
    }

    [Fact]
    public void Banner_LoginReadsTheAccountClock_FallingBackToTheProcess()
    {
        var access = Now + AuthExpiryRules.SessionAccessSoon + TimeSpan.FromHours(1);   // resting, so only logins speak
        var process = new AuthExpiry(access, Now + AuthExpiryRules.RefreshSoon - TimeSpan.FromHours(1));    // the login it started with
        var relogged = new AuthExpiry(access, Now + AuthExpiryRules.RefreshSoon + TimeSpan.FromDays(1));    // since renewed by /login

        Assert.Null(AuthExpiryRules.Banner(process, relogged, running: true, dormant: false, Now).Text);

        var (text, level) = AuthExpiryRules.Banner(process, AuthExpiry.None, running: true, dormant: false, Now);
        Assert.Equal(AuthExpiryLevel.Soon, level);
        Assert.Contains("/login", text);
    }

    [Fact]
    public void Banner_DormantSession_WarnsOnlyAboutTheLogin()
    {
        // No process yet: its wake loads the current credentials, so a lapsed process clock is irrelevant.
        var process = new AuthExpiry(Now.AddHours(-3), null);
        var account = new AuthExpiry(Now.AddHours(-1), Now.AddHours(-2));

        var (text, level) = AuthExpiryRules.Banner(process, account, running: false, dormant: true, Now);

        Assert.Equal(AuthExpiryLevel.Expired, level);
        Assert.Contains("before replying", text);
        Assert.DoesNotContain("This session's sign-in", text);
    }

    [Fact]
    public void Banner_EndedSession_ShowsNothing()
    {
        var lapsed = new AuthExpiry(Now.AddHours(-1), Now.AddHours(-1));

        Assert.Null(AuthExpiryRules.Banner(lapsed, lapsed, running: false, dormant: false, Now).Text);
    }

    [Fact]
    public void Banner_BothClose_ListsBoth_AtTheLouderLevel()
    {
        var process = new AuthExpiry(Now.AddMinutes(-1), Now.AddHours(10));

        var (text, level) = AuthExpiryRules.Banner(process, AuthExpiry.None, running: true, dormant: false, Now);

        Assert.Equal(AuthExpiryLevel.Expired, level);
        Assert.Equal(2, text!.Split('\n').Length);
    }
}
