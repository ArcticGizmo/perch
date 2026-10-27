namespace Perch.Data;

/// <summary>
/// The account a config dir is <b>meant</b> to be signed into (org-discovery M3, the dir→org declaration), persisted in
/// <see cref="AppSettings.ExpectedAccounts"/>. When the dir's live sign-in drifts from it — logged out, another org, or
/// another seat in the same org — Perch flags the dir, since a login has probably broken or been swapped. Alerting only.
/// </summary>
public sealed class ExpectedAccount
{
    /// <summary>The config dir's resolved real path (its <see cref="ClaudeConfigDir.RealRoot"/>).</summary>
    public string Path { get; set; } = "";

    /// <summary>The org's <c>organizationUuid</c> — always asserted.</summary>
    public string OrgUuid { get; set; } = "";

    /// <summary>The org name captured when it was chosen, so the warning can name it while signed out. Display only.</summary>
    public string? OrgName { get; set; }

    /// <summary>Optionally, the account email that must be signed in too. Null/blank = any login in the org.</summary>
    public string? Email { get; set; }
}

/// <summary>How a config dir's live sign-in compares with its <see cref="ExpectedAccount"/>.</summary>
internal enum AccountDrift
{
    /// <summary>No expectation, or the live sign-in satisfies it.</summary>
    None,
    /// <summary>Not signed in to any org (logged out, or a personal login).</summary>
    NoOrg,
    /// <summary>Signed in to a different org.</summary>
    WrongOrg,
    /// <summary>The right org, but a different account email than expected.</summary>
    WrongEmail,
}

/// <summary>Pure checks of a config dir's live org against its <see cref="ExpectedAccount"/>. No IO — the caller reads
/// the live org (<see cref="IOrgProvider"/> / <see cref="ClaudeJsonReader"/>).</summary>
internal static class ExpectedAccounts
{
    /// <summary>The expectation declared for <paramref name="dir"/>, if any (matched on its real path).</summary>
    public static ExpectedAccount? For(ClaudeConfigDir dir, IReadOnlyList<ExpectedAccount>? list) =>
        list?.FirstOrDefault(e => !string.IsNullOrWhiteSpace(e.OrgUuid) && ClaudeConfigDir.SameRoot(e.Path, dir.RealRoot));

    /// <summary>Compares <paramref name="live"/> with <paramref name="expected"/>. An expected email is only checked when
    /// the live sign-in reports one, so an unreadable email never raises a false alarm.</summary>
    public static AccountDrift Evaluate(ExpectedAccount? expected, Org? live)
    {
        if (expected is null || string.IsNullOrWhiteSpace(expected.OrgUuid)) return AccountDrift.None;
        if (live is null || string.IsNullOrWhiteSpace(live.Uuid)) return AccountDrift.NoOrg;
        if (!string.Equals(live.Uuid, expected.OrgUuid, StringComparison.Ordinal)) return AccountDrift.WrongOrg;
        if (!string.IsNullOrWhiteSpace(expected.Email) && !string.IsNullOrWhiteSpace(live.AccountEmail)
            && !string.Equals(expected.Email.Trim(), live.AccountEmail.Trim(), StringComparison.OrdinalIgnoreCase))
            return AccountDrift.WrongEmail;
        return AccountDrift.None;
    }

    /// <summary>The one-line warning for a drift, e.g. <c>Expected Acme — signed in to Contoso</c>; empty for
    /// <see cref="AccountDrift.None"/>.</summary>
    public static string Describe(AccountDrift drift, ExpectedAccount expected, Org? live) => drift switch
    {
        AccountDrift.NoOrg => $"Expected {OrgLabel(expected)} — not signed in to it",
        AccountDrift.WrongOrg => $"Expected {OrgLabel(expected)} — signed in to {live?.DisplayName}",
        AccountDrift.WrongEmail => $"Expected {expected.Email!.Trim()} — signed in as {live?.AccountEmail?.Trim()}",
        _ => "",
    };

    /// <summary>The drift warning for a picker choice, judged on the org it was read with; null when it matches or
    /// has no expectation.</summary>
    public static string? WarningFor(AccountChoice choice, IReadOnlyList<ExpectedAccount>? list)
    {
        if (For(choice.Dir, list) is not { } expected) return null;
        var drift = Evaluate(expected, choice.Org);
        return drift == AccountDrift.None ? null : Describe(drift, expected, choice.Org);
    }

    /// <summary><paramref name="label"/> with a leading ⚠ when <paramref name="warning"/> is set.</summary>
    public static string Flag(string label, string? warning) => warning is null ? label : "⚠ " + label;

    /// <summary>The expectation for display: <c>Acme</c>, or <c>Acme · me@acme.com</c> when an email is asserted.</summary>
    public static string Label(ExpectedAccount expected) =>
        string.IsNullOrWhiteSpace(expected.Email) ? OrgLabel(expected) : $"{OrgLabel(expected)} · {expected.Email.Trim()}";

    private static string OrgLabel(ExpectedAccount e) => string.IsNullOrWhiteSpace(e.OrgName) ? e.OrgUuid : e.OrgName.Trim();
}
