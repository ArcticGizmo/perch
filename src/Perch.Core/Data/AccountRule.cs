namespace Perch.Data;

/// <summary>
/// A user-declared <b>account guardrail</b> (org-discovery Layer 2): "anything under this directory must be
/// run on one of these Claude accounts." <see cref="Path"/> is a directory prefix (its resolved real path);
/// any session whose working directory is at or below it is expected to be signed into one of
/// <see cref="Allowed"/>. When several rules match a directory, the <b>most specific</b> (longest) path wins
/// — a rule on <c>C:\work\acme</c> overrides a broader one on <c>C:\work</c>. Perch only <i>alerts</i> on a
/// mismatch (an aggressive outline on the session row); it never blocks the session.
/// </summary>
/// <remarks>
/// The allowed accounts are matched on <see cref="AccountRef.Uuid"/> (the org's <c>organizationUuid</c>), the
/// same stable identity the rest of Layer 2 keys on — names duplicate and can be renamed, so they are display
/// only. Persisted in <see cref="AppSettings.AccountRules"/> and managed by the Config directories settings
/// page. See <c>docs/org-discovery-plan.md</c> (M2).
/// </remarks>
public sealed class AccountRule
{
    /// <summary>The directory this rule governs, as a resolved real path. A session whose working directory is
    /// this path — or any descendant of it — is expected to be on one of <see cref="Allowed"/>.</summary>
    public string Path { get; set; } = "";

    /// <summary>The Claude accounts permitted under <see cref="Path"/>. Empty means the rule asserts nothing
    /// (no alert). Matched on <see cref="AccountRef.Uuid"/>.</summary>
    public List<AccountRef> Allowed { get; set; } = new();
}

/// <summary>A reference to a Claude account (an org sign-in) allowed by an <see cref="AccountRule"/>. Identity
/// is <see cref="Uuid"/> (<c>organizationUuid</c>); <see cref="Name"/> and <see cref="Email"/> are the
/// display values captured when the account was picked, so a rule row still reads sensibly even for an account
/// that isn't currently signed into any config dir.</summary>
public sealed class AccountRef
{
    /// <summary>The org's <c>organizationUuid</c> — the sole identity this rule matches on.</summary>
    public string Uuid { get; set; } = "";

    /// <summary>The org display name captured when the account was chosen. Display only.</summary>
    public string? Name { get; set; }

    /// <summary>The signed-in account email captured when the account was chosen. Display only.</summary>
    public string? Email { get; set; }
}
