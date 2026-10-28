namespace Perch.Data;

/// <summary>
/// Picks which config dirs the per-org usage strip should show: every distinct dir a live session is
/// attributed to, plus the primary dir — which is <b>always</b> shown even with no active session there.
/// Pure so it can be unit-tested away from the live session monitor.
/// </summary>
internal static class UsageDirSelection
{
    /// <summary>
    /// The primary first, then each other distinct config dir drawn from <paramref name="sessionDirs"/>
    /// (a session's <c>AttributedConfigDir</c>, which may be null for an unattributable session). Dedupe is by
    /// <see cref="ClaudeConfigDir"/> identity (its <c>RealRoot</c>), so link aliases collapse to one entry.
    /// </summary>
    public static IReadOnlyList<ClaudeConfigDir> InUse(
        IEnumerable<ClaudeConfigDir?> sessionDirs, ClaudeConfigDir primary)
    {
        var seen = new HashSet<ClaudeConfigDir> { primary };
        var result = new List<ClaudeConfigDir> { primary };
        foreach (var dir in sessionDirs)
        {
            if (dir is not null && seen.Add(dir))
                result.Add(dir);
        }
        return result;
    }

    /// <summary>
    /// Collapses resolved <c>(dir, org)</c> pairs so the same <b>account</b> appears once — several config dirs
    /// can be signed into the same account/org (a junctioned or duplicated setup), and their usage readings are
    /// identical. Keeps the first occurrence (the primary leads, so its set wins). Pairs whose org is null (not
    /// signed in / unreadable) are never collapsed — an unattributable dir keeps its own set.
    /// </summary>
    public static IReadOnlyList<(ClaudeConfigDir Dir, Org? Org)> DedupeAccounts(
        IEnumerable<(ClaudeConfigDir Dir, Org? Org)> resolved)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<(ClaudeConfigDir, Org?)>();
        foreach (var pair in resolved)
        {
            if (pair.Org is { } org && !seen.Add(AccountKey(org)))
                continue;
            result.Add(pair);
        }
        return result;
    }

    /// <summary>
    /// The full set of accounts the usage strip should show — every <paramref name="active"/> dir (a live
    /// session's, plus the primary), followed by every other known-but-idle dir from <paramref name="allKnown"/>
    /// — each resolved to its org and tagged <c>Active</c>. Active dirs lead so that when the same account is
    /// signed into both an active and an idle dir the account is shown once, as active (dedupe keeps the first;
    /// null-org dirs are never collapsed). This is the input the poller turns into <c>OrgUsage</c> rows: the
    /// active ones default to full bars on the overlay, the idle ones to compact chips. <c>Merged</c> lists the dirs
    /// each entry absorbed, in order, so their own sign-in state can still be shown on it.
    /// </summary>
    public static IReadOnlyList<(ClaudeConfigDir Dir, Org? Org, bool Active, IReadOnlyList<ClaudeConfigDir> Merged)> Resolve(
        IReadOnlyList<ClaudeConfigDir> active,
        IReadOnlyList<ClaudeConfigDir> allKnown,
        Func<ClaudeConfigDir, Org?> resolveOrg)
    {
        var activeSet = new HashSet<ClaudeConfigDir>(active);

        // Active dirs first (in order), then the known dirs that aren't active — deduped by dir so an idle
        // entry that's also active doesn't appear twice.
        var ordered = new List<(ClaudeConfigDir Dir, bool Active)>();
        var seenDir = new HashSet<ClaudeConfigDir>();
        foreach (var d in active) if (seenDir.Add(d)) ordered.Add((d, true));
        foreach (var d in allKnown) if (seenDir.Add(d)) ordered.Add((d, activeSet.Contains(d)));

        // Account key → the merged-dir list of the entry that owns it.
        var owners = new Dictionary<string, List<ClaudeConfigDir>>(StringComparer.Ordinal);
        var result = new List<(ClaudeConfigDir, Org?, bool, IReadOnlyList<ClaudeConfigDir>)>();
        foreach (var (dir, isActive) in ordered)
        {
            var org = resolveOrg(dir);
            // Same account + org across dirs → one set (active-first, so the active one wins). A null org
            // (not signed in / unreadable) is never collapsed — each such dir keeps its own set.
            if (org is { } o && owners.TryGetValue(AccountKey(o), out var merged))
            {
                merged.Add(dir);
                continue;
            }
            var mine = new List<ClaudeConfigDir>();
            if (org is { } owned) owners[AccountKey(owned)] = mine;
            result.Add((dir, org, isActive, mine));
        }
        return result;
    }

    /// <summary>Account identity within an org: the signed-in email plus the org uuid. Same account + org in two
    /// dirs → same usage → one set. Different accounts in one org, or one account across orgs, stay distinct.</summary>
    public static string AccountKey(Org org) => $"{org.AccountEmail} {org.Uuid}";
}
