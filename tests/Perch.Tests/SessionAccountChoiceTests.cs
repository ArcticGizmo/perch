using Perch.Data;
using Xunit;

namespace Perch.Tests;

/// <summary>
/// Covers <see cref="SessionAccountChoice.Resolve"/>: the pure reconciliation of discovered config dirs + their
/// live sign-ins + the account guardrails into the (de-duplicated) choices a Perch-controlled session launcher
/// presents, plus the inherit-vs-inject decision (<see cref="AccountChoiceSet.InjectRootFor"/>). No IO —
/// sign-ins are passed in, so these run on any host.
/// </summary>
public class SessionAccountChoiceTests
{
    private static ClaudeConfigDir Dir(string root) => new(root, realRoot: root);

    private static AccountChoice SignedIn(string root, string orgUuid, string? name = null, string? email = null) =>
        new(Dir(root), new Org(orgUuid) { Name = name, AccountEmail = email }, email);

    private static AccountChoice Personal(string root, string email) => new(Dir(root), Org: null, Email: email);

    private static AccountChoice SignedOut(string root) => new(Dir(root), Org: null);

    private static AccountRule Rule(string path, params string[] uuids) => new()
    {
        Path = path,
        Allowed = uuids.Select(u => new AccountRef { Uuid = u }).ToList(),
    };

    // Guardrails normalise paths via Path.GetFullPath, so the rule-driven cases run on Windows.
    private static bool Win => OperatingSystem.IsWindows();

    [Fact]
    public void NoRule_SingleDir_HidesSelector_AndInherits()
    {
        var acme = SignedIn(@"C:\Users\me\.claude", "acme");
        var set = SessionAccountChoice.Resolve(@"C:\anywhere", new[] { acme }, rules: null);

        Assert.False(set.ShowSelector);
        Assert.Equal(acme, set.Default);
        Assert.Equal(acme, set.Primary);
        Assert.False(set.Restricted);
        Assert.Null(set.InjectRootFor(set.Default));   // primary + no guardrail → inherit (don't inject)
    }

    [Fact]
    public void NoRule_MultipleDirs_OffersAll_DefaultsToPrimary_WhichInherits()
    {
        var acme = SignedIn(@"C:\Users\me\.claude", "acme");            // primary-first
        var contoso = SignedIn(@"C:\Users\me\.claude-contoso", "contoso");

        var set = SessionAccountChoice.Resolve(@"C:\anywhere", new[] { acme, contoso }, rules: null);

        Assert.True(set.ShowSelector);
        Assert.Equal(2, set.Options.Count);
        Assert.Equal(acme, set.Default);                    // the primary is the default
        Assert.Null(set.InjectRootFor(set.Default));        // …and selecting it inherits
        Assert.Equal(contoso.Dir.Root, set.InjectRootFor(contoso));   // a non-primary account is pinned
        Assert.False(set.Restricted);
    }

    [Fact]
    public void DeduplicatesAccounts_SameOrgAcrossTwoDirs_CollapsesToOne()
    {
        var primary = SignedIn(@"C:\Users\me\.claude", "acme-uuid");
        var alias = SignedIn(@"C:\Users\me\.claude-work", "acme-uuid");   // same org, different dir

        var set = SessionAccountChoice.Resolve(@"C:\anywhere", new[] { primary, alias }, rules: null);

        var only = Assert.Single(set.Options);
        Assert.Equal(primary.Dir, only.Dir);       // representative = primary-first
        Assert.False(set.ShowSelector);            // only one distinct account → nothing to choose
    }

    [Fact]
    public void DeduplicatesPersonalAccounts_ByEmail()
    {
        var a = Personal(@"C:\Users\me\.claude", "me@example.com");
        var b = Personal(@"C:\Users\me\.claude-copy", "ME@example.com");   // same email, different case

        var set = SessionAccountChoice.Resolve(@"C:\anywhere", new[] { a, b }, rules: null);

        var only = Assert.Single(set.Options);
        Assert.Equal("me@example.com", only.Label);   // personal account shows its email
    }

    [Fact]
    public void Guardrail_SingleAllowed_DefaultsToThatOne_AndPins()
    {
        if (!Win) return;
        var acme = SignedIn(@"C:\Users\me\.claude", "acme-uuid");
        var contoso = SignedIn(@"C:\Users\me\.claude-contoso", "contoso-uuid");
        var rules = new[] { Rule(@"C:\work\acme", "acme-uuid") };

        var set = SessionAccountChoice.Resolve(@"C:\work\acme\repo", new[] { contoso, acme }, rules);

        Assert.True(set.Restricted);
        var only = Assert.Single(set.Options);
        Assert.Equal("acme-uuid", only.Org!.Uuid);
        Assert.Equal(acme, set.Default);
        Assert.Equal(acme.Dir.Root, set.InjectRootFor(set.Default));   // under a guardrail, a non-primary is pinned
        Assert.False(set.GuardrailUnsatisfiable);
    }

    [Fact]
    public void Guardrail_PrimaryAllowed_StillInherits()
    {
        if (!Win) return;
        // Pinning the default ~/.claude explicitly moves Claude's .claude.json lookup off the legacy ~/.claude.json
        // onto a divergent copy inside the dir, so the primary must inherit even when a guardrail picks it.
        var acme = SignedIn(@"C:\Users\me\.claude", "acme-uuid");        // primary
        var contoso = SignedIn(@"C:\Users\me\.claude-contoso", "contoso-uuid");
        var rules = new[] { Rule(@"C:\work\acme", "acme-uuid") };

        var set = SessionAccountChoice.Resolve(@"C:\work\acme\repo", new[] { acme, contoso }, rules);

        Assert.True(set.Restricted);
        Assert.Equal(acme, set.Default);
        Assert.Null(set.InjectRootFor(set.Default));
    }

    [Fact]
    public void Guardrail_MultipleAllowed_RestrictsToThem_DefaultsToFirst()
    {
        if (!Win) return;
        var acme = SignedIn(@"C:\Users\me\.claude", "acme-uuid");        // primary-first ordering
        var contoso = SignedIn(@"C:\Users\me\.claude-contoso", "contoso-uuid");
        var other = SignedIn(@"C:\Users\me\.claude-other", "other-uuid");
        var rules = new[] { Rule(@"C:\work\shared", "acme-uuid", "contoso-uuid") };

        var set = SessionAccountChoice.Resolve(@"C:\work\shared\repo", new[] { acme, contoso, other }, rules);

        Assert.True(set.Restricted);
        Assert.Equal(2, set.Options.Count);
        Assert.DoesNotContain(set.Options, o => o.Org!.Uuid == "other-uuid");
        Assert.Equal(acme, set.Default);   // primary-first, so acme wins
    }

    [Fact]
    public void Guardrail_NoSignedInDirQualifies_FallsBackToPrimary_WithWarning()
    {
        if (!Win) return;
        var contoso = SignedIn(@"C:\Users\me\.claude-contoso", "contoso-uuid");
        var rules = new[] { Rule(@"C:\work\acme", "acme-uuid") };   // acme isn't signed in anywhere

        var set = SessionAccountChoice.Resolve(@"C:\work\acme\repo", new[] { contoso }, rules);

        Assert.True(set.Restricted);
        Assert.True(set.GuardrailUnsatisfiable);
        Assert.Empty(set.Options);
        Assert.Equal(contoso, set.Default);   // the primary (the only dir) — alerting-only, never blocks
        Assert.Null(set.InjectRootFor(set.Default));   // …and falling back to it inherits, never pins
        Assert.True(set.ShowSelector);         // still surfaces (to carry the warning)
    }

    [Fact]
    public void Guardrail_IgnoresSignedOutDirs()
    {
        if (!Win) return;
        var acme = SignedIn(@"C:\Users\me\.claude", "acme-uuid");
        var loggedOut = SignedOut(@"C:\Users\me\.claude-empty");
        var rules = new[] { Rule(@"C:\work\acme", "acme-uuid") };

        var set = SessionAccountChoice.Resolve(@"C:\work\acme\repo", new[] { acme, loggedOut }, rules);

        var only = Assert.Single(set.Options);
        Assert.Equal(acme.Dir, only.Dir);
    }

    [Fact]
    public void RuleWithNoAllowedAccounts_IsInert_TreatedAsNoRule()
    {
        if (!Win) return;
        var a = SignedIn(@"C:\Users\me\.claude", "acme");
        var b = SignedIn(@"C:\Users\me\.claude-contoso", "contoso");
        var rules = new[] { Rule(@"C:\work\acme") };   // rule present but asserts nothing

        var set = SessionAccountChoice.Resolve(@"C:\work\acme\repo", new[] { a, b }, rules);

        Assert.False(set.Restricted);
        Assert.Equal(2, set.Options.Count);   // behaves like "no governing rule"
        Assert.Null(set.InjectRootFor(set.Default));   // default primary still inherits
    }

    // ── ResumeAccountChoice: switching an existing session's account ──────────────────────────────────────────────

    [Fact]
    public void Resume_OffersOnlyDirsThatCanReadTheTranscript()
    {
        var acme = SignedIn(@"C:\Users\me\.claude", "acme");
        var contoso = SignedIn(@"C:\Users\me\.claude-contoso", "contoso");
        var other = SignedIn(@"C:\Users\me\.claude-other", "other");   // its own projects/

        var set = ResumeAccountChoice.Resolve(@"C:\anywhere", new[] { acme.Dir, contoso.Dir }, current: acme.Dir,
            new[] { acme, contoso, other }, rules: null);

        Assert.Equal(new[] { acme, contoso }, set.Options);
        Assert.Equal(acme, set.Current);
        Assert.True(set.IsCurrent(acme));
        Assert.True(set.CanSwitch);
    }

    [Fact]
    public void Resume_TwoDirsOnTheSameAccount_HaveNothingToSwitchTo()
    {
        var a = SignedIn(@"C:\Users\me\.claude", "acme");
        var b = SignedIn(@"C:\Users\me\.claude-work", "acme");

        var set = ResumeAccountChoice.Resolve(@"C:\anywhere", new[] { a.Dir, b.Dir }, current: b.Dir,
            new[] { a, b }, rules: null);

        Assert.Single(set.Options);
        Assert.True(set.IsCurrent(set.Options[0]));   // same account, matched by key not dir
        Assert.False(set.CanSwitch);
    }

    [Fact]
    public void Resume_UnshareableTranscript_HasNothingToSwitchTo()
    {
        var acme = SignedIn(@"C:\Users\me\.claude", "acme");
        var contoso = SignedIn(@"C:\Users\me\.claude-contoso", "contoso");

        Assert.False(ResumeAccountChoice.Resolve(@"C:\anywhere", new[] { contoso.Dir }, contoso.Dir,
            new[] { acme, contoso }, rules: null).CanSwitch);
        Assert.Same(ResumeAccountSet.None, ResumeAccountChoice.Resolve(@"C:\anywhere", [], null, new[] { acme }, null));
    }

    [Fact]
    public void Resume_GuardrailRestrictsTheSwitch()
    {
        if (!Win) return;
        var acme = SignedIn(@"C:\Users\me\.claude", "acme-uuid");
        var contoso = SignedIn(@"C:\Users\me\.claude-contoso", "contoso-uuid");
        var other = SignedIn(@"C:\Users\me\.claude-other", "other-uuid");
        var rules = new[] { Rule(@"C:\work\acme", "acme-uuid", "contoso-uuid") };

        var set = ResumeAccountChoice.Resolve(@"C:\work\acme\repo", new[] { acme.Dir, contoso.Dir, other.Dir },
            current: other.Dir, new[] { acme, contoso, other }, rules);

        Assert.Equal(new[] { acme, contoso }, set.Options);   // `other` can read it but isn't allowed here
        Assert.Equal(other, set.Current);
        Assert.True(set.CanSwitch);                           // …so moving off it is offered
    }
}
