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
    /// When <paramref name="privateWindow"/> is true the authorize page opens in a private/incognito browser
    /// window, so the provider (GitHub) doesn't silently reuse the browser's logged-in account and the user
    /// can choose which account to sign in with.
    /// </summary>
    Task<AuthState> SignInAsync(bool privateWindow = false, CancellationToken ct = default);

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

    /// <summary>Removes the friendship edge between you and <paramref name="otherUserId"/> outright — whether
    /// it's an accepted friend or a request you sent that you want to cancel. Deletes the edge in whichever
    /// direction it was stored; either party may do it. Idempotent (no edge → no-op). Unlike <see
    /// cref="BlockAsync"/> it leaves no trace: the other person can send a fresh request afterwards.</summary>
    Task RemoveFriendAsync(Guid otherUserId, CancellationToken ct = default);

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

    // ── Networked Connect 4 ("play a friend") ────────────────────────────────────────────────────────
    // A game is between two people who are already accepted friends; the creator plays red and moves first.
    // The server is authoritative — every move is validated in the database, so the client only proposes
    // moves and renders the state it gets back.

    /// <summary>Invites <paramref name="opponentUserId"/> (an accepted friend) to a game — the normal way to
    /// start one. You make your first move <em>before</em> sending, so <paramref name="firstColumn"/> (0–6) is
    /// carried on the invite and applied the instant they accept — the invitee is never left staring at an empty
    /// board waiting for you to move. No game exists yet: a request is created (and broadcast for instant
    /// delivery) and the game is only born when they accept (see <see cref="AcceptGameRequestAsync"/>). You play
    /// red and moved first. Throws <see cref="SocialException"/> if they aren't a friend or an invite to them is
    /// already outstanding.</summary>
    Task<GameRequest> RequestGameAsync(Guid opponentUserId, int firstColumn, CancellationToken ct = default);

    /// <summary>Your outstanding game invites — both the ones you've sent and the ones waiting on you.</summary>
    Task<IReadOnlyList<GameRequest>> GetGameRequestsAsync(CancellationToken ct = default);

    /// <summary>Accepts an invite you received, which creates the game (seeded with the inviter's first move so
    /// it is immediately your turn) and removes the request. The inviter is notified instantly over their inbox.
    /// Returns the new game's state. Throws <see cref="SocialException"/> if you're not the invitee or it's gone.</summary>
    Task<GameState> AcceptGameRequestAsync(Guid requestId, CancellationToken ct = default);

    /// <summary>Declines an invite you received, or cancels one you sent — either way the request is removed.
    /// Idempotent (a request that's already gone is a no-op).</summary>
    Task DeclineGameRequestAsync(Guid requestId, CancellationToken ct = default);

    /// <summary>Creates a live game with <paramref name="opponentUserId"/> directly, bypassing the invite
    /// handshake — used by the developer testing tool (which opens both boards at once). Real invites and
    /// rematches go through the compose → <see cref="RequestGameAsync"/> flow instead, so the opener plays their
    /// first move before it's sent. You are red and move first. Throws <see cref="SocialException"/> if they
    /// aren't an accepted friend.</summary>
    Task<GameSummary> CreateGameAsync(Guid opponentUserId, CancellationToken ct = default);

    /// <summary>Your Connect 4 games (both players are you-or-a-friend, so RLS returns only your own),
    /// most-recently-active first — the "your turn / their turn / finished" list.</summary>
    Task<IReadOnlyList<GameSummary>> GetGamesAsync(CancellationToken ct = default);

    /// <summary>The full state of one game — its summary plus the ordered move list to rebuild the board.
    /// Throws <see cref="SocialException"/> if the game doesn't exist or isn't yours.</summary>
    Task<GameState> GetGameAsync(Guid gameId, CancellationToken ct = default);

    /// <summary>Drops your disc into <paramref name="column"/> (0–6) in <paramref name="gameId"/>. The move is
    /// validated server-side; returns the updated state. Throws <see cref="SocialException"/> if it isn't your
    /// turn, the column is full, or the game is over.</summary>
    Task<GameState> DropAsync(Guid gameId, int column, CancellationToken ct = default);

    /// <summary>Resigns <paramref name="gameId"/>, conceding the win to your opponent. Idempotent on an
    /// already-finished game. Returns the updated state.</summary>
    Task<GameState> ResignGameAsync(Guid gameId, CancellationToken ct = default);

    /// <summary>Permanently removes <paramref name="gameId"/> and its moves. Either player may remove a shared
    /// game; idempotent (a game that's already gone is a no-op). Old finished games are also pruned server-side
    /// on a retention schedule, so this is for tidying up now rather than a requirement.</summary>
    Task DeleteGameAsync(Guid gameId, CancellationToken ct = default);

    /// <summary>Subscribes to live changes for one game, invoking <paramref name="onChanged"/> (off the UI
    /// thread) whenever a move lands so the caller re-fetches. Returns a handle whose disposal unsubscribes.
    /// A client may implement this as a no-op and rely on polling.</summary>
    IDisposable SubscribeGame(Guid gameId, Action onChanged);

    /// <summary>Subscribes to the signed-in user's transient broadcast inbox, invoking <paramref name="onMessage"/>
    /// (off the UI thread) for each incoming invite / invite-response / nudge, so the app reacts instantly rather
    /// than waiting for the next poll. Returns a handle whose disposal unsubscribes. Best-effort: a client may
    /// implement this as a no-op, in which case the games/requests poll still surfaces everything (just slower).</summary>
    IDisposable SubscribeInbox(Action<InboxMessage> onMessage);

    /// <summary>Nudges <paramref name="opponentUserId"/> that it is their turn in <paramref name="gameId"/> —
    /// delivered to their inbox as a transient "your turn" broadcast (no DB write). Best-effort and idempotent to
    /// spam from the caller's point of view; a failure is swallowed. Only meaningful while it's their turn.</summary>
    Task SendNudgeAsync(Guid gameId, Guid opponentUserId, CancellationToken ct = default);
}

/// <summary>A Social operation failed in a way the UI should surface (handle taken, body too long, not
/// signed in, backend rejected the request). Distinct from transient network errors, which surface as empty
/// results the caller retries.</summary>
public class SocialException : Exception
{
    public SocialException(string message) : base(message) { }
}

/// <summary>The backend rejected a token because its validity window (iat/nbf) is still in the future — a
/// clock-skew race just after minting. A subtype of <see cref="SocialException"/> so existing handlers still
/// catch it, but distinct enough for a wait-and-retry before giving up.</summary>
public sealed class TokenNotYetValidException : SocialException
{
    public TokenNotYetValidException(string message) : base(message) { }
}
