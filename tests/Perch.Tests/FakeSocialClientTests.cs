using Perch.Social;
using Xunit;

namespace Perch.Tests;

/// <summary>
/// The in-memory <see cref="FakeSocialClient"/> is the reference behaviour for the Social contract — most
/// importantly the authorization rule the backend's row-level security will enforce: you see only your own
/// posts and <em>accepted</em> friends' posts. These tests pin that rule plus handle validation, the friend
/// state machine, post limits, and live-subscription visibility.
/// </summary>
public sealed class FakeSocialClientTests
{
    private static FakeSocialClient SignedIn(string handle = "myself")
    {
        var c = new FakeSocialClient();
        c.SignInAs(handle);
        return c;
    }

    [Fact]
    public async Task Feed_hides_a_non_friends_posts()
    {
        var c = SignedIn();
        var stranger = c.SeedUser("stranger");
        c.SimulatePost(stranger.Id, "you can't see this");

        var feed = await c.GetFeedAsync();
        Assert.Empty(feed);
    }

    [Fact]
    public async Task Pending_friend_is_still_not_visible()
    {
        var c = SignedIn();
        var ada = c.SeedUser("ada");
        await c.SendRequestAsync(ada.Id);          // requested, not yet accepted
        c.SimulatePost(ada.Id, "still hidden");

        Assert.Empty(await c.GetFeedAsync());
    }

    [Fact]
    public async Task Accepted_friends_posts_appear_newest_first()
    {
        var c = SignedIn();
        var ada = c.SeedUser("ada");
        await c.SendRequestAsync(ada.Id);
        c.SimulateAccept(ada.Id);

        c.SimulatePost(ada.Id, "first");
        await c.PostAsync("mine");
        c.SimulatePost(ada.Id, "latest");

        var feed = await c.GetFeedAsync();
        Assert.Equal(3, feed.Count);
        Assert.Equal("latest", feed[0].Body);      // newest first
        Assert.Contains(feed, f => f.Author.Handle == "myself" && f.Body == "mine");
    }

    [Fact]
    public async Task Incoming_request_can_be_accepted_and_declined()
    {
        var c = SignedIn();
        var grace = c.SeedUser("grace");
        c.SimulateIncomingRequest(grace.Id);

        var friends = await c.GetFriendsAsync();
        Assert.Equal(FriendshipState.Incoming, Assert.Single(friends).State);

        await c.RespondAsync(grace.Id, accept: true);
        Assert.Equal(FriendshipState.Accepted, Assert.Single(await c.GetFriendsAsync()).State);

        // Declining a different incoming request forgets the edge entirely.
        var linus = c.SeedUser("linus");
        c.SimulateIncomingRequest(linus.Id);
        await c.RespondAsync(linus.Id, accept: false);
        Assert.DoesNotContain(await c.GetFriendsAsync(), f => f.Profile.Handle == "linus");
    }

    [Fact]
    public async Task FindByHandle_is_exact_match_only()
    {
        var c = SignedIn();
        c.SeedUser("ada");
        Assert.NotNull(await c.FindByHandleAsync("ada"));
        Assert.NotNull(await c.FindByHandleAsync("ADA"));   // case-insensitive…
        Assert.Null(await c.FindByHandleAsync("ad"));       // …but not a prefix/substring
    }

    [Theory]
    [InlineData("ab")]                 // too short
    [InlineData("Has Spaces")]
    [InlineData("Uppercase")]
    [InlineData("waaaaaaaaaaaaaaaaaaaaaaay_too_long")]
    public async Task ClaimHandle_rejects_malformed_handles(string bad)
    {
        var c = new FakeSocialClient();
        await c.SignInAsync();
        await Assert.ThrowsAsync<SocialException>(() => c.ClaimHandleAsync(bad));
    }

    [Fact]
    public async Task ClaimHandle_rejects_a_taken_handle()
    {
        var c = SignedIn("myself");
        c.SeedUser("taken");
        await Assert.ThrowsAsync<SocialException>(() => c.ClaimHandleAsync("taken"));
    }

    [Fact]
    public async Task Post_rejects_empty_and_over_long_bodies()
    {
        var c = SignedIn();
        await Assert.ThrowsAsync<SocialException>(() => c.PostAsync("   "));
        await Assert.ThrowsAsync<SocialException>(() => c.PostAsync(new string('x', 281)));
        var ok = await c.PostAsync(new string('x', 280));
        Assert.NotEqual(default, ok);
    }

    [Fact]
    public async Task Subscribe_fires_for_visible_posts_only()
    {
        var c = SignedIn();
        var ada = c.SeedUser("ada");
        var stranger = c.SeedUser("stranger");
        await c.SendRequestAsync(ada.Id);
        c.SimulateAccept(ada.Id);

        var seen = new List<string>();
        using (c.SubscribeFeed(item => { lock (seen) seen.Add(item.Body); }))
        {
            c.SimulatePost(ada.Id, "friend post");        // visible → fires
            c.SimulatePost(stranger.Id, "stranger post"); // not visible → no fire
            await c.PostAsync("my own post");             // visible → fires
        }
        c.SimulatePost(ada.Id, "after dispose");          // unsubscribed → no fire

        Assert.Equal(new[] { "friend post", "my own post" }, seen);
    }

    [Fact]
    public async Task Requires_a_handle_before_posting()
    {
        var c = new FakeSocialClient();
        await c.SignInAsync();                 // signed in, but no handle yet
        await Assert.ThrowsAsync<SocialException>(() => c.PostAsync("hi"));
    }
}
