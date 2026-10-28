namespace Perch.Data;

/// <summary>An account's config dir that needs logging in again, with the reason to show.</summary>
internal readonly record struct LoginNeed(ClaudeConfigDir Dir, string Reason);

/// <summary>
/// One config dir's usage reading, tagged with the org that dir is signed into — a single set of bars in
/// the overlay's per-org usage strip. <see cref="Org"/> is null when the dir isn't signed in to an org (or
/// its credential can't be read); the <see cref="Header"/> still names the set from the dir's label.
/// <para><see cref="Active"/> is true when this account is currently in use by a live session; idle-but-known
/// accounts (also surfaced, so the overlay shows the whole estate) are false. The overlay defaults idle
/// accounts to a compact chip and active ones to full bars — see OverlayCanvas.IsUsageCollapsed.</para>
/// </summary>
internal sealed record OrgUsage(ClaudeConfigDir Dir, Org? Org, UsageInfo Usage, bool Active = true)
{
    /// <summary>The other config dirs signed into the same account, folded into this set (see
    /// <see cref="UsageDirSelection.Resolve"/>). Each holds its own sign-in, so the sign-in refresher's state for
    /// them still belongs on these bars.</summary>
    public IReadOnlyList<ClaudeConfigDir> Merged { get; init; } = [];

    /// <summary>The heading shown above this set's bars: the org's display name when known, else the dir's
    /// own label (custom label, slug, or directory name).</summary>
    public string Header =>
        Org?.DisplayName is { Length: > 0 } n ? n
        : Dir.DisplayLabel is { Length: > 0 } l ? l
        : Dir.Label;

    /// <summary>
    /// The dir on this account that needs a login, or null when none does. <see cref="Dir"/> leads, judged from its
    /// own reading and <paramref name="refresherReasons"/> (by <see cref="ClaudeConfigDir.RealRoot"/>); a merged dir
    /// can only be judged by the refresher, since its usage isn't read. A merged dir's reason names it, because the
    /// set's heading names the account, not that dir.
    /// </summary>
    public LoginNeed? LoginNeed(IReadOnlyDictionary<string, string> refresherReasons, DateTime now)
    {
        if (AuthExpiryRules.LoginRequired(Usage.Auth, refresherReasons.GetValueOrDefault(Dir.RealRoot), now) is { } own)
            return new LoginNeed(Dir, own);
        foreach (var dir in Merged)
            if (refresherReasons.GetValueOrDefault(dir.RealRoot) is { } reason)
                return new LoginNeed(dir, $"{dir.Label}: {reason}");
        return null;
    }

    /// <summary>What the sign-in refresher is doing on any of this account's dirs (by
    /// <see cref="ClaudeConfigDir.RealRoot"/>), or null. A renewal in flight outranks a finished one's confirmation.</summary>
    public SignInActivity? Activity(IReadOnlyDictionary<string, SignInActivity> byRoot)
    {
        SignInActivity? found = null;
        foreach (var dir in (IEnumerable<ClaudeConfigDir>)[Dir, .. Merged])
        {
            if (!byRoot.TryGetValue(dir.RealRoot, out var a)) continue;
            if (a == SignInActivity.Renewing) return a;
            found = a;
        }
        return found;
    }
}
