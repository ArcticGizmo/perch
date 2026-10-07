namespace Perch.Feeds;

/// <summary>
/// What the user typed into the Add feed dialog, turned into a subscription URL (or a reason it can't be one),
/// plus the plain-language hints the dialog shows for a failed check. Pure and UI-free so the rules are tested.
/// </summary>
internal static class FeedAddress
{
    /// <summary>The outcome of reading the address box.</summary>
    internal sealed record Parsed(Uri? Url, string? Problem, bool Insecure);

    /// <summary>
    /// Normalizes typed input: trims it, maps the <c>feed:</c> pseudo-scheme (<c>feed://host/x</c>,
    /// <c>feed:https://host/x</c>) to a web URL, and assumes <c>https://</c> when no scheme is given. The result
    /// must pass <see cref="FeedUrl.Safe"/> (http/https, no userinfo). <see cref="Parsed.Insecure"/> marks plain
    /// http, which is allowed with a warning.
    /// </summary>
    public static Parsed Parse(string? text)
    {
        var s = (text ?? "").Trim();
        if (s.Length == 0) return new(null, null, false);

        if (s.StartsWith("feed:", StringComparison.OrdinalIgnoreCase))
        {
            s = s[5..];
            if (s.StartsWith("//", StringComparison.Ordinal)) s = "https:" + s;
        }
        bool hadScheme = s.Contains("://", StringComparison.Ordinal) || s.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase)
                         || s.StartsWith("data:", StringComparison.OrdinalIgnoreCase);
        if (!hadScheme) s = "https://" + s;

        var url = FeedUrl.Safe(s, null);
        if (url is null) return new(null, "Enter a web address (http or https)", false);

        // A bare word ("news") typed without a scheme is almost certainly not a host; a typed scheme means the
        // user meant it (an intranet name like http://wiki/feed).
        if (!hadScheme && url.HostNameType == UriHostNameType.Dns && !url.Host.Contains('.')
            && !string.Equals(url.Host, "localhost", StringComparison.OrdinalIgnoreCase))
            return new(null, "That doesn't look like a web address", false);
        return new(url, null, url.Scheme == Uri.UriSchemeHttp);
    }

    /// <summary>Whether <paramref name="url"/> is already followed by another subscription (ignoring
    /// <paramref name="exceptId"/>, the one being edited).</summary>
    public static bool IsDuplicate(Uri url, IEnumerable<FeedSubscription> existing, string? exceptId) =>
        existing.Any(s => s.Id != exceptId && FeedUrl.Safe(s.Url, null) is { } u && Same(u, url));

    private static bool Same(Uri a, Uri b) =>
        string.Equals(a.IdnHost, b.IdnHost, StringComparison.OrdinalIgnoreCase)
        && a.Port == b.Port
        && string.Equals(a.PathAndQuery.TrimEnd('/'), b.PathAndQuery.TrimEnd('/'), StringComparison.Ordinal);

    /// <summary>A next step for a failed check, or null when the error speaks for itself.</summary>
    public static string? HintFor(string? error) => error switch
    {
        null => null,
        _ when error.Contains("web page", StringComparison.OrdinalIgnoreCase) =>
            "That address is a web page, not a feed. Look for an \"Atom\" or \"Feed\" link on the site, or try " +
            "adding /atom.xml or /feed to the address.",
        _ when error.Contains("RSS", StringComparison.Ordinal) =>
            "RSS support is coming. Many sites offer an Atom version too — try /atom.xml or ?feed=atom.",
        _ when error.StartsWith("404", StringComparison.Ordinal) => "Check the address — the server says there's nothing there.",
        _ when error.StartsWith("401", StringComparison.Ordinal) || error.StartsWith("403", StringComparison.Ordinal) =>
            "This feed needs signing in, which isn't supported yet.",
        _ when error.Contains("private network", StringComparison.OrdinalIgnoreCase) =>
            "Perch won't follow a public feed into your local network.",
        _ => null,
    };
}
