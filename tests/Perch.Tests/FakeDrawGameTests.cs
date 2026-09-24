using Perch.Games;
using Perch.Social;
using Xunit;

namespace Perch.Tests;

/// <summary>
/// The networked "Draw with Perch" contract, proven against <see cref="FakeSocialClient"/> (which mirrors the
/// Supabase backend's rules): you can only challenge an accepted friend, the invite carries the challenger's
/// first drawing (seeded as round 1 on accept), turns/phases are validated, guesses are checked and scored, and
/// the roles swap every round.
/// </summary>
public class FakeDrawGameTests
{
    private static (FakeSocialClient fake, Profile me, Profile friend) SignedInWithFriend()
    {
        var fake = new FakeSocialClient();
        var me = fake.SignInAs("alice");
        var friend = fake.SeedUser("bob");
        fake.SimulateAccept(friend.Id);
        return (fake, me, friend);
    }

    private static IReadOnlyList<DrawStroke> Doodle() =>
        new[] { new DrawStroke(0, 1, new[] { new DrawPoint(10, 10), new DrawPoint(500, 500) }) };

    [Fact]
    public async Task Request_rejects_a_non_friend()
    {
        var fake = new FakeSocialClient();
        fake.SignInAs("alice");
        var stranger = fake.SeedUser("carol");
        await Assert.ThrowsAsync<SocialException>(() =>
            fake.RequestDrawGameAsync(stranger.Id, DrawDifficulty.Easy, "cat", "3", Doodle()));
    }

    [Fact]
    public async Task Request_shows_up_in_my_requests()
    {
        var (fake, me, friend) = SignedInWithFriend();
        var req = await fake.RequestDrawGameAsync(friend.Id, DrawDifficulty.Medium, "ice cream", "3 5", Doodle());

        var list = await fake.GetDrawRequestsAsync();
        Assert.Single(list);
        Assert.Equal(req.Id, list[0].Id);
        Assert.Equal(me.Id, list[0].Requester.Id);
        Assert.Equal(DrawDifficulty.Medium, list[0].Difficulty);
        Assert.Equal("3 5", list[0].LetterHint);
    }

    [Fact]
    public async Task Accepting_seeds_round_one_as_the_accepters_turn_to_guess()
    {
        var (fake, me, friend) = SignedInWithFriend();
        var req = fake.SimulateIncomingDrawRequest(friend.Id, DrawDifficulty.Hard, "gravity");

        var state = await fake.AcceptDrawRequestAsync(req.Id);
        Assert.Equal(DrawGameStatus.InProgress, state.Summary.Status);
        Assert.Equal(DrawPhase.Guess, state.Summary.Phase);
        Assert.True(state.Summary.IsTurnOf(me.Id));               // it's immediately my turn to guess
        Assert.Equal(1, state.Summary.RoundNo);
        Assert.NotNull(state.Current);
        Assert.Equal(friend.Id, state.Current!.Drawer.Id);       // the challenger drew it
        Assert.Equal(me.Id, state.Current.Guesser.Id);
        Assert.Equal(DrawRoundStatus.Guessing, state.Current.Status);
        Assert.Equal(DrawGuessing.LetterHint("gravity"), state.Current.LetterHint);
    }

    [Fact]
    public async Task Word_is_hidden_from_the_guesser_until_the_round_resolves()
    {
        var (fake, _, friend) = SignedInWithFriend();
        var req = fake.SimulateIncomingDrawRequest(friend.Id, DrawDifficulty.Easy, "cat");
        var state = await fake.AcceptDrawRequestAsync(req.Id);
        Assert.Null(state.Current!.Word);                        // masked while guessing

        var solved = await fake.SubmitDrawGuessAsync(state.Current.Id, "cat");
        Assert.Equal("cat", solved.Current!.Word);               // revealed once solved
    }

    [Fact]
    public async Task Correct_guess_solves_scores_both_players_and_flips_to_my_draw_turn()
    {
        var (fake, me, friend) = SignedInWithFriend();
        var req = fake.SimulateIncomingDrawRequest(friend.Id, DrawDifficulty.Hard, "gravity");
        var state = await fake.AcceptDrawRequestAsync(req.Id);

        var after = await fake.SubmitDrawGuessAsync(state.Current!.Id, "Gravity!");
        Assert.Equal(DrawRoundStatus.Solved, after.Current!.Status);
        var (gp, dp) = DrawScoring.Points(DrawDifficulty.Hard, 1);
        Assert.Equal(gp, after.Current.PointsGuesser);
        Assert.Equal(dp, after.Current.PointsDrawer);
        Assert.Equal(gp, after.Summary.MyScore(me.Id));          // I guessed
        Assert.Equal(dp, after.Summary.TheirScore(me.Id));       // the friend drew
        // Roles swap: now it's my turn to draw the next round.
        Assert.Equal(DrawPhase.Draw, after.Summary.Phase);
        Assert.True(after.Summary.IsTurnOf(me.Id));
    }

    [Fact]
    public async Task Wrong_guess_is_recorded_and_the_round_stays_open()
    {
        var (fake, me, friend) = SignedInWithFriend();
        var req = fake.SimulateIncomingDrawRequest(friend.Id, DrawDifficulty.Easy, "cat");
        var state = await fake.AcceptDrawRequestAsync(req.Id);

        var after = await fake.SubmitDrawGuessAsync(state.Current!.Id, "dog");
        Assert.Equal(DrawRoundStatus.Guessing, after.Current!.Status);
        Assert.Equal(new[] { "dog" }, after.Current.Guesses);
        Assert.True(after.Summary.IsTurnOf(me.Id));              // still my guess
    }

    [Fact]
    public async Task Give_up_reveals_the_word_scores_nothing_and_flips_to_my_draw_turn()
    {
        var (fake, me, friend) = SignedInWithFriend();
        var req = fake.SimulateIncomingDrawRequest(friend.Id, DrawDifficulty.Medium, "robot");
        var state = await fake.AcceptDrawRequestAsync(req.Id);

        var after = await fake.GiveUpDrawRoundAsync(state.Current!.Id);
        Assert.Equal(DrawRoundStatus.GaveUp, after.Current!.Status);
        Assert.Equal("robot", after.Current.Word);              // revealed
        Assert.Equal(0, after.Summary.MyScore(me.Id));
        Assert.Equal(DrawPhase.Draw, after.Summary.Phase);
        Assert.True(after.Summary.IsTurnOf(me.Id));
    }

    [Fact]
    public async Task Drawing_the_next_round_swaps_roles_and_hands_the_guess_to_the_opponent()
    {
        var (fake, me, friend) = SignedInWithFriend();
        var req = fake.SimulateIncomingDrawRequest(friend.Id, DrawDifficulty.Easy, "cat");
        var state = await fake.AcceptDrawRequestAsync(req.Id);
        await fake.SubmitDrawGuessAsync(state.Current!.Id, "cat");     // solved → my turn to draw

        var next = await fake.SubmitDrawRoundAsync(state.Summary.Id, DrawDifficulty.Medium, "rocket", "6", Doodle());
        Assert.Equal(2, next.Summary.RoundNo);
        Assert.Equal(me.Id, next.Current!.Drawer.Id);
        Assert.Equal(friend.Id, next.Current.Guesser.Id);
        Assert.Equal(DrawPhase.Guess, next.Summary.Phase);
        Assert.True(next.Summary.IsTurnOf(friend.Id));                 // waiting on the opponent to guess
    }

    [Fact]
    public async Task Cannot_draw_a_new_round_while_a_guess_is_pending()
    {
        var (fake, _, friend) = SignedInWithFriend();
        var req = fake.SimulateIncomingDrawRequest(friend.Id, DrawDifficulty.Easy, "cat");
        var state = await fake.AcceptDrawRequestAsync(req.Id);
        // The round is still 'guessing' (it's my turn to guess, not draw).
        await Assert.ThrowsAsync<SocialException>(() =>
            fake.SubmitDrawRoundAsync(state.Summary.Id, DrawDifficulty.Easy, "dog", "3", Doodle()));
    }

    [Fact]
    public async Task Cannot_guess_a_round_you_are_not_the_guesser_of()
    {
        var (fake, me, friend) = SignedInWithFriend();
        var req = fake.SimulateIncomingDrawRequest(friend.Id, DrawDifficulty.Easy, "cat");
        var state = await fake.AcceptDrawRequestAsync(req.Id);
        await fake.SubmitDrawGuessAsync(state.Current!.Id, "cat");     // solve → my turn to draw

        var next = await fake.SubmitDrawRoundAsync(state.Summary.Id, DrawDifficulty.Easy, "dog", "3", Doodle());
        // Now I'm the drawer; guessing my own round must be rejected.
        await Assert.ThrowsAsync<SocialException>(() => fake.SubmitDrawGuessAsync(next.Current!.Id, "dog"));
    }

    [Fact]
    public async Task Opponent_guessing_fires_the_subscription()
    {
        var (fake, me, friend) = SignedInWithFriend();
        var req = fake.SimulateIncomingDrawRequest(friend.Id, DrawDifficulty.Easy, "cat");
        var state = await fake.AcceptDrawRequestAsync(req.Id);
        await fake.SubmitDrawGuessAsync(state.Current!.Id, "cat");            // my turn to draw
        var next = await fake.SubmitDrawRoundAsync(state.Summary.Id, DrawDifficulty.Easy, "dog", "3", Doodle());

        int fired = 0;
        using var sub = fake.SubscribeDrawGame(state.Summary.Id, () => fired++);
        fake.SimulateOpponentGuess(state.Summary.Id, "dog");
        Assert.True(fired > 0);

        var back = await fake.GetDrawGameAsync(state.Summary.Id);
        Assert.Equal(DrawRoundStatus.Solved, back.Current!.Status);
        Assert.True(back.Summary.MyScore(friend.Id) > 0);                     // the friend (guesser) scored
    }

    [Fact]
    public async Task Resign_abandons_the_game()
    {
        var (fake, _, friend) = SignedInWithFriend();
        var req = fake.SimulateIncomingDrawRequest(friend.Id, DrawDifficulty.Easy, "cat");
        var state = await fake.AcceptDrawRequestAsync(req.Id);

        var after = await fake.ResignDrawGameAsync(state.Summary.Id);
        Assert.Equal(DrawGameStatus.Abandoned, after.Summary.Status);
    }

    [Fact]
    public async Task Delete_removes_the_game()
    {
        var (fake, _, friend) = SignedInWithFriend();
        var req = fake.SimulateIncomingDrawRequest(friend.Id, DrawDifficulty.Easy, "cat");
        var state = await fake.AcceptDrawRequestAsync(req.Id);

        await fake.DeleteDrawGameAsync(state.Summary.Id);
        Assert.Empty(await fake.GetDrawGamesAsync());
    }

    [Fact]
    public void SubscribeInbox_routes_a_draw_invite()
    {
        var (fake, _, friend) = SignedInWithFriend();
        var received = new List<InboxMessage>();
        using var sub = fake.SubscribeInbox(received.Add);

        fake.SimulateInbox(new InboxMessage(InboxKind.DrawInvite, friend.Id, "bob", RequestId: Guid.NewGuid()));
        Assert.Single(received);
        Assert.Equal(InboxKind.DrawInvite, received[0].Kind);
        Assert.Equal(friend.Id, received[0].FromUserId);
    }
}
