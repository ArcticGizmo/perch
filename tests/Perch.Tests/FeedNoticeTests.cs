using Perch.Feeds;
using Xunit;

namespace Perch.Tests;

/// <summary>Feed toasts (docs/feeds-plan.md §4.2): one per feed per check, batched, and a single summary once
/// several feeds have news at once.</summary>
public class FeedNoticeTests
{
    private static readonly DateTime T0 = FeedTestSupport.T0;

    private static FeedArrivals Arrivals(string id, string title, params (string Title, int MinutesAgo)[] entries) =>
        new(id, title, entries.Select((e, i) => FeedTestSupport.Entry($"{id}-{i}", T0.AddMinutes(-e.MinutesAgo), e.Title)).ToList());

    [Fact]
    public void One_entry_names_it()
    {
        var t = Assert.Single(FeedNotice.Build([Arrivals("a", ".NET Blog", ("Hello .NET 11", 5))]));
        Assert.Equal("New in .NET Blog", t.Title);
        Assert.Equal("Hello .NET 11", t.Body);
        Assert.Equal("a", t.SubId);
    }

    [Fact]
    public void Several_entries_batch_into_one_toast_led_by_the_newest()
    {
        var t = Assert.Single(FeedNotice.Build([Arrivals("a", ".NET Blog", ("Older", 30), ("Newest", 1), ("Middle", 10))]));
        Assert.Equal("3 new posts in .NET Blog", t.Title);
        Assert.Equal("Latest: Newest", t.Body);
    }

    [Fact]
    public void Each_feed_gets_its_own_toast_up_to_the_limit()
    {
        var toasts = FeedNotice.Build(
        [
            Arrivals("a", "A", ("a1", 1)),
            Arrivals("b", "B", ("b1", 1), ("b2", 2)),
            Arrivals("c", "C", ("c1", 1)),
        ]);
        Assert.Equal(["a", "b", "c"], toasts.Select(t => t.SubId));
    }

    [Fact]
    public void Past_the_limit_one_summary_toast_plays_the_first_feed_with_news()
    {
        var arrivals = Enumerable.Range(1, FeedNotice.MaxSeparate + 1)
            .Select(i => Arrivals($"f{i}", $"Feed {i}", ("x", 1), ("y", 2))).ToList();
        var t = Assert.Single(FeedNotice.Build(arrivals));
        Assert.Null(t.SubId);
        Assert.Equal($"{2 * arrivals.Count} new posts in {arrivals.Count} feeds", t.Title);
        Assert.StartsWith("Feed 1 (2), Feed 2 (2)", t.Body);
    }

    [Fact]
    public void Nothing_new_means_no_toast()
    {
        Assert.Empty(FeedNotice.Build([]));
        Assert.Empty(FeedNotice.Build([new FeedArrivals("a", "A", [])]));
    }

    [Fact]
    public void Toast_lines_are_capped_and_stay_on_one_line()
    {
        var t = Assert.Single(FeedNotice.Build([Arrivals("a", new string('F', 300), (new string('t', 400), 1))]));
        Assert.True(t.Title.Length <= 80, t.Title);
        Assert.True(t.Body.Length <= 140, t.Body);
        Assert.DoesNotContain('\n', t.Title + t.Body);
    }

    [Theory]
    [InlineData("Timed out", "retrying")]
    [InlineData("Couldn't connect", "retrying")]
    [InlineData("Couldn't find feeds.example.com", "retrying")]
    [InlineData("503 Service Unavailable", "trouble")]
    [InlineData("HTTP 599", "trouble")]
    [InlineData("429 Too Many Requests", "trouble")]
    [InlineData("Feed is too large", "4 MB")]
    [InlineData("404 Not Found", "address")]                // falls through to the add dialog's hints
    public void A_failing_feed_gets_a_next_step(string error, string mentions) =>
        Assert.Contains(mentions, FeedAddress.StatusHint(error));

    [Theory]
    [InlineData(null)]
    [InlineData("Not a feed")]
    [InlineData("500px is not a status")]
    public void Some_failures_need_no_status_hint(string? error) => Assert.Null(FeedAddress.StatusHint(error));
}
