using Perch.Data.Control;
using Xunit;

namespace Perch.Tests;

/// <summary>The shared http(s) link finder (<see cref="UrlDetect"/>) behind both the composer highlighter and
/// the clickable links in the conversation surfaces.</summary>
public class UrlDetectTests
{
    [Fact]
    public void Empty_ReturnsNothing()
    {
        Assert.Empty(UrlDetect.Find(null));
        Assert.Empty(UrlDetect.Find(""));
        Assert.Empty(UrlDetect.Find("no links here at all"));
    }

    [Fact]
    public void FindsUrl_AndCovers_ExactSpan()
    {
        const string src = "go to https://example.com/path now";
        var u = Assert.Single(UrlDetect.Find(src));
        Assert.Equal("https://example.com/path", u.Url);
        Assert.Equal("https://example.com/path", src.Substring(u.Start, u.Length));
    }

    [Fact]
    public void TrimsTrailingPunctuation()
    {
        var u = Assert.Single(UrlDetect.Find("see https://example.com/x, then stop"));
        Assert.Equal("https://example.com/x", u.Url);   // comma not swallowed
    }

    [Fact]
    public void FindsMultiple_InOrder()
    {
        var found = UrlDetect.Find("http://a.b/1 and https://c.d/2");
        Assert.Equal(2, found.Count);
        Assert.Equal("http://a.b/1", found[0].Url);
        Assert.Equal("https://c.d/2", found[1].Url);
        Assert.True(found[0].Start < found[1].Start);
    }

    [Fact]
    public void IgnoresNonHttpSchemes()
    {
        // Only http/https are linkified (a bare "www." or ftp: is not).
        Assert.Empty(UrlDetect.Find("ftp://host/file and www.example.com"));
    }
}
