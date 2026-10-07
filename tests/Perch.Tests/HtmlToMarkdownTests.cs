using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Perch.Data;
using Perch.Feeds;
using Xunit;

namespace Perch.Tests;

/// <summary>
/// The structure <see cref="HtmlToMarkdown"/> keeps for ordinary feed content. (What it refuses is covered by
/// <see cref="FeedInjectionTests"/>.)
/// </summary>
public class HtmlToMarkdownTests
{
    private static readonly Uri Base = new("https://site.example/posts/1");

    private static MarkdownDocument Md(string html) =>
        Markdown.Parse(HtmlToMarkdown.Convert(html, Base), MarkdownSourceMap.Pipeline);

    private static string Text(MarkdownObject o) =>
        string.Concat((o is LeafBlock { Inline: { } inl } ? inl.Descendants() : o.Descendants())
            .OfType<LiteralInline>().Select(l => l.Content.ToString()));

    [Fact]
    public void Paragraphs_and_inline_emphasis()
    {
        var doc = Md("<p>Hello <strong>bold</strong> and <em>soft</em></p><div>second</div>");
        Assert.Equal(2, doc.OfType<ParagraphBlock>().Count());
        var em = doc.Descendants().OfType<EmphasisInline>().ToList();
        Assert.Contains(em, e => e.DelimiterCount == 2 && Text(e) == "bold");
        Assert.Contains(em, e => e.DelimiterCount == 1 && Text(e) == "soft");
    }

    [Fact]
    public void Headings_keep_their_level()
    {
        var doc = Md("<h1>One</h1><h3>Three <i>x</i></h3>");
        var h = doc.OfType<HeadingBlock>().ToList();
        Assert.Equal([1, 3], h.Select(x => x.Level));
        Assert.Equal("Three x", Text(h[1]));
    }

    [Fact]
    public void Relative_links_resolve_and_mailto_is_allowed()
    {
        var links = Md("<a href=\"../about\">about</a> <a href=\"mailto:jane@site.example\">mail</a>")
            .Descendants().OfType<LinkInline>().Select(l => l.Url).ToList();
        Assert.Equal(["https://site.example/about", "mailto:jane@site.example"], links);
    }

    [Fact]
    public void Empty_link_text_shows_the_host()
    {
        var link = Md("<a href=\"https://www.other.example/x\"></a>").Descendants().OfType<LinkInline>().Single();
        Assert.Equal("other.example", Text(link));
    }

    [Fact]
    public void Images_become_link_stubs_and_never_images()
    {
        var doc = Md("<p><img src=\"/a.png\" alt=\"A cat\"> <a href=\"/p\"><img src=\"/b.png\"></a></p>");
        var links = doc.Descendants().OfType<LinkInline>().ToList();
        Assert.All(links, l => Assert.False(l.IsImage));
        Assert.Equal(["https://site.example/a.png", "https://site.example/p"], links.Select(l => l.Url));
        Assert.Contains("🖼 A cat", Text(doc));
        Assert.Contains("🖼 image", Text(links[1]));   // an image inside a link is a label, not a second link
    }

    [Fact]
    public void Nested_lists_and_blockquotes()
    {
        var doc = Md("<ul><li>a<ul><li>a1</li></ul></li><li>b</li></ul><blockquote><p>q1</p><p>q2</p></blockquote>");
        var outer = doc.OfType<ListBlock>().Single();
        Assert.Equal(2, outer.Count);
        Assert.Single(outer.Descendants().OfType<ListBlock>());   // the nested one
        var quote = doc.OfType<QuoteBlock>().Single();
        Assert.Equal(2, quote.OfType<ParagraphBlock>().Count());
    }

    [Fact]
    public void Implicit_closes_follow_html()
    {
        var doc = Md("<ul><li>one<li>two</ul><p>a<p>b");
        Assert.Equal(2, doc.OfType<ListBlock>().Single().Count);
        Assert.Equal(2, doc.OfType<ParagraphBlock>().Count());
    }

    [Fact]
    public void Line_breaks_are_hard_breaks()
    {
        var doc = Md("<p>one<br>two</p>");
        Assert.Contains(doc.Descendants().OfType<LineBreakInline>(), b => b.IsHard);
    }

    [Fact]
    public void Tables_pad_ragged_rows()
    {
        var doc = Md("<table><thead><tr><th>A</th><th>B</th></tr></thead><tbody><tr><td>1</td></tr></tbody></table>");
        var table = doc.OfType<Markdig.Extensions.Tables.Table>().Single();
        Assert.Equal(2, table.Count);
    }

    [Fact]
    public void Pre_keeps_whitespace_and_inner_markup_text()
    {
        var doc = Md("<pre><code>  indented\n\n  <b>bold</b>  spaced</code></pre>");
        var code = doc.OfType<FencedCodeBlock>().Single().Lines.ToString();
        Assert.Contains("  indented", code);
        Assert.Contains("  bold  spaced", code);
    }

    [Fact]
    public void Entities_and_whitespace()
    {
        var doc = Md("<p>Caf&eacute;   &mdash;\n\tok &#x1F44D;</p>");
        Assert.Equal("Café — ok 👍", Text(doc));
    }

    [Fact]
    public void Oversized_input_is_cut_with_a_note()
    {
        var md = HtmlToMarkdown.Convert("<p>" + new string('a', HtmlToMarkdown.MaxInput + 100) + "</p>", Base);
        Assert.Contains("continued in the browser", Text(Markdown.Parse(md, MarkdownSourceMap.Pipeline)));
    }

    [Fact]
    public void Empty_and_whitespace_input()
    {
        Assert.Equal("", HtmlToMarkdown.Convert(null, Base));
        Assert.Equal("", HtmlToMarkdown.Convert("  \n ", Base));
        Assert.Equal("", HtmlToMarkdown.Convert("<p></p><div> </div>", Base));
    }
}
