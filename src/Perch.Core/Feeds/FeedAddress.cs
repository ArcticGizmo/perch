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
            "That address is a web page, not a feed, and it doesn't advertise one. Look for an \"RSS\", \"Atom\" or " +
            "\"Feed\" link on the site, or try adding /feed or /atom.xml to the address.",
        _ when error.StartsWith("404", StringComparison.Ordinal) => "Check the address — the server says there's nothing there.",
        _ when error.StartsWith("401", StringComparison.Ordinal) || error.StartsWith("403", StringComparison.Ordinal) =>
            "This feed needs signing in, which isn't supported yet.",
        _ when error.Contains("private network", StringComparison.OrdinalIgnoreCase) =>
            "Perch won't follow a public feed into your local network.",
        _ => null,
    };

    /// <summary>
    /// A next step for a subscription that's failing in the background (the overlay tooltip, the story player's
    /// error card, the Settings row). Covers everything <see cref="HintFor"/> does, plus the transient failures a
    /// first check needn't explain — those say that Perch keeps retrying on its own.
    /// </summary>
    public static string? StatusHint(string? error) => error switch
    {
        null => null,
        _ when HintFor(error) is { } hint => hint,
        _ when error.StartsWith("Timed out", StringComparison.Ordinal)
            || error.StartsWith("Couldn't connect", StringComparison.Ordinal)
            || error.StartsWith("Couldn't find", StringComparison.Ordinal) =>
            "The site didn't answer. Perch keeps retrying, backing off to once every 2 hours.",
        _ when error.StartsWith("429", StringComparison.Ordinal) || IsServerError(error) =>
            "The server is having trouble. Perch keeps retrying, backing off to once every 2 hours.",
        _ when error.Contains("too large", StringComparison.OrdinalIgnoreCase) =>
            "The feed is bigger than Perch's 4 MB limit.",
        _ => null,
    };

    // "503 Service Unavailable" or "HTTP 599".
    private static bool IsServerError(string error)
    {
        var s = error.StartsWith("HTTP ", StringComparison.Ordinal) ? error[5..] : error;
        return s.Length >= 3 && s[0] == '5' && char.IsAsciiDigit(s[1]) && char.IsAsciiDigit(s[2])
            && (s.Length == 3 || s[3] == ' ');
    }
}
