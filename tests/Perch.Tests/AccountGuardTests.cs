using Perch.Data;
using Xunit;

namespace Perch.Tests;

/// <summary>
/// Covers the Layer-2 M2 <see cref="AccountGuard"/>: longest-prefix rule matching for a session's working
/// directory, whole-segment boundaries (so <c>…\acme</c> never matches <c>…\acme-two</c>), and the
/// alerting-only reconciliation — a mismatch is raised only when the session is signed into a <i>different,
/// real</i> org than the rule allows, never on a blank/signed-out sign-in.
/// </summary>
public class AccountGuardTests
{
    private static AccountRule Rule(string path, params string[] uuids) => new()
    {
        Path = path,
        Allowed = uuids.Select(u => new AccountRef { Uuid = u }).ToList(),
    };

    // Rules use Windows-style paths; the matcher normalises via Path.GetFullPath, so run these on Windows.
    private static bool Win => OperatingSystem.IsWindows();

    [Fact]
    public void RuleFor_MostSpecificPathWins()
    {
        if (!Win) return;
        var rules = new[] { Rule(@"C:\work", "broad"), Rule(@"C:\work\acme", "deep") };

        var match = AccountGuard.RuleFor(@"C:\work\acme\repo", rules);

        Assert.NotNull(match);
        Assert.Equal("deep", match!.Allowed[0].Uuid);
    }

    [Fact]
    public void RuleFor_MatchesTheDirectoryItself()
    {
        if (!Win) return;
        var rules = new[] { Rule(@"C:\work\acme", "acme") };
        Assert.NotNull(AccountGuard.RuleFor(@"C:\work\acme", rules));
    }

    [Fact]
    public void RuleFor_RespectsSegmentBoundaries()
    {
        if (!Win) return;
        var rules = new[] { Rule(@"C:\work\acme", "acme") };

        // A sibling that merely shares a string prefix must NOT match.
        Assert.Null(AccountGuard.RuleFor(@"C:\work\acme-two\repo", rules));
    }

    [Fact]
    public void RuleFor_NoRuleForUnrelatedDir()
    {
        if (!Win) return;
        var rules = new[] { Rule(@"C:\work\acme", "acme") };
        Assert.Null(AccountGuard.RuleFor(@"C:\personal\thing", rules));
    }

    [Fact]
    public void Evaluate_AllowedOrg_IsOk()
    {
        var rule = Rule(@"C:\work\acme", "acme-uuid", "other-uuid");
        Assert.Equal(AccountVerdict.Ok, AccountGuard.Evaluate(rule, "acme-uuid"));
    }

    [Fact]
    public void Evaluate_ForbiddenOrg_IsMismatch()
    {
        var rule = Rule(@"C:\work\acme", "acme-uuid");
        Assert.Equal(AccountVerdict.Mismatch, AccountGuard.Evaluate(rule, "contoso-uuid"));
    }

    [Fact]
    public void Evaluate_BlankSignIn_NeverAlarms()
    {
        var rule = Rule(@"C:\work\acme", "acme-uuid");
        Assert.Equal(AccountVerdict.NoRule, AccountGuard.Evaluate(rule, null));
        Assert.Equal(AccountVerdict.NoRule, AccountGuard.Evaluate(rule, ""));
    }

    [Fact]
    public void Evaluate_RuleWithNoAllowedAccounts_IsInert()
    {
        var rule = Rule(@"C:\work\acme"); // no allowed accounts chosen
        Assert.Equal(AccountVerdict.NoRule, AccountGuard.Evaluate(rule, "anything"));
    }

    [Fact]
    public void Evaluate_NoGoverningRule_IsNoRule()
    {
        if (!Win) return;
        var rules = new[] { Rule(@"C:\work\acme", "acme-uuid") };
        Assert.Equal(AccountVerdict.NoRule, AccountGuard.Evaluate(@"C:\elsewhere", "contoso-uuid", rules));
    }

    [Fact]
    public void Evaluate_EndToEnd_WrongOrgUnderRuledPath_IsMismatch()
    {
        if (!Win) return;
        var rules = new[] { Rule(@"C:\work\acme", "acme-uuid") };
        Assert.Equal(AccountVerdict.Mismatch,
            AccountGuard.Evaluate(@"C:\work\acme\repo", "contoso-uuid", rules));
    }
}
