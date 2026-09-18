namespace Perch.Data;

/// <summary>One account the session launcher can offer: a config dir paired with the org it is <b>currently</b>
/// signed into (<c>null</c> when signed out / personal), and the signed-in email when there is no org. Launching
/// under this option injects <see cref="ClaudeConfigDir.Root"/> as <c>CLAUDE_CONFIG_DIR</c> — see
/// <see cref="Control.ClaudeSessionController.Start"/>.</summary>
internal sealed record AccountChoice(ClaudeConfigDir Dir, Org? Org, string? Email = null)
{
    /// <summary>Stable identity for de-duplication: the org (the same org signed into two dirs is <i>one</i>
    /// account), else the signed-in email (a personal, org-less account), else the dir itself (signed out —
    /// nothing better to key on).</summary>
    public string Key =>
        Org is { } o && !string.IsNullOrWhiteSpace(o.Uuid) ? "o:" + o.Uuid
        : !string.IsNullOrWhiteSpace(Email) ? "e:" + Email!.Trim().ToLowerInvariant()
        : "d:" + Dir.RealRoot;

    /// <summary>What to show for this account: the org name, else the signed-in email, else the dir label.</summary>
    public string Label =>
        Org is { } o ? o.DisplayName
        : !string.IsNullOrWhiteSpace(Email) ? Email!.Trim()
        : string.IsNullOrWhiteSpace(Dir.DisplayLabel) ? Dir.Label : Dir.DisplayLabel;
}

/// <summary>The set of account choices to present when starting a Perch-controlled session in a given directory,
/// already de-duplicated and reconciled against the <see cref="AccountRule"/> guardrails (org-discovery Layer 2 /
/// config-dir Layer 3). The launcher renders <see cref="Options"/> and pre-selects <see cref="Default"/> (shown
/// with a "(default)" marker).</summary>
internal sealed record AccountChoiceSet(
    IReadOnlyList<AccountChoice> Options,
    AccountChoice? Default,
    AccountChoice? Primary,
    bool Restricted,
    bool GuardrailUnsatisfiable)
{
    /// <summary>Whether the launcher should surface a selector at all. Hidden when there is nothing to choose:
    /// no guardrail and at most one account. A guardrail always surfaces something (the restricted list, or the
    /// unsatisfiable warning).</summary>
    public bool ShowSelector => Restricted || Options.Count > 1;

    /// <summary>The <c>CLAUDE_CONFIG_DIR</c> to inject for <paramref name="choice"/>, or <c>null</c> to inherit
    /// Perch's environment. Inherit only when the account is the <see cref="Primary"/> and no guardrail governs
    /// the folder — that preserves the historical no-injection default (the ambient env already resolves to the
    /// primary); everything else is pinned explicitly.</summary>
    public string? InjectRootFor(AccountChoice? choice)
    {
        if (choice is null) return null;
        if (!Restricted && Primary is { } p && ClaudeConfigDir.PathComparer.Equals(choice.Dir.RealRoot, p.Dir.RealRoot))
            return null;
        return choice.Dir.Root;
    }
}

/// <summary>Pure resolver that turns the discovered config dirs + their live sign-ins + the account guardrails
/// into the (de-duplicated) choices a session launcher should show for a working directory. Side-effect free
/// (all IO — reading each dir's sign-in — happens in the caller) so it is unit-testable without files.</summary>
internal static class SessionAccountChoice
{
    /// <param name="cwd">The session's working directory (drives which guardrail, if any, applies).</param>
    /// <param name="signIns">Every discovered config dir with the org/email it is currently signed into, primary
    /// first. The caller reads these (e.g. via <c>ClaudeJsonReader.ReadSignIn</c>) off the UI thread.</param>
    /// <param name="rules">The account guardrails (<see cref="AppSettings.AccountRules"/>).</param>
    public static AccountChoiceSet Resolve(
        string? cwd,
        IReadOnlyList<AccountChoice> signIns,
        IReadOnlyList<AccountRule>? rules)
    {
        signIns ??= Array.Empty<AccountChoice>();

        // De-duplicate to distinct accounts, preserving primary-first order (so deduped[0] is the account the
        // ambient environment already resolves to).
        var deduped = new List<AccountChoice>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var c in signIns)
            if (seen.Add(c.Key)) deduped.Add(c);

        var primary = deduped.Count > 0 ? deduped[0] : null;
        var rule = AccountGuard.RuleFor(cwd, rules);

        // No governing guardrail (or a rule that asserts nothing): offer every account, defaulting to the primary.
        // ShowSelector hides it when there is only one.
        if (rule is null || rule.Allowed.Count == 0)
            return new AccountChoiceSet(deduped, Default: primary, Primary: primary,
                Restricted: false, GuardrailUnsatisfiable: false);

        // A guardrail governs this directory: restrict to accounts whose org it allows.
        var allowed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var a in rule.Allowed)
            if (!string.IsNullOrWhiteSpace(a.Uuid)) allowed.Add(a.Uuid);

        var matches = new List<AccountChoice>();
        foreach (var choice in deduped)
            if (choice.Org is { } org && !string.IsNullOrWhiteSpace(org.Uuid) && allowed.Contains(org.Uuid))
                matches.Add(choice);

        // No signed-in account satisfies the guardrail. Guardrails are alerting-only, so we don't block the
        // launch: fall back to the primary (inherit) and let the caller warn.
        if (matches.Count == 0)
            return new AccountChoiceSet(Array.Empty<AccountChoice>(), Default: primary, Primary: primary,
                Restricted: true, GuardrailUnsatisfiable: true);

        // One or more allowed accounts are available. Default to the first (primary-first ordering, so the
        // primary wins when it qualifies); exactly one → that one.
        return new AccountChoiceSet(matches, Default: matches[0], Primary: primary,
            Restricted: true, GuardrailUnsatisfiable: false);
    }
}
