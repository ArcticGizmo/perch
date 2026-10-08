using Perch.Feeds;
using Xunit;

namespace Perch.Tests;

/// <summary>Feed autodiscovery from a web page (<see cref="FeedDiscovery"/>). The hostile cases live in
/// <see cref="FeedInjectionTests"/>.</summary>
public class FeedDiscoveryTests
{
    private static readonly Uri Page = new("https://blog.example/posts/hello");

    [Fact]
    public void Finds_atom_and_rss_links_in_document_order()
    {
        var html = "<!DOCTYPE html><html><head><title>Blog</title>" +
            "<LINK REL=\"Alternate\" TYPE=\"application/rss+xml\" TITLE=\"Blog &raquo; Feed\" HREF=\"/feed/\">" +
            "<link rel='alternate' type='application/atom+xml; charset=utf-8' href='https://blog.example/feed/atom/' />" +
            "<link rel=alternate type=application/rdf+xml href=/index.rdf>" +
            "</head><body>…</body></html>";
        var found = FeedDiscovery.Find(html, Page);
        Assert.Equal(["https://blog.example/feed/", "https://blog.example/feed/atom/", "https://blog.example/index.rdf"],
            found.Select(c => c.Url.AbsoluteUri));
        Assert.Equal(["RSS", "Atom", "RSS"], found.Select(c => c.Format));
        Assert.Equal("Blog » Feed", found[0].Title);
        Assert.Null(found[1].Title);
    }

    [Fact]
    public void A_base_href_rebases_relative_links()
    {
        var html = "<head><base href=\"https://cdn.example/site/\"><link rel=\"alternate\" type=\"application/rss+xml\" href=\"feed.xml\"></head>";
        Assert.Equal("https://cdn.example/site/feed.xml", FeedDiscovery.Find(html, Page).Single().Url.AbsoluteUri);
    }

    [Fact]
    public void Hrefs_are_entity_decoded_once_and_duplicates_dropped()
    {
        var html = "<link rel=\"alternate\" type=\"application/rss+xml\" href=\"/feed?a=1&amp;b=2\">" +
                   "<link rel=\"alternate\" type=\"application/rss+xml\" href=\"/feed?a=1&#38;b=2\">";
        Assert.Equal("https://blog.example/feed?a=1&b=2", FeedDiscovery.Find(html, Page).Single().Url.AbsoluteUri);
    }

    [Fact]
    public void Commented_out_links_and_non_feed_links_are_ignored()
    {
        var html = "<!-- <link rel=\"alternate\" type=\"application/rss+xml\" href=\"/old\"> -->" +
                   "<link rel=\"icon\" href=\"/favicon.ico\"><link rel=\"alternate\" hreflang=\"fr\" href=\"/fr/\">" +
                   "<linkage rel=\"alternate\" type=\"application/rss+xml\" href=\"/nope\">";
        Assert.Empty(FeedDiscovery.Find(html, Page));
    }

    [Fact]
    public void Candidates_are_capped_and_garbage_never_throws()
    {
        var many = string.Concat(Enumerable.Range(0, 50).Select(i =>
            $"<link rel=\"alternate\" type=\"application/rss+xml\" href=\"/f{i}\">"));
        Assert.Equal(FeedDiscovery.MaxCandidates, FeedDiscovery.Find(many, Page).Count);
        Assert.Empty(FeedDiscovery.Find("<link rel=\"alternate\" type=\"application/rss+xml\" href=\"/unterminated", Page));
        Assert.Empty(FeedDiscovery.Find("<<<<link", Page));
        Assert.Empty(FeedDiscovery.Find(null, Page));
    }
}
