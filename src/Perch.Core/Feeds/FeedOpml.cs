using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Perch.Feeds;

/// <summary>One feed found in an OPML file: its vetted address and the file's (cleaned) name for it.</summary>
internal sealed record OpmlFeed(Uri Url, string? Title);

/// <summary>What an OPML import found: the feeds new to Perch, plus how many were already followed and how many
/// were unusable (not http(s), malformed) or over the cap — or why the file couldn't be read at all.</summary>
internal sealed record OpmlImport(IReadOnlyList<OpmlFeed> Feeds, int AlreadyFollowed, int Skipped, string? Error)
{
    public static OpmlImport Fail(string error) => new([], 0, 0, error);
}

/// <summary>
/// OPML, the format every feed reader imports and exports subscription lists in (docs/feeds-plan.md, F7).
/// <para><b>Import</b> treats the file as untrusted, like a feed. It goes through the parser's hardened reader (no
/// DOCTYPE, nothing resolved, entity and size caps), a depth limit checked before the tree is built, and a byte cap.
/// It collects every <c>outline</c> with an <c>xmlUrl</c>, however the folders nest. Each address must pass
/// <see cref="FeedUrl.Safe"/> as an absolute http(s) URL, titles are <see cref="FeedText"/>-cleaned, duplicates and
/// feeds already followed drop out, and at most <see cref="MaxFeeds"/> come back. Nothing is fetched here. Pure;
/// never throws.</para>
/// <para><b>Export</b> writes OPML 2.0 through <see cref="XDocument"/>, so every title and address is escaped.</para>
/// </summary>
internal static class FeedOpml
{
    public const int MaxBytes = 2 * 1024 * 1024;
    public const int MaxFeeds = 500;
    private const int MaxDepth = 64;

    public static OpmlImport Parse(Stream stream, IReadOnlyList<FeedSubscription> existing)
    {
        byte[] bytes;
        try
        {
            using var ms = new MemoryStream();
            var buf = new byte[81920];
            int read;
            while ((read = stream.Read(buf, 0, buf.Length)) > 0)
            {
                if (ms.Length + read > MaxBytes) return OpmlImport.Fail("The file is too large to be a feed list");
                ms.Write(buf, 0, read);
            }
            bytes = ms.ToArray();
        }
        catch
        {
            return OpmlImport.Fail("Couldn't read the file");
        }
        return ParseBytes(bytes, existing);
    }

    public static OpmlImport Parse(string xml, IReadOnlyList<FeedSubscription> existing)
    {
        var bytes = Encoding.UTF8.GetBytes(xml ?? "");
        return bytes.Length > MaxBytes ? OpmlImport.Fail("The file is too large to be a feed list") : ParseBytes(bytes, existing);
    }

    private static OpmlImport ParseBytes(byte[] bytes, IReadOnlyList<FeedSubscription> existing)
    {
        XDocument doc;
        try
        {
            using (var scan = XmlReader.Create(new MemoryStream(bytes), FeedParser.ReaderSettings()))
            {
                while (scan.Read())
                    if (scan.Depth > MaxDepth) return OpmlImport.Fail("Refused: the file is nested too deeply");
            }
            using var reader = XmlReader.Create(new MemoryStream(bytes), FeedParser.ReaderSettings());
            doc = XDocument.Load(reader, LoadOptions.None);
        }
        catch
        {
            return OpmlImport.Fail("Not an OPML file (couldn't read it as XML)");
        }

        if (doc.Root is not { } root || root.Name.LocalName != "opml" || root.Element("body") is not { } body)
            return OpmlImport.Fail("Not an OPML file");

        var feeds = new List<OpmlFeed>();
        var seen = new List<FeedSubscription>(existing);   // existing + accepted so far, for one duplicate rule
        int already = 0, skipped = 0;
        foreach (var outline in body.Descendants("outline"))
        {
            var raw = (string?)outline.Attribute("xmlUrl");
            if (raw is null) continue;   // a folder (or a plain outline item), not a feed

            if (FeedUrl.Safe(raw.Trim(), null) is not { } url) { skipped++; continue; }
            if (FeedAddress.IsDuplicate(url, existing, exceptId: null)) { already++; continue; }
            if (FeedAddress.IsDuplicate(url, seen, exceptId: null)) continue;   // listed twice in this file
            if (feeds.Count >= MaxFeeds) { skipped++; continue; }

            var title = FeedText.Clean((string?)outline.Attribute("title") ?? (string?)outline.Attribute("text"), FeedText.FeedTitleMax);
            feeds.Add(new OpmlFeed(url, title.Length > 0 ? title : null));
            seen.Add(new FeedSubscription { Id = "opml", Url = url.AbsoluteUri });
        }
        return new OpmlImport(feeds, already, skipped, null);
    }

    /// <summary>The subscriptions as an OPML 2.0 document. <paramref name="titleOf"/> names each (the user's
    /// override, the feed's own title, or its host); disabled feeds are included, so the list round-trips.</summary>
    public static string Export(IReadOnlyList<FeedSubscription> subscriptions, Func<FeedSubscription, string> titleOf, DateTime nowUtc)
    {
        var body = new XElement("body");
        foreach (var s in subscriptions)
        {
            if (FeedUrl.Safe(s.Url, null) is not { } url) continue;
            var title = titleOf(s);
            body.Add(new XElement("outline",
                new XAttribute("type", "rss"), new XAttribute("text", title), new XAttribute("title", title),
                new XAttribute("xmlUrl", url.AbsoluteUri)));
        }
        var doc = new XDocument(new XDeclaration("1.0", "utf-8", null),
            new XElement("opml", new XAttribute("version", "2.0"),
                new XElement("head",
                    new XElement("title", "Perch feeds"),
                    new XElement("dateCreated", nowUtc.ToString("r", CultureInfo.InvariantCulture))),
                body));
        var sb = new StringBuilder();
        using (var w = XmlWriter.Create(new Utf8StringWriter(sb), new XmlWriterSettings { Indent = true, Encoding = Encoding.UTF8 }))
            doc.Save(w);
        return sb.ToString();
    }

    // StringWriter reports UTF-16; the declaration should say what the file will be written as.
    private sealed class Utf8StringWriter(StringBuilder sb) : StringWriter(sb, CultureInfo.InvariantCulture)
    {
        public override Encoding Encoding => Encoding.UTF8;
    }
}
