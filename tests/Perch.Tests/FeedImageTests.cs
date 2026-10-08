using System.Net;
using Perch.Feeds;
using Xunit;

namespace Perch.Tests;

/// <summary>
/// Opt-in post images (docs/feeds-plan.md, F7): block-level images lifted out of the body in order, captions from
/// <c>title</c> (XKCD), the fetcher's image acceptance, and the XKCD suggestion. The hostile cases live in
/// <see cref="FeedInjectionTests"/>.
/// </summary>
public class FeedImageTests
{
    private static readonly Uri Base = new("https://site.example/posts/");

    private static FeedEntry Entry(string html) =>
        new("e", "t", null, null, null, FeedTestSupport.T0, html, Base.AbsoluteUri, "summary text");

    [Fact]
    public void Xkcd_comic_becomes_an_image_with_its_hover_text_as_the_caption()
    {
        // The shape of an xkcd.com/atom.xml summary.
        var parts = FeedCard.Body(Entry(
            "<img src=\"https://imgs.xkcd.com/comics/the_comic.png\" title=\"The punchline &amp; more.\" alt=\"The Comic\" />"),
            images: true);
        var img = Assert.Single(parts).Image!;
        Assert.Equal("https://imgs.xkcd.com/comics/the_comic.png", img.Src.AbsoluteUri);
        Assert.Equal("The Comic", img.Alt);
        Assert.Equal("The punchline & more.", img.Caption);
    }

    [Fact]
    public void Images_keep_their_place_between_text()
    {
        var parts = FeedCard.Body(Entry(
            "<p>Before <img src=\"a.png\" alt=\"A\"> after.</p><figure><img src=\"/b.jpg\"><figcaption>Fig</figcaption></figure><p>End</p>"),
            images: true);
        Assert.Equal(["md", "img", "md", "img", "md"], parts.Select(p => p.Image is null ? "md" : "img"));
        Assert.Equal("Before", parts[0].Markdown);
        Assert.Equal("https://site.example/posts/a.png", parts[1].Image!.Src.AbsoluteUri);
        Assert.Equal("https://site.example/b.jpg", parts[3].Image!.Src.AbsoluteUri);
        Assert.StartsWith("Fig", parts[4].Markdown);
        Assert.EndsWith("End", parts[4].Markdown);
    }

    [Fact]
    public void A_linked_image_shows_and_gives_up_the_link()
    {
        var parts = FeedCard.Body(Entry("<p><a href=\"https://site.example/full.png\"><img src=\"thumb.png\"></a></p>"), images: true);
        Assert.Equal("https://site.example/posts/thumb.png", Assert.Single(parts).Image!.Src.AbsoluteUri);
    }

    [Theory]
    [InlineData("<ul><li><img src=\"x.png\" alt=\"in list\"></li></ul>")]
    [InlineData("<table><tr><td><img src=\"x.png\" alt=\"in table\"></td></tr></table>")]
    [InlineData("<blockquote><img src=\"x.png\" alt=\"in quote\"></blockquote>")]
    [InlineData("<h2><img src=\"x.png\" alt=\"in heading\"></h2>")]
    [InlineData("<p><strong><img src=\"x.png\" alt=\"in bold\"></strong></p>")]
    public void Images_inside_structure_stay_link_stubs(string html)
    {
        var parts = FeedCard.Body(Entry(html), images: true);
        var md = Assert.Single(parts).Markdown!;
        Assert.Contains("🖼", md);
        Assert.Contains("x.png", md);
    }

    [Fact]
    public void Images_off_means_stubs_only()
    {
        var parts = FeedCard.Body(Entry("<img src=\"https://imgs.xkcd.com/comics/c.png\" alt=\"C\">"), images: false);
        var md = Assert.Single(parts).Markdown!;
        Assert.Contains("🖼", md);
    }

    [Fact]
    public void Images_past_the_cap_are_stubs()
    {
        var html = string.Concat(Enumerable.Range(0, 20).Select(i => $"<p><img src=\"/i{i}.png\"></p>"));
        var parts = FeedCard.Body(Entry(html), images: true);
        Assert.Equal(HtmlToMarkdown.MaxImages, parts.Count(p => p.Image is not null));
        Assert.Contains("🖼", parts.Last().Markdown);
    }

    [Fact]
    public void Nothing_to_show_falls_back_to_the_summary()
    {
        var parts = FeedCard.Body(Entry("<script>x</script>"), images: true);
        Assert.Equal("summary text", Assert.Single(parts).Markdown);
    }

    [Fact]
    public void Only_raster_images_within_the_pixel_cap_are_accepted()
    {
        Assert.True(FeedFetcher.IsAcceptableImage(FeedTestSupport.Png(740, 400)));
        Assert.False(FeedFetcher.IsAcceptableImage(FeedTestSupport.Png(20_000, 20_000)));   // a decompression bomb's header
        Assert.False(FeedFetcher.IsAcceptableImage("<svg xmlns=\"http://www.w3.org/2000/svg\"/>"u8.ToArray()));
        Assert.False(FeedFetcher.IsAcceptableImage("GIF"u8.ToArray()));
        Assert.False(FeedFetcher.IsAcceptableImage([]));
    }

    [Fact]
    public async Task Image_fetch_returns_only_acceptable_bytes()
    {
        var png = FeedTestSupport.Png(300, 200);
        var routes = new RouteHandler()
            .On("https://imgs.example/ok.png", _ => Bytes(png, "image/png"))
            .On("https://imgs.example/vector.svg", _ => Bytes("<svg/>"u8.ToArray(), "image/svg+xml"))
            .On("https://imgs.example/lying.png", _ => Bytes("<html>not an image</html>"u8.ToArray(), "image/png"))
            .On("https://imgs.example/huge.png", _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new UnknownLengthContent(FeedFetcher.MaxImageBytes + 1) });
        using var f = new FeedFetcher(routes, routes, FeedTestSupport.Resolver());

        Assert.Equal(png, await f.FetchImageAsync(new Uri("https://imgs.example/ok.png"), null, default));
        Assert.Null(await f.FetchImageAsync(new Uri("https://imgs.example/vector.svg"), null, default));
        Assert.Null(await f.FetchImageAsync(new Uri("https://imgs.example/lying.png"), null, default));
        Assert.Null(await f.FetchImageAsync(new Uri("https://imgs.example/huge.png"), null, default));
        Assert.Null(await f.FetchImageAsync(new Uri("https://imgs.example/missing.png"), null, default));

        static HttpResponseMessage Bytes(byte[] b, string type) => new(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(b) { Headers = { ContentType = new(type) } },
        };
    }

    [Fact]
    public void Xkcd_is_suggested_with_images_until_followed()
    {
        var xkcd = Assert.Single(FeedSuggestions.NotFollowed([]), s => s.Title == "xkcd");
        Assert.True(xkcd.ShowImages);
        Assert.Equal("https://xkcd.com/atom.xml", xkcd.Url);
        Assert.DoesNotContain(FeedSuggestions.NotFollowed([new FeedSubscription { Url = "https://xkcd.com/atom.xml/" }]), s => s.Title == "xkcd");
    }

    [Fact]
    public void Known_feeds_are_safe_unique_and_only_xkcd_turns_images_on()
    {
        Assert.All(FeedSuggestions.All, s => Assert.NotNull(FeedUrl.Safe(s.Url, null)));
        Assert.All(FeedSuggestions.All, s => Assert.StartsWith("https://", s.Url));
        Assert.Equal(FeedSuggestions.All.Count, FeedSuggestions.All.Select(s => s.Url).Distinct().Count());
        Assert.Equal(["xkcd"], FeedSuggestions.All.Where(s => s.ShowImages).Select(s => s.Title));
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("  ", true)]
    [InlineData("SIMON", true)]            // title, any case
    [InlineData("llms", true)]             // blurb
    [InlineData("simonwillison.net", true)] // address
    [InlineData("simon llms", true)]       // every word, anywhere
    [InlineData("simon rust", false)]      // one word missing
    [InlineData("kubernetes", false)]
    public void Known_feed_search_matches_every_word_in_title_blurb_or_address(string? query, bool expected)
    {
        var simon = Assert.Single(FeedSuggestions.All, s => s.Title == "Simon Willison");
        Assert.Equal(expected, FeedSuggestions.Matches(simon, query));
    }

    [Fact]
    public void The_images_switch_survives_clone_and_the_settings_file()
    {
        var sub = new FeedSubscription { Url = "https://xkcd.com/atom.xml", ShowImages = true };
        Assert.True(sub.Clone().ShowImages);
        var json = System.Text.Json.JsonSerializer.Serialize(sub);
        Assert.True(System.Text.Json.JsonSerializer.Deserialize<FeedSubscription>(json)!.ShowImages);
    }
}
