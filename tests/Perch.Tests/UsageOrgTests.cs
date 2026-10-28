using Perch.Data;
using Xunit;

namespace Perch.Tests;

/// <summary>
/// Covers the per-org usage pieces: the in-use dir selection, the condensed scoped labels, the token
/// extraction the per-dir monitor uses, and the <see cref="OrgUsage"/> header fallback.
/// </summary>
public class UsageOrgTests
{
    private static ClaudeConfigDir Dir(string root) => new(root);

    [Fact]
    public void InUse_AlwaysLeadsWithPrimary_AndDedupes()
    {
        var primary = Dir(@"C:\Users\me\.claude");
        var a = Dir(@"C:\envs\a\.claude");
        var b = Dir(@"C:\envs\b\.claude");

        // Sessions across a, b, a-again, a null, and the primary itself — result is primary first, then a, b.
        var result = UsageDirSelection.InUse(new ClaudeConfigDir?[] { a, b, a, null, primary }, primary);

        Assert.Equal(new[] { primary, a, b }, result);
    }

    [Fact]
    public void InUse_PrimaryShownEvenWithNoSessions()
    {
        var primary = Dir(@"C:\Users\me\.claude");
        Assert.Equal(new[] { primary }, UsageDirSelection.InUse(System.Array.Empty<ClaudeConfigDir?>(), primary));
    }

    [Fact]
    public void InUse_DedupesLinkAliases_ByRealRoot()
    {
        var primary = Dir(@"C:\Users\me\.claude");
        var alias1 = new ClaudeConfigDir(@"C:\envs\alpha\.claude", realRoot: @"C:\shared\store");
        var alias2 = new ClaudeConfigDir(@"C:\envs\beta\.claude", realRoot: @"C:\shared\store");

        var result = UsageDirSelection.InUse(new ClaudeConfigDir?[] { alias1, alias2 }, primary);

        Assert.Equal(2, result.Count); // primary + one entry for the shared real root
    }

    [Fact]
    public void DedupeAccounts_CollapsesSameAccount_KeepsFirst()
    {
        var primary = Dir(@"C:\Users\me\.claude");
        var envA1 = Dir(@"C:\envs\a1\.claude");
        var envA2 = Dir(@"C:\envs\a2\.claude"); // different dir, SAME account/org as primary
        var envB = Dir(@"C:\envs\b\.claude");

        var acme = new Org("org-acme") { Name = "Acme", AccountEmail = "me@example.com" };
        var beta = new Org("org-beta") { Name = "Beta", AccountEmail = "me@example.com" };

        var result = UsageDirSelection.DedupeAccounts(new (ClaudeConfigDir, Org?)[]
        {
            (primary, acme),   // kept
            (envA1, acme),     // same account+org → dropped
            (envB, beta),      // different org → kept
            (envA2, acme),     // same again → dropped
        });

        Assert.Equal(new[] { primary, envB }, result.Select(r => r.Dir));
    }

    [Fact]
    public void DedupeAccounts_NeverCollapsesNullOrgs()
    {
        var a = Dir(@"C:\envs\a\.claude");
        var b = Dir(@"C:\envs\b\.claude"); // both signed out — can't prove same account, keep both

        var result = UsageDirSelection.DedupeAccounts(new (ClaudeConfigDir, Org?)[] { (a, null), (b, null) });

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void DedupeAccounts_SameOrgDifferentAccounts_StayDistinct()
    {
        var a = Dir(@"C:\envs\a\.claude");
        var b = Dir(@"C:\envs\b\.claude");
        var acct1 = new Org("org-x") { AccountEmail = "one@example.com" };
        var acct2 = new Org("org-x") { AccountEmail = "two@example.com" }; // same org, different login

        var result = UsageDirSelection.DedupeAccounts(new (ClaudeConfigDir, Org?)[] { (a, acct1), (b, acct2) });

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void Resolve_TagsActiveDirs_AndAppendsIdleKnownDirs()
    {
        var primary = Dir(@"C:\Users\me\.claude");
        var work = Dir(@"C:\envs\work\.claude");   // active (a live session)
        var idle = Dir(@"C:\envs\idle\.claude");   // known but not in use

        var active = new[] { primary, work };
        var known = new[] { primary, work, idle };

        var result = UsageDirSelection.Resolve(active, known,
            d => new Org("u-" + d.RealRoot) { AccountEmail = d.RealRoot });

        // Active dirs lead (in order), then the idle-but-known one; activity is tagged.
        Assert.Equal(new[] { primary, work, idle }, result.Select(r => r.Dir));
        Assert.Equal(new[] { true, true, false }, result.Select(r => r.Active));
    }

    [Fact]
    public void Resolve_SameAccountActiveAndIdle_ShownOnceAsActive()
    {
        var primary = Dir(@"C:\Users\me\.claude");
        var idleDup = Dir(@"C:\envs\dup\.claude");   // idle dir signed into the SAME account as primary

        var acme = new Org("org-acme") { AccountEmail = "me@example.com" };

        var result = UsageDirSelection.Resolve(
            active: new[] { primary },
            allKnown: new[] { primary, idleDup },
            resolveOrg: _ => acme);

        // The account collapses to one entry, and the active one wins (idle duplicate dropped).
        var only = Assert.Single(result);
        Assert.Equal(primary, only.Dir);
        Assert.True(only.Active);
    }

    [Fact]
    public void Resolve_NullOrgIdleDirs_AreNotCollapsed()
    {
        var primary = Dir(@"C:\Users\me\.claude");
        var idleA = Dir(@"C:\envs\a\.claude");   // signed out
        var idleB = Dir(@"C:\envs\b\.claude");   // signed out

        var result = UsageDirSelection.Resolve(
            active: new[] { primary },
            allKnown: new[] { primary, idleA, idleB },
            resolveOrg: d => d.Equals(primary) ? new Org("p") { AccountEmail = "p@x" } : null);

        Assert.Equal(new[] { primary, idleA, idleB }, result.Select(r => r.Dir));
        Assert.Equal(new[] { true, false, false }, result.Select(r => r.Active));
    }

    [Fact]
    public void Resolve_RecordsTheDirsFoldedIntoEachAccount()
    {
        var primary = Dir(@"C:\Users\me\.claude");
        var work = Dir(@"C:\envs\work\.claude");     // a second account
        var dupA = Dir(@"C:\envs\dup-a\.claude");    // same account as primary
        var dupB = Dir(@"C:\envs\dup-b\.claude");    // same account as primary
        var me = new Org("org-acme") { AccountEmail = "me@example.com" };
        var other = new Org("org-acme") { AccountEmail = "other@example.com" };

        var result = UsageDirSelection.Resolve(
            active: new[] { primary },
            allKnown: new[] { primary, dupA, work, dupB },
            resolveOrg: d => d.Equals(work) ? other : me);

        Assert.Equal(new[] { primary, work }, result.Select(r => r.Dir));
        Assert.Equal(new[] { dupA, dupB }, result[0].Merged);
        Assert.Empty(result[1].Merged);
    }

    // A test dir signed into the same account as the primary shares its bars, so the refresher's state for it must
    // show there, and the button must log in the dir that actually needs it.
    [Fact]
    public void LoginNeed_ComesFromAMergedDirWhenTheOwnerIsFine()
    {
        var primary = Dir(@"C:\Users\me\.claude");
        var test = Dir(@"C:\envs\test-dir");
        var set = new OrgUsage(primary, null, UsageInfo.Empty) { Merged = [test] };
        var now = new DateTime(2026, 10, 28, 12, 0, 0, DateTimeKind.Utc);

        Assert.Null(set.LoginNeed(new Dictionary<string, string>(), now));

        var need = set.LoginNeed(new Dictionary<string, string> { [test.RealRoot] = "Rejected." }, now)!.Value;
        Assert.Equal(test, need.Dir);
        Assert.Equal($"{test.Label}: Rejected.", need.Reason);

        // The owner's own need leads, and keeps its reason unprefixed (the heading already names it).
        var both = set.LoginNeed(new Dictionary<string, string> { [primary.RealRoot] = "Own.", [test.RealRoot] = "Rejected." }, now)!.Value;
        Assert.Equal(new LoginNeed(primary, "Own."), both);
    }

    [Fact]
    public void LoginNeed_OwnersLapsedLoginClockStillCounts()
    {
        var now = new DateTime(2026, 10, 28, 12, 0, 0, DateTimeKind.Utc);
        var primary = Dir(@"C:\Users\me\.claude");
        var usage = UsageInfo.Empty with { Auth = new AuthExpiry(null, now.AddMinutes(-1)) };
        var set = new OrgUsage(primary, null, usage) { Merged = [Dir(@"C:\envs\test-dir")] };

        Assert.Equal(primary, set.LoginNeed(new Dictionary<string, string>(), now)?.Dir);
    }

    [Fact]
    public void Activity_CoversMergedDirs_AndRenewingOutranksRenewed()
    {
        var primary = Dir(@"C:\Users\me\.claude");
        var a = Dir(@"C:\envs\a");
        var b = Dir(@"C:\envs\b");
        var set = new OrgUsage(primary, null, UsageInfo.Empty) { Merged = [a, b] };

        Assert.Null(set.Activity(new Dictionary<string, SignInActivity>()));
        Assert.Equal(SignInActivity.Renewed, set.Activity(new Dictionary<string, SignInActivity> { [b.RealRoot] = SignInActivity.Renewed }));
        Assert.Equal(SignInActivity.Renewing, set.Activity(new Dictionary<string, SignInActivity>
        {
            [primary.RealRoot] = SignInActivity.Renewed,
            [b.RealRoot] = SignInActivity.Renewing,
        }));
    }

    [Theory]
    [InlineData("Fable", "F")]
    [InlineData("Claude Fable 5.1", "F")]
    [InlineData("Opus", "O")]
    [InlineData("Sonnet", "S")]
    [InlineData("Haiku", "H")]
    [InlineData("Zephyr", "Z")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void ShortLabel_MapsFamiliesThenFirstLetter(string? input, string expected)
    {
        Assert.Equal(expected, UsageLabels.Short(input));
    }

    [Fact]
    public void TokenFromJson_ReadsAccessToken()
    {
        Assert.Equal("sk-ant-oat-xyz",
            UsageMonitor.TokenFromJson("""{ "claudeAiOauth": { "accessToken": "sk-ant-oat-xyz" } }"""));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("{ not json ")]
    [InlineData("""{ "somethingElse": true }""")]
    public void TokenFromJson_NullOnMissingOrBadInput(string? json)
    {
        Assert.Null(UsageMonitor.TokenFromJson(json));
    }

    [Fact]
    public void OrgUsage_Header_PrefersOrgName_ThenDirLabel()
    {
        var dir = new ClaudeConfigDir(@"C:\envs\acme\.claude", slug: "acme");

        Assert.Equal("Acme Corp",
            new OrgUsage(dir, new Org("u1") { Name = "Acme Corp" }, UsageInfo.Empty).Header);
        Assert.Equal("acme",
            new OrgUsage(dir, null, UsageInfo.Empty).Header); // no org → the dir's own label
    }
}
