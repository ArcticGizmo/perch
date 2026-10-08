using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Perch.Data;
using Perch.Feeds;
using Xunit;

namespace Perch.Tests;

/// <summary>
/// The story card's body (<see cref="FeedCard"/>) and the render guard from docs/feeds-plan.md §5: the card is built
/// without a file-reference context (pinned by <c>UiConventionTests</c>), and nothing a feed emits is a link the
/// renderer would treat as a local path — so even a future change that wired file refs in couldn't arm one.
/// </summary>
public class FeedCardTests
{
    private const string Base = "https://example.com/blog/";

    private static FeedEntry Entry(string? html, string? summary = null, string? contentBase = Base) =>
        new("e1", "Title", "https://example.com/blog/1", null, null, FeedTestSupport.T0, html, contentBase, summary);

    [Theory]
    [InlineData(@"<a href=""C:\Windows\System32\calc.exe"">drive</a>")]
    [InlineData(@"<a href=""\\host\share\x.exe"">unc</a>")]
    [InlineData(@"<a href=""file:///C:/Windows/win.ini"">file</a>")]
    [InlineData(@"<a href=""file://host/share/x"">file unc</a>")]
    [InlineData(@"<a href=""/etc/passwd"">rooted</a>")]
    [InlineData(@"<a href=""..\..\secrets.txt"">relative</a>")]
    [InlineData(@"<a href=""ms-settings:privacy"">ms-settings</a>")]
    [InlineData(@"<img src=""C:\Users\me\photo.png"" alt=""pic"">")]
    public void Path_like_links_never_reach_the_renderer_as_file_targets(string html)
    {
        foreach (var @base in new[] { Base, null })
        {
            var md = FeedCard.BodyMarkdown(Entry("<p>" + html + "</p>", contentBase: @base));
            foreach (var node in Markdown.Parse(md, MarkdownSourceMap.Pipeline).Descendants())
            {
                string? url = node switch { LinkInline l => l.Url, AutolinkInline a => a.Url, _ => null };
                if (url is null) continue;
                Assert.Null(OpenTargets.LinkFilePath(url));
                Assert.NotNull(OpenTargets.WebUrl(url));
            }
        }
    }

    [Fact]
    public void Content_wins_over_the_summary()
    {
        var md = FeedCard.BodyMarkdown(Entry("<p>From <strong>content</strong></p>", summary: "From summary"));
        Assert.Contains("**content**", md);
        Assert.DoesNotContain("summary", md);
    }

    [Fact]
    public void Summary_is_the_fallback_and_renders_literally()
    {
        var md = FeedCard.BodyMarkdown(Entry(null, summary: "[click](javascript:alert(1)) # not a heading <b>x</b>"));
        var doc = Markdown.Parse(md, MarkdownSourceMap.Pipeline);
        Assert.Empty(doc.Descendants().OfType<LinkInline>());
        Assert.Empty(doc.Descendants().OfType<HeadingBlock>());
        Assert.Empty(doc.Descendants().OfType<HtmlInline>());
        var text = string.Concat(doc.Descendants().OfType<LiteralInline>().Select(l => l.Content.ToString()));
        Assert.Equal("[click](javascript:alert(1)) # not a heading <b>x</b>", text);
    }

    [Fact]
    public void Empty_content_and_summary_give_an_empty_body()
    {
        Assert.Equal("", FeedCard.BodyMarkdown(Entry(null)));
        Assert.Equal("", FeedCard.BodyMarkdown(Entry("<script>alert(1)</script>  ", summary: " ")));
    }

    [Fact]
    public void Relative_links_resolve_against_the_content_base()
    {
        var md = FeedCard.BodyMarkdown(Entry("<p><a href=\"post-2\">next</a></p>"));
        var link = Markdown.Parse(md, MarkdownSourceMap.Pipeline).Descendants().OfType<LinkInline>().Single();
        Assert.Equal("https://example.com/blog/post-2", link.Url);
    }
}
