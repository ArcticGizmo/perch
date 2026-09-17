namespace Perch.Data;

/// <summary>The outcome of reconciling a session's working directory + live account against the
/// <see cref="AccountRule"/> set (Layer 2, M2).</summary>
internal enum AccountVerdict
{
    /// <summary>No rule governs this directory, or the matched rule allows any account — nothing to say.</summary>
    NoRule,
    /// <summary>A rule governs this directory and the session's live account is one it allows.</summary>
    Ok,
    /// <summary>A rule governs this directory and the session is signed into an account it does <b>not</b>
    /// allow — the footgun the outline shouts about.</summary>
    Mismatch,
}

/// <summary>
/// Reconciles a session against the account guardrails: finds the most specific <see cref="AccountRule"/> for
/// a working directory and decides whether the session's <b>live</b> account is one the rule permits.
/// </summary>
/// <remarks>
/// Pure and side-effect free so it can be unit-tested without files. Alerting-only semantics: a directory
/// that is signed out / unreadable (a <c>null</c> live org) is <b>not</b> flagged — a mismatch is only ever
/// raised when the session is demonstrably signed into a <i>different, real</i> account than the rule allows,
/// which is the exact "you /login'd into the wrong org" case and avoids crying wolf on a transient read or a
/// not-yet-signed-in dir.
/// </remarks>
internal static class AccountGuard
{
    /// <summary>The most specific rule governing <paramref name="cwd"/> (the longest matching
    /// <see cref="AccountRule.Path"/>), or <c>null</c> when none applies.</summary>
    public static AccountRule? RuleFor(string? cwd, IReadOnlyList<AccountRule>? rules)
    {
        if (string.IsNullOrWhiteSpace(cwd) || rules is null || rules.Count == 0) return null;
        var target = Normalize(cwd);
        if (target is null) return null;

        AccountRule? best = null;
        int bestLen = -1;
        foreach (var rule in rules)
        {
            var basePath = Normalize(rule.Path);
            if (basePath is null || !IsAtOrUnder(target, basePath)) continue;
            if (basePath.Length > bestLen) { best = rule; bestLen = basePath.Length; }
        }
        return best;
    }

    /// <summary>Reconciles a matched <paramref name="rule"/> against the session's live account
    /// <paramref name="liveOrgUuid"/> (the <c>organizationUuid</c> the session's config dir is signed into
    /// right now, or <c>null</c> if signed out / unknown).</summary>
    public static AccountVerdict Evaluate(AccountRule? rule, string? liveOrgUuid)
    {
        if (rule is null || rule.Allowed.Count == 0) return AccountVerdict.NoRule;
        if (string.IsNullOrWhiteSpace(liveOrgUuid)) return AccountVerdict.NoRule; // alerting-only: never flag a blank sign-in
        foreach (var a in rule.Allowed)
            if (string.Equals(a.Uuid, liveOrgUuid, StringComparison.Ordinal))
                return AccountVerdict.Ok;
        return AccountVerdict.Mismatch;
    }

    /// <summary>Convenience: match then evaluate in one call.</summary>
    public static AccountVerdict Evaluate(string? cwd, string? liveOrgUuid, IReadOnlyList<AccountRule>? rules) =>
        Evaluate(RuleFor(cwd, rules), liveOrgUuid);

    /// <summary>True when <paramref name="target"/> is <paramref name="basePath"/> itself or a descendant of
    /// it, compared on whole path segments so <c>C:\work\acme</c> never matches <c>C:\work\acme-two</c>.</summary>
    private static bool IsAtOrUnder(string target, string basePath)
    {
        if (basePath.Length == 0) return false;
        if (ClaudeConfigDir.PathComparer.Equals(target, basePath)) return true;
        if (target.Length <= basePath.Length) return false;
        if (!target.StartsWith(basePath, PathComparison)) return false;
        char boundary = target[basePath.Length];
        return boundary == System.IO.Path.DirectorySeparatorChar
            || boundary == System.IO.Path.AltDirectorySeparatorChar;
    }

    /// <summary>Resolves a path to a normalised absolute form (trailing separator trimmed) for comparison, or
    /// <c>null</c> when it can't be made sense of. Best-effort — never throws.</summary>
    private static string? Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            return System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(path.Trim()));
        }
        catch
        {
            return null;
        }
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
}
