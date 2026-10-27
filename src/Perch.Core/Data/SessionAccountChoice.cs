namespace Perch.Data;

/// <summary>One choice the session launcher can offer: a config dir paired with the org it is <b>currently</b>
/// signed into (<c>null</c> when signed out / personal), and the signed-in email when there is no org. Launching
/// under this option injects <see cref="ClaudeConfigDir.Root"/> as <c>CLAUDE_CONFIG_DIR</c> — see
/// <see cref="Control.ClaudeSessionController.Start"/>.</summary>
internal sealed record AccountChoice(ClaudeConfigDir Dir, Org? Org, string? Email = null)
{
    /// <summary>The choice's identity: its config dir. Two dirs signed into one account are still two choices, since
    /// each carries its own settings, MCP servers and history.</summary>
    public string Key => "d:" + Dir.RealRoot;

    /// <summary>The signed-in account's identity: the org, else the signed-in email (a personal, org-less account),
    /// else the dir itself (signed out — nothing better to key on).</summary>
    public string AccountKey =>
        Org is { } o && !string.IsNullOrWhiteSpace(o.Uuid) ? "o:" + o.Uuid
        : !string.IsNullOrWhiteSpace(Email) ? "e:" + Email!.Trim().ToLowerInvariant()
        : Key;

    /// <summary>The account's name: the org name, else the signed-in email, else the dir label.</summary>
    public string Label =>
        Org is { } o ? o.DisplayName
        : !string.IsNullOrWhiteSpace(Email) ? Email!.Trim()
        : string.IsNullOrWhiteSpace(Dir.DisplayLabel) ? Dir.Label : Dir.DisplayLabel;

    /// <summary>The config dir's own name: the user's label, else the derived one.</summary>
    public string DirName => string.IsNullOrWhiteSpace(Dir.CustomLabel) ? Dir.Label : Dir.CustomLabel!.Trim();

    /// <summary>What to show for this choice among <paramref name="all"/> the config dirs: the account alone when no
    /// other dir is signed into it, else <c>Account | dir</c> so the dirs sharing it can be told apart — just the account
    /// when the dir is already named after it.</summary>
    public string LabelAmong(IReadOnlyList<AccountChoice> all)
    {
        var account = Label;
        if (!all.Any(o => o.Key != Key && o.AccountKey == AccountKey)) return account;
        var dir = DirName;
        return string.Equals(dir.Trim(), account.Trim(), StringComparison.OrdinalIgnoreCase) ? account : $"{account} | {dir}";
    }
}

/// <summary>The set of config-dir choices to present when starting a Perch-controlled session in a given directory,
/// reconciled against the <see cref="AccountRule"/> guardrails (org-discovery Layer 2 / config-dir Layer 3). The
/// launcher renders <see cref="Options"/> and pre-selects <see cref="Default"/> (shown with a "(default)" marker).</summary>
internal sealed record AccountChoiceSet(
    IReadOnlyList<AccountChoice> Options,
    AccountChoice? Default,
    AccountChoice? Primary,
    bool Restricted,
    bool GuardrailUnsatisfiable)
{
    /// <summary>Every config dir's sign-in, guardrail or not — what <see cref="LabelOf"/> disambiguates against, so a dir
    /// reads the same in every picker whichever of its siblings a guardrail filtered out.</summary>
    public IReadOnlyList<AccountChoice> All { get; init; } = [];

    public string LabelOf(AccountChoice choice) => choice.LabelAmong(All);

    /// <summary>Whether the launcher should surface a selector at all. Hidden when there is nothing to choose:
    /// no guardrail and at most one account. A guardrail always surfaces something (the restricted list, or the
    /// unsatisfiable warning).</summary>
    public bool ShowSelector => Restricted || Options.Count > 1;

    /// <summary>The <c>CLAUDE_CONFIG_DIR</c> to inject for <paramref name="choice"/>, or <c>null</c> to inherit
    /// Perch's environment. The <see cref="Primary"/> always inherits, guardrail or not: the ambient env already
    /// resolves to it, and pinning the default <c>~/.claude</c> explicitly would move Claude's <c>.claude.json</c>
    /// lookup off the legacy <c>~/.claude.json</c> onto a second, divergent copy inside the dir (the same reason
    /// <see cref="TranscriptLocator.ResumeConfigRoot"/> never pins the primary). Every other account is pinned.</summary>
    public string? InjectRootFor(AccountChoice? choice)
    {
        if (choice is null) return null;
        if (Primary is { } p && ClaudeConfigDir.PathComparer.Equals(choice.Dir.RealRoot, p.Dir.RealRoot))
            return null;
        return choice.Dir.Root;
    }
}

/// <summary>Pure resolver that turns the discovered config dirs + their live sign-ins + the account guardrails
/// into the choices a session launcher should show for a working directory — one per config dir. Side-effect free
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

        // One choice per config dir, primary-first (so dirs[0] is the dir the ambient environment already resolves to).
        // Dirs sharing an account stay separate choices; LabelOf tells them apart.
        var dirs = new List<AccountChoice>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var c in signIns)
            if (seen.Add(c.Key)) dirs.Add(c);

        var primary = dirs.Count > 0 ? dirs[0] : null;
        var rule = AccountGuard.RuleFor(cwd, rules);

        // No governing guardrail (or a rule that asserts nothing): offer every dir, defaulting to the primary.
        // ShowSelector hides it when there is only one.
        if (rule is null || rule.Allowed.Count == 0)
            return new AccountChoiceSet(dirs, Default: primary, Primary: primary,
                Restricted: false, GuardrailUnsatisfiable: false) { All = dirs };

        // A guardrail governs this directory: restrict to accounts whose org it allows.
        var allowed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var a in rule.Allowed)
            if (!string.IsNullOrWhiteSpace(a.Uuid)) allowed.Add(a.Uuid);

        var matches = new List<AccountChoice>();
        foreach (var choice in dirs)
            if (choice.Org is { } org && !string.IsNullOrWhiteSpace(org.Uuid) && allowed.Contains(org.Uuid))
                matches.Add(choice);

        // No signed-in account satisfies the guardrail. Guardrails are alerting-only, so we don't block the
        // launch: fall back to the primary (inherit) and let the caller warn.
        if (matches.Count == 0)
            return new AccountChoiceSet(Array.Empty<AccountChoice>(), Default: primary, Primary: primary,
                Restricted: true, GuardrailUnsatisfiable: true) { All = dirs };

        // One or more allowed dirs are available. Default to the first (primary-first ordering, so the
        // primary wins when it qualifies); exactly one → that one.
        return new AccountChoiceSet(matches, Default: matches[0], Primary: primary,
            Restricted: true, GuardrailUnsatisfiable: false) { All = dirs };
    }
}

/// <summary>The config dirs an existing session can be resumed (or restarted) under, and the one it uses now.</summary>
/// <param name="Options">Config dirs that can read the session's transcript, filtered by the folder's guardrail. Empty
/// when nothing qualifies.</param>
/// <param name="Current">The dir the session runs, or would resume, under; null when unknown.</param>
internal sealed record ResumeAccountSet(IReadOnlyList<AccountChoice> Options, AccountChoice? Current)
{
    public static ResumeAccountSet None { get; } = new(Array.Empty<AccountChoice>(), null);

    /// <summary>Every config dir's sign-in, as for <see cref="AccountChoiceSet.All"/>.</summary>
    public IReadOnlyList<AccountChoice> All { get; init; } = [];

    /// <summary>Whether there's another config dir to move the session to.</summary>
    public bool CanSwitch => Options.Any(o => !IsCurrent(o));

    public bool IsCurrent(AccountChoice choice) => Current is { } c && c.Key == choice.Key;

    public string LabelOf(AccountChoice choice) => choice.LabelAmong(All);
}

/// <summary>Pure resolver for switching an existing session's account: the session's resumable config dirs
/// (<see cref="TranscriptLocator.ResumableDirs"/>) × their live sign-ins × the guardrails. Only dirs that can read the
/// transcript qualify — a <c>claude --resume</c> anywhere else finds no session.</summary>
internal static class ResumeAccountChoice
{
    /// <param name="cwd">The session's working directory (selects the guardrail).</param>
    /// <param name="resumable">The dirs that can read the transcript, primary first.</param>
    /// <param name="current">The dir the session runs (or would resume) under.</param>
    /// <param name="signIns">Every config dir's live sign-in (<c>SessionWindow.ReadSignIns</c>).</param>
    /// <param name="rules">The account guardrails.</param>
    public static ResumeAccountSet Resolve(
        string? cwd,
        IReadOnlyList<ClaudeConfigDir> resumable,
        ClaudeConfigDir? current,
        IReadOnlyList<AccountChoice> signIns,
        IReadOnlyList<AccountRule>? rules)
    {
        var eligible = signIns.Where(s => resumable.Contains(s.Dir)).ToList();
        if (eligible.Count == 0) return ResumeAccountSet.None;
        var now = current is null ? null : signIns.FirstOrDefault(s => s.Dir.Equals(current));
        return new ResumeAccountSet(SessionAccountChoice.Resolve(cwd, eligible, rules).Options, now) { All = signIns };
    }
}
