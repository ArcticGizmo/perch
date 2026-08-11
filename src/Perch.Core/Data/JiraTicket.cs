namespace Perch.Data;

using System.Text.RegularExpressions;

/// <summary>
/// A Jira issue deep-linked from a session's branch name — the key (e.g. <c>SFTY-1234</c>) plus the
/// <c>browse</c> URL it resolves to. Pure and offline: derived from the branch name and the configured
/// site by string manipulation alone, so surfacing it never touches the Jira API and needs no credentials.
/// Public because <see cref="ClaudeSession"/> exposes it. See <see cref="JiraLink"/>.
/// </summary>
public readonly record struct JiraTicketInfo(string Key, string Url);

/// <summary>
/// Turns a git branch name into a Jira ticket deep-link, entirely by string manipulation — the whole point
/// is that surfacing the ticket costs nothing (no gh, no REST, no credentials, no cache). A branch like
/// <c>SFTY-1234-add-audit-log</c> yields key <c>SFTY-1234</c> and URL
/// <c>https://{site}.atlassian.net/browse/SFTY-1234</c>. All methods are static and side-effect free, so the
/// resolver is called inline on the scan rather than behind a background service. Internal for unit testing.
/// </summary>
internal static class JiraLink
{
    // The canonical Jira issue-key shape: an uppercase project key (a letter then 1+ letters/digits) + "-" +
    // an issue number. A lookbehind rejects a key glued to the tail of another token (so "xSFTY-1" doesn't
    // match "SFTY-1"), while "feature/SFTY-1234-thing" matches cleanly after the slash.
    private static readonly Regex KeyPattern = new(
        @"(?<![A-Za-z0-9])([A-Z][A-Z0-9]+)-([0-9]+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// The branch leaf from a HEAD ref string (<c>ref: refs/heads/&lt;branch&gt;</c>), or null when the ref
    /// is null/blank or a detached-HEAD commit SHA (no <c>ref:</c> prefix). Feed it the output of
    /// <see cref="PrStatusService.ReadHeadRef"/>.
    /// </summary>
    public static string? BranchFromHeadRef(string? headRef)
    {
        if (string.IsNullOrWhiteSpace(headRef)) return null;
        const string prefix = "ref: refs/heads/";
        var trimmed = headRef.Trim();
        if (!trimmed.StartsWith(prefix, StringComparison.Ordinal)) return null; // detached HEAD (raw SHA)
        var branch = trimmed[prefix.Length..].Trim();
        return branch.Length == 0 ? null : branch;
    }

    /// <summary>
    /// Normalises a user-entered Jira site to its bare sub-domain slug: accepts <c>acme</c>,
    /// <c>acme.atlassian.net</c>, or <c>https://acme.atlassian.net/</c> and returns <c>acme</c>. Null/blank
    /// — or a value that normalises to nothing — returns null, which disables the whole feature.
    /// </summary>
    public static string? NormalizeSubdomain(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var s = raw.Trim();

        // Drop a scheme and anything from the first slash on (path / query fragments a paste might carry).
        int scheme = s.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0) s = s[(scheme + 3)..];
        int slash = s.IndexOf('/');
        if (slash >= 0) s = s[..slash];
        s = s.Trim();

        // Drop a trailing ".atlassian.net" (any case) so "acme" and "acme.atlassian.net" both reduce to
        // "acme" — done before trimming dots so a bare/leading-dot "atlassian.net" reduces to nothing.
        const string suffix = ".atlassian.net";
        if (s.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            s = s[..^suffix.Length];
        else if (s.Equals("atlassian.net", StringComparison.OrdinalIgnoreCase))
            s = "";

        s = s.Trim().Trim('.');
        return s.Length == 0 ? null : s;
    }

    /// <summary>
    /// Resolves the first Jira ticket referenced by <paramref name="branch"/> into a deep-link, or null when
    /// there is no configured <paramref name="subdomain"/>, no key in the branch, or — with a non-empty
    /// <paramref name="projectFilter"/> — no key whose project is in the filter. The filter is a list of
    /// project keys separated by commas/semicolons/whitespace (e.g. <c>"SFTY, PROJ"</c>); empty matches any key.
    /// </summary>
    public static JiraTicketInfo? Resolve(string? branch, string? subdomain, string? projectFilter)
    {
        var site = NormalizeSubdomain(subdomain);
        if (site is null || string.IsNullOrWhiteSpace(branch)) return null;

        var allowed = ParseProjectFilter(projectFilter);
        foreach (Match m in KeyPattern.Matches(branch))
        {
            var project = m.Groups[1].Value;
            if (allowed.Count > 0 && !allowed.Contains(project)) continue;
            var key = $"{project}-{m.Groups[2].Value}";
            return new JiraTicketInfo(key, $"https://{site}.atlassian.net/browse/{key}");
        }
        return null;
    }

    // The set of project keys a branch key must belong to (case-insensitive); empty ⇒ accept any. Split on
    // comma, semicolon and whitespace so "SFTY, PROJ" and "SFTY PROJ" both parse.
    private static HashSet<string> ParseProjectFilter(string? filter)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(filter)) return set;
        foreach (var part in filter.Split([',', ';', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries))
            set.Add(part.Trim());
        return set;
    }
}
