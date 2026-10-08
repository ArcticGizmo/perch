using System.Net;

namespace Perch.Feeds;

/// <summary>A feed a web page advertises: its address, the page's name for it (cleaned) and its format.</summary>
internal sealed record FeedCandidate(Uri Url, string? Title, string Format);

/// <summary>
/// Feed autodiscovery (docs/feeds-plan.md, F6): when the address the user gave is a web page, find the feeds it
/// advertises — <c>&lt;link rel="alternate" type="application/atom+xml|rss+xml|rdf+xml" href="…"&gt;</c> — so the
/// add dialog can offer them. Pure and throw-free; it only reads tags (never scripts or bodies), honours a
/// <c>&lt;base href&gt;</c>, and every address goes through <see cref="FeedUrl.Safe"/>. The page is untrusted, so a
/// candidate is only ever <em>offered</em>: choosing one runs the normal check, and the fetcher drops any that point
/// into a private network from a public page (<see cref="FeedFetcher"/>).
/// </summary>
internal static class FeedDiscovery
{
    public const int MaxCandidates = 8;
    private const int MaxScan = 512 * 1024;   // feeds are announced in <head>; never scan a whole huge page

    private static readonly HashSet<string> Attributes = new(StringComparer.Ordinal) { "rel", "type", "href", "title" };

    public static IReadOnlyList<FeedCandidate> Find(string? html, Uri pageUrl)
    {
        if (string.IsNullOrEmpty(html)) return [];
        if (html.Length > MaxScan) html = html[..MaxScan];

        var found = new List<FeedCandidate>();
        Uri baseUri = pageUrl;
        bool baseSeen = false;
        int i = 0, n = html.Length;
        while (i < n && found.Count < MaxCandidates)
        {
            int lt = html.IndexOf('<', i);
            if (lt < 0 || lt + 1 >= n) break;

            if (string.CompareOrdinal(html, lt, "<!--", 0, 4) == 0)
            {
                int end = html.IndexOf("-->", lt + 4, StringComparison.Ordinal);
                i = end < 0 ? n : end + 3;
                continue;
            }

            int p = lt + 1;
            while (p < n && char.IsAsciiLetter(html[p])) p++;
            string name = html[(lt + 1)..p].ToLowerInvariant();
            if (name is not ("link" or "base") || p >= n || !(char.IsWhiteSpace(html[p]) || html[p] is '/' or '>'))
            {
                i = lt + 1;
                continue;
            }
            if (!HtmlTokenizer.TryReadAttributes(html, p, out int tagEnd, out _, out var attrs, Attributes)) break;
            i = tagEnd;

            string Attr(string key) => WebUtility.HtmlDecode(attrs.GetValueOrDefault(key) ?? "").Trim();

            if (name == "base")
            {
                // Only the first <base> counts (as in browsers), and only an http(s) one.
                if (!baseSeen && FeedUrl.Safe(Attr("href"), pageUrl) is { } b) baseUri = b;
                baseSeen = true;
                continue;
            }

            var rels = Attr("rel").ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (!rels.Contains("alternate") || rels.Contains("stylesheet")) continue;
            string? format = Attr("type").ToLowerInvariant().Split(';')[0].Trim() switch
            {
                "application/atom+xml" => "Atom",
                "application/rss+xml" or "application/rdf+xml" => "RSS",
                _ => null,
            };
            if (format is null || FeedUrl.Safe(Attr("href"), baseUri) is not { } url) continue;
            if (found.Any(c => c.Url == url)) continue;

            string title = FeedText.Clean(Attr("title"), FeedText.FeedTitleMax);
            found.Add(new FeedCandidate(url, title.Length > 0 ? title : null, format));
        }
        return found;
    }
}
