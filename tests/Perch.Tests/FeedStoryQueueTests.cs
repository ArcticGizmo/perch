using Perch.Feeds;
using Xunit;
using static Perch.Tests.FeedTestSupport;

namespace Perch.Tests;

/// <summary><see cref="StoryPlan"/>: which cards play, in what order, and where next/previous go.</summary>
public class FeedStoryQueueTests
{
    // A feed with entries e{n}..e1 (newest first); `unread` names which are unread.
    private static StoryFeed Feed(string id, int count, params int[] unread) => Feed(id, count, null, unread);

    private static StoryFeed Feed(string id, int count, string? error, params int[] unread)
    {
        var entries = Enumerable.Range(1, count).Reverse()
            .Select(n => Entry($"{id}{n}", T0.AddMinutes(n))).ToList();
        return new StoryFeed(id, entries, unread.Select(n => $"{id}{n}").ToHashSet(), error);
    }

    private static List<string> Ids(StoryRun run) =>
        run.Cards.Select(c => c.Entry?.Id ?? c.Kind.ToString()).ToList();

    [Fact]
    public void Unread_head_plays_oldest_first_then_chains_then_ends()
    {
        var feeds = new[] { Feed("a", 5, 4, 5), Feed("b", 3), Feed("c", 3, 1, 3) };
        var plan = StoryPlan.ForHead(feeds, "a");
        Assert.False(plan.IsReplay);
        Assert.Equal(["a4", "a5"], Ids(plan.Runs[0]));
        Assert.Equal(["c1", "c3"], Ids(plan.Runs[1]));         // b has nothing unread: skipped
        Assert.Equal(StoryCardKind.CaughtUp, plan.Runs[2].Cards.Single().Kind);
        Assert.Equal("a4", plan.Current.Entry!.Id);
    }

    [Fact]
    public void Chaining_wraps_round_to_feeds_before_the_clicked_one()
    {
        var feeds = new[] { Feed("a", 2, 2), Feed("b", 2, 1), Feed("c", 2, 2) };
        var plan = StoryPlan.ForHead(feeds, "b");
        Assert.Equal(["b", "c", "a", ""], plan.Runs.Select(r => r.SubId));
    }

    [Fact]
    public void Seen_head_replays_its_newest_ten_opened_on_the_newest()
    {
        var feeds = new[] { Feed("a", 15) };
        var plan = StoryPlan.ForHead(feeds, "a");
        Assert.True(plan.IsReplay);
        Assert.Single(plan.Runs);
        Assert.Equal(10, plan.CurrentRun.Cards.Count);
        Assert.Equal("a15", plan.Current.Entry!.Id);
        Assert.Equal("a6", plan.CurrentRun.Cards[0].Entry!.Id);
        Assert.Equal(StoryMove.Ended, plan.Next());
        Assert.Equal(StoryMove.Moved, plan.Prev());
        Assert.Equal("a14", plan.Current.Entry!.Id);
    }

    [Fact]
    public void Failing_feed_leads_with_an_error_card()
    {
        var plan = StoryPlan.ForHead([Feed("a", 2, "404 Not Found", 2)], "a");
        Assert.Equal(StoryCardKind.Error, plan.Current.Kind);
        Assert.Equal("404 Not Found", plan.Current.Error);
        Assert.Equal(StoryMove.Moved, plan.Next());
        Assert.Equal("a2", plan.Current.Entry!.Id);

        var empty = StoryPlan.ForHead([Feed("x", 0, "Timed out")], "x");
        Assert.Equal(StoryCardKind.Error, empty.Current.Kind);
    }

    [Fact]
    public void Empty_or_unknown_feeds_get_an_empty_card()
    {
        Assert.Equal(StoryCardKind.Empty, StoryPlan.ForHead([Feed("a", 0)], "a").Current.Kind);
        Assert.Equal(StoryCardKind.Empty, StoryPlan.ForHead([Feed("a", 2)], "zzz").Current.Kind);
        Assert.Equal(StoryCardKind.Empty, StoryPlan.ForOverflow([]).Current.Kind);
    }

    [Fact]
    public void Navigation_crosses_feed_boundaries_both_ways()
    {
        var plan = StoryPlan.ForHead([Feed("a", 2, 1, 2), Feed("b", 1, 1)], "a");
        Assert.Equal(StoryMove.Moved, plan.Next());          // a1 → a2
        Assert.Equal(StoryMove.FeedChanged, plan.Next());    // → b1
        Assert.Equal("b1", plan.Current.Entry!.Id);
        Assert.Equal(StoryMove.FeedChanged, plan.Prev());    // ← a2
        Assert.Equal("a2", plan.Current.Entry!.Id);
        Assert.Equal(StoryMove.Moved, plan.Prev());
        Assert.Equal(StoryMove.None, plan.Prev());           // at the very start
        plan.Next(); plan.Next();
        Assert.Equal(StoryMove.FeedChanged, plan.Next());    // → caught up
        Assert.Equal(StoryCardKind.CaughtUp, plan.Current.Kind);
        Assert.Equal(StoryMove.Ended, plan.Next());
    }

    [Fact]
    public void Jump_to_a_segment_stays_within_the_run()
    {
        var plan = StoryPlan.ForHead([Feed("a", 3, 1, 2, 3)], "a");
        Assert.Equal(StoryMove.Moved, plan.JumpTo(2));
        Assert.Equal("a3", plan.Current.Entry!.Id);
        Assert.Equal(StoryMove.None, plan.JumpTo(9));
        Assert.Equal(StoryMove.None, plan.JumpTo(2));
    }

    [Fact]
    public void Overflow_starts_at_the_first_feed_with_unread()
    {
        var plan = StoryPlan.ForOverflow([Feed("a", 2), Feed("b", 2, 2)]);
        Assert.Equal("b", plan.CurrentRun.SubId);
        Assert.True(StoryPlan.ForOverflow([Feed("a", 2)]).IsReplay);
    }

    [Fact]
    public void Merge_only_adds_ahead_of_the_cursor()
    {
        var plan = StoryPlan.ForHead([Feed("a", 3, 1, 2), Feed("b", 2, 1)], "a");
        plan.Next(); plan.Next();                     // a1, a2 watched; now on b1
        Assert.Equal("b1", plan.Current.Entry!.Id);

        // New arrivals: a3 (behind the cursor), b2 (current run), and a new feed c.
        plan.Merge([Feed("a", 3, 1, 2, 3), Feed("b", 2, 1, 2), Feed("c", 1, 1)]);
        Assert.Equal(["a1", "a2"], Ids(plan.Runs[0]));          // untouched
        Assert.Equal(["b1", "b2"], Ids(plan.Runs[1]));          // appended after the cursor
        Assert.Equal("c", plan.Runs[2].SubId);                   // inserted before the end card
        Assert.Equal(StoryCardKind.CaughtUp, plan.Runs[^1].Cards.Single().Kind);
        Assert.Equal("b1", plan.Current.Entry!.Id);              // cursor unmoved
    }

    [Fact]
    public void Merge_leaves_a_replay_alone()
    {
        var plan = StoryPlan.ForHead([Feed("a", 3)], "a");
        plan.Merge([Feed("a", 4, 4)]);
        Assert.Equal(3, plan.CurrentRun.Cards.Count);
    }
}
