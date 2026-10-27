using Perch.Data;
using Xunit;

namespace Perch.Tests;

/// <summary>
/// Covers <see cref="SessionAccountChoice.Resolve"/>: the pure reconciliation of discovered config dirs + their
/// live sign-ins + the account guardrails into the per-config-dir choices a Perch-controlled session launcher
/// presents and how each is labelled, plus the inherit-vs-inject decision (<see cref="AccountChoiceSet.InjectRootFor"/>). No IO —
/// sign-ins are passed in, so these run on any host.
/// </summary>
public class SessionAccountChoiceTests
{
    // Roots are written Windows-style but mapped to the host's separator, so a label's folder name (Path.GetFileName)
    // reads the same on macOS, where a backslash isn't a separator.
    private static ClaudeConfigDir Dir(string root, string? customLabel = null)
    {
        var native = OperatingSystem.IsWindows() ? root : root.Replace('\\', '/');
        return new(native, realRoot: native) { CustomLabel = customLabel };
    }

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
    public void SameOrgAcrossTwoDirs_StaysTwoChoices_LabelledByDir()
    {
        var primary = SignedIn(@"C:\Users\me\.claude", "acme-uuid", name: "Acme");
        var alias = SignedIn(@"C:\Users\me\.claude-work", "acme-uuid", name: "Acme");   // same org, different dir

        var set = SessionAccountChoice.Resolve(@"C:\anywhere", new[] { primary, alias }, rules: null);

        Assert.Equal(new[] { primary, alias }, set.Options);
        Assert.True(set.ShowSelector);             // two config dirs → something to choose
        Assert.Equal(primary, set.Default);
        Assert.Equal("Acme | .claude", set.LabelOf(primary));
        Assert.Equal("Acme | .claude-work", set.LabelOf(alias));
    }

    [Fact]
    public void PersonalAccountsSharedByEmail_AreLabelledByDir()
    {
        var a = Personal(@"C:\Users\me\.claude", "me@example.com");
        var b = Personal(@"C:\Users\me\.claude-copy", "ME@example.com");   // same email, different case

        var set = SessionAccountChoice.Resolve(@"C:\anywhere", new[] { a, b }, rules: null);

        Assert.Equal(2, set.Options.Count);
        Assert.Equal("ME@example.com | .claude-copy", set.LabelOf(b));   // matched case-insensitively, shown as signed in
    }

    [Fact]
    public void Label_IsTheAccountAlone_WhenNoOtherDirUsesIt()
    {
        var acme = SignedIn(@"C:\Users\me\.claude", "acme", name: "Acme");
        var contoso = SignedIn(@"C:\Users\me\.claude-contoso", "contoso", name: "Contoso");

        var set = SessionAccountChoice.Resolve(@"C:\anywhere", new[] { acme, contoso }, rules: null);

        Assert.Equal("Acme", set.LabelOf(acme));
        Assert.Equal("Contoso", set.LabelOf(contoso));
    }

    [Fact]
    public void Label_DropsTheDirName_WhenItMatchesTheAccount()
    {
        var acme = SignedIn(@"C:\Users\me\.claude", "acme", name: "Acme");
        var named = new AccountChoice(Dir(@"C:\Users\me\.claude-acme", customLabel: "acme"), new Org("acme") { Name = "Acme" });

        var set = SessionAccountChoice.Resolve(@"C:\anywhere", new[] { acme, named }, rules: null);

        Assert.Equal("Acme", set.LabelOf(named));            // "Acme | acme" would repeat itself
        Assert.Equal("Acme | .claude", set.LabelOf(acme));   // the other dir still says which it is
    }

    [Fact]
    public void Label_PrefersTheDirsCustomLabel()
    {
        var acme = SignedIn(@"C:\Users\me\.claude", "acme", name: "Acme");
        var work = new AccountChoice(Dir(@"C:\Users\me\.claude-w", customLabel: "Work laptop"), new Org("acme") { Name = "Acme" });

        Assert.Equal("Acme | Work laptop", work.LabelAmong(new[] { acme, work }));
    }

    [Fact]
    public void Label_DisambiguatesAgainstEveryDir_NotJustTheGuardrailsPicks()
    {
        if (!Win) return;
        var acme = SignedIn(@"C:\Users\me\.claude", "acme-uuid", name: "Acme");
        var acmeWork = SignedIn(@"C:\Users\me\.claude-work", "acme-uuid", name: "Acme");
        var contoso = SignedIn(@"C:\Users\me\.claude-contoso", "contoso-uuid", name: "Contoso");
        var rules = new[] { Rule(@"C:\work\acme", "acme-uuid") };

        var set = SessionAccountChoice.Resolve(@"C:\work\acme\repo", new[] { contoso, acme, acmeWork }, rules);

        Assert.Equal(new[] { acme, acmeWork }, set.Options);   // both dirs on the allowed org
        Assert.Equal("Acme | .claude-work", set.LabelOf(acmeWork));
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
    public void Resume_TwoDirsOnTheSameAccount_CanSwitchBetweenThem()
    {
        var a = SignedIn(@"C:\Users\me\.claude", "acme", name: "Acme");
        var b = SignedIn(@"C:\Users\me\.claude-work", "acme", name: "Acme");

        var set = ResumeAccountChoice.Resolve(@"C:\anywhere", new[] { a.Dir, b.Dir }, current: b.Dir,
            new[] { a, b }, rules: null);

        Assert.Equal(new[] { a, b }, set.Options);
        Assert.True(set.IsCurrent(b));    // matched by dir, not account
        Assert.False(set.IsCurrent(a));
        Assert.True(set.CanSwitch);
        Assert.Equal("Acme | .claude-work", set.LabelOf(b));
    }

    [Fact]
    public void Resume_LabelsAgainstEveryDir_NotJustTheResumableOnes()
    {
        var a = SignedIn(@"C:\Users\me\.claude", "acme", name: "Acme");
        var b = SignedIn(@"C:\Users\me\.claude-work", "acme", name: "Acme");   // can't read this transcript
        var contoso = SignedIn(@"C:\Users\me\.claude-contoso", "contoso", name: "Contoso");

        var set = ResumeAccountChoice.Resolve(@"C:\anywhere", new[] { a.Dir, contoso.Dir }, current: a.Dir,
            new[] { a, b, contoso }, rules: null);

        Assert.Equal("Acme | .claude", set.LabelOf(a));
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
