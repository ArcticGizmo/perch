namespace Perch.Social;

/// <summary>
/// The Social feature's whole surface to the backend: sign-in, the friend graph, posting a status, and
/// reading the friends' feed (poll now, live later). One implementation talks to Supabase
/// (<c>SupabaseSocialClient</c>, added in M2); <see cref="FakeSocialClient"/> is the in-memory stand-in that
/// tests and the <c>render</c> preview drive without a network.
///
/// This is a network seam, not an OS one, so it is <em>not</em> resolved through <c>PlatformServices</c> —
/// the app composes the single client for all OSes (the only per-OS piece is <see cref="Perch.Platform.ISecretStore"/>,
/// where the refresh token lives). All methods run off the UI thread and are best-effort: a network failure
/// surfaces as an empty result or a thrown <see cref="SocialException"/> the caller handles, never a crash.
///
/// <para><b>Authorization is the server's job.</b> The client only ever sees what the signed-in user is
/// permitted to see — the feed already excludes non-friends because the database's row-level security
/// filters it. The fake enforces the same rule so tests prove the contract, not just the wire calls.</para>
/// </summary>
public interface ISocialClient
{
    /// <summary>The current sign-in state without a round-trip (from the cached session/token).</summary>
    AuthState Current { get; }

    /// <summary>Raised (off the UI thread) whenever <see cref="Current"/> changes — sign-in, sign-out, or a
    /// handle being claimed. The UI marshals to the dispatcher itself.</summary>
    event Action<AuthState>? AuthChanged;

    /// <summary>
    /// Launches the OAuth sign-in (system browser + loopback redirect) and persists the resulting refresh
    /// token via <see cref="Perch.Platform.ISecretStore"/>. Returns the new state — signed-in, but with a
    /// null <see cref="AuthState.Me"/> if the user hasn't claimed a handle yet (the caller then prompts).
    /// </summary>
    Task<AuthState> SignInAsync(CancellationToken ct = default);

    /// <summary>Clears the stored token and local session, returning to <see cref="AuthState.SignedOut"/>.</summary>
    Task SignOutAsync(CancellationToken ct = default);

    /// <summary>The signed-in user's own profile, or null if signed out / no handle claimed.</summary>
    Task<Profile?> GetMeAsync(CancellationToken ct = default);

    /// <summary>Claims (or updates) the signed-in user's handle + optional display name/mood, creating the
    /// profile row on first claim. Throws <see cref="SocialException"/> if the handle is taken or malformed.</summary>
    Task<Profile> ClaimHandleAsync(string handle, string? displayName = null, string? moodEmoji = null,
        CancellationToken ct = default);

    /// <summary>Finds a profile by its <em>exact</em> handle (no partial/browse — you must know it to add
    /// someone), or null if no such handle exists.</summary>
    Task<Profile?> FindByHandleAsync(string handle, CancellationToken ct = default);

    /// <summary>Sends a friend request to <paramref name="addresseeId"/>. Idempotent — re-sending an existing
    /// pending request is a no-op.</summary>
    Task SendRequestAsync(Guid addresseeId, CancellationToken ct = default);

    /// <summary>Accepts (<paramref name="accept"/> = true) or declines an incoming request from
    /// <paramref name="requesterId"/>. Only the addressee of a pending request may call this.</summary>
    Task RespondAsync(Guid requesterId, bool accept, CancellationToken ct = default);

    /// <summary>The signed-in user's friend graph — accepted friends plus pending/incoming requests, each
    /// tagged with its <see cref="FriendshipState"/>.</summary>
    Task<IReadOnlyList<Friend>> GetFriendsAsync(CancellationToken ct = default);

    /// <summary>Posts a status (1–280 chars, optional mood). Manual only — nothing is ever posted on the
    /// user's behalf. Throws <see cref="SocialException"/> on an empty/over-long body.</summary>
    Task<PostId> PostAsync(string body, string? moodEmoji = null, CancellationToken ct = default);

    /// <summary>The most recent <paramref name="limit"/> feed items — the signed-in user's own posts and
    /// accepted friends' posts, newest first. Excludes everyone else (enforced server-side).</summary>
    Task<IReadOnlyList<FeedItem>> GetFeedAsync(int limit = 50, CancellationToken ct = default);

    /// <summary>The friends roster the overlay's social region shows: your own profile plus one entry per
    /// accepted friend (their latest visible status and its reactions), ordered most-recently-active first.
    /// Composed from the friend graph + feed + reactions in one call.</summary>
    Task<RosterSnapshot> GetRosterAsync(CancellationToken ct = default);

    /// <summary>Adds (<paramref name="on"/> = true) or removes your <paramref name="emoji"/> reaction on a
    /// post you can see. Idempotent in both directions. Only your own reaction is affected.</summary>
    Task ReactAsync(Guid postId, string emoji, bool on, CancellationToken ct = default);

    /// <summary>Blocks <paramref name="userId"/>: their posts vanish from your feed and yours from theirs, in
    /// both directions, regardless of any friendship. Idempotent. The block is one-sided and private — the
    /// blocked user is never told and cannot undo it.</summary>
    Task BlockAsync(Guid userId, CancellationToken ct = default);

    /// <summary>Removes a block you placed, restoring normal visibility (an accepted friendship, if any, works
    /// again). Idempotent.</summary>
    Task UnblockAsync(Guid userId, CancellationToken ct = default);

    /// <summary>The profiles you have blocked (for the "unblock" affordance). Never includes people who blocked
    /// you — blocks are private.</summary>
    Task<IReadOnlyList<Profile>> GetBlockedAsync(CancellationToken ct = default);

    /// <summary>Reports <paramref name="userId"/> to moderation with an optional reason (≤500 chars). Write-only:
    /// the report is invisible to other users. Reporting does not block — the caller usually does both.</summary>
    Task ReportAsync(Guid userId, string? reason = null, CancellationToken ct = default);

    /// <summary>
    /// Subscribes to live feed inserts, invoking <paramref name="onPost"/> (off the UI thread) for each new
    /// post from you or an accepted friend. Returns a handle whose disposal unsubscribes. Real-time is wired
    /// in M5; until then a client may implement this as a no-op and rely on <see cref="GetFeedAsync"/> polling.
    /// </summary>
    IDisposable SubscribeFeed(Action<FeedItem> onPost);
}

/// <summary>A Social operation failed in a way the UI should surface (handle taken, body too long, not
/// signed in, backend rejected the request). Distinct from transient network errors, which surface as empty
/// results the caller retries.</summary>
public sealed class SocialException : Exception
{
    public SocialException(string message) : base(message) { }
}
