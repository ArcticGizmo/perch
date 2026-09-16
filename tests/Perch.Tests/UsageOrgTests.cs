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
