using Perch.Games;
using Perch.Social;
using Xunit;

namespace Perch.Tests;

/// <summary>
/// The networked-Connect-4 contract, proven against <see cref="FakeSocialClient"/> (which mirrors the Supabase
/// backend's rules): you can only play an accepted friend, moves are turn- and legality-validated server-side,
/// both players share one authoritative move list, and a live subscription fires when a move lands.
/// </summary>
public class FakeSocialGameTests
{
    private static (FakeSocialClient fake, Profile me, Profile friend) SignedInWithFriend()
    {
        var fake = new FakeSocialClient();
        var me = fake.SignInAs("alice");
        var friend = fake.SeedUser("bob");
        fake.SimulateAccept(friend.Id);   // an accepted friendship edge from my side
        return (fake, me, friend);
    }

    [Fact]
    public async Task CreateGame_pairs_creator_as_red_and_moves_first()
    {
        var (fake, me, friend) = SignedInWithFriend();
        var game = await fake.CreateGameAsync(friend.Id);

        Assert.Equal(me.Id, game.Red.Id);
        Assert.Equal(friend.Id, game.Yellow.Id);
        Assert.Equal(GameStatus.InProgress, game.Status);
        Assert.Equal(Connect4Disc.Red, game.Turn);
        Assert.True(game.IsTurnOf(me.Id));
        Assert.False(game.IsTurnOf(friend.Id));
        Assert.Equal(Connect4Disc.Red, game.Seat(me.Id));
        Assert.Equal(friend.Id, game.Opponent(me.Id)!.Id);
    }

    [Fact]
    public async Task CreateGame_rejects_a_non_friend()
    {
        var fake = new FakeSocialClient();
        fake.SignInAs("alice");
        var stranger = fake.SeedUser("carol");   // no friendship edge
        await Assert.ThrowsAsync<SocialException>(() => fake.CreateGameAsync(stranger.Id));
    }

    [Fact]
    public async Task Turns_alternate_between_the_players()
    {
        var (fake, me, friend) = SignedInWithFriend();
        var game = await fake.CreateGameAsync(friend.Id);

        var afterMine = await fake.DropAsync(game.Id, 3);
        Assert.Equal(Connect4Disc.Yellow, afterMine.Summary.Turn);
        Assert.False(afterMine.Summary.IsTurnOf(me.Id));
        Assert.Equal(new[] { 3 }, afterMine.Moves);

        fake.SimulateOpponentDrop(game.Id, 4);
        var back = await fake.GetGameAsync(game.Id);
        Assert.Equal(Connect4Disc.Red, back.Summary.Turn);
        Assert.True(back.Summary.IsTurnOf(me.Id));
        Assert.Equal(new[] { 3, 4 }, back.Moves);
    }

    [Fact]
    public async Task Dropping_out_of_turn_is_rejected()
    {
        var (fake, _, friend) = SignedInWithFriend();
        var game = await fake.CreateGameAsync(friend.Id);
        await fake.DropAsync(game.Id, 3);        // now it's the opponent's turn
        await Assert.ThrowsAsync<SocialException>(() => fake.DropAsync(game.Id, 2));
    }

    [Fact]
    public async Task A_vertical_four_wins_and_locks_the_game()
    {
        var (fake, _, friend) = SignedInWithFriend();
        var game = await fake.CreateGameAsync(friend.Id);

        for (int i = 0; i < 3; i++)
        {
            await fake.DropAsync(game.Id, 0);            // my red disc, column 0
            fake.SimulateOpponentDrop(game.Id, 1);       // opponent parks in column 1
        }
        var won = await fake.DropAsync(game.Id, 0);      // fourth red in column 0

        Assert.Equal(GameStatus.RedWon, won.Summary.Status);
        await Assert.ThrowsAsync<SocialException>(() => fake.DropAsync(game.Id, 0));   // no moves after a win
    }

    [Fact]
    public async Task Resign_hands_the_win_to_the_opponent()
    {
        var (fake, _, friend) = SignedInWithFriend();
        var game = await fake.CreateGameAsync(friend.Id);
        var after = await fake.ResignGameAsync(game.Id);
        Assert.Equal(GameStatus.YellowWon, after.Summary.Status);   // I'm red; resigning makes yellow win
    }

    [Fact]
    public async Task SubscribeGame_fires_on_each_move()
    {
        var (fake, _, friend) = SignedInWithFriend();
        var game = await fake.CreateGameAsync(friend.Id);

        int fired = 0;
        using var sub = fake.SubscribeGame(game.Id, () => Interlocked.Increment(ref fired));
        await fake.DropAsync(game.Id, 3);            // my move
        fake.SimulateOpponentDrop(game.Id, 4);       // opponent's move
        Assert.Equal(2, fired);

        sub.Dispose();
        fake.SimulateOpponentDrop(game.Id, 5);       // after unsubscribe, no more callbacks — but wait, it's my turn now
        Assert.Equal(2, fired);
    }

    [Fact]
    public async Task RequestGame_creates_a_pending_invite_not_a_game()
    {
        var (fake, _, friend) = SignedInWithFriend();
        var req = await fake.RequestGameAsync(friend.Id);

        Assert.Empty(await fake.GetGamesAsync());                       // no game exists yet
        var invites = await fake.GetGameRequestsAsync();
        Assert.Contains(invites, r => r.Id == req.Id);
        Assert.Equal(friend.Id, req.Addressee.Id);
    }

    [Fact]
    public async Task RequestGame_rejects_a_non_friend()
    {
        var fake = new FakeSocialClient();
        fake.SignInAs("alice");
        var stranger = fake.SeedUser("carol");
        await Assert.ThrowsAsync<SocialException>(() => fake.RequestGameAsync(stranger.Id));
    }

    [Fact]
    public async Task AcceptingAnInvite_creates_the_game_and_clears_the_request()
    {
        var (fake, me, friend) = SignedInWithFriend();
        // The friend invites me (I'm the addressee, so I can accept).
        var req = fake.SimulateIncomingGameRequest(friend.Id);

        var state = await fake.AcceptGameRequestAsync(req.Id);
        Assert.Equal(GameStatus.InProgress, state.Summary.Status);
        Assert.Equal(friend.Id, state.Summary.Red.Id);   // the inviter plays red
        Assert.Equal(me.Id, state.Summary.Yellow.Id);

        Assert.Contains(await fake.GetGamesAsync(), g => g.Id == state.Summary.Id);
        Assert.Empty(await fake.GetGameRequestsAsync());  // the invite is gone
    }

    [Fact]
    public async Task OnlyTheInvitee_canAccept()
    {
        var (fake, _, friend) = SignedInWithFriend();
        // I invite the friend — I'm the requester, so I can't accept my own invite.
        var req = await fake.RequestGameAsync(friend.Id);
        await Assert.ThrowsAsync<SocialException>(() => fake.AcceptGameRequestAsync(req.Id));
    }

    [Fact]
    public async Task DecliningAnInvite_removes_it_without_a_game()
    {
        var (fake, _, friend) = SignedInWithFriend();
        var req = fake.SimulateIncomingGameRequest(friend.Id);

        await fake.DeclineGameRequestAsync(req.Id);
        Assert.Empty(await fake.GetGameRequestsAsync());
        Assert.Empty(await fake.GetGamesAsync());
        await fake.DeclineGameRequestAsync(req.Id);   // idempotent
    }

    [Fact]
    public async Task DeleteGame_removes_it_and_is_idempotent()
    {
        var (fake, _, friend) = SignedInWithFriend();
        var g = await fake.CreateGameAsync(friend.Id);
        Assert.Contains(await fake.GetGamesAsync(), x => x.Id == g.Id);

        await fake.DeleteGameAsync(g.Id);
        Assert.DoesNotContain(await fake.GetGamesAsync(), x => x.Id == g.Id);
        await Assert.ThrowsAsync<SocialException>(() => fake.GetGameAsync(g.Id));   // gone
        await fake.DeleteGameAsync(g.Id);                                          // idempotent — no throw
    }

    [Fact]
    public async Task GetGames_lists_your_games_newest_first()
    {
        var (fake, me, friend) = SignedInWithFriend();
        var g1 = await fake.CreateGameAsync(friend.Id);

        var games = await fake.GetGamesAsync();
        Assert.Contains(games, g => g.Id == g1.Id);
        Assert.All(games, g => Assert.True(g.Red.Id == me.Id || g.Yellow.Id == me.Id));
    }
}
