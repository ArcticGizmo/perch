using System.Net;
using System.Text;
using Markdig;
using Markdig.Extensions.Yaml;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Perch.Data;
using Perch.Feeds;
using Xunit;

namespace Perch.Tests;

/// <summary>
/// The hostile corpus for the feeds feature (docs/feeds-plan.md §3.4). Every byte of a feed is attacker-controlled,
/// so each defence layer — the hardened XML read (<see cref="FeedParser"/>), the allowlist HTML conversion
/// (<see cref="HtmlToMarkdown"/>), the URL gate (<see cref="FeedUrl"/>) and the plain-text cleaner
/// (<see cref="FeedText"/>) — has at least one case here that must come out inert: an error result, a refusal or
/// neutralized output, and never a throw or a hang. A change to any of those types has to keep this suite green.
/// </summary>
public class FeedInjectionTests
{
    private static readonly Uri Doc = new("https://example.com/feed.atom");
    private static readonly DateTime Now = new(2026, 10, 7, 12, 0, 0, DateTimeKind.Utc);

    private static string Atom(string entries, string head = "<title>T</title>") =>
        "<?xml version=\"1.0\" encoding=\"utf-8\"?><feed xmlns=\"http://www.w3.org/2005/Atom\">" + head + entries + "</feed>";

    private static string Entry(string inner) =>
        "<entry><id>urn:x:1</id><updated>2026-10-01T00:00:00Z</updated>" + inner + "</entry>";

    private static FeedParseResult Parse(string xml) => FeedParser.Parse(xml, Doc, Now);

    // ── Output inertness: the Markdown we emit, parsed with the exact pipeline MarkdownView uses ──────────────────

    // Asserts the Markdown can only render as text plus web links: no raw HTML, no images, no frontmatter-hidden
    // block, and every link is something OpenTargets would open.
    private static void AssertInert(string md)
    {
        var doc = Markdown.Parse(md, MarkdownSourceMap.Pipeline);
        foreach (var node in doc.Descendants())
        {
            Assert.IsNotType<HtmlBlock>(node);
            Assert.IsNotType<HtmlInline>(node);
            Assert.IsNotType<YamlFrontMatterBlock>(node);
            if (node is LinkInline link)
            {
                Assert.False(link.IsImage, $"image emitted: {link.Url}");
                Assert.NotNull(OpenTargets.WebUrl(link.Url));
                Assert.True(link.Url!.StartsWith("http://") || link.Url.StartsWith("https://") || link.Url.StartsWith("mailto:"),
                    $"unexpected link target: {link.Url}");
            }
            if (node is AutolinkInline auto)
                Assert.NotNull(OpenTargets.WebUrl(auto.Url));
        }
    }

    // The visible text Markdig would render: literals, code, and line breaks — so a test can assert hostile text
    // survives as literal characters rather than structure.
    private static string Visible(string md)
    {
        var doc = Markdown.Parse(md, MarkdownSourceMap.Pipeline);
        var sb = new StringBuilder();
        foreach (var node in doc.Descendants())
        {
            switch (node)
            {
                case LiteralInline lit: sb.Append(lit.Content.ToString()); break;
                case CodeInline code: sb.Append(code.Content); break;
                case LineBreakInline: sb.Append('\n'); break;
                case CodeBlock cb: sb.Append(cb.Lines.ToString()).Append('\n'); break;
                case ParagraphBlock or HeadingBlock: sb.Append('\n'); break;
            }
        }
        return sb.ToString();
    }

    private static List<LinkInline> Links(string md) =>
        Markdown.Parse(md, MarkdownSourceMap.Pipeline).Descendants().OfType<LinkInline>().ToList();

    // ── XML layer ──────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void External_entity_is_refused()
    {
        var r = Parse("<?xml version=\"1.0\"?><!DOCTYPE feed [<!ENTITY x SYSTEM \"file:///C:/Windows/win.ini\">]>" +
                      "<feed xmlns=\"http://www.w3.org/2005/Atom\"><title>&x;</title></feed>");
        Assert.Null(r.Doc);
        Assert.Contains("DOCTYPE", r.Error);
    }

    [Fact]
    public void Remote_entity_is_refused()
    {
        var r = Parse("<?xml version=\"1.0\"?><!DOCTYPE feed SYSTEM \"http://169.254.169.254/latest/meta-data\">" +
                      "<feed xmlns=\"http://www.w3.org/2005/Atom\"><title>t</title></feed>");
        Assert.Null(r.Doc);
        Assert.Contains("DOCTYPE", r.Error);
    }

    [Fact]
    public void Billion_laughs_is_refused_quickly()
    {
        var sb = new StringBuilder("<?xml version=\"1.0\"?><!DOCTYPE lolz [<!ENTITY lol \"lol\">");
        for (int i = 1; i < 10; i++)
            sb.Append($"<!ENTITY lol{i} \"{string.Concat(Enumerable.Repeat($"&lol{(i == 1 ? "" : (i - 1).ToString())};", 10))}\">");
        sb.Append("]><feed xmlns=\"http://www.w3.org/2005/Atom\"><title>&lol9;</title></feed>");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var r = Parse(sb.ToString());
        Assert.Null(r.Doc);
        Assert.True(sw.ElapsedMilliseconds < 2000);
    }

    [Fact]
    public void Parameter_entities_are_refused()
    {
        var r = Parse("<?xml version=\"1.0\"?><!DOCTYPE feed [<!ENTITY % p SYSTEM \"http://evil.example/x.dtd\"> %p;]>" +
                      "<feed xmlns=\"http://www.w3.org/2005/Atom\"/>");
        Assert.Null(r.Doc);
    }

    [Fact]
    public void Any_doctype_is_refused_even_a_harmless_one()
    {
        var r = Parse("<?xml version=\"1.0\"?><!DOCTYPE feed><feed xmlns=\"http://www.w3.org/2005/Atom\"><title>t</title></feed>");
        Assert.Null(r.Doc);
        Assert.Contains("DOCTYPE", r.Error);
    }

    [Fact]
    public void Deep_xml_nesting_is_refused_without_overflow()
    {
        var deep = string.Concat(Enumerable.Repeat("<x>", 5000)) + string.Concat(Enumerable.Repeat("</x>", 5000));
        var r = Parse(Atom(Entry("<title>t</title><content type=\"xhtml\"><div xmlns=\"http://www.w3.org/1999/xhtml\">" + deep + "</div></content>")));
        Assert.Null(r.Doc);
        Assert.Contains("nested", r.Error);
    }

    [Fact]
    public void Oversized_body_is_refused()
    {
        var huge = Atom(Entry("<title>t</title><content>" + new string('a', FeedParser.MaxBytes + 10) + "</content>"));
        var r = FeedParser.Parse(new MemoryStream(Encoding.UTF8.GetBytes(huge)), Doc, Now);
        Assert.Null(r.Doc);
        Assert.Contains("too large", r.Error);
    }

    [Fact]
    public void Processing_instructions_and_comments_are_ignored()
    {
        var r = Parse(Atom(Entry("<?evil run?><!-- <script>alert(1)</script> --><title>Hi<?x y?></title>")));
        Assert.NotNull(r.Doc);
        Assert.Equal("Hi", r.Doc!.Entries.Single().Title);
    }

    [Fact]
    public void Garbage_and_html_pages_are_errors_not_throws()
    {
        Assert.Contains("web page", Parse("<!DOCTYPE html><html><head><title>x</title></head><body>hi</body></html>").Error);
        Assert.NotNull(Parse("\0\0\0 not xml at all <<<").Error);
        Assert.NotNull(Parse("").Error);
        Assert.NotNull(Parse(Atom(Entry("<title>t</title>"))[..60]).Error);   // truncated mid-document
        Assert.NotNull(Parse("<foo/>").Error);
    }

    // ── Content HTML ───────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("<script>alert('pwned')</script>ok", "pwned")]
    [InlineData("<SCRIPT type=text/javascript>pwned()</SCRIPT>ok", "pwned")]
    [InlineData("<style>body{display:none} pwned</style>ok", "pwned")]
    [InlineData("<iframe src=https://evil.example>pwned</iframe>ok", "pwned")]
    [InlineData("<svg onload=alert(1)><text>pwned</text></svg>ok", "pwned")]
    [InlineData("<math><mi>pwned</mi></math>ok", "pwned")]
    [InlineData("<noscript>pwned</noscript>ok", "pwned")]
    [InlineData("<template><p>pwned</p></template>ok", "pwned")]
    [InlineData("<form action=https://evil.example><input value=pwned><button>pwned</button></form>ok", "pwned")]
    [InlineData("<object data=x.swf>pwned</object>ok", "pwned")]
    [InlineData("<textarea>pwned</textarea>ok", "pwned")]
    [InlineData("<title>pwned</title>ok", "pwned")]
    [InlineData("<script>var s='</scr'+'ipt>pwned';</script>ok", "pwned")]
    public void Dangerous_elements_are_dropped_with_their_content(string html, string mustVanish)
    {
        var md = HtmlToMarkdown.Convert(html, Doc);
        AssertInert(md);
        Assert.DoesNotContain(mustVanish, Visible(md));
        Assert.Contains("ok", Visible(md));
    }

    [Fact]
    public void Unclosed_dangerous_element_swallows_the_rest_rather_than_leaking()
    {
        var md = HtmlToMarkdown.Convert("before<script>alert(1)<p>after</p>", Doc);
        AssertInert(md);
        Assert.Contains("before", Visible(md));
        Assert.DoesNotContain("alert", Visible(md));
    }

    [Fact]
    public void Event_handlers_and_style_attributes_never_survive()
    {
        var md = HtmlToMarkdown.Convert(
            "<p onclick=\"alert(1)\" style=\"position:fixed;top:0\" class=\"x\">hello</p><img src=x onerror=alert(1)>", Doc);
        AssertInert(md);
        Assert.DoesNotContain("onclick", md);
        Assert.DoesNotContain("onerror", md);
        Assert.DoesNotContain("position", md);
        Assert.DoesNotContain("alert", md);
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("JaVaScRiPt:alert(1)")]
    [InlineData("  javascript:alert(1)")]
    [InlineData("java\tscript:alert(1)")]
    [InlineData("java&#x0A;script:alert(1)")]
    [InlineData("jav&#x61;script:alert(1)")]
    [InlineData("&#106;&#97;&#118;&#97;&#115;&#99;&#114;&#105;&#112;&#116;&#58;alert(1)")]
    [InlineData("vbscript:msgbox(1)")]
    [InlineData("data:text/html;base64,PHNjcmlwdD5hbGVydCgxKTwvc2NyaXB0Pg==")]
    [InlineData("file:///C:/Windows/System32/calc.exe")]
    [InlineData("\\\\attacker\\share\\x.exe")]
    [InlineData("ms-settings:privacy")]
    [InlineData("search-ms:query=x&crumb=location:\\\\attacker\\s")]
    [InlineData("vscode://file/C:/x")]
    public void Dangerous_link_schemes_become_plain_text(string href)
    {
        var md = HtmlToMarkdown.Convert($"<p><a href=\"{href}\">click me</a></p>", Doc);
        AssertInert(md);
        Assert.Empty(Links(md));
        Assert.Contains("click me", Visible(md));
    }

    [Fact]
    public void Base_tag_cannot_rebase_links()
    {
        var md = HtmlToMarkdown.Convert("<base href=\"https://evil.example/\"><a href=\"/post\">p</a>", Doc);
        Assert.Equal("https://example.com/post", Links(md).Single().Url);
    }

    [Fact]
    public void Deep_html_nesting_is_flattened_not_recursed()
    {
        var html = string.Concat(Enumerable.Repeat("<div><blockquote><ul><li>", 10_000)) + "deep" +
                   string.Concat(Enumerable.Repeat("</li></ul></blockquote></div>", 10_000));
        var md = HtmlToMarkdown.Convert(html, Doc);
        AssertInert(md);
        Assert.Contains("deep", Visible(md));
    }

    [Fact]
    public void Mis_nesting_and_unclosed_tags_stay_inert()
    {
        var md = HtmlToMarkdown.Convert("<b><i>a</b>c</i><p><ul><li>x<li>y</p></ul><a href=https://a.example>u<a href=https://b.example>v", Doc);
        AssertInert(md);
        var text = Visible(md);
        foreach (var s in new[] { "a", "c", "x", "y", "u", "v" }) Assert.Contains(s, text);
    }

    [Fact]
    public void Entities_decode_exactly_once()
    {
        var md = HtmlToMarkdown.Convert("<p>&amp;lt;script&amp;gt;alert(1)&amp;lt;/script&amp;gt;</p>", Doc);
        AssertInert(md);
        Assert.Contains("&lt;script&gt;", Visible(md));
    }

    [Fact]
    public void Encoded_tags_render_as_literal_text()
    {
        var md = HtmlToMarkdown.Convert("<p>&lt;img src=x onerror=alert(1)&gt;</p>", Doc);
        AssertInert(md);
        Assert.Contains("<img src=x onerror=alert(1)>", Visible(md));
    }

    // ── Markdown injection ─────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("[click](javascript:alert(1))")]
    [InlineData("[click](https://evil.example)")]
    [InlineData("![](https://tracker.example/pixel.gif)")]
    [InlineData("<b>raw</b>")]
    [InlineData("<https://evil.example>")]
    [InlineData("| a | b |\n|---|---|\n| c | d |")]
    [InlineData("# Heading")]
    [InlineData("`code`")]
    [InlineData("***")]
    [InlineData("---")]
    [InlineData("- [ ] task")]
    [InlineData("1. item")]
    [InlineData("> quote")]
    [InlineData("    indented code")]
    [InlineData("&lt;b&gt;")]
    [InlineData("\\*not em\\*")]
    [InlineData("~~strike~~ ==mark== ^sup^")]
    [InlineData("[ref]: https://evil.example")]
    public void Markdown_in_feed_text_stays_literal(string text)
    {
        var html = "<p>" + WebUtility.HtmlEncode(text) + "</p>";
        var md = HtmlToMarkdown.Convert(html, Doc);
        AssertInert(md);
        Assert.Empty(Links(md));
        var doc = Markdown.Parse(md, MarkdownSourceMap.Pipeline);
        Assert.All(doc, b => Assert.IsType<ParagraphBlock>(b));   // no headings, lists, tables, quotes or code blocks
        Assert.Equal(System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ").Trim(), Visible(md).Trim());
    }

    [Fact]
    public void Frontmatter_cannot_hide_content()
    {
        var md = HtmlToMarkdown.Convert("<hr><p>hidden?</p><hr><p>after</p>", Doc);
        AssertInert(md);
        Assert.Contains("hidden?", Visible(md));
    }

    [Fact]
    public void Url_with_parens_and_spaces_cannot_close_its_link_early()
    {
        var md = HtmlToMarkdown.Convert("<a href=\"https://example.com/a) [x](javascript:alert(1)\">t</a>", Doc);
        AssertInert(md);
        var link = Links(md).Single();
        Assert.StartsWith("https://example.com/a", link.Url);
    }

    [Fact]
    public void Code_span_cannot_escape_its_fence()
    {
        var md = HtmlToMarkdown.Convert("<code>a `` b ``` [x](javascript:y) <b>c</b></code>", Doc);
        AssertInert(md);
        Assert.Empty(Links(md));
        var code = Markdown.Parse(md, MarkdownSourceMap.Pipeline).Descendants().OfType<CodeInline>().Single();
        Assert.Contains("``` [x](javascript:y)", code.Content);
    }

    [Fact]
    public void Pre_block_cannot_escape_its_fence()
    {
        var md = HtmlToMarkdown.Convert("<pre>line\n```\n# not a heading\n<b>x</b>\n````</pre><p>after</p>", Doc);
        AssertInert(md);
        var doc = Markdown.Parse(md, MarkdownSourceMap.Pipeline);
        Assert.Empty(doc.Descendants().OfType<HeadingBlock>());
        var block = doc.Descendants().OfType<FencedCodeBlock>().Single();
        Assert.Contains("# not a heading", block.Lines.ToString());
        Assert.Contains("after", Visible(md));
    }

    // ── URLs ───────────────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("https://bank.example@evil.example/")]
    [InlineData("https://user:pass@evil.example/")]
    public void Userinfo_urls_are_refused(string url) => Assert.Null(FeedUrl.Safe(url, Doc));

    [Fact]
    public void Protocol_relative_resolves_against_the_document_scheme()
    {
        Assert.Equal("https://cdn.example/x.png", FeedUrl.Safe("//cdn.example/x.png", Doc)!.AbsoluteUri);
    }

    [Fact]
    public void Relative_urls_need_a_web_base()
    {
        Assert.Null(FeedUrl.Safe("/post", null));
        Assert.Null(FeedUrl.Safe("/post", new Uri("file:///C:/feeds/")));
        Assert.Equal("https://example.com/post", FeedUrl.Safe("/post", Doc)!.AbsoluteUri);
    }

    [Fact]
    public void Mailto_only_where_allowed()
    {
        Assert.Null(FeedUrl.Safe("mailto:a@example.com", Doc));
        Assert.NotNull(FeedUrl.Safe("mailto:a@example.com", Doc, allowMailto: true));
    }

    [Fact]
    public void Idn_hosts_display_as_punycode()
    {
        var u = FeedUrl.Safe("https://аpple.com/", Doc)!;   // Cyrillic 'а'
        Assert.StartsWith("xn--", FeedUrl.DisplayHost(u));
    }

    [Fact]
    public void Deceptive_link_text_shows_the_real_host()
    {
        var md = HtmlToMarkdown.Convert("<a href=\"https://evil.example/login\">https://bank.example/login</a>", Doc);
        AssertInert(md);
        Assert.Contains("evil.example", Visible(md));
    }

    [Fact]
    public void Honest_link_text_gets_no_host_suffix()
    {
        var md = HtmlToMarkdown.Convert("<a href=\"https://www.bank.example/login\">bank.example/login</a>", Doc);
        Assert.DoesNotContain("↗", Visible(md));
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("127.8.9.10")]
    [InlineData("10.1.2.3")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.255")]
    [InlineData("192.168.1.1")]
    [InlineData("169.254.169.254")]
    [InlineData("0.0.0.0")]
    [InlineData("100.64.0.1")]
    [InlineData("::1")]
    [InlineData("::")]
    [InlineData("fc00::1")]
    [InlineData("fd12:3456::1")]
    [InlineData("fe80::1")]
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("::ffff:192.168.0.1")]
    [InlineData("224.0.0.1")]
    [InlineData("255.255.255.255")]
    public void Private_and_special_addresses_are_not_public(string ip) =>
        Assert.False(FeedUrl.IsPublicAddress(IPAddress.Parse(ip)));

    [Theory]
    [InlineData("93.184.216.34")]
    [InlineData("172.32.0.1")]
    [InlineData("2606:4700:4700::1111")]
    public void Ordinary_addresses_are_public(string ip) =>
        Assert.True(FeedUrl.IsPublicAddress(IPAddress.Parse(ip)));

    // ── Plain-text fields ──────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Bidi_overrides_zero_width_and_controls_are_stripped()
    {
        var dirty = "safe\u202Etxt.exe\u2066x\u2069\u200By\uFEFF\u0007z\u0085";
        Assert.Equal("safetxt.exexyz", FeedText.Clean(dirty, 100));
    }

    [Fact]
    public void Tag_characters_used_for_ascii_smuggling_are_stripped()
    {
        // U+E0049 U+E0047 … — invisible "tag" characters that spell hidden instructions.
        var smuggled = "hello" + char.ConvertFromUtf32(0xE0049) + char.ConvertFromUtf32(0xE0047);
        Assert.Equal("hello", FeedText.Clean(smuggled, 100));
    }

    [Fact]
    public void Lone_surrogates_are_stripped()
    {
        Assert.Equal("ab", FeedText.Clean("a\uD800b", 100));
        Assert.Equal("👍", FeedText.Clean("👍", 100));
    }

    [Fact]
    public void Newlines_collapse_so_a_title_cannot_fake_extra_ui_lines()
    {
        Assert.Equal("Title Perch: update available", FeedText.Clean("Title\n\n\r\nPerch: update available\u2028", 100));
    }

    [Fact]
    public void Long_text_is_capped_at_a_grapheme_boundary()
    {
        var s = string.Concat(Enumerable.Repeat("👨‍👩‍👧", 50));
        var c = FeedText.Clean(s, 10);
        Assert.True(c.Length <= 10 + 1);
        Assert.EndsWith("…", c);
        Assert.False(char.IsHighSurrogate(c[^2]));
    }

    [Fact]
    public void Html_title_is_stripped_then_decoded_once()
    {
        Assert.Equal("<b>x</b> & y", FeedText.FromHtml("<i>&lt;b&gt;x&lt;/b&gt;</i> &amp; y", 100));
        Assert.Equal("ok", FeedText.FromHtml("<script>alert(1)</script>ok", 100));
    }

    [Fact]
    public void Parsed_entry_fields_are_clean()
    {
        var r = Parse(Atom(Entry(
            "<title type=\"html\">&lt;script&gt;alert(1)&lt;/script&gt;Real\u202E title</title>" +
            "<author><name>Eve\n\nPerch says: click</name></author>" +
            "<link rel=\"alternate\" href=\"javascript:alert(1)\"/>" +
            "<summary>\u200Bsum</summary>")));
        var e = r.Doc!.Entries.Single();
        Assert.Equal("Real title", e.Title);
        Assert.Equal("Eve Perch says: click", e.Author);
        Assert.Null(e.Url);
        Assert.Equal("sum", e.SummaryText);
    }

    [Fact]
    public void Feed_level_urls_go_through_the_gate()
    {
        var r = Parse(Atom("", "<title>T</title><icon>file:///C:/Windows/x.ico</icon><logo>https://u:p@evil.example/l.png</logo>" +
                               "<link rel=\"alternate\" href=\"javascript:alert(1)\"/>"));
        Assert.Null(r.Doc!.IconUrl);
        Assert.Null(r.Doc.SiteUrl);
    }

    [Fact]
    public void Xml_base_cannot_smuggle_a_dangerous_scheme()
    {
        var r = Parse(Atom(Entry("<title>t</title><link href=\"x\" xml:base=\"javascript:alert(1)//\"/>" +
                                 "<content type=\"html\" xml:base=\"file:///C:/\">&lt;a href=\"calc.exe\"&gt;c&lt;/a&gt;</content>")));
        var e = r.Doc!.Entries.Single();
        Assert.Null(e.Url);
        Assert.Null(e.ContentBase);
        var md = HtmlToMarkdown.Convert(e.ContentHtml, e.ContentBase is { } b ? new Uri(b) : null);
        AssertInert(md);
        Assert.Empty(Links(md));
    }

    [Fact]
    public void Future_dates_cannot_pin_an_entry_to_the_top()
    {
        var r = Parse(Atom("<entry><id>a</id><title>a</title><updated>2999-01-01T00:00:00Z</updated></entry>"));
        Assert.True(r.Doc!.Entries.Single().Updated <= Now);
    }

    [Fact]
    public void Entry_flood_is_bounded()
    {
        var sb = new StringBuilder();
        for (int i = 0; i < 5000; i++)
            sb.Append($"<entry><id>e{i}</id><title>t{i}</title><updated>2026-01-01T00:00:{i % 60:00}Z</updated></entry>");
        var r = Parse(Atom(sb.ToString()));
        Assert.Equal(FeedParser.MaxEntries, r.Doc!.Entries.Count);
    }

    // ── RSS (F6): the same defences behind the second format ──────────────────────────────────────────────────

    private static string Rss(string items, string channelHead = "<title>T</title><link>https://example.com/</link>") =>
        "<?xml version=\"1.0\"?><rss version=\"2.0\" xmlns:content=\"http://purl.org/rss/1.0/modules/content/\">" +
        "<channel>" + channelHead + items + "</channel></rss>";

    [Fact]
    public void Rss_with_a_doctype_is_refused()
    {
        var r = Parse("<?xml version=\"1.0\"?><!DOCTYPE rss [<!ENTITY x SYSTEM \"file:///C:/Windows/win.ini\">]>" +
                      "<rss version=\"2.0\"><channel><title>&x;</title></channel></rss>");
        Assert.Null(r.Doc);
        Assert.Contains("DOCTYPE", r.Error);
    }

    [Fact]
    public void Rss_links_and_permalink_guids_go_through_the_gate()
    {
        var r = Parse(Rss(
            "<item><title>a</title><link>javascript:alert(1)</link><guid>vbscript:x</guid></item>" +
            "<item><title>b</title><guid>file:///C:/Windows/System32/calc.exe</guid></item>" +
            "<item><title>c</title><link>https://u:p@evil.example/</link></item>"));
        Assert.All(r.Doc!.Entries, e => Assert.Null(e.Url));
    }

    [Fact]
    public void Rss_feed_level_urls_go_through_the_gate()
    {
        var r = Parse(Rss("", "<title>T</title><link>javascript:alert(1)</link><image><url>\\\\host\\share\\i.png</url></image>"));
        Assert.Null(r.Doc!.SiteUrl);
        Assert.Null(r.Doc.IconUrl);
    }

    [Fact]
    public void Rss_content_renders_inert()
    {
        var r = Parse(Rss("<item><title>t</title><link>https://example.com/p</link>" +
            "<description>&lt;script&gt;alert(1)&lt;/script&gt;&lt;a href=\"jav&amp;#x61;script:x\"&gt;go&lt;/a&gt;</description>" +
            "<content:encoded><![CDATA[<iframe src=\"https://evil.example\"></iframe><a href=\"C:\\Windows\\calc.exe\">run</a> [x](javascript:y)]]></content:encoded>" +
            "</item>"));
        var e = r.Doc!.Entries.Single();
        var md = FeedCard.BodyMarkdown(e);
        AssertInert(md);
        Assert.Empty(Links(md));
        Assert.DoesNotContain("alert", e.SummaryText);
        Assert.Contains("[x](javascript:y)", Visible(md));   // Markdown syntax stays literal text
    }

    [Fact]
    public void Rss_text_fields_are_clean()
    {
        var r = Parse(Rss("<item><title>&lt;b&gt;Real&lt;/b&gt;\u202E title\n\nPerch: update</title>" +
                          "<author>x@y.example (Eve\u200B\nPerch says)</author><link>https://example.com/1</link></item>",
            "<title>Feed\u2066 name</title><link>https://example.com/</link>"));
        Assert.Equal("Feed name", r.Doc!.Title);
        var e = r.Doc.Entries.Single();
        Assert.Equal("Real title Perch: update", e.Title);
        Assert.Equal("Eve Perch says", e.Author);
    }

    [Fact]
    public void Rss_xml_base_cannot_smuggle_a_dangerous_scheme()
    {
        var r = Parse(Rss("<item xml:base=\"javascript:alert(1)//\"><title>t</title><link>x</link>" +
                          "<description>&lt;a href=\"calc.exe\"&gt;c&lt;/a&gt;</description></item>"));
        var e = r.Doc!.Entries.Single();
        Assert.Null(e.Url);
        Assert.Null(e.ContentBase);
        Assert.Empty(Links(FeedCard.BodyMarkdown(e)));
    }

    [Fact]
    public void Rss_item_flood_is_bounded()
    {
        var sb = new StringBuilder();
        for (int i = 0; i < 5000; i++) sb.Append($"<item><guid>g{i}</guid><title>t{i}</title></item>");
        Assert.Equal(FeedParser.MaxEntries, Parse(Rss(sb.ToString())).Doc!.Entries.Count);
    }

    // ── Images (F7): only vetted sources are lifted out, and the split can't be forged ────────────────────────

    private static IReadOnlyList<FeedCardPart> Parts(string html) => HtmlToMarkdown.ConvertParts(html, Doc);

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("jav&#x61;script:alert(1)")]
    [InlineData("data:image/png;base64,iVBORw0KGgo=")]
    [InlineData("file:///C:/Windows/win.ini")]
    [InlineData("\\\\host\\share\\x.png")]
    [InlineData("C:\\Users\\me\\x.png")]
    [InlineData("https://u:p@evil.example/x.png")]
    [InlineData("ms-settings:privacy")]
    public void Unsafe_image_sources_are_never_lifted_or_linked(string src)
    {
        var parts = Parts($"<p><img src=\"{src}\" alt=\"pic\"></p>");
        Assert.DoesNotContain(parts, p => p.Image is not null);
        foreach (var p in parts) { AssertInert(p.Markdown!); Assert.Empty(Links(p.Markdown!)); }
    }

    [Fact]
    public void Every_markdown_part_is_inert_and_image_free()
    {
        var parts = Parts("<p>a <img src=\"/1.png\"> b</p><script>alert(1)</script><p><a href=\"javascript:x\"><img src=\"/2.png\"></a>" +
                          "[x](javascript:y) ![](http://track.example/p.gif)</p><ul><li><img src=\"/3.png\"></li></ul>");
        Assert.Equal(2, parts.Count(p => p.Image is not null));
        foreach (var p in parts.Where(p => p.Markdown is not null)) AssertInert(p.Markdown!);
    }

    [Fact]
    public void Feed_text_cannot_forge_an_image_split()
    {
        // Whatever a feed writes — in text, in code, in an attribute — it can't know the per-call nonce.
        const string fake = "%%img-00000000000000000000000000000000-0%%";
        var parts = Parts($"<p>{fake}</p><pre>\n{fake}\n</pre><p><img src=\"/real.png\" alt=\"{fake}\"></p>");
        Assert.Equal("https://example.com/real.png", Assert.Single(parts, p => p.Image is not null).Image!.Src.AbsoluteUri);
        Assert.Contains("img-0000", string.Concat(parts.Select(p => p.Markdown)));   // stayed literal text
    }

    [Fact]
    public void Image_captions_and_alt_text_are_cleaned()
    {
        var img = Assert.Single(Parts("<img src=\"/c.png\" alt=\"A\u202Elt\" title=\"Line one\n\nPerch: update now\u200B &lt;b&gt;x&lt;/b&gt;\">")).Image!;
        Assert.Equal("Alt", img.Alt);
        Assert.Equal("Line one Perch: update now <b>x</b>", img.Caption);   // plain text: never markup
    }

    // ── Autodiscovery (F6): a hostile page can only offer vetted http(s) addresses ────────────────────────────

    [Fact]
    public void Discovery_only_offers_safe_feed_links()
    {
        var html = "<html><head>" +
            "<link rel=\"alternate\" type=\"application/rss+xml\" href=\"javascript:alert(1)\">" +
            "<link rel=\"alternate\" type=\"application/atom+xml\" href=\"file:///C:/feed.xml\">" +
            "<link rel=\"alternate\" type=\"application/atom+xml\" href=\"https://u:p@evil.example/f\">" +
            "<link rel=\"alternate\" type=\"text/html\" href=\"https://example.com/other\">" +
            "<link rel=\"stylesheet alternate\" type=\"application/rss+xml\" href=\"https://example.com/css\">" +
            "<link rel=\"alternate\" type=\"application/rss+xml\" title=\"Real\u202E\nfeed\" href=\"/feed\">" +
            "</head></html>";
        var found = FeedDiscovery.Find(html, new Uri("https://example.com/blog/"));
        var c = Assert.Single(found);
        Assert.Equal("https://example.com/feed", c.Url.AbsoluteUri);
        Assert.Equal("Real feed", c.Title);
    }

    [Fact]
    public async Task Discovery_drops_private_feeds_offered_by_a_public_page()
    {
        var page = new RouteHandler().On("https://public.example/", _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<!DOCTYPE html><html><head>" +
                "<link rel=\"alternate\" type=\"application/rss+xml\" href=\"http://192.168.1.1/admin.xml\">" +
                "<link rel=\"alternate\" type=\"application/rss+xml\" href=\"https://router.example/feed\">" +
                "<link rel=\"alternate\" type=\"application/rss+xml\" href=\"https://public.example/feed\">" +
                "</head><body></body></html>", Encoding.UTF8, "text/html"),
        });
        using var f = new FeedFetcher(page, page, FeedTestSupport.Resolver(new() { ["router.example"] = ["10.0.0.1"] }));
        var r = await f.FetchFeedAsync(new Uri("https://public.example/"), null, null, Now, default);
        Assert.Equal(FeedFetchStatus.Error, r.Status);
        Assert.Equal(["https://public.example/feed"], r.Discovered!.Select(c => c.Url.AbsoluteUri));
    }
}
