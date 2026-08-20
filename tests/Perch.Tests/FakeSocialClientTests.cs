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
    public async Task Blocking_a_friend_hides_their_posts_and_lists_them_as_blocked()
    {
        var c = SignedIn();
        var ada = c.SeedUser("ada");
        await c.SendRequestAsync(ada.Id);
        c.SimulateAccept(ada.Id);
        c.SimulatePost(ada.Id, "before the block");
        Assert.Single(await c.GetFeedAsync());               // visible while friends

        await c.BlockAsync(ada.Id);
        Assert.Empty(await c.GetFeedAsync());                 // their posts vanish
        Assert.Contains(await c.GetBlockedAsync(), p => p.Id == ada.Id);

        await c.UnblockAsync(ada.Id);
        Assert.Single(await c.GetFeedAsync());                // visibility restored (still accepted friends)
        Assert.Empty(await c.GetBlockedAsync());
    }

    [Fact]
    public async Task Being_blocked_by_a_friend_hides_their_posts_too()
    {
        var c = SignedIn();
        var ada = c.SeedUser("ada");
        await c.SendRequestAsync(ada.Id);
        c.SimulateAccept(ada.Id);
        c.SimulateBlockedBy(ada.Id);            // they blocked me — I can't see it, but visibility dies both ways
        c.SimulatePost(ada.Id, "you won't see this");

        Assert.Empty(await c.GetFeedAsync());
        Assert.Empty(await c.GetBlockedAsync()); // it's their block, not mine
    }

    [Fact]
    public async Task Roster_has_one_entry_per_accepted_friend_with_their_latest_status()
    {
        var c = SignedIn();
        var ada = c.SeedUser("ada", moodEmoji: "🦉");
        var grace = c.SeedUser("grace");
        var stranger = c.SeedUser("stranger");
        await c.SendRequestAsync(ada.Id); c.SimulateAccept(ada.Id);
        await c.SendRequestAsync(grace.Id); c.SimulateAccept(grace.Id);
        // stranger stays a stranger — never in the roster.

        c.SimulatePost(ada.Id, "older");
        c.SimulatePost(ada.Id, "ada's latest");   // grace has no post yet

        var roster = await c.GetRosterAsync();
        Assert.Equal("myself", roster.Me!.Handle);
        Assert.Equal(2, roster.Friends.Count);                       // ada + grace, not the stranger
        Assert.DoesNotContain(roster.Friends, f => f.Profile.Handle == "stranger");

        var adaEntry = roster.Friends.Single(f => f.Profile.Handle == "ada");
        Assert.Equal("ada's latest", adaEntry.Latest!.Body);         // most recent only
        var graceEntry = roster.Friends.Single(f => f.Profile.Handle == "grace");
        Assert.Null(graceEntry.Latest);                              // no status yet
        Assert.Equal(adaEntry, roster.Friends[0]);                   // active friend sorts first
    }

    [Fact]
    public async Task Reactions_toggle_and_count_and_flag_mine()
    {
        var c = SignedIn();
        var ada = c.SeedUser("ada");
        await c.SendRequestAsync(ada.Id); c.SimulateAccept(ada.Id);
        var post = c.SimulatePost(ada.Id, "shipped it");

        c.SimulateReaction(post.Value, ada.Id, "🔥");   // a friend reacts
        await c.ReactAsync(post.Value, "🔥", on: true);  // and so do I

        var entry = (await c.GetRosterAsync()).Friends.Single(f => f.Profile.Handle == "ada");
        var fire = entry.Reactions.Single(r => r.Emoji == "🔥");
        Assert.Equal(2, fire.Count);
        Assert.True(fire.Mine);

        await c.ReactAsync(post.Value, "🔥", on: false); // I take mine back
        var after = (await c.GetRosterAsync()).Friends.Single(f => f.Profile.Handle == "ada")
            .Reactions.Single(r => r.Emoji == "🔥");
        Assert.Equal(1, after.Count);
        Assert.False(after.Mine);
    }

    [Fact]
    public async Task Reacting_with_a_second_emoji_replaces_your_first_one()
    {
        var c = SignedIn();
        var ada = c.SeedUser("ada");
        await c.SendRequestAsync(ada.Id); c.SimulateAccept(ada.Id);
        var post = c.SimulatePost(ada.Id, "shipped it");

        await c.ReactAsync(post.Value, "🔥", on: true);
        await c.ReactAsync(post.Value, "👍", on: true);   // one reaction per user → replaces 🔥

        var reactions = (await c.GetRosterAsync()).Friends.Single(f => f.Profile.Handle == "ada").Reactions;
        Assert.DoesNotContain(reactions, r => r.Emoji == "🔥");            // the first one is gone
        var thumbs = Assert.Single(reactions);
        Assert.Equal("👍", thumbs.Emoji);
        Assert.True(thumbs.Mine);
    }

    [Fact]
    public async Task Removing_a_friend_drops_the_edge_and_the_roster_entry()
    {
        var c = SignedIn();
        var ada = c.SeedUser("ada");
        await c.SendRequestAsync(ada.Id); c.SimulateAccept(ada.Id);
        c.SimulatePost(ada.Id, "hi");
        Assert.Single((await c.GetRosterAsync()).Friends);

        await c.RemoveFriendAsync(ada.Id);
        Assert.DoesNotContain(await c.GetFriendsAsync(), f => f.Profile.Id == ada.Id);   // edge gone
        Assert.Empty((await c.GetRosterAsync()).Friends);                                 // and out of the roster
        Assert.Empty(await c.GetFeedAsync());                                             // their posts no longer visible
    }

    [Fact]
    public async Task Cancelling_your_own_request_via_remove()
    {
        var c = SignedIn();
        var ada = c.SeedUser("ada");
        await c.SendRequestAsync(ada.Id);   // pending, sent by me
        await c.RemoveFriendAsync(ada.Id);  // cancel it
        Assert.DoesNotContain(await c.GetFriendsAsync(), f => f.Profile.Id == ada.Id);
    }

    [Fact]
    public async Task Removing_someone_who_isnt_a_friend_is_a_no_op()
    {
        var c = SignedIn();
        var stranger = c.SeedUser("stranger");
        await c.RemoveFriendAsync(stranger.Id);   // no edge → no throw
        Assert.Empty(await c.GetFriendsAsync());
    }

    [Fact]
    public async Task A_blocked_friend_leaves_the_roster()
    {
        var c = SignedIn();
        var ada = c.SeedUser("ada");
        await c.SendRequestAsync(ada.Id); c.SimulateAccept(ada.Id);
        c.SimulatePost(ada.Id, "hi");
        Assert.Single((await c.GetRosterAsync()).Friends);

        // Blocking keeps the 'accepted' friendship edge, but the roster must not show a blocked person.
        await c.BlockAsync(ada.Id);
        Assert.Empty((await c.GetRosterAsync()).Friends);
    }

    [Fact]
    public async Task Mutual_requests_become_an_accepted_friendship()
    {
        var c = SignedIn();
        var ada = c.SeedUser("ada");
        c.SimulateIncomingRequest(ada.Id);       // ada invited me first
        await c.SendRequestAsync(ada.Id);         // I invite ada back → handshake completes

        Assert.Equal(FriendshipState.Accepted, Assert.Single(await c.GetFriendsAsync()).State);
    }

    [Fact]
    public async Task Re_requesting_an_accepted_friend_is_a_no_op()
    {
        var c = SignedIn();
        var ada = c.SeedUser("ada");
        await c.SendRequestAsync(ada.Id); c.SimulateAccept(ada.Id);
        await c.SendRequestAsync(ada.Id);         // sending again mustn't downgrade or duplicate

        Assert.Equal(FriendshipState.Accepted, Assert.Single(await c.GetFriendsAsync()).State);
    }

    [Fact]
    public async Task Changing_your_handle_keeps_friends_display_name_and_mood()
    {
        var c = SignedIn("oldname");
        await c.ClaimHandleAsync("oldname", "Ada L.", "🦉");   // set a display name + mood
        var ada = c.SeedUser("ada");
        await c.SendRequestAsync(ada.Id); c.SimulateAccept(ada.Id);

        var renamed = await c.ClaimHandleAsync("newname", "Ada L.", "🦉");   // rename (id is stable)

        Assert.Equal("newname", renamed.Handle);
        Assert.Equal("Ada L.", renamed.DisplayName);
        Assert.Equal("🦉", renamed.MoodEmoji);
        Assert.Equal("newname", (await c.GetMeAsync())!.Handle);
        // The friendship survives the rename — id, not handle, is what the edge references.
        Assert.Equal(FriendshipState.Accepted, Assert.Single(await c.GetFriendsAsync()).State);
    }

    [Fact]
    public async Task Requires_a_handle_before_posting()
    {
        var c = new FakeSocialClient();
        await c.SignInAsync();                 // signed in, but no handle yet
        await Assert.ThrowsAsync<SocialException>(() => c.PostAsync("hi"));
    }
}
