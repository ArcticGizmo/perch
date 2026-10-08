using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Perch.Feeds;

/// <summary>
/// Reads a feed document into the normalized <see cref="FeedDoc"/> (docs/feeds-plan.md §3.1, §3.4.1). Atom 1.0
/// here; RSS 2.0/0.9x and RSS 1.0 (RDF) in <c>FeedParser.Rss.cs</c>, behind the same entry point and the same
/// hardened read. Pure (the clock is passed in) and throw-free: anything
/// that isn't a well-formed, acceptable feed comes back as <see cref="FeedParseResult.Fail"/> with a short reason
/// fit to show the user.
///
/// <para>This is where untrusted bytes are admitted, so it's where the XML-level defences live: a byte cap, a
/// reader that refuses any DOCTYPE (no XXE, no entity expansion) and never resolves anything, a depth limit
/// checked by a non-recursive pre-scan before the tree is built, and a cap on entries processed. Every string it
/// hands out is <see cref="FeedText"/>-clean and every URL <see cref="FeedUrl"/>-vetted; only
/// <see cref="FeedEntry.ContentHtml"/> stays raw, for <see cref="HtmlToMarkdown"/> at render time.</para>
/// </summary>
internal static partial class FeedParser
{
    public const int MaxBytes = 4 * 1024 * 1024;
    public const int MaxDepth = 64;
    public const int MaxEntries = 100;
    private const int MaxEntriesScanned = 1000;
    private const int MaxIdLength = 2048;

    private static readonly XNamespace AtomNs = "http://www.w3.org/2005/Atom";
    private static readonly XName XmlBase = XNamespace.Xml + "base";

    // Stated explicitly — never left to defaults — so a refactor can't quietly loosen them. Shared with FeedOpml, so
    // every untrusted XML Perch reads goes through the one configuration.
    internal static XmlReaderSettings ReaderSettings() => new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        MaxCharactersFromEntities = 1024,
        MaxCharactersInDocument = 8_000_000,
        IgnoreProcessingInstructions = true,
        IgnoreComments = true,
        CloseInput = false,
    };

    /// <summary>Parses up to <see cref="MaxBytes"/> from <paramref name="body"/>; a longer body is refused.</summary>
    public static FeedParseResult Parse(Stream body, Uri documentUrl, DateTime nowUtc)
    {
        byte[] bytes;
        try
        {
            using var ms = new MemoryStream();
            var buf = new byte[81920];
            int read;
            while ((read = body.Read(buf, 0, buf.Length)) > 0)
            {
                if (ms.Length + read > MaxBytes) return FeedParseResult.Fail("Feed is too large");
                ms.Write(buf, 0, read);
            }
            bytes = ms.ToArray();
        }
        catch
        {
            return FeedParseResult.Fail("Couldn't read the feed");
        }
        return ParseBytes(bytes, documentUrl, nowUtc);
    }

    /// <summary>Parses an in-memory document (tests, the cache).</summary>
    public static FeedParseResult Parse(string xml, Uri documentUrl, DateTime nowUtc)
    {
        var bytes = Encoding.UTF8.GetBytes(xml ?? "");
        if (bytes.Length > MaxBytes) return FeedParseResult.Fail("Feed is too large");
        return ParseBytes(bytes, documentUrl, nowUtc);
    }

    private static FeedParseResult ParseBytes(byte[] bytes, Uri documentUrl, DateTime nowUtc)
    {
        if (bytes.Length == 0) return FeedParseResult.Fail("The feed is empty");

        XDocument doc;
        try
        {
            // Pass 1: walk the whole document with the hardened reader, checking depth. Iterative, so a hostile
            // nesting depth fails here instead of reaching anything recursive.
            using (var scan = XmlReader.Create(new MemoryStream(bytes), ReaderSettings()))
            {
                while (scan.Read())
                    if (scan.Depth > MaxDepth) return FeedParseResult.Fail("Refused: the feed is nested too deeply");
            }

            // Pass 2: build the tree with the same settings.
            using var reader = XmlReader.Create(new MemoryStream(bytes), ReaderSettings());
            doc = XDocument.Load(reader, LoadOptions.None);
        }
        catch (XmlException)
        {
            // An HTML page (the most common wrong URL) usually fails here on its DOCTYPE or its markup; say so
            // rather than blaming the DOCTYPE.
            var head = Head(bytes, 64 * 1024);
            return FeedParseResult.Fail(
                head.Contains("<html", StringComparison.OrdinalIgnoreCase) ? "Not a feed (looks like a web page)"
                : head.Contains("<!DOCTYPE", StringComparison.OrdinalIgnoreCase) ? "Refused: the feed declares a DOCTYPE"
                : "Not a feed (couldn't read it as XML)");
        }
        catch
        {
            return FeedParseResult.Fail("Not a feed (couldn't read it as XML)");
        }

        var root = doc.Root;
        if (root is null) return FeedParseResult.Fail("Not a feed");
        if (root.Name == AtomNs + "feed") return ParseAtom(root, documentUrl, nowUtc);
        if (root.Name.LocalName == "rss") return ParseRss(root, documentUrl, nowUtc);
        if (root.Name == RdfNs + "RDF") return ParseRdf(root, documentUrl, nowUtc);
        return root.Name.LocalName switch
        {
            "html" => FeedParseResult.Fail("Not a feed (looks like a web page)"),
            "feed" => FeedParseResult.Fail("Not a feed (unsupported Atom version)"),
            _ => FeedParseResult.Fail("Not a feed"),
        };
    }

    private static string Head(byte[] bytes, int max) => Encoding.UTF8.GetString(bytes, 0, Math.Min(bytes.Length, max));

    // ── Atom 1.0 (RFC 4287) ─────────────────────────────────────────────────────────────────────────────────────

    private static FeedParseResult ParseAtom(XElement feed, Uri documentUrl, DateTime nowUtc)
    {
        string title = TextConstruct(feed.Element(AtomNs + "title"), FeedText.FeedTitleMax);
        if (title.Length == 0) title = FeedText.Clean(documentUrl.Host, FeedText.FeedTitleMax);

        string? site = AlternateLink(feed, documentUrl);
        string? icon = UriElement(feed.Element(AtomNs + "icon"), documentUrl)
                    ?? UriElement(feed.Element(AtomNs + "logo"), documentUrl);
        DateTime? updated = Date(feed.Element(AtomNs + "updated"), nowUtc);
        string? feedAuthor = AuthorName(feed);

        var entries = Entries(feed.Elements(AtomNs + "entry"), e => ParseEntry(e, documentUrl, feedAuthor, nowUtc));
        return FeedParseResult.Ok(new FeedDoc(title, site, icon, updated, entries));
    }

    // Shared by every format: parse up to MaxEntriesScanned items, skipping any that throw (one bad entry never
    // costs the rest of the feed), then dedupe by id (first wins), newest first, capped at MaxEntries. The sort is
    // stable, so undated items keep their document order.
    private static List<FeedEntry> Entries(IEnumerable<XElement> items, Func<XElement, FeedEntry?> parse)
    {
        var entries = new List<FeedEntry>();
        int scanned = 0;
        foreach (var e in items)
        {
            if (++scanned > MaxEntriesScanned) break;
            try
            {
                if (parse(e) is { } entry) entries.Add(entry);
            }
            catch
            {
            }
        }
        return entries
            .GroupBy(x => x.Id).Select(g => g.First())
            .OrderByDescending(x => x.Updated)
            .Take(MaxEntries)
            .ToList();
    }

    private static string EntryId(string? raw, string? url, string title, DateTime updated)
    {
        string id = (raw ?? "").Trim();
        if (id.Length == 0) id = url ?? Hash(title + "|" + updated.ToString("O", CultureInfo.InvariantCulture));
        return id.Length > MaxIdLength ? Hash(id) : id;
    }

    private static FeedEntry? ParseEntry(XElement e, Uri documentUrl, string? feedAuthor, DateTime nowUtc)
    {
        string title = TextConstruct(e.Element(AtomNs + "title"), FeedText.TitleMax);
        string? url = AlternateLink(e, documentUrl);

        DateTime? published = Date(e.Element(AtomNs + "published"), nowUtc);
        DateTime updated = Date(e.Element(AtomNs + "updated"), nowUtc) ?? published ?? nowUtc;

        string id = EntryId(e.Element(AtomNs + "id")?.Value, url, title, updated);

        string? author = AuthorName(e) ?? feedAuthor;

        string? contentHtml = null, contentBase = null;
        var content = e.Element(AtomNs + "content");
        if (content is not null)
        {
            if (content.Attribute("src") is { } src)
            {
                // Out-of-line content: nothing to render, but it may be the best link we have.
                url ??= FeedUrl.SafeString(src.Value, BaseOf(content, documentUrl));
            }
            else
            {
                contentHtml = ContentAsHtml(content);
                contentBase = BaseOf(content, documentUrl)?.AbsoluteUri;
            }
        }

        var summaryEl = e.Element(AtomNs + "summary");
        string? summary = summaryEl is not null ? TextConstruct(summaryEl, FeedText.SummaryMax) : null;
        if (contentHtml is null && summaryEl is not null)
        {
            contentHtml = ContentAsHtml(summaryEl);
            contentBase = BaseOf(summaryEl, documentUrl)?.AbsoluteUri;
        }
        if (string.IsNullOrEmpty(summary) && contentHtml is not null)
            summary = FeedText.FromHtml(contentHtml, FeedText.SummaryMax);

        if (title.Length == 0) title = string.IsNullOrEmpty(summary) ? "(untitled)" : FeedText.Clean(summary, 80);

        return new FeedEntry(id, title, url, author, published, updated, contentHtml, contentBase,
            string.IsNullOrEmpty(summary) ? null : summary);
    }

    // An Atom text construct (title/summary/subtitle) as clean plain text. "text" is literal; "html" is markup to
    // strip and decode once; "xhtml" is a div whose text the XML reader has already decoded.
    private static string TextConstruct(XElement? el, int max)
    {
        if (el is null) return "";
        return TypeOf(el) switch
        {
            "html" => FeedText.FromHtml(el.Value, max),
            "xhtml" => FeedText.Clean(XhtmlDiv(el)?.Value ?? el.Value, max),
            _ => FeedText.Clean(el.Value, max),
        };
    }

    // An Atom content/summary element as an HTML string (raw; sanitized later by HtmlToMarkdown).
    private static string? ContentAsHtml(XElement el)
    {
        switch (TypeOf(el))
        {
            case "html":
                return el.Value;
            case "xhtml":
            {
                var div = XhtmlDiv(el);
                var nodes = (div ?? el).Nodes();
                return string.Concat(nodes.Select(n => n.ToString(SaveOptions.DisableFormatting)));
            }
            case "text":
                return TextToHtml(el.Value);
            default:
                // A media type: only text/* is readable here.
                var t = (string?)el.Attribute("type") ?? "";
                return t.StartsWith("text/", StringComparison.OrdinalIgnoreCase) ? TextToHtml(el.Value) : null;
        }
    }

    private static string TextToHtml(string text)
    {
        var paras = text.Replace("\r\n", "\n").Split("\n\n", StringSplitOptions.RemoveEmptyEntries);
        return string.Concat(paras.Select(p => "<p>" + System.Net.WebUtility.HtmlEncode(p.Trim()).Replace("\n", "<br>") + "</p>"));
    }

    private static string TypeOf(XElement el)
    {
        var t = ((string?)el.Attribute("type") ?? "text").Trim().ToLowerInvariant();
        return t switch
        {
            "" or "text" or "text/plain" => "text",
            "html" or "text/html" => "html",
            "xhtml" or "application/xhtml+xml" => "xhtml",
            _ => t,
        };
    }

    private static XElement? XhtmlDiv(XElement el) =>
        el.Elements().FirstOrDefault(c => c.Name.LocalName == "div");

    // The entry/feed link to open in a browser: rel="alternate" (or no rel), preferring an HTML type.
    private static string? AlternateLink(XElement parent, Uri documentUrl)
    {
        string? fallback = null;
        foreach (var link in parent.Elements(AtomNs + "link"))
        {
            var rel = ((string?)link.Attribute("rel") ?? "alternate").Trim().ToLowerInvariant();
            if (rel is not ("alternate" or "http://www.iana.org/assignments/relation/alternate")) continue;
            var safe = FeedUrl.SafeString((string?)link.Attribute("href"), BaseOf(link, documentUrl));
            if (safe is null) continue;
            var type = ((string?)link.Attribute("type") ?? "").ToLowerInvariant();
            if (type.Length == 0 || type.Contains("html")) return safe;
            fallback ??= safe;
        }
        return fallback;
    }

    private static string? UriElement(XElement? el, Uri documentUrl) =>
        el is null ? null : FeedUrl.SafeString(el.Value, BaseOf(el, documentUrl));

    private static string? AuthorName(XElement parent)
    {
        var name = parent.Element(AtomNs + "author")?.Element(AtomNs + "name")?.Value;
        var clean = FeedText.Clean(name, FeedText.AuthorMax);
        return clean.Length == 0 ? null : clean;
    }

    /// <summary>
    /// The base URI in scope at <paramref name="el"/>: the document URL, re-based by each <c>xml:base</c> from the
    /// outermost ancestor in. Any <c>xml:base</c> that lands outside http(s) poisons the scope — null, so nothing
    /// under it can resolve to a <c>file:</c>/<c>javascript:</c> target. (FeedUrl would refuse those anyway; this
    /// keeps the model honest about it.)
    /// </summary>
    private static Uri? BaseOf(XElement el, Uri documentUrl)
    {
        var bases = new List<string>();
        for (var e = el; e is not null; e = e.Parent)
            if (e.Attribute(XmlBase) is { } b) bases.Add(b.Value);
        Uri? current = documentUrl;
        for (int i = bases.Count - 1; i >= 0; i--)
        {
            var raw = bases[i].Trim();
            if (raw.Length == 0) continue;
            if (FeedUrl.Safe(raw, current) is not { } next) return null;
            current = next;
        }
        return current is not null && (current.Scheme == Uri.UriSchemeHttp || current.Scheme == Uri.UriSchemeHttps)
            ? current : null;
    }

    // RFC 3339 (Atom's format), RFC 822 as RSS writes it (tolerantly, see Rfc822), or anything else DateTimeOffset
    // reads invariantly. A date more than a day in the future is clamped to now, so a feed can't pin an entry to
    // the top of the story forever; one before 1990 is junk.
    private static DateTime? Date(XElement? el, DateTime nowUtc)
    {
        var s = el?.Value.Trim();
        if (string.IsNullOrEmpty(s) || s.Length > 100) return null;
        DateTime? utc = Rfc822(s);
        if (utc is null && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal, out var dto))
            utc = dto.UtcDateTime;
        if (utc is not { } d || d.Year < 1990) return null;
        return d > nowUtc.AddDays(1) ? nowUtc : d;
    }

    private static string Hash(string s) =>
        "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)))[..32].ToLowerInvariant();
}
