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

/// <summary>The sign-in state: whether we hold a valid session and, if so, who we are.</summary>
/// <param name="SignedIn">True once authenticated (a token is held and valid).</param>
/// <param name="Me">The signed-in profile, or null if signed out or the handle isn't claimed yet.</param>
public sealed record AuthState(bool SignedIn, Profile? Me)
{
    public static readonly AuthState SignedOut = new(false, null);
}

/// <summary>A newly created post's id, returned from <see cref="ISocialClient.PostAsync"/>.</summary>
public readonly record struct PostId(Guid Value);

/// <summary>
/// The feed as pushed to the overlay strip — the recent items, newest first. A record purely so the overlay
/// can skip a no-op repaint, but it hand-writes value equality (like <c>MicSnapshot</c>): the compiler-
/// generated record equality compares the <see cref="Items"/> list by <em>reference</em>, which would make
/// every fresh snapshot look different and defeat the "only relayout when it actually changed" check.
/// </summary>
public sealed record FeedSnapshot(IReadOnlyList<FeedItem> Items)
{
    public bool Any => Items.Count > 0;

    public bool Equals(FeedSnapshot? other) => other is not null && Items.SequenceEqual(other.Items);

    public override int GetHashCode()
    {
        var hc = new HashCode();
        foreach (var i in Items) hc.Add(i);
        return hc.ToHashCode();
    }
}
