using Perch.Data.Control;
using Xunit;

namespace Perch.Tests;

/// <summary>The check behind the link popup (<see cref="LinkLabel"/>): a link opens straight away only when its text
/// is its own address, and one whose text names another site is flagged.</summary>
public class LinkLabelTests
{
    [Theory]
    [InlineData("https://github.com/anthropics/claude-code", "https://github.com/anthropics/claude-code")]
    [InlineData("github.com/anthropics/claude-code", "https://github.com/anthropics/claude-code/")]   // scheme, slash
    [InlineData("www.example.com", "https://example.com")]                                             // www.
    [InlineData("HTTPS://Example.com/Docs", "https://example.com/docs")]                               // case
    [InlineData("https://en.wikipedia.org/wiki/C#", "https://en.wikipedia.org/wiki/C%23")]             // escaping
    [InlineData("  https://example.com  ", "https://example.com")]                                     // whitespace
    [InlineData("jon@example.com", "mailto:jon@example.com")]
    public void Text_that_is_the_address_opens_straight_away(string label, string url) =>
        Assert.Equal(LinkLabelMatch.Same, LinkLabel.Check(label, url).Match);

    [Theory]
    [InlineData("the release notes", "https://github.com/x/y/releases")]
    [InlineData("", "https://example.com")]
    [InlineData(null, "https://example.com")]
    [InlineData("github.com", "https://github.com/anthropics/claude-code")]           // same site, another page
    [InlineData("https://example.com/a", "https://example.com/b")]
    [InlineData("v1.2", "https://example.com/v1.2")]                                 // not a host (no alphabetic TLD)
    [InlineData("README.md", "https://github.com/x/y/blob/main/README.md")]          // a file name, not a site
    [InlineData("Program.cs", "https://github.com/x/y/blob/main/src/Program.cs")]
    public void Other_text_shows_the_popup_without_a_warning(string? label, string url)
    {
        var check = LinkLabel.Check(label, url);
        Assert.Equal(LinkLabelMatch.Different, check.Match);
        Assert.Null(check.ShownHost);
    }

    [Theory]
    [InlineData("github.com", "https://evil.example/login", "github.com", "evil.example")]
    [InlineData("https://www.paypal.com/signin", "https://paypal.com.evil.example/signin", "paypal.com", "paypal.com.evil.example")]
    [InlineData("docs.anthropic.com/claude", "http://203.0.113.9/claude", "docs.anthropic.com", "203.0.113.9")]
    [InlineData("example.com", "https://www.evil.example", "example.com", "evil.example")]
    public void Text_naming_another_site_is_flagged(string label, string url, string shown, string host)
    {
        var check = LinkLabel.Check(label, url);
        Assert.Equal(LinkLabelMatch.OtherSite, check.Match);
        Assert.Equal(shown, check.ShownHost);
        Assert.Equal(host, check.Host);
    }

    [Fact]
    public void A_subdomain_of_the_shown_site_is_still_another_site()
    {
        // "github.com" over a link to gist.github.com is benign, but "paypal.com" over login.paypal.com.evil… isn't, and
        // only exact host equality tells them apart without a public-suffix list — so both are flagged.
        Assert.Equal(LinkLabelMatch.OtherSite, LinkLabel.Check("github.com", "https://gist.github.com/x").Match);
    }

    [Fact]
    public void A_url_it_cannot_read_never_opens_blind()
    {
        Assert.NotEqual(LinkLabelMatch.Same, LinkLabel.Check("example.com", "not a url").Match);
    }
}
