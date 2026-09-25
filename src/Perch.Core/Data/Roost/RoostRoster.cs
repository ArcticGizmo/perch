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
}

/// <summary>A user's manual expand/collapse pin on a pane. <see cref="Auto"/> = no pin; the collapse
/// resolver decides from status.</summary>
public enum RoostPin
{
    Auto = 0,
    Expanded = 1,
    Collapsed = 2,
}

/// <summary>
/// One pane in the Roost: the latest snapshot of a session, keyed by <see cref="Key"/> (the process id — stable
/// across a <c>/clear</c>, which swaps the session id under the same process). An <see cref="Ended"/> pane keeps
/// its last live snapshot while it lingers.
/// </summary>
public sealed record RoostPane(string Key, ClaudeSession Session, RoostGroup Group, RoostPin Pin, DateTime? EndedAt)
{
    /// <summary>True once the session has left the scan (its process exited); the pane lingers greyed.</summary>
    public bool Ended => EndedAt is not null;
}

/// <summary>A rail heading and its panes, in rail order.</summary>
public sealed record RoostRailGroup(RoostGroup Group, IReadOnlyList<RoostPane> Panes);

/// <summary>The title-bar summary chips.</summary>
public readonly record struct RoostCounts(int NeedsYou, int DoneReview, int Working, int Quiet);

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
/// The set round-trips through settings via <see cref="ClosedKeys"/>.</para>
///
/// <para>Lives in the app (not the window) so pins and order survive closing and reopening the Roost.</para>
/// </summary>
public sealed class RoostRoster
{
    public static readonly TimeSpan EndedLinger = TimeSpan.FromMinutes(10);

    private sealed class Entry
    {
        public required ClaudeSession Session;
        public DateTime? EndedAt;
        public RoostPin Pin;
    }

    // Insertion-ordered: _order is first-seen, _entries the state by key.
    private readonly List<string> _order = [];
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly HashSet<string> _closed;

    private IReadOnlyList<RoostPane> _panes = [];
    private IReadOnlyList<RoostRailGroup> _rail = [];

    public RoostRoster(IEnumerable<string>? closedKeys = null) =>
        _closed = new HashSet<string>(closedKeys ?? [], StringComparer.Ordinal);

    /// <summary>Visible panes (closed ones excluded) in stable first-seen order.</summary>
    public IReadOnlyList<RoostPane> Panes => _panes;

    /// <summary>The rail: all four groups in urgency order (a group may be empty). Within <see cref="RoostGroup.NeedsYou"/>
    /// the longest-waiting pane leads; other groups keep first-seen order, with ended panes last in Quiet.</summary>
    public IReadOnlyList<RoostRailGroup> Rail => _rail;

    public RoostCounts Counts { get; private set; }

    /// <summary>The closed pane keys, for persisting. Pruned to keys the roster still holds.</summary>
    public IReadOnlyCollection<string> ClosedKeys => _closed;

    /// <summary>Folds a scan into the roster. <paramref name="now"/> ages lingering ended panes.</summary>
    public void Update(IReadOnlyList<ClaudeSession> live, DateTime now)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var s in live)
        {
            if (!seen.Add(s.Pid)) continue;   // defensive: one pane per process
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

        // Anything that left the scan starts (or keeps) lingering; expired or closed ones drop.
        for (int i = _order.Count - 1; i >= 0; i--)
        {
            var key = _order[i];
            if (seen.Contains(key)) continue;
            var e = _entries[key];
            e.EndedAt ??= now;
            if (_closed.Contains(key) || now - e.EndedAt.Value >= EndedLinger)
            {
                _entries.Remove(key);
                _order.RemoveAt(i);
            }
        }

        _closed.IntersectWith(_entries.Keys);
        Rebuild();
    }

    /// <summary>Hides a pane. Returns true when the closed set changed (so the caller persists it).</summary>
    public bool Close(string key)
    {
        if (!_entries.ContainsKey(key) || !_closed.Add(key)) return false;
        Rebuild();
        return true;
    }

    /// <summary>Un-hides a closed pane. Returns true when the closed set changed.</summary>
    public bool Reopen(string key)
    {
        if (!_closed.Remove(key)) return false;
        Rebuild();
        return true;
    }

    /// <summary>Sets (or, with <see cref="RoostPin.Auto"/>, clears) a pane's manual collapse pin.</summary>
    public void SetPin(string key, RoostPin pin)
    {
        if (!_entries.TryGetValue(key, out var e) || e.Pin == pin) return;
        e.Pin = pin;
        Rebuild();
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
        foreach (var key in _order)
        {
            if (_closed.Contains(key)) continue;
            var e = _entries[key];
            panes.Add(new RoostPane(key, e.Session, GroupFor(e.Session.Status, e.EndedAt is not null), e.Pin, e.EndedAt));
        }
        _panes = panes;

        var rail = new List<RoostRailGroup>(4);
        foreach (var g in Enum.GetValues<RoostGroup>())
        {
            IEnumerable<RoostPane> members = panes.Where(p => p.Group == g);
            members = g switch
            {
                // Longest-waiting first. ApiError carries no AwaitingSince; its LastUpdated is when it stopped.
                // OrderBy is stable, so ties keep first-seen order.
                RoostGroup.NeedsYou => members.OrderBy(p => p.Session.AwaitingSince ?? p.Session.LastUpdated),
                RoostGroup.Quiet => members.OrderBy(p => p.Ended),
                _ => members,
            };
            rail.Add(new RoostRailGroup(g, members.ToList()));
        }
        _rail = rail;

        Counts = new RoostCounts(
            rail[(int)RoostGroup.NeedsYou].Panes.Count,
            rail[(int)RoostGroup.DoneReview].Panes.Count,
            rail[(int)RoostGroup.Working].Panes.Count,
            rail[(int)RoostGroup.Quiet].Panes.Count);
    }
}
