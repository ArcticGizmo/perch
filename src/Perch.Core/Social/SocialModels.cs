namespace Perch.Social;

/// <summary>
/// A public-facing Social identity — the whole of a user's profile. Deliberately minimal (no real name, no
/// email): the handle is how friends find and recognise each other, and a mood emoji is optional flavour.
/// The email used to sign in lives with the auth provider, never here. See <c>docs/social-feed-plan.md</c>.
/// </summary>
/// <param name="Id">Stable server id (maps to the Supabase <c>profiles.id</c> / auth user).</param>
/// <param name="Handle">Unique lowercase handle, 3–20 of <c>[a-z0-9_]</c> (no leading <c>@</c>).</param>
/// <param name="DisplayName">Optional friendly name, ≤40 chars; null when the user set none.</param>
/// <param name="MoodEmoji">Optional current-status glyph; null when unset.</param>
public sealed record Profile(Guid Id, string Handle, string? DisplayName = null, string? MoodEmoji = null);

/// <summary>Where a friend edge stands from the signed-in user's point of view.</summary>
public enum FriendshipState
{
    /// <summary>You sent a request; waiting on them to accept.</summary>
    Pending,
    /// <summary>They sent you a request; waiting on you to accept or decline.</summary>
    Incoming,
    /// <summary>Mutually accepted — you can see each other's posts.</summary>
    Accepted,
    /// <summary>You blocked them (or they you); no posts flow either way.</summary>
    Blocked,
}

/// <summary>A person in your friend graph, with the current state of the edge to them.</summary>
public sealed record Friend(Profile Profile, FriendshipState State);

/// <summary>A single status post as it appears in the feed.</summary>
/// <param name="Id">Server id of the post.</param>
/// <param name="Author">Who posted it (you, or an accepted friend).</param>
/// <param name="Body">The status text, 1–280 characters.</param>
/// <param name="MoodEmoji">Optional mood glyph attached to the post.</param>
/// <param name="CreatedAt">When it was posted (server time).</param>
public sealed record FeedItem(Guid Id, Profile Author, string Body, string? MoodEmoji, DateTimeOffset CreatedAt);

/// <summary>One emoji's worth of reactions on a post — how many friends reacted with it, and whether you're
/// one of them (so the UI can highlight your own reaction and toggle it off on a second click).</summary>
/// <param name="Emoji">The reaction glyph (1–16 chars).</param>
/// <param name="Count">How many people (you + friends you can see) reacted with it.</param>
/// <param name="Mine">Whether the signed-in user is one of them.</param>
public sealed record ReactionGroup(string Emoji, int Count, bool Mine);

/// <summary>
/// A friend as the overlay's social region shows them: their profile (handle + mood), their single most recent
/// visible status (or null if they haven't posted), and the reactions on that status. One row per accepted
/// friend — the region is a roster of who's around and what they're up to, not a chronological feed.
/// </summary>
public sealed record RosterFriend(Profile Profile, FeedItem? Latest, IReadOnlyList<ReactionGroup> Reactions)
{
    // Compiler-generated record equality would compare Reactions by reference, so a fresh list every poll would
    // read as "changed" and force a needless relayout. Compare it by value (like MicSnapshot).
    public bool Equals(RosterFriend? other) =>
        other is not null && Profile == other.Profile && Latest == other.Latest
        && Reactions.SequenceEqual(other.Reactions);

    public override int GetHashCode()
    {
        var hc = new HashCode();
        hc.Add(Profile);
        hc.Add(Latest);
        foreach (var r in Reactions) hc.Add(r);
        return hc.ToHashCode();
    }
}

/// <summary>
/// The whole social region as pushed to the overlay: the signed-in user's own profile and their own latest
/// status (for the "you" row), plus one <see cref="RosterFriend"/> per accepted friend, ordered
/// most-recently-active first. A record so the overlay can skip a no-op repaint; value equality is hand-written
/// so a fresh list per poll doesn't relayout.
/// </summary>
/// <param name="Me">The signed-in user's own profile, or null when not yet loaded.</param>
/// <param name="MyLatest">The signed-in user's own most recent status, or null if they haven't posted.</param>
/// <param name="MyReactions">The reactions on your own latest status (so the "you" row can show what friends
/// thought of it). Empty when you have no status or nobody's reacted.</param>
/// <param name="Friends">One entry per accepted friend, most-recently-active first.</param>
/// <param name="IncomingRequests">How many pending friend requests are waiting on you — the region shows a
/// badge when this is &gt; 0.</param>
public sealed record RosterSnapshot(
    Profile? Me, FeedItem? MyLatest, IReadOnlyList<ReactionGroup> MyReactions,
    IReadOnlyList<RosterFriend> Friends, int IncomingRequests = 0)
{
    public static readonly RosterSnapshot Empty = new(null, null, [], []);

    public bool Any => Friends.Count > 0;

    public bool Equals(RosterSnapshot? other) =>
        other is not null && Me == other.Me && MyLatest == other.MyLatest
        && IncomingRequests == other.IncomingRequests
        && MyReactions.SequenceEqual(other.MyReactions) && Friends.SequenceEqual(other.Friends);

    public override int GetHashCode()
    {
        var hc = new HashCode();
        hc.Add(Me);
        hc.Add(MyLatest);
        hc.Add(IncomingRequests);
        foreach (var r in MyReactions) hc.Add(r);
        foreach (var f in Friends) hc.Add(f);
        return hc.ToHashCode();
    }
}

/// <summary>The sign-in state: whether we hold a valid session and, if so, who we are.</summary>
/// <param name="SignedIn">True once authenticated (a token is held and valid).</param>
/// <param name="Me">The signed-in profile, or null if signed out or the handle isn't claimed yet.</param>
public sealed record AuthState(bool SignedIn, Profile? Me)
{
    public static readonly AuthState SignedOut = new(false, null);
}

/// <summary>A newly created post's id, returned from <see cref="ISocialClient.PostAsync"/>.</summary>
public readonly record struct PostId(Guid Value);
