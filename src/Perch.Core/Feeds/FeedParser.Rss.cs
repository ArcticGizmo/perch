using System.Globalization;
using System.Xml.Linq;

namespace Perch.Feeds;

/// <summary>
/// RSS for <see cref="FeedParser"/> (docs/feeds-plan.md, F6): RSS 2.0 and its 0.9x ancestors (<c>&lt;rss&gt;</c>
/// with a <c>channel</c> holding the <c>item</c>s), and RSS 1.0 / 0.90 (<c>rdf:RDF</c>, with the items beside the
/// channel). Both land in the same <see cref="FeedDoc"/> as Atom, through the same hardened read and the same
/// cleaners — nothing above the parser knows which format a feed was.
///
/// <para>RSS is loose where Atom is strict, so the mapping is deliberately forgiving:</para>
/// <list type="bullet">
///   <item><b>Ids:</b> <c>guid</c>, else RDF's <c>rdf:about</c>, else the link, else a hash of title and date.</item>
///   <item><b>Links:</b> <c>link</c>, else a <c>guid</c> that is a permalink (the default unless
///   <c>isPermaLink="false"</c>). Both go through <see cref="FeedUrl"/>.</item>
///   <item><b>Dates:</b> <c>pubDate</c> (RFC 822, parsed tolerantly — see <see cref="Rfc822"/>), else
///   <c>dc:date</c>, else the fetch time.</item>
///   <item><b>Content:</b> <c>content:encoded</c>, else <c>description</c>; both are HTML (escaped or CDATA), raw
///   until <see cref="HtmlToMarkdown"/> renders it.</item>
///   <item><b>Titles</b> may carry markup or entities in the wild, so they're read as HTML and stripped to text.</item>
/// </list>
/// </summary>
internal static partial class FeedParser
{
    private static readonly XNamespace RdfNs = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";
    private static readonly XNamespace Rss1Ns = "http://purl.org/rss/1.0/";
    private static readonly XNamespace Rss090Ns = "http://my.netscape.com/rdf/simple/0.9/";
    private static readonly XNamespace DcNs = "http://purl.org/dc/elements/1.1/";
    private static readonly XNamespace ContentNs = "http://purl.org/rss/1.0/modules/content/";

    // ── RSS 2.0 / 0.9x ──────────────────────────────────────────────────────────────────────────────────────────

    private static FeedParseResult ParseRss(XElement rss, Uri documentUrl, DateTime nowUtc)
    {
        var channel = rss.Elements().FirstOrDefault(e => e.Name == "channel");
        if (channel is null) return FeedParseResult.Fail("Not a feed (an RSS document with no channel)");
        // RSS 0.91 put items inside the channel; a few feeds put them beside it. Take both.
        var items = channel.Elements("item").Concat(rss.Elements("item"));
        return RssDoc(channel, items, XNamespace.None, documentUrl, nowUtc);
    }

    // ── RSS 1.0 / 0.90 (RDF) ────────────────────────────────────────────────────────────────────────────────────

    private static FeedParseResult ParseRdf(XElement rdf, Uri documentUrl, DateTime nowUtc)
    {
        foreach (var ns in new[] { Rss1Ns, Rss090Ns })
        {
            var channel = rdf.Element(ns + "channel");
            if (channel is null) continue;
            return RssDoc(channel, rdf.Elements(ns + "item"), ns, documentUrl, nowUtc, rdfImage: rdf.Element(ns + "image"));
        }
        return FeedParseResult.Fail("Not a feed (an RDF document that isn't RSS)");
    }

    // ── Shared ──────────────────────────────────────────────────────────────────────────────────────────────────

    private static FeedParseResult RssDoc(XElement channel, IEnumerable<XElement> items, XNamespace ns, Uri documentUrl,
        DateTime nowUtc, XElement? rdfImage = null)
    {
        string title = FeedText.FromHtml(channel.Element(ns + "title")?.Value ?? "", FeedText.FeedTitleMax);
        if (title.Length == 0) title = FeedText.Clean(documentUrl.Host, FeedText.FeedTitleMax);

        string? site = UriText(channel.Element(ns + "link"), documentUrl);
        // Relative links in items resolve against the site when it's given, else the feed's own address.
        var siteBase = site is not null ? new Uri(site) : documentUrl;
        var image = channel.Element(ns + "image") ?? rdfImage;
        string? icon = UriText(image?.Element(ns + "url"), documentUrl);
        DateTime? updated = Date(channel.Element(ns + "lastBuildDate"), nowUtc)
                         ?? Date(channel.Element(ns + "pubDate"), nowUtc)
                         ?? Date(channel.Element(DcNs + "date"), nowUtc);

        var entries = Entries(items, i => RssItem(i, ns, siteBase, nowUtc));
        return FeedParseResult.Ok(new FeedDoc(title, site, icon, updated, entries));
    }

    private static FeedEntry? RssItem(XElement item, XNamespace ns, Uri siteBase, DateTime nowUtc)
    {
        var @base = BaseOf(item, siteBase);
        string title = FeedText.FromHtml(item.Element(ns + "title")?.Value ?? "", FeedText.TitleMax);

        // A null base (a poisoned xml:base) still admits absolute links, as in the Atom path.
        string? url = FeedUrl.SafeString(item.Element(ns + "link")?.Value.Trim(), @base);
        var guid = item.Element(ns + "guid");
        bool permalink = !string.Equals(((string?)guid?.Attribute("isPermaLink"))?.Trim(), "false", StringComparison.OrdinalIgnoreCase);
        if (url is null && guid is not null && permalink)
            url = FeedUrl.SafeString(guid.Value.Trim(), @base);

        DateTime? published = Date(item.Element(ns + "pubDate"), nowUtc) ?? Date(item.Element(DcNs + "date"), nowUtc);
        DateTime updated = published ?? nowUtc;

        string id = EntryId(guid?.Value ?? (string?)item.Attribute(RdfNs + "about"), url, title, updated);

        var description = item.Element(ns + "description")?.Value;
        var encoded = item.Element(ContentNs + "encoded")?.Value;
        string? contentHtml = !string.IsNullOrWhiteSpace(encoded) ? encoded
                            : !string.IsNullOrWhiteSpace(description) ? description : null;
        string? summary = !string.IsNullOrWhiteSpace(description) ? FeedText.FromHtml(description, FeedText.SummaryMax)
                        : contentHtml is not null ? FeedText.FromHtml(contentHtml, FeedText.SummaryMax) : null;

        if (title.Length == 0) title = string.IsNullOrEmpty(summary) ? "(untitled)" : FeedText.Clean(summary, 80);

        return new FeedEntry(id, title, url, RssAuthor(item, ns), published, updated, contentHtml, @base?.AbsoluteUri,
            string.IsNullOrEmpty(summary) ? null : summary);
    }

    // dc:creator, else RSS 2.0's author — an email, conventionally "jane@example.com (Jane Doe)": the name wins.
    private static string? RssAuthor(XElement item, XNamespace ns)
    {
        string raw = item.Element(DcNs + "creator")?.Value ?? item.Element(ns + "author")?.Value ?? "";
        int open = raw.IndexOf('('), close = raw.LastIndexOf(')');
        if (open >= 0 && close > open + 1) raw = raw[(open + 1)..close];
        var clean = FeedText.Clean(raw, FeedText.AuthorMax);
        return clean.Length == 0 ? null : clean;
    }

    private static string? UriText(XElement? el, Uri documentUrl) =>
        el is null ? null : FeedUrl.SafeString(el.Value.Trim(), BaseOf(el, documentUrl));

    // ── Dates ───────────────────────────────────────────────────────────────────────────────────────────────────

    private static readonly string[] Months = ["jan", "feb", "mar", "apr", "may", "jun", "jul", "aug", "sep", "oct", "nov", "dec"];

    private static readonly Dictionary<string, int> ZoneHours = new(StringComparer.OrdinalIgnoreCase)
    {
        ["GMT"] = 0, ["UT"] = 0, ["UTC"] = 0, ["Z"] = 0,
        ["EST"] = -5, ["EDT"] = -4, ["CST"] = -6, ["CDT"] = -5, ["MST"] = -7, ["MDT"] = -6, ["PST"] = -8, ["PDT"] = -7,
        ["BST"] = 1, ["CET"] = 1, ["CEST"] = 2, ["EET"] = 2, ["EEST"] = 3, ["JST"] = 9, ["AEST"] = 10, ["AEDT"] = 11,
    };

    /// <summary>
    /// RFC 822 / 1123 as feeds actually write it: an optional (and possibly wrong) weekday, one- or two-digit days,
    /// two- or four-digit years, a time with or without seconds (or none), and a zone given as an offset
    /// (<c>+0200</c>, <c>-05:00</c>), a common name (<c>GMT</c>, <c>EST</c>, <c>CEST</c>…) or nothing (UTC). Also
    /// "Oct 7 2026"-style month-first dates. Returns null for anything else, including ISO 8601 (handled elsewhere).
    /// </summary>
    internal static DateTime? Rfc822(string s)
    {
        var parts = s.Replace(',', ' ').Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).ToList();
        if (parts.Count < 3) return null;
        if (parts[0].All(char.IsAsciiLetter) && Month(parts[0]) < 0) parts.RemoveAt(0);   // the weekday, ignored
        if (parts.Count < 3) return null;

        int day, month;
        if (Month(parts[1]) is var m1 && m1 >= 0 && int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out day))
            month = m1;
        else if (Month(parts[0]) is var m0 && m0 >= 0 && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out day))
            month = m0;
        else return null;

        if (!int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out int year)) return null;
        if (parts[2].Length <= 2) year += year < 50 ? 2000 : 1900;

        int h = 0, min = 0, sec = 0, at = 3;
        if (parts.Count > at && parts[at].Contains(':'))
        {
            var t = parts[at].Split(':');
            if (t.Length is < 2 or > 3
                || !int.TryParse(t[0], NumberStyles.None, CultureInfo.InvariantCulture, out h)
                || !int.TryParse(t[1], NumberStyles.None, CultureInfo.InvariantCulture, out min)
                || (t.Length == 3 && !int.TryParse(t[2].Split('.')[0], NumberStyles.None, CultureInfo.InvariantCulture, out sec)))
                return null;
            at++;
        }

        TimeSpan offset = TimeSpan.Zero;   // no zone: UTC
        if (parts.Count > at)
        {
            if (Offset(parts[at]) is not { } o) return null;
            offset = o;
        }

        try
        {
            return new DateTimeOffset(year, month, day, h, min, sec == 60 ? 59 : sec, offset).UtcDateTime;
        }
        catch (ArgumentException)
        {
            return null;   // 31 Feb, 25:00, a +2500 offset…
        }
    }

    // 1–12 for a month name or its abbreviation ("Oct", "October", "Sept"), else -1.
    private static int Month(string token)
    {
        if (token.Length < 3 || !token.All(char.IsAsciiLetter)) return -1;
        int i = Array.IndexOf(Months, token[..3].ToLowerInvariant());
        return i < 0 ? -1 : i + 1;
    }

    private static TimeSpan? Offset(string zone)
    {
        if (ZoneHours.TryGetValue(zone, out int hours)) return TimeSpan.FromHours(hours);
        if (zone.Length == 1 && char.IsAsciiLetter(zone[0])) return TimeSpan.Zero;   // military zones: rare, treat as UTC
        if (zone.Length is 5 or 6 && zone[0] is '+' or '-')
        {
            var digits = zone[1..].Replace(":", "");
            if (digits.Length == 4 && digits.All(char.IsAsciiDigit))
            {
                var span = new TimeSpan(int.Parse(digits[..2], CultureInfo.InvariantCulture), int.Parse(digits[2..], CultureInfo.InvariantCulture), 0);
                if (span > TimeSpan.FromHours(14)) return null;
                return zone[0] == '-' ? -span : span;
            }
        }
        return null;
    }
}
