using System.Text.Json;
using Perch.Data;
using Perch.Feeds;
using Xunit;

namespace Perch.Tests;

/// <summary><see cref="FeedAddress"/> (what the Add feed dialog accepts) and the feed settings' persistence.</summary>
public class FeedAddressTests
{
    [Theory]
    [InlineData("https://example.com/atom.xml", "https://example.com/atom.xml", false)]
    [InlineData("  example.com/atom.xml  ", "https://example.com/atom.xml", false)]
    [InlineData("http://example.com/atom.xml", "http://example.com/atom.xml", true)]
    [InlineData("feed://example.com/atom.xml", "https://example.com/atom.xml", false)]
    [InlineData("feed:https://example.com/atom.xml", "https://example.com/atom.xml", false)]
    [InlineData("http://wiki/feed", "http://wiki/feed", true)]           // a typed scheme: an intranet name is fine
    [InlineData("localhost:8080/feed", "https://localhost:8080/feed", false)]
    public void Accepts_and_normalizes(string typed, string expected, bool insecure)
    {
        var p = FeedAddress.Parse(typed);
        Assert.Null(p.Problem);
        Assert.Equal(expected, p.Url!.AbsoluteUri);
        Assert.Equal(insecure, p.Insecure);
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/xml,<feed/>")]
    [InlineData("file:///C:/feeds/atom.xml")]
    [InlineData("ftp://example.com/atom")]
    [InlineData("https://user:pw@example.com/atom")]
    [InlineData("\\\\server\\share\\atom.xml")]
    [InlineData("news")]
    public void Refuses_what_cannot_be_a_feed_address(string typed)
    {
        var p = FeedAddress.Parse(typed);
        Assert.Null(p.Url);
        Assert.NotNull(p.Problem);
    }

    [Fact]
    public void Blank_is_neither_valid_nor_an_error()
    {
        var p = FeedAddress.Parse("   ");
        Assert.Null(p.Url);
        Assert.Null(p.Problem);
    }

    [Fact]
    public void Duplicates_ignore_trailing_slash_and_host_case_but_not_the_one_being_edited()
    {
        var existing = new[] { new FeedSubscription { Id = "a", Url = "https://Example.com/feed/" } };
        Assert.True(FeedAddress.IsDuplicate(new Uri("https://example.com/feed"), existing, exceptId: null));
        Assert.False(FeedAddress.IsDuplicate(new Uri("https://example.com/feed"), existing, exceptId: "a"));
        Assert.False(FeedAddress.IsDuplicate(new Uri("https://example.com/other"), existing, exceptId: null));
    }

    [Theory]
    [InlineData("Not a feed (looks like a web page)", "web page")]
    [InlineData("404 Not Found", "address")]
    [InlineData("403 Forbidden", "signing in")]
    [InlineData("Refused: x points at a private network address", "local network")]
    public void Failed_checks_get_a_next_step(string error, string mentions) =>
        Assert.Contains(mentions, FeedAddress.HintFor(error));

    [Fact]
    public void Timeouts_need_no_hint() => Assert.Null(FeedAddress.HintFor("Timed out"));

    [Fact]
    public void Feed_settings_round_trip_through_the_settings_json()
    {
        var s = new AppSettings
        {
            ShowFeeds = true,
            FeedsIntervalMinutes = 45,
            NotifyOnFeedEntry = true,
            Feeds = [new FeedSubscription { Id = "abc", Url = "https://example.com/atom", TitleOverride = "Ex", Enabled = false }],
        };
        var back = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(s))!;
        Assert.True(back.ShowFeeds);
        Assert.Equal(45, back.FeedsIntervalMinutes);
        Assert.True(back.NotifyOnFeedEntry);
        var f = Assert.Single(back.Feeds!);
        Assert.Equal(("abc", "https://example.com/atom", "Ex", false), (f.Id, f.Url, f.TitleOverride, f.Enabled));
    }

    [Fact]
    public void Feeds_default_off_with_a_thirty_minute_interval()
    {
        var s = new AppSettings();
        Assert.False(s.ShowFeeds);
        Assert.False(s.NotifyOnFeedEntry);
        Assert.Equal(30, s.FeedsIntervalMinutes);
        Assert.Null(s.Feeds);
    }
}
