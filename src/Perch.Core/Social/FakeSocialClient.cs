using System.Text.RegularExpressions;

namespace Perch.Social;

/// <summary>
/// An in-memory <see cref="ISocialClient"/> with no network — the stand-in that unit tests drive and the
/// <c>render</c> preview seeds, and the reference for how the real Supabase client must behave. It enforces
/// the same rules the backend's row-level security will: the feed shows only the signed-in user's own posts
/// and <em>accepted</em> friends' posts, handles must be well-formed and unique, and only exact-handle lookups
/// resolve. Beyond the interface it exposes a few <c>Seed*</c>/<c>Simulate*</c> helpers so a test can stand up
/// other users and have them act (send a request, accept, post).
/// </summary>
public sealed partial class FakeSocialClient : ISocialClient
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, Profile> _profiles = new();
    private readonly Dictionary<Guid, FriendshipState> _edges = new();   // other user id -> state, from "me"'s view
    private readonly List<FeedItem> _posts = new();                       // all posts by any user; feed filters
    private readonly List<Action<FeedItem>> _subscribers = new();
    private readonly HashSet<Guid> _blocked = new();                      // users I've blocked
    private readonly HashSet<Guid> _blockedByOthers = new();              // users who've blocked me (test seam)
    private readonly Dictionary<Guid, Dictionary<string, HashSet<Guid>>> _reactions = new();  // postId -> emoji -> reactors
    private Profile? _me;
    private bool _signedIn;

    public AuthState Current
    {
        get { lock (_gate) return new AuthState(_signedIn, _me); }
    }

    public event Action<AuthState>? AuthChanged;

    public Task<AuthState> SignInAsync(CancellationToken ct = default)
    {
        AuthState state;
        lock (_gate) { _signedIn = true; state = new AuthState(true, _me); }
        AuthChanged?.Invoke(state);
        return Task.FromResult(state);
    }

    public Task SignOutAsync(CancellationToken ct = default)
    {
        lock (_gate) { _signedIn = false; }
        AuthChanged?.Invoke(AuthState.SignedOut);
        return Task.CompletedTask;
    }

    public Task<Profile?> GetMeAsync(CancellationToken ct = default)
    {
        lock (_gate) return Task.FromResult(_me);
    }

    public Task<Profile> ClaimHandleAsync(string handle, string? displayName = null, string? moodEmoji = null,
        CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (!_signedIn) throw new SocialException("Sign in before claiming a handle.");
            if (!IsValidHandle(handle)) throw new SocialException("Handle must be 3–20 of a–z, 0–9 or _.");
            var existing = FindByHandleLocked(handle);
            if (existing is not null && existing.Id != _me?.Id)
                throw new SocialException($"@{handle} is already taken.");

            var id = _me?.Id ?? Guid.NewGuid();
            _me = new Profile(id, handle, displayName, moodEmoji);
            _profiles[id] = _me;
        }
        AuthChanged?.Invoke(new AuthState(true, _me));
        return Task.FromResult(_me!);
    }

    public Task<Profile?> FindByHandleAsync(string handle, CancellationToken ct = default)
    {
        lock (_gate) return Task.FromResult(FindByHandleLocked(handle));
    }

    public Task SendRequestAsync(Guid addresseeId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            RequireMe();
            if (!_edges.TryGetValue(addresseeId, out var s) || s == FriendshipState.Incoming)
                _edges[addresseeId] = FriendshipState.Pending;   // idempotent; incoming+send = accept-ish, kept pending
        }
        return Task.CompletedTask;
    }

    public Task RespondAsync(Guid requesterId, bool accept, CancellationToken ct = default)
    {
        lock (_gate)
        {
            RequireMe();
            if (_edges.TryGetValue(requesterId, out var s) && s == FriendshipState.Incoming)
            {
                if (accept) _edges[requesterId] = FriendshipState.Accepted;
                else _edges.Remove(requesterId);   // declined: forget the edge
            }
        }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<Friend>> GetFriendsAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            var list = _edges
                .Where(e => e.Value != FriendshipState.Blocked && _profiles.ContainsKey(e.Key))
                .Select(e => new Friend(_profiles[e.Key], e.Value))
                .ToList();
            return Task.FromResult<IReadOnlyList<Friend>>(list);
        }
    }

    public Task<PostId> PostAsync(string body, string? moodEmoji = null, CancellationToken ct = default)
    {
        FeedItem item;
        lock (_gate)
        {
            RequireMe();
            body = body?.Trim() ?? "";
            if (body.Length == 0) throw new SocialException("A status can't be empty.");
            if (body.Length > 280) throw new SocialException("A status can't be longer than 280 characters.");
            item = new FeedItem(Guid.NewGuid(), _me!, body, moodEmoji, DateTimeOffset.UtcNow);
            _posts.Add(item);
        }
        Notify(item);
        return Task.FromResult(new PostId(item.Id));
    }

    public Task<IReadOnlyList<FeedItem>> GetFeedAsync(int limit = 50, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var feed = _posts
                .Where(p => CanSeeLocked(p.Author.Id))
                .OrderByDescending(p => p.CreatedAt)
                .Take(Math.Max(0, limit))
                .ToList();
            return Task.FromResult<IReadOnlyList<FeedItem>>(feed);
        }
    }

    public IDisposable SubscribeFeed(Action<FeedItem> onPost)
    {
        lock (_gate) _subscribers.Add(onPost);
        return new Subscription(this, onPost);
    }

    public Task<RosterSnapshot> GetRosterAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            var entries = _edges
                .Where(e => e.Value == FriendshipState.Accepted && _profiles.ContainsKey(e.Key))
                .Select(e => _profiles[e.Key])
                .Select(p =>
                {
                    var latest = CanSeeLocked(p.Id)
                        ? _posts.Where(x => x.Author.Id == p.Id).OrderByDescending(x => x.CreatedAt).FirstOrDefault()
                        : null;
                    var rx = latest is not null ? GroupReactionsLocked(latest.Id) : (IReadOnlyList<ReactionGroup>)[];
                    return new RosterFriend(p, latest, rx);
                })
                .OrderByDescending(e => e.Latest?.CreatedAt ?? DateTimeOffset.MinValue)
                .ThenBy(e => e.Profile.Handle, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var myLatest = _me is null ? null
                : _posts.Where(x => x.Author.Id == _me.Id).OrderByDescending(x => x.CreatedAt).FirstOrDefault();
            int incoming = _edges.Count(e => e.Value == FriendshipState.Incoming);
            return Task.FromResult(new RosterSnapshot(_me, myLatest, entries, incoming));
        }
    }

    public Task ReactAsync(Guid postId, string emoji, bool on, CancellationToken ct = default)
    {
        lock (_gate)
        {
            RequireMe();
            emoji = emoji?.Trim() ?? "";
            // One reaction per user: clear my existing one first, then (if turning on) add the new emoji.
            if (_reactions.TryGetValue(postId, out var m))
                foreach (var set in m.Values) set.Remove(_me!.Id);
            if (on && emoji.Length > 0)
            {
                if (!_reactions.TryGetValue(postId, out var mm)) _reactions[postId] = mm = new();
                if (!mm.TryGetValue(emoji, out var s)) mm[emoji] = s = new();
                s.Add(_me!.Id);
            }
            // Drop any emoji buckets that emptied out.
            if (_reactions.TryGetValue(postId, out var clean))
                foreach (var key in clean.Where(kv => kv.Value.Count == 0).Select(kv => kv.Key).ToList())
                    clean.Remove(key);
        }
        return Task.CompletedTask;
    }

    public Task BlockAsync(Guid userId, CancellationToken ct = default)
    {
        lock (_gate) { RequireMe(); _blocked.Add(userId); }
        return Task.CompletedTask;
    }

    public Task UnblockAsync(Guid userId, CancellationToken ct = default)
    {
        lock (_gate) { RequireMe(); _blocked.Remove(userId); }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<Profile>> GetBlockedAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            var list = _blocked.Where(_profiles.ContainsKey).Select(id => _profiles[id]).ToList();
            return Task.FromResult<IReadOnlyList<Profile>>(list);
        }
    }

    public Task ReportAsync(Guid userId, string? reason = null, CancellationToken ct = default)
    {
        lock (_gate) RequireMe();   // write-only; the fake keeps no report store to read back
        return Task.CompletedTask;
    }

    // ── Test/preview seeding (not part of ISocialClient) ────────────────────────────────────────────

    /// <summary>Signs in as <paramref name="handle"/> in one step (sign-in + claim), for preview/test setup.</summary>
    public Profile SignInAs(string handle, string? displayName = null, string? moodEmoji = null)
    {
        SignInAsync().GetAwaiter().GetResult();
        return ClaimHandleAsync(handle, displayName, moodEmoji).GetAwaiter().GetResult();
    }

    /// <summary>Adds another user to the directory (findable by handle), without any relationship to you.</summary>
    public Profile SeedUser(string handle, string? displayName = null, string? moodEmoji = null)
    {
        lock (_gate)
        {
            if (!IsValidHandle(handle)) throw new SocialException("Invalid seed handle.");
            var p = new Profile(Guid.NewGuid(), handle, displayName, moodEmoji);
            _profiles[p.Id] = p;
            return p;
        }
    }

    /// <summary>Simulates <paramref name="fromUserId"/> sending you a friend request (shows as Incoming).</summary>
    public void SimulateIncomingRequest(Guid fromUserId)
    {
        lock (_gate) _edges[fromUserId] = FriendshipState.Incoming;
    }

    /// <summary>Simulates <paramref name="addresseeId"/> accepting a request you sent them (Pending → Accepted).</summary>
    public void SimulateAccept(Guid addresseeId)
    {
        lock (_gate) _edges[addresseeId] = FriendshipState.Accepted;
    }

    /// <summary>Simulates <paramref name="userId"/> having blocked you (their block, which you can't see) — so
    /// their posts vanish from your feed even while the friendship edge reads accepted.</summary>
    public void SimulateBlockedBy(Guid userId)
    {
        lock (_gate) _blockedByOthers.Add(userId);
    }

    /// <summary>Simulates <paramref name="reactor"/> reacting to a post (for roster/reaction tests).</summary>
    public void SimulateReaction(Guid postId, Guid reactor, string emoji)
    {
        lock (_gate)
        {
            if (!_reactions.TryGetValue(postId, out var m)) _reactions[postId] = m = new();
            if (!m.TryGetValue(emoji, out var set)) m[emoji] = set = new();
            set.Add(reactor);
        }
    }

    /// <summary>Simulates <paramref name="authorId"/> posting a status. Fires live subscribers if you can see it.</summary>
    public PostId SimulatePost(Guid authorId, string body, string? moodEmoji = null)
    {
        FeedItem item;
        bool visible;
        lock (_gate)
        {
            if (!_profiles.TryGetValue(authorId, out var author))
                throw new SocialException("Unknown seed author.");
            item = new FeedItem(Guid.NewGuid(), author, body, moodEmoji, DateTimeOffset.UtcNow);
            _posts.Add(item);
            visible = CanSeeLocked(authorId);
        }
        if (visible) Notify(item);
        return new PostId(item.Id);
    }

    // ── internals ───────────────────────────────────────────────────────────────────────────────────

    private Profile? FindByHandleLocked(string handle) =>
        _profiles.Values.FirstOrDefault(p => string.Equals(p.Handle, handle, StringComparison.OrdinalIgnoreCase));

    // Mirrors the backend rule after M6: your own post is always visible; a friend's post is visible only while
    // the edge is accepted AND neither of you has blocked the other. A block kills visibility both directions.
    private IReadOnlyList<ReactionGroup> GroupReactionsLocked(Guid postId)
    {
        if (!_reactions.TryGetValue(postId, out var m)) return [];
        return m.Where(kv => kv.Value.Count > 0)
            .Select(kv => new ReactionGroup(kv.Key, kv.Value.Count, _me is not null && kv.Value.Contains(_me.Id)))
            .OrderByDescending(g => g.Count).ThenBy(g => g.Emoji, StringComparer.Ordinal)
            .ToList();
    }

    private bool CanSeeLocked(Guid authorId)
    {
        if (authorId == _me?.Id) return true;
        if (_blocked.Contains(authorId) || _blockedByOthers.Contains(authorId)) return false;
        return _edges.TryGetValue(authorId, out var s) && s == FriendshipState.Accepted;
    }

    private void RequireMe()
    {
        if (_me is null) throw new SocialException("Claim a handle first.");
    }

    private void Notify(FeedItem item)
    {
        Action<FeedItem>[] subs;
        lock (_gate) subs = _subscribers.ToArray();
        foreach (var s in subs) s(item);
    }

    private static bool IsValidHandle(string handle) => handle is not null && HandleRegex().IsMatch(handle);

    [GeneratedRegex("^[a-z0-9_]{3,20}$")]
    private static partial Regex HandleRegex();

    private sealed class Subscription : IDisposable
    {
        private readonly FakeSocialClient _owner;
        private readonly Action<FeedItem> _cb;
        public Subscription(FakeSocialClient owner, Action<FeedItem> cb) { _owner = owner; _cb = cb; }
        public void Dispose() { lock (_owner._gate) _owner._subscribers.Remove(_cb); }
    }
}
