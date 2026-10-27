using Perch.Data;
using Xunit;

namespace Perch.Tests;

/// <summary>Covers <see cref="ExpectedAccounts"/>: matching a config dir's live org against the account it's expected
/// to be signed into, and the warning text the pickers, Settings row and overlay chip show.</summary>
public class ExpectedAccountTests
{
    private static ClaudeConfigDir Dir(string root) => new(root, realRoot: root);

    private static ExpectedAccount Acme(string? email = null) =>
        new() { Path = @"C:\Users\me\.claude-work", OrgUuid = "acme-uuid", OrgName = "Acme", Email = email };

    private static Org Org(string uuid, string name, string? email = null) => new(uuid) { Name = name, AccountEmail = email };

    [Fact]
    public void NoExpectation_NeverDrifts() =>
        Assert.Equal(AccountDrift.None, ExpectedAccounts.Evaluate(null, null));

    [Fact]
    public void SameOrg_NoEmailAsserted_IsFine() =>
        Assert.Equal(AccountDrift.None, ExpectedAccounts.Evaluate(Acme(), Org("acme-uuid", "Acme", "anyone@acme.com")));

    [Fact]
    public void SignedOutOrPersonal_IsNoOrg() =>
        Assert.Equal(AccountDrift.NoOrg, ExpectedAccounts.Evaluate(Acme(), null));

    [Fact]
    public void AnotherOrg_IsWrongOrg()
    {
        var live = Org("contoso-uuid", "Contoso");
        Assert.Equal(AccountDrift.WrongOrg, ExpectedAccounts.Evaluate(Acme(), live));
        Assert.Equal("Expected Acme — signed in to Contoso", ExpectedAccounts.Describe(AccountDrift.WrongOrg, Acme(), live));
    }

    [Fact]
    public void OrgRenamed_StillMatches_ByUuid() =>
        Assert.Equal(AccountDrift.None, ExpectedAccounts.Evaluate(Acme(), Org("acme-uuid", "Acme Holdings")));

    [Fact]
    public void AssertedEmail_MatchesCaseInsensitively() =>
        Assert.Equal(AccountDrift.None, ExpectedAccounts.Evaluate(Acme("Me@Acme.com"), Org("acme-uuid", "Acme", "me@acme.com ")));

    [Fact]
    public void AssertedEmail_AnotherSeatInTheOrg_IsWrongEmail()
    {
        var live = Org("acme-uuid", "Acme", "bot@acme.com");
        Assert.Equal(AccountDrift.WrongEmail, ExpectedAccounts.Evaluate(Acme("me@acme.com"), live));
        Assert.Equal("Expected me@acme.com — signed in as bot@acme.com",
            ExpectedAccounts.Describe(AccountDrift.WrongEmail, Acme("me@acme.com"), live));
    }

    [Fact]
    public void AssertedEmail_UnknownLiveEmail_DoesNotAlarm() =>
        Assert.Equal(AccountDrift.None, ExpectedAccounts.Evaluate(Acme("me@acme.com"), Org("acme-uuid", "Acme")));

    [Fact]
    public void For_MatchesTheDirByRealPath_AndIgnoresBlankOrgs()
    {
        var list = new[] { new ExpectedAccount { Path = @"C:\Users\me\.claude-work\", OrgUuid = "" }, Acme() };
        Assert.Same(list[1], ExpectedAccounts.For(Dir(@"C:\Users\me\.claude-work"), list));
        Assert.Null(ExpectedAccounts.For(Dir(@"C:\Users\me\.claude"), list));
    }

    [Fact]
    public void WarningFor_JudgesAPickerChoiceOnItsReadOrg()
    {
        var list = new[] { Acme() };
        var drifted = new AccountChoice(Dir(@"C:\Users\me\.claude-work"), Org("contoso-uuid", "Contoso"));
        var fine = new AccountChoice(Dir(@"C:\Users\me\.claude-work"), Org("acme-uuid", "Acme"));
        var unexpected = new AccountChoice(Dir(@"C:\Users\me\.claude"), null);

        Assert.Equal("Expected Acme — signed in to Contoso", ExpectedAccounts.WarningFor(drifted, list));
        Assert.Null(ExpectedAccounts.WarningFor(fine, list));
        Assert.Null(ExpectedAccounts.WarningFor(unexpected, list));
        Assert.Equal("⚠ Acme | work", ExpectedAccounts.Flag("Acme | work", "x"));
        Assert.Equal("Acme", ExpectedAccounts.Flag("Acme", null));
    }

    [Fact]
    public void Label_ShowsTheOrg_AndTheEmailWhenAsserted()
    {
        Assert.Equal("Acme", ExpectedAccounts.Label(Acme()));
        Assert.Equal("Acme · me@acme.com", ExpectedAccounts.Label(Acme("me@acme.com")));
    }
}
