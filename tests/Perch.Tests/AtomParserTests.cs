using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Perch.Data;
using Perch.Feeds;
using Xunit;

namespace Perch.Tests;

/// <summary>
/// The happy path of <see cref="FeedParser"/> (Atom 1.0) and <see cref="HtmlToMarkdown"/> against realistic feeds —
/// GitHub-releases-style escaped HTML and a blog using xhtml content with nested <c>xml:base</c>. The hostile
/// corpus lives in <see cref="FeedInjectionTests"/>.
/// </summary>
public class AtomParserTests
{
    private static readonly DateTime Now = new(2026, 10, 7, 12, 0, 0, DateTimeKind.Utc);

    private static FeedDoc Load(string name, string url)
    {
        using var fs = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "fixtures", "feeds", name));
        var r = FeedParser.Parse(fs, new Uri(url), Now);
        Assert.Null(r.Error);
        return r.Doc!;
    }

    private static FeedDoc Releases() => Load("atom-releases.xml", "https://github.com/example/widget/releases.atom");
    private static FeedDoc Blog() => Load("atom-blog-xhtml.xml", "https://blog.example/feed.atom");

    private static string Render(FeedEntry e) =>
        HtmlToMarkdown.Convert(e.ContentHtml, e.ContentBase is { } b ? new Uri(b) : null);

    private static List<string> LinkUrls(string md) =>
        Markdown.Parse(md, MarkdownSourceMap.Pipeline).Descendants().OfType<LinkInline>().Select(l => l.Url!).ToList();

    [Fact]
    public void Reads_feed_metadata()
    {
        var f = Releases();
        Assert.Equal("Release notes from widget", f.Title);
        Assert.Equal("https://github.com/example/widget/releases", f.SiteUrl);
        Assert.Equal("https://github.com/favicon.ico", f.IconUrl);
        Assert.Equal(new DateTime(2026, 10, 5, 9, 30, 0, DateTimeKind.Utc), f.Updated);
    }

    [Fact]
    public void Reads_entries_newest_first_with_utc_dates()
    {
        var f = Releases();
        Assert.Equal(["v2.4.0", "v2.3.1"], f.Entries.Select(e => e.Title));
        var older = f.Entries[1];
        Assert.Equal(new DateTime(2026, 9, 20, 12, 0, 0, DateTimeKind.Utc), older.Updated);
        Assert.Equal(DateTimeKind.Utc, older.Updated.Kind);
        Assert.Equal("hubot", older.Author);
        Assert.Equal("tag:github.com,2008:Repository/1/v2.3.1", older.Id);
        Assert.Equal("https://github.com/example/widget/releases/tag/v2.3.1", older.Url);
    }

    [Fact]
    public void Escaped_html_content_renders_with_resolved_links()
    {
        var e = Releases().Entries[0];
        var md = Render(e);
        var doc = Markdown.Parse(md, MarkdownSourceMap.Pipeline);
        Assert.Equal("What's new", Assert.Single(doc.Descendants().OfType<HeadingBlock>()).Inline!
            .Descendants<LiteralInline>().Aggregate("", (s, l) => s + l.Content));
        Assert.Single(doc.Descendants().OfType<ListBlock>());
        Assert.Contains(doc.Descendants().OfType<CodeInline>(), c => c.Content == "render()");
        Assert.Equal(["https://github.com/example/widget/pull/42"], LinkUrls(md));
        Assert.StartsWith("What's new", e.SummaryText);
    }

    [Fact]
    public void Html_titles_and_xml_base_on_the_feed()
    {
        var f = Blog();
        Assert.Equal("Jane’s blog", f.Title);
        Assert.Equal("https://blog.example/", f.SiteUrl);
        Assert.Equal("https://blog.example/logo.png", f.IconUrl);
    }

    [Fact]
    public void Xhtml_entry_resolves_nested_bases_and_keeps_structure()
    {
        var e = Blog().Entries[0];
        Assert.Equal("Tables & lists", e.Title);
        Assert.Equal("https://blog.example/posts/tables", e.Url);
        Assert.Equal("Jane Doe", e.Author);
        Assert.Equal("A short tour.", e.SummaryText);
        Assert.Equal(new DateTime(2026, 9, 30, 8, 0, 0, DateTimeKind.Utc), e.Published);
        Assert.Equal("https://blog.example/posts/", e.ContentBase);

        var md = Render(e);
        var doc = Markdown.Parse(md, MarkdownSourceMap.Pipeline);
        Assert.Equal(["https://blog.example/posts/part-2", "https://blog.example/img/chart.png"], LinkUrls(md));
        Assert.Contains("1 < 2.", string.Concat(doc.Descendants<LiteralInline>().Select(l => l.Content.ToString())));
        Assert.Single(doc.Descendants().OfType<Markdig.Extensions.Tables.Table>());
        var list = Assert.Single(doc.Descendants().OfType<ListBlock>());
        Assert.True(list.IsOrdered);
        Assert.Equal("3", list.OrderedStart);
        var code = Assert.Single(doc.Descendants().OfType<FencedCodeBlock>());
        Assert.Contains("if (x < 2) { }", code.Lines.ToString());
        Assert.DoesNotContain(doc.Descendants().OfType<LinkInline>(), l => l.IsImage);
    }

    [Fact]
    public void Missing_id_falls_back_to_the_link_and_text_content_keeps_paragraphs()
    {
        var e = Blog().Entries[1];
        Assert.Equal("https://blog.example/posts/plain", e.Id);
        Assert.Equal("https://blog.example/posts/plain", e.Url);   // rel="related" skipped
        var md = Render(e);
        var doc = Markdown.Parse(md, MarkdownSourceMap.Pipeline);
        Assert.Equal(2, doc.OfType<ParagraphBlock>().Count());
        Assert.Empty(doc.Descendants().OfType<EmphasisInline>());   // "<b>" in a text construct is literal text
        Assert.Contains("<b>not bold</b>", string.Concat(doc.Descendants<LiteralInline>().Select(l => l.Content.ToString())));
    }

    [Fact]
    public void Bad_updated_falls_back_to_published_and_out_of_line_content_becomes_the_link()
    {
        var e = Blog().Entries[2];
        Assert.Equal(new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc), e.Updated);
        Assert.Equal("https://media.example/video.mp4", e.Url);
        Assert.Null(e.ContentHtml);
        Assert.Null(e.SummaryText);
    }

    [Fact]
    public void Malformed_entry_does_not_cost_the_rest()
    {
        var xml = "<feed xmlns=\"http://www.w3.org/2005/Atom\"><title>t</title>" +
                  "<entry><id>ok1</id><title>first</title><updated>2026-01-02T00:00:00Z</updated></entry>" +
                  "<entry><id></id><title/><updated/></entry>" +
                  "<entry><id>ok2</id><title>second</title><updated>2026-01-01T00:00:00Z</updated></entry></feed>";
        var r = FeedParser.Parse(xml, new Uri("https://x.example/f"), Now);
        Assert.Contains(r.Doc!.Entries, e => e.Title == "first");
        Assert.Contains(r.Doc.Entries, e => e.Title == "second");
    }

    [Fact]
    public void Duplicate_ids_keep_one_entry()
    {
        var xml = "<feed xmlns=\"http://www.w3.org/2005/Atom\"><title>t</title>" +
                  "<entry><id>dup</id><title>a</title><updated>2026-01-02T00:00:00Z</updated></entry>" +
                  "<entry><id>dup</id><title>b</title><updated>2026-01-01T00:00:00Z</updated></entry></feed>";
        Assert.Single(FeedParser.Parse(xml, new Uri("https://x.example/f"), Now).Doc!.Entries);
    }

    [Fact]
    public void Untitled_feed_uses_the_host()
    {
        var r = FeedParser.Parse("<feed xmlns=\"http://www.w3.org/2005/Atom\"/>", new Uri("https://news.example/atom"), Now);
        Assert.Equal("news.example", r.Doc!.Title);
        Assert.Empty(r.Doc.Entries);
    }

    [Theory]
    [InlineData("<rss version=\"2.0\"><channel/></rss>", "RSS")]
    [InlineData("<feed xmlns=\"http://purl.org/atom/ns#\"/>", "unsupported Atom")]
    [InlineData("<html><body>hi</body></html>", "web page")]
    public void Unsupported_documents_say_why(string xml, string reason) =>
        Assert.Contains(reason, FeedParser.Parse(xml, new Uri("https://x.example/f"), Now).Error);
}
