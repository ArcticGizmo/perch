namespace Perch.Social;

/// <summary>What a transient inbox broadcast announces. These ride the user's Realtime broadcast inbox (see
/// <see cref="RealtimeChannel.Inbox"/>) purely to make the corresponding persistent change appear instantly —
/// they are best-effort accelerators, never the source of truth. A missed broadcast just means the next poll
/// (games/requests) surfaces the same thing a little later.</summary>
public enum InboxKind
{
    /// <summary>Someone invited you to a game — a <c>game_requests</c> row was just written for you.</summary>
    GameInvite,
    /// <summary>An invite you sent was accepted — the game now exists and it carries your first move.</summary>
    GameInviteAccepted,
    /// <summary>An invite you sent was declined (or cancelled) — the request is gone.</summary>
    GameInviteDeclined,
    /// <summary>Your opponent nudged you because it's your turn — surface a "your turn" bubble.</summary>
    Nudge,
}

/// <summary>
/// One transient message delivered over a user's Realtime broadcast inbox. Deliberately minimal: it names what
/// happened and the ids needed to react (re-fetch, open a board, show a bubble), leaving the authoritative
/// detail to the normal REST reads.
/// </summary>
/// <param name="Kind">Which event this is.</param>
/// <param name="FromUserId">The user who caused it (the inviter / accepter / nudger).</param>
/// <param name="FromHandle">Their @handle if the sender included it, for an immediate label without a lookup.</param>
/// <param name="GameId">The game this concerns, when there is one (accepted invite, nudge).</param>
/// <param name="RequestId">The invite this concerns, when there is one (invite, decline).</param>
public sealed record InboxMessage(
    InboxKind Kind, Guid FromUserId, string? FromHandle = null, Guid? GameId = null, Guid? RequestId = null);
