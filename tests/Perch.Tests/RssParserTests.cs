using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Perch.Data;
using Perch.Feeds;
using Xunit;

namespace Perch.Tests;

/// <summary>
/// RSS through <see cref="FeedParser"/> (docs/feeds-plan.md, F6): RSS 2.0 (a WordPress-shaped blog with
/// <c>content:encoded</c>, <c>dc:creator</c>, non-permalink guids and messy dates) and RSS 1.0 / RDF, both landing in
/// the same model as Atom. RSS-specific hostile cases live in <see cref="FeedInjectionTests"/>.
/// </summary>
public class RssParserTests
{
    private static readonly DateTime Now = new(2026, 10, 7, 12, 0, 0, DateTimeKind.Utc);

    private static FeedDoc Load(string name, string url)
    {
        using var fs = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "fixtures", "feeds", name));
        var r = FeedParser.Parse(fs, new Uri(url), Now);
        Assert.Null(r.Error);
        return r.Doc!;
    }

    private static FeedDoc Blog() => Load("rss2-blog.xml", "https://blog.example/feed/");
    private static FeedDoc Rdf() => Load("rss1-rdf.xml", "https://news.example/index.rdf");

    private static DateTime Utc(int y, int mo, int d, int h = 0, int mi = 0, int s = 0) => new(y, mo, d, h, mi, s, DateTimeKind.Utc);

    [Fact]
    public void Reads_rss2_channel_metadata()
    {
        var f = Blog();
        Assert.Equal("Widget Weekly & Friends", f.Title);   // double-escaped entity decoded exactly once
        Assert.Equal("https://blog.example/", f.SiteUrl);
        Assert.Equal("https://blog.example/icon-32.png", f.IconUrl);
        Assert.Equal(Utc(2026, 10, 5, 9, 30), f.Updated);
    }

    [Fact]
    public void Content_encoded_wins_and_resolves_against_the_site()
    {
        var e = Blog().Entries.Single(x => x.Title == "Widget 3.0 is out");
        Assert.Equal("https://blog.example/?p=301", e.Id);                   // a non-permalink guid is still the id
        Assert.Equal("https://blog.example/2026/10/widget-3/", e.Url);       // …but not the link
        Assert.Equal("Jane Doe", e.Author);
        Assert.Equal(Utc(2026, 10, 5, 9), e.Published);
        Assert.Contains("<strong>full</strong>", e.ContentHtml);
        Assert.Equal("Short summary of 3.0 …", e.SummaryText);

        var md = FeedCard.BodyMarkdown(e);
        var link = Markdown.Parse(md, MarkdownSourceMap.Pipeline).Descendants().OfType<LinkInline>().Single();
        Assert.Equal("https://blog.example/docs/upgrade", link.Url);
    }

    [Fact]
    public void Messy_dates_and_email_authors_are_read()
    {
        var e = Blog().Entries.Single(x => x.Title.StartsWith("Wrong weekday"));
        Assert.Equal(Utc(2026, 10, 4, 12, 15), e.Published);   // "Fri, 4 Oct 26 8:15 EDT": weekday wrong, ignored
        Assert.Equal("Bob Smith", e.Author);
        Assert.Equal("Plain escaped HTML description.", e.SummaryText);
        Assert.Contains("<em>escaped</em>", e.ContentHtml);   // description is HTML; rendered by the sanitizer
    }

    [Fact]
    public void A_permalink_guid_stands_in_for_a_missing_link()
    {
        var e = Blog().Entries.Single(x => x.Title == "Permalink guid, no link");
        Assert.Equal("https://blog.example/2026/09/permalink/", e.Url);
        Assert.Equal(Utc(2026, 9, 26, 23, 59, 59), e.Updated);
    }

    [Fact]
    public void Untitled_undated_items_survive()
    {
        var e = Blog().Entries.Single(x => x.Id == "tag:blog.example,2026:untitled");
        Assert.Equal("An item with no title and an unparseable date.", e.Title);
        Assert.Null(e.Url);
        Assert.Null(e.Published);
        Assert.Equal(Now, e.Updated);
    }

    [Fact]
    public void Entries_are_newest_first()
    {
        Assert.Equal(
            ["tag:blog.example,2026:untitled", "https://blog.example/?p=301", "https://blog.example/2026/10/odd-date/",
             "https://blog.example/2026/09/permalink/"],
            Blog().Entries.Select(e => e.Id));
    }

    [Fact]
    public void Reads_rss1_rdf()
    {
        var f = Rdf();
        Assert.Equal("Example News", f.Title);
        Assert.Equal("https://news.example/", f.SiteUrl);
        Assert.Equal("https://news.example/logo.png", f.IconUrl);
        Assert.Equal(["https://news.example/b", "https://news.example/a"], f.Entries.Select(e => e.Id));

        var a = f.Entries[1];
        Assert.Equal("First headline", a.Title);
        Assert.Equal("https://news.example/a", a.Url);
        Assert.Equal("Ada", a.Author);
        Assert.Equal(Utc(2026, 10, 6, 8), a.Published);   // dc:date with an offset
        Assert.Equal("First story.", a.SummaryText);
    }

    [Fact]
    public void Rss_091_items_beside_the_channel_are_read_too()
    {
        var r = FeedParser.Parse("<rss version=\"0.91\"><channel><title>Old</title><item><title>In</title><link>https://o.example/1</link></item></channel>" +
                                 "<item><title>Beside</title><link>https://o.example/2</link></item></rss>",
            new Uri("https://o.example/rss"), Now);
        Assert.Equal(["In", "Beside"], r.Doc!.Entries.Select(e => e.Title));
    }

    [Theory]
    [InlineData("Tue, 10 Jun 2003 04:00:00 GMT", 2003, 6, 10, 4, 0)]
    [InlineData("Sun, 6 Oct 2026 09:00:00 +0200", 2026, 10, 6, 7, 0)]
    [InlineData("06 Oct 2026 09:00 -05:30", 2026, 10, 6, 14, 30)]
    [InlineData("Oct 6 2026 09:00:00 PST", 2026, 10, 6, 17, 0)]
    [InlineData("Wed, 07 Oct 2026 12:00:00", 2026, 10, 7, 12, 0)]
    [InlineData("Wednesday, 07 October 2026 12:00:00 CEST", 2026, 10, 7, 10, 0)]
    [InlineData("7 Oct 2026", 2026, 10, 7, 0, 0)]
    [InlineData("Mon, 05 Oct 99 10:00:00 Z", 1999, 10, 5, 10, 0)]
    public void Rfc822_reads_what_feeds_write(string s, int y, int mo, int d, int h, int mi) =>
        Assert.Equal(Utc(y, mo, d, h, mi), FeedParser.Rfc822(s));

    [Theory]
    [InlineData("2026-10-07T12:00:00Z")]
    [InlineData("31 Feb 2026 10:00 GMT")]
    [InlineData("7 Foo 2026 10:00 GMT")]
    [InlineData("7 Oct 2026 10:00 +9999")]
    [InlineData("7 Oct 2026 10:00 Mars")]
    [InlineData("Tuesday")]
    [InlineData("")]
    public void Rfc822_refuses_the_rest(string s) => Assert.Null(FeedParser.Rfc822(s));
}
