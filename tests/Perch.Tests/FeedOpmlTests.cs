using System.Text;
using Perch.Feeds;
using Xunit;

namespace Perch.Tests;

/// <summary>OPML import and export (<see cref="FeedOpml"/>): a realistic export from another reader, the hostile
/// cases (the file is as untrusted as a feed), and a round trip.</summary>
public class FeedOpmlTests
{
    private static readonly IReadOnlyList<FeedSubscription> None = [];

    // The shape Feedly / Inoreader / NetNewsWire write: folders of outlines, text + title, type="rss", htmlUrl.
    private const string ReaderExport = """
        <?xml version="1.0" encoding="UTF-8"?>
        <opml version="1.0">
          <head><title>My subscriptions</title></head>
          <body>
            <outline text="Tech" title="Tech">
              <outline type="rss" text=".NET Blog" title=".NET Blog" xmlUrl="https://devblogs.microsoft.com/dotnet/feed/" htmlUrl="https://devblogs.microsoft.com/dotnet/"/>
              <outline type="rss" text="Avalonia" xmlUrl="https://avaloniaui.net/blog/rss.xml"/>
              <outline text="Nested">
                <outline type="rss" title="xkcd" text="xkcd.com" xmlUrl="https://xkcd.com/atom.xml"/>
              </outline>
            </outline>
            <outline type="rss" text="No title attr &amp; an entity" xmlUrl="https://news.example/feed"/>
            <outline text="Just a note, not a feed"/>
          </body>
        </opml>
        """;

    [Fact]
    public void Reads_every_feed_however_folders_nest()
    {
        var r = FeedOpml.Parse(ReaderExport, None);
        Assert.Null(r.Error);
        Assert.Equal(
            ["https://devblogs.microsoft.com/dotnet/feed/", "https://avaloniaui.net/blog/rss.xml", "https://xkcd.com/atom.xml", "https://news.example/feed"],
            r.Feeds.Select(f => f.Url.AbsoluteUri));
        Assert.Equal([".NET Blog", "Avalonia", "xkcd", "No title attr & an entity"], r.Feeds.Select(f => f.Title));
        Assert.Equal(0, r.Skipped);
    }

    [Fact]
    public void Feeds_already_followed_or_listed_twice_drop_out()
    {
        var existing = new List<FeedSubscription> { new() { Url = "https://xkcd.com/atom.xml" } };
        var twice = ReaderExport.Replace("<outline text=\"Just a note",
            "<outline type=\"rss\" text=\"again\" xmlUrl=\"https://news.example/feed/\"/><outline text=\"Just a note");
        var r = FeedOpml.Parse(twice, existing);
        Assert.Equal(1, r.AlreadyFollowed);
        Assert.Equal(3, r.Feeds.Count);
        Assert.Single(r.Feeds, f => f.Url.Host == "news.example");
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("file:///C:/Windows/win.ini")]
    [InlineData("\\\\host\\share\\feed.xml")]
    [InlineData("feed.xml")]
    [InlineData("https://u:p@evil.example/feed")]
    [InlineData("ms-settings:privacy")]
    public void Unsafe_addresses_are_skipped(string url)
    {
        var r = FeedOpml.Parse($"<opml version=\"2.0\"><body><outline xmlUrl=\"{System.Net.WebUtility.HtmlEncode(url)}\" text=\"x\"/></body></opml>", None);
        Assert.Empty(r.Feeds);
        Assert.Equal(1, r.Skipped);
    }

    [Fact]
    public void A_doctype_is_refused()
    {
        var r = FeedOpml.Parse("<?xml version=\"1.0\"?><!DOCTYPE opml [<!ENTITY x SYSTEM \"file:///C:/Windows/win.ini\">]>" +
                               "<opml><body><outline text=\"&x;\" xmlUrl=\"https://a.example/f\"/></body></opml>", None);
        Assert.NotNull(r.Error);
        Assert.Empty(r.Feeds);
    }

    [Fact]
    public void Deep_nesting_size_and_count_are_bounded()
    {
        var deep = new StringBuilder("<opml><body>");
        for (int i = 0; i < 200; i++) deep.Append("<outline>");
        for (int i = 0; i < 200; i++) deep.Append("</outline>");
        deep.Append("</body></opml>");
        Assert.Contains("nested", FeedOpml.Parse(deep.ToString(), None).Error);

        Assert.Contains("too large", FeedOpml.Parse(new MemoryStream(new byte[FeedOpml.MaxBytes + 1]), None).Error);

        var many = new StringBuilder("<opml><body>");
        for (int i = 0; i < FeedOpml.MaxFeeds + 20; i++) many.Append($"<outline xmlUrl=\"https://f{i}.example/feed\"/>");
        many.Append("</body></opml>");
        var r = FeedOpml.Parse(many.ToString(), None);
        Assert.Equal(FeedOpml.MaxFeeds, r.Feeds.Count);
        Assert.Equal(20, r.Skipped);
    }

    [Fact]
    public void Titles_are_cleaned()
    {
        var r = FeedOpml.Parse("<opml><body><outline xmlUrl=\"https://a.example/f\" title=\"Real&#x202E;\nPerch: update now&#x200B;\"/></body></opml>", None);
        Assert.Equal("Real Perch: update now", r.Feeds.Single().Title);
    }

    [Theory]
    [InlineData("<html><body>not opml</body></html>")]
    [InlineData("<opml/>")]
    [InlineData("not xml at all")]
    [InlineData("")]
    public void Non_opml_files_say_so(string text) => Assert.NotNull(FeedOpml.Parse(text, None).Error);

    [Fact]
    public void Export_round_trips_and_escapes()
    {
        var subs = new List<FeedSubscription>
        {
            new() { Url = "https://xkcd.com/atom.xml" },
            new() { Url = "https://a.example/feed?x=1&y=2", Enabled = false },
        };
        var xml = FeedOpml.Export(subs, s => s.Url.Contains("xkcd") ? "xkcd \"<comics>\" & more" : "A", FeedTestSupport.T0);
        Assert.StartsWith("<?xml version=\"1.0\" encoding=\"utf-8\"?>", xml);
        Assert.Contains("&amp;y=2", xml);

        var back = FeedOpml.Parse(xml, None);
        Assert.Null(back.Error);
        Assert.Equal(["https://xkcd.com/atom.xml", "https://a.example/feed?x=1&y=2"], back.Feeds.Select(f => f.Url.AbsoluteUri));
        Assert.Equal("xkcd \"<comics>\" & more", back.Feeds[0].Title);
    }
}
