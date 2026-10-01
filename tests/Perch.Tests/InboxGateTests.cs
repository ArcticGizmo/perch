using Perch.Social;
using Xunit;

namespace Perch.Tests;

/// <summary>
/// Review fixes CP4: an inbox broadcast is only surfaced when its claimed sender is one of your accepted,
/// unblocked friends, and the handle shown is always your friend list's, never the sender-written payload's.
/// </summary>
public sealed class InboxGateTests
{
    private static readonly Guid Me = Guid.NewGuid();
    private static readonly Guid Friend = Guid.NewGuid();
    private static readonly Dictionary<Guid, string> Friends = new() { [Friend] = "ada" };

    [Fact]
    public void A_friend_is_accepted_and_named_from_the_friend_list()
    {
        var raw = new InboxMessage(InboxKind.Nudge, Friend, FromHandle: "totally_the_admin", GameId: Guid.NewGuid());
        Assert.True(InboxGate.TryAccept(raw, Me, Friends, out var accepted));
        Assert.Equal("ada", accepted.FromHandle);
        Assert.Equal(raw.GameId, accepted.GameId);
        Assert.Equal(InboxKind.Nudge, accepted.Kind);
    }

    [Fact]
    public void A_stranger_is_dropped()
    {
        var raw = new InboxMessage(InboxKind.GameInvite, Guid.NewGuid(), FromHandle: "ada");
        Assert.False(InboxGate.TryAccept(raw, Me, Friends, out _));
    }

    [Fact]
    public void A_missing_or_self_sender_is_dropped()
    {
        Assert.False(InboxGate.TryAccept(new InboxMessage(InboxKind.Nudge, Guid.Empty), Me, Friends, out _));
        var withMe = new Dictionary<Guid, string>(Friends) { [Me] = "me" };
        Assert.False(InboxGate.TryAccept(new InboxMessage(InboxKind.Nudge, Me), Me, withMe, out _));
    }

    [Fact]
    public void Nobody_is_accepted_before_the_friend_list_loads()
    {
        var raw = new InboxMessage(InboxKind.Nudge, Friend);
        Assert.False(InboxGate.TryAccept(raw, Me, new Dictionary<Guid, string>(), out _));
    }
}
