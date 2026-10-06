namespace Perch.Data.Roost;

/// <summary>Where a pane sits in the Roost's left-rail attention queue, most urgent first.</summary>
public enum RoostGroup
{
    /// <summary>Blocked on the user: <see cref="SessionStatus.AwaitingInput"/> or <see cref="SessionStatus.ApiError"/>.</summary>
    NeedsYou = 0,
    /// <summary>Finished and unreviewed: <see cref="SessionStatus.NeedsAttention"/>.</summary>
    DoneReview = 1,
    /// <summary><see cref="SessionStatus.Running"/>.</summary>
    Working = 2,
    /// <summary><see cref="SessionStatus.Idle"/>, and ended sessions while they linger.</summary>
    Quiet = 3,
    /// <summary>Dormant panes (<see cref="RoostPane.IsDormant"/>): sessions with no process that can be picked back up —
    /// the ones Perch had open when it closed, the ones a tab still holds, and the Recent list.</summary>
    Recent = 4,
}

/// <summary>Why a dormant pane is in the Roost, most specific first — what its pill says.</summary>
public enum RoostDormantKind
{
    /// <summary>A Perch session Perch still holds with no process: it was running when Perch closed (an Exit, an update, a
    /// restart), or its process ended without the user ending it. It sits where a live one would, not in the Recent
    /// list (<see cref="RoostDormant.IsHeld"/>).</summary>
    WasOpenInPerch = 0,
    /// <summary>It died without a clean exit (a restart, a crash).</summary>
    Interrupted = 1,
    /// <summary>It ended within the shutdown window of a device shutdown.</summary>
    BeforeShutdown = 2,
    /// <summary>It ended normally.</summary>
    Ended = 3,
    /// <summary>It ended with <c>/exit</c>.</summary>
    Exited = 4,
    /// <summary>Nothing more is known: a tab holds it, or it's open dormant in a Perch window.</summary>
    NotRunning = 5,
}

/// <summary>A session the Roost shows <b>dormant</b> (docs/session-recovery-plan.md, R6): its conversation, no process.
/// The app supplies these to <see cref="RoostRoster.Update"/>; the first send resumes it. <paramref name="JustBeforeShutdown"/>
/// says it ended within a shutdown's window even when <paramref name="Kind"/> names something more specific (a restart's
/// victim is <see cref="RoostDormantKind.Interrupted"/>) — the Recent list's "Before shutdown" filter.</summary>
public sealed record RoostDormant(
    string SessionId,
    string Cwd,
    string ProjectName,
    string? Title,
    DateTime LastActive,
    RoostDormantKind Kind,
    bool PerchOrigin = false,
    bool JustBeforeShutdown = false)
{
    /// <summary>Badged and sorted first in the Recent list: interrupted, or ended just before a shutdown.</summary>
    public bool IsFlagged => Kind is RoostDormantKind.Interrupted or RoostDormantKind.BeforeShutdown;

    /// <summary>Cut off rather than ended: it died without a clean exit, or Perch closing ended it.</summary>
    public bool WasInterrupted => Kind is RoostDormantKind.Interrupted or RoostDormantKind.WasOpenInPerch;

    /// <summary>A Perch session Perch still holds: it keeps its place among the live sessions (the rail's Quiet group,
    /// any tab) rather than going to the Recent list, until it's woken or the user ends it.</summary>
    public bool IsHeld => Kind == RoostDormantKind.WasOpenInPerch;

    /// <summary>Ended within a shutdown's window, whatever its kind.</summary>
    public bool EndedBeforeShutdown => JustBeforeShutdown || Kind == RoostDormantKind.BeforeShutdown;

    /// <summary>The snapshot a dormant pane carries: keyed <see cref="RoostToken.DormantKey"/>, idle, never live.</summary>
    internal ClaudeSession ToSession() => new(
        RoostToken.DormantKey(SessionId), SessionId, SessionStatus.Idle, Cwd, ProjectName, LastActive,
        Title: Title, PerchControlled: PerchOrigin);
}

/// <summary>
/// One pane in the Roost: the latest snapshot of a session, keyed by <see cref="Key"/> (the process id — stable
/// across a <c>/clear</c>, which swaps the session id under the same process). An <see cref="Ended"/> pane keeps
/// its last live snapshot while it lingers. A <see cref="IsDormant"/> pane has no process at all: its key is
/// <see cref="RoostToken.DormantKey"/> and <see cref="Dormant"/> says why it's here.
/// </summary>
public sealed record RoostPane(string Key, ClaudeSession Session, RoostGroup Group, DateTime? EndedAt, RoostDormant? Dormant = null)
{
    /// <summary>True once the session has left the scan (its process exited); the pane lingers greyed.</summary>
    public bool Ended => EndedAt is not null;

    /// <summary>Shown with no process: the first send resumes it. Neither live nor ended.</summary>
    public bool IsDormant => Dormant is not null;

    /// <summary>A running session (not ended, not dormant).</summary>
    public bool IsLive => !Ended && !IsDormant;
}

/// <summary>How the Roost's left rail orders its sessions (a persisted toggle in the rail's header).</summary>
public enum RoostRailSort
{
    /// <summary>Grouped by urgency: Needs you → Done · review → Working → Quiet (<see cref="RoostRoster.Rail"/>).</summary>
    Status = 0,
    /// <summary>One flat list by name (<see cref="RoostRoster.RailAlphabetical"/>).</summary>
    Alphabetical = 1,
}

/// <summary>A rail heading and its panes, in rail order.</summary>
public sealed record RoostRailGroup(RoostGroup Group, IReadOnlyList<RoostPane> Panes);

/// <summary>
/// The Roost's session roster (UI-free, unit-tested): folds each monitor scan into a stable pane list plus
/// the urgency-ranked rail.
///
/// <para><b>Panes never reorder.</b> <see cref="Panes"/> is first-seen order and a status change never moves a
/// pane — reordering under someone typing a reply is hostile. Urgency is expressed by <see cref="Rail"/>
/// instead, which is regrouped on every scan.</para>
///
/// <para><b>Ended sessions linger</b> for <see cref="EndedLinger"/>, greyed in the Quiet group, then drop. A
/// session reappearing under the same key while lingering simply comes back to life in place.</para>
///
/// <para><b>Closed panes</b> are hidden until their session leaves the scan; the closed set is pruned to keys
/// still present, so it never grows without bound and a fresh session (a new process) is never pre-closed.
/// The set round-trips through settings via <see cref="PersistedClosed"/> as <c>pid/sessionId</c> tokens, never
/// bare pids: after a restart a token only re-hides a pane whose process <em>and</em> session id both match, so a
/// recycled pid can't pre-hide an unrelated session. <see cref="ClosedChanged"/> fires whenever that persisted
/// form moves — a close or reopen, and also a prune, a <c>/clear</c> or a seed resolving — so the file on disk
/// stays pruned too (review fixes CP26).</para>
///
/// <para><b>Dormant panes</b> (docs/session-recovery-plan.md, R6) are sessions with no process, supplied by the app with
/// each <see cref="Update"/> and gone as soon as it stops supplying one. They're the continuity across a process
/// ending: a dormant pane takes over the slot of the ended pane of the same conversation, and a live process takes
/// over a dormant (or ended) one's — reported through <see cref="Adopted"/>, so a tab region keeps its session
/// from live to dormant (Perch closed, a reboot) and back (the first send, or a <c>--resume</c> anywhere).</para>
///
/// <para>Lives in the app (not the window) so the closed set survives closing and reopening the Roost. Where a pane
/// is shown is the tabs' business (<see cref="RoostTabSet"/>), not the roster's.</para>
/// </summary>
public sealed class RoostRoster
{
    public static readonly TimeSpan EndedLinger = TimeSpan.FromMinutes(10);

    private sealed class Entry
    {
        public required ClaudeSession Session;
        public DateTime? EndedAt;
        // Set for a dormant pane, with its place in the app's dormant list (the Recent group's order).
        public RoostDormant? Dormant;
        public int DormantRank;
        public bool IsLive => EndedAt is null && Dormant is null;
    }

    // Insertion-ordered: _order is first-seen (an adoption puts the new key in the ended one's place), _entries
    // the state by key.
    private readonly List<string> _order = [];
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly HashSet<string> _closed = new(StringComparer.Ordinal);
    // Persisted closed tokens not yet matched: pid -> the session id it was closed under. Resolved (or dropped)
    // the first time that pid shows up in a scan.
    private readonly Dictionary<string, string> _seeds = new(StringComparer.Ordinal);
    private string _persistedSig = "";

    private IReadOnlyList<RoostPane> _panes = [];
    private IReadOnlyList<RoostRailGroup> _rail = [];

    /// <param name="persistedClosed">A persisted closed set (<see cref="PersistedClosed"/> tokens).</param>
    public RoostRoster(IEnumerable<string?>? persistedClosed = null)
    {
        AddSeeds(persistedClosed ?? []);
        _persistedSig = Signature(persistedClosed ?? []);
    }

    /// <summary>The persisted closed set (<see cref="PersistedClosed"/>) changed — save it.</summary>
    public event Action? ClosedChanged;

    /// <summary>Visible panes (closed ones excluded) in stable first-seen order.</summary>
    public IReadOnlyList<RoostPane> Panes => _panes;

    /// <summary>The rail: every group in urgency order (a group may be empty). Within <see cref="RoostGroup.NeedsYou"/>
    /// the longest-waiting pane leads; <see cref="RoostGroup.Recent"/> keeps the app's dormant order; other groups keep
    /// first-seen order, with ended panes last in Quiet.</summary>
    public IReadOnlyList<RoostRailGroup> Rail => _rail;

    /// <summary>The rail as one flat list: live panes by <see cref="ClaudeSession.DisplayName"/> (case-insensitive;
    /// ties keep first-seen order), then dormant panes, then ended panes, each the same way.</summary>
    public IReadOnlyList<RoostPane> RailAlphabetical { get; private set; } = [];

    /// <summary>A persisted sort read back as a real one: a value no build defines falls back to
    /// <see cref="RoostRailSort.Status"/>.</summary>
    public static RoostRailSort Normalize(RoostRailSort sort) => Enum.IsDefined(sort) ? sort : RoostRailSort.Status;

    /// <summary>Panes that entered <see cref="RoostGroup.NeedsYou"/> in the latest <see cref="Update"/> (weren't
    /// in it after the previous one) — a new "needs you" arrival, which flashes an inactive Roost's taskbar.</summary>
    public IReadOnlyList<string> NeedsYouArrivals { get; private set; } = [];

    /// <summary>The panes the latest <see cref="Update"/> re-keyed: ended key → the live key that took its place
    /// (Take over in Perch, or a <c>--resume</c> while the old pane lingered). Anything holding pane keys — the
    /// Roost's tabs — follows the move.</summary>
    public IReadOnlyDictionary<string, string> Adopted => _adopted;

    private readonly Dictionary<string, string> _adopted = new(StringComparer.Ordinal);

    private HashSet<string> _needsYouBefore = new(StringComparer.Ordinal);

    /// <summary>The closed pane keys (pids). Pruned to keys the roster still holds.</summary>
    public IReadOnlyCollection<string> ClosedKeys => _closed;

    /// <summary>The closed set as it's persisted: one <c>pid/sessionId</c> token per closed pane, in first-seen
    /// order. Seeds that haven't matched a live session yet are left out, so a save prunes them.</summary>
    public IReadOnlyList<string> PersistedClosed =>
        _order.Where(_closed.Contains).Select(k => RoostToken.Format(k, _entries[k].Session.SessionId)).ToList();

    /// <summary>The hidden (closed) panes whose sessions are still live, in first-seen order — what a "N hidden"
    /// menu offers to reopen.</summary>
    public IReadOnlyList<RoostPane> ClosedPanes { get; private set; } = [];

    /// <summary>Restores a persisted closed set (the roster can exist before settings load). A token only hides
    /// the pane whose pid and session id both match it; one that never matches is dropped from the next save.
    /// Bare pids (the pre-CP26 format) can't be trusted after a restart and are ignored, as are malformed or null
    /// entries (a hand-edited file).</summary>
    public void SeedClosed(IEnumerable<string?> tokens)
    {
        var list = tokens.ToList();
        AddSeeds(list);
        _persistedSig = Signature(list);
        ResolveSeeds();
        Rebuild();
        RaiseIfClosedChanged();
    }

    private void AddSeeds(IEnumerable<string?> tokens)
    {
        foreach (var t in tokens)
            if (RoostToken.Parse(t) is { } p) _seeds[p.Pid] = p.SessionId;
    }

    // A seed meets its pid: it closes the pane only if the session id matches too (else the pid was recycled).
    private void ResolveSeeds()
    {
        if (_seeds.Count == 0) return;
        foreach (var key in _order)
        {
            if (!_seeds.Remove(key, out var sessionId)) continue;
            var e = _entries[key];
            if (e.EndedAt is null && e.Session.SessionId == sessionId) _closed.Add(key);
        }
    }

    private static string Signature(IEnumerable<string?> tokens) => string.Join("\n", tokens);

    private void RaiseIfClosedChanged()
    {
        var sig = Signature(PersistedClosed);
        if (sig == _persistedSig) return;
        _persistedSig = sig;
        ClosedChanged?.Invoke();
    }

    /// <summary>Folds a scan into the roster. <paramref name="now"/> ages lingering ended panes.
    /// <paramref name="dormant"/> is every session to show dormant, in the Recent group's order (null = none): one
    /// that's live in <paramref name="live"/> is left out, and a dormant pane the list no longer names goes at once.</summary>
    public void Update(IReadOnlyList<ClaudeSession> live, DateTime now, IReadOnlyList<RoostDormant>? dormant = null)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var liveSessions = new HashSet<string>(StringComparer.Ordinal);
        foreach (var s in live)
        {
            if (RoostToken.IsDormantKey(s.Pid) || !seen.Add(s.Pid)) continue;   // defensive: one pane per process
            if (!string.IsNullOrEmpty(s.SessionId)) liveSessions.Add(s.SessionId);
            if (_entries.TryGetValue(s.Pid, out var e))
            {
                e.Session = s;
                e.EndedAt = null;             // back from the dead (or never left)
            }
            else
            {
                _entries[s.Pid] = new Entry { Session = s };
                _order.Add(s.Pid);
            }
        }

        // Anything that left the scan starts (or keeps) lingering. Dormant panes never ran, so never end.
        foreach (var key in _order)
            if (!seen.Contains(key) && _entries[key].Dormant is null) _entries[key].EndedAt ??= now;

        // Dormant panes: one per conversation, and never one that's live (the live pane takes its slot below).
        var dormantKept = new HashSet<string>(StringComparer.Ordinal);
        foreach (var d in dormant ?? [])
        {
            if (string.IsNullOrEmpty(d.SessionId) || liveSessions.Contains(d.SessionId)) continue;
            var key = RoostToken.DormantKey(d.SessionId);
            if (!dormantKept.Add(key)) continue;
            if (_entries.TryGetValue(key, out var e))
            {
                e.Session = d.ToSession();
                e.Dormant = d;
                e.DormantRank = dormantKept.Count;
            }
            else
            {
                _entries[key] = new Entry { Session = d.ToSession(), Dormant = d, DormantRank = dormantKept.Count };
                _order.Add(key);
            }
        }

        ResolveSeeds();
        _adopted.Clear();
        AdoptContinuations(dormantKept);

        // Dormant panes the app stopped naming, and expired or closed lingering panes, drop.
        for (int i = _order.Count - 1; i >= 0; i--)
        {
            var key = _order[i];
            if (seen.Contains(key)) continue;
            var e = _entries[key];
            bool drop = e.Dormant is not null
                ? !dormantKept.Contains(key)
                : _closed.Contains(key) || now - e.EndedAt!.Value >= EndedLinger;
            if (drop)
            {
                _entries.Remove(key);
                _order.RemoveAt(i);
            }
        }

        _closed.IntersectWith(_entries.Keys);
        Rebuild();

        var needsYou = _rail[(int)RoostGroup.NeedsYou].Panes.Select(p => p.Key).ToHashSet(StringComparer.Ordinal);
        NeedsYouArrivals = needsYou.Where(k => !_needsYouBefore.Contains(k)).ToList();
        _needsYouBefore = needsYou;
        RaiseIfClosedChanged();
    }

    // The same conversation continuing in another pane takes over the old pane's slot rather than appending a second
    // pane at the end: the newcomer moves into the old one's position and the old one goes (the tabs follow it through
    // Adopted). A live process takes over an ended or dormant pane — "Take over in Perch" (the terminal process stops,
    // Perch resumes the session id), a dormant pane's first send, or any `claude --resume` of the session. A dormant
    // pane takes over an ended one: the process went, the place it had stays.
    private void AdoptContinuations(HashSet<string> dormantKept)
    {
        Adopt(old => !old.IsLive, (_, e) => e.IsLive);
        Adopt(old => old.EndedAt is not null, (key, e) => e.Dormant is not null && dormantKept.Contains(key));
    }

    private void Adopt(Func<Entry, bool> isOld, Func<string, Entry, bool> isNew)
    {
        for (int i = 0; i < _order.Count; i++)
        {
            var oldKey = _order[i];
            var old = _entries[oldKey];
            if (!isOld(old) || string.IsNullOrEmpty(old.Session.SessionId)) continue;
            int j = _order.FindIndex(k => k != oldKey && isNew(k, _entries[k])
                                          && _entries[k].Session.SessionId == old.Session.SessionId);
            if (j < 0) continue;
            var newKey = _order[j];
            _order[i] = newKey;
            _order.RemoveAt(j);
            _entries.Remove(oldKey);
            _closed.Remove(oldKey);
            _adopted[oldKey] = newKey;
            if (j < i) i--;   // the slot we filled shifted left
        }
    }

    /// <summary>Hides a pane. Returns true when the closed set changed. A dormant pane can't be closed (it isn't running,
    /// so there's nothing to hide it from): the app dismisses it instead.</summary>
    public bool Close(string key)
    {
        if (RoostToken.IsDormantKey(key) || !_entries.ContainsKey(key) || !_closed.Add(key)) return false;
        Rebuild();
        RaiseIfClosedChanged();
        return true;
    }

    /// <summary>Un-hides a closed pane. Returns true when the closed set changed.</summary>
    public bool Reopen(string key)
    {
        if (!_closed.Remove(key)) return false;
        Rebuild();
        RaiseIfClosedChanged();
        return true;
    }

    /// <summary>The visible pane with <paramref name="key"/>, or null.</summary>
    public RoostPane? Find(string key) => _panes.FirstOrDefault(p => p.Key == key);

    /// <summary>
    /// The next pane wanting the user after <paramref name="afterKey"/>: walks <see cref="RoostGroup.NeedsYou"/>
    /// then <see cref="RoostGroup.DoneReview"/> in rail order, wrapping. With no (or an unknown) key, the first
    /// candidate. Null when nothing wants the user.
    /// </summary>
    public string? NextNeedingYou(string? afterKey)
    {
        var candidates = _rail
            .Where(g => g.Group is RoostGroup.NeedsYou or RoostGroup.DoneReview)
            .SelectMany(g => g.Panes)
            .Select(p => p.Key)
            .ToList();
        if (candidates.Count == 0) return null;
        int at = afterKey is null ? -1 : candidates.IndexOf(afterKey);
        return candidates[(at + 1) % candidates.Count];
    }

    /// <summary>The rail group a session's status (or an ended pane) falls into.</summary>
    public static RoostGroup GroupFor(SessionStatus status, bool ended) =>
        ended ? RoostGroup.Quiet : status switch
        {
            SessionStatus.AwaitingInput or SessionStatus.ApiError => RoostGroup.NeedsYou,
            SessionStatus.NeedsAttention => RoostGroup.DoneReview,
            SessionStatus.Running => RoostGroup.Working,
            _ => RoostGroup.Quiet,
        };

    private void Rebuild()
    {
        var panes = new List<RoostPane>(_order.Count);
        var closed = new List<RoostPane>();
        foreach (var key in _order)
        {
            var e = _entries[key];
            var group = e.Dormant is { IsHeld: false } ? RoostGroup.Recent : GroupFor(e.Session.Status, e.EndedAt is not null);
            var pane = new RoostPane(key, e.Session, group, e.EndedAt, e.Dormant);
            (_closed.Contains(key) ? closed : panes).Add(pane);
        }
        _panes = panes;
        ClosedPanes = closed;

        var rail = new List<RoostRailGroup>(5);
        foreach (var g in Enum.GetValues<RoostGroup>())
        {
            IEnumerable<RoostPane> members = panes.Where(p => p.Group == g);
            members = g switch
            {
                // Longest-waiting first. ApiError carries no AwaitingSince; its LastUpdated is when it stopped.
                // OrderBy is stable, so ties keep first-seen order.
                RoostGroup.NeedsYou => members.OrderBy(p => p.Session.AwaitingSince ?? p.Session.LastUpdated),
                RoostGroup.Quiet => members.OrderBy(p => p.Ended),
                RoostGroup.Recent => members.OrderBy(p => _entries[p.Key].DormantRank),
                _ => members,
            };
            rail.Add(new RoostRailGroup(g, members.ToList()));
        }
        _rail = rail;
        // OrderBy is stable, so equal names keep first-seen order.
        RailAlphabetical = panes.OrderBy(p => p.Ended ? 2 : p.Dormant is { IsHeld: false } ? 1 : 0)
            .ThenBy(p => p.Session.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
