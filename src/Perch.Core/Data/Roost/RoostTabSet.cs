namespace Perch.Data.Roost;

/// <summary>Where a session sits: a region of a tab.</summary>
public readonly record struct RoostPlacement(string TabId, int RegionId);

/// <summary>One Roost tab: a name, a painted layout and the session (pane key) in each region. Mutated only
/// through its <see cref="RoostTabSet"/>.</summary>
public sealed class RoostTab
{
    internal RoostTab(string id, string name, RoostGridLayout layout)
    {
        Id = id;
        Name = name;
        Layout = layout;
    }

    internal readonly Dictionary<int, string> CellMap = new();

    public string Id { get; }

    public string Name { get; internal set; }

    public RoostGridLayout Layout { get; internal set; }

    /// <summary>Region id → pane key, for the regions that hold a session.</summary>
    public IReadOnlyDictionary<int, string> Cells => CellMap;

    /// <summary>The region temporarily filling the tab (double-click a header, Ctrl+Shift+Z), or null. Not persisted.</summary>
    public int? Zoomed { get; internal set; }

    /// <summary>The region last assigned or focused in this tab — where a drop on the tab's header lands when it has
    /// no empty region. Not persisted.</summary>
    public int? LastRegion { get; internal set; }

    /// <summary>The fixed first "tab": one full region for sessions that aren't in any tab.</summary>
    public bool IsFocus => Id == RoostTabSet.FocusId;

    public string? At(int regionId) => CellMap.TryGetValue(regionId, out var key) ? key : null;
}

/// <summary>The persisted <see cref="RoostTabSet"/> (<c>AppSettings.RoostTabs</c>). Cells hold <c>pid/sessionId</c>
/// tokens, so after a restart a recycled pid never inherits a region.</summary>
public sealed class RoostTabsState
{
    public List<RoostTabState> Tabs { get; set; } = [];
    public string? Active { get; set; }
    /// <summary>The Focus region's session token, if any.</summary>
    public string? Focus { get; set; }
}

/// <summary>One persisted tab.</summary>
public sealed class RoostTabState
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public List<RoostRegion> Layout { get; set; } = [];
    /// <summary>Region id → <c>pid/sessionId</c> token.</summary>
    public Dictionary<int, string>? Cells { get; set; }
}

/// <summary>
/// The Roost's tabs (docs/roost-tabs-plan.md): the fixed <see cref="Focus"/> tab plus the user's own, each a painted
/// <see cref="RoostGridLayout"/> with a session per region. UI-free, unit-tested; app-owned (like the roster) so tabs
/// survive closing the window.
///
/// <para><b>Placement is entirely the user's</b> (D2): nothing here moves a session by itself, except following it
/// to a new key (<see cref="Sync"/>'s adoptions) and emptying its region once it leaves the roster.</para>
///
/// <para><b>One place per session</b> (D1) is a policy, <see cref="UniquePlacement"/>, not a property of the model:
/// cells may in principle name the same key twice, <see cref="Locate"/> returns a list, and only
/// <see cref="Assign"/> evicts a key from its old place. Lifting the rule later is a flag, not a rewrite.</para>
/// </summary>
public sealed class RoostTabSet
{
    public const string FocusId = "focus";
    public const int MaxTabs = 12;
    public const int MaxNameLength = 40;

    private readonly RoostTab _focus = new(FocusId, "Focus", RoostGridLayout.Full);
    private readonly List<RoostTab> _tabs = [];
    private readonly Dictionary<string, long> _shown = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _sessionIds = new(StringComparer.Ordinal);
    // Persisted cells not yet matched against a live session (resolved on the first Sync).
    private List<(string TabId, int RegionId, string Pid, string SessionId)>? _seeds;
    private long _tick;
    private int _nextId = 1;
    private string _sig;

    public RoostTabSet(bool uniquePlacement = true)
    {
        UniquePlacement = uniquePlacement;
        _sig = Signature();
    }

    /// <summary>A session lives in at most one region (D1). Assigning it elsewhere moves it.</summary>
    public bool UniquePlacement { get; }

    /// <summary>The persisted form (<see cref="ToState"/>) changed — save it.</summary>
    public event Action? Changed;

    public RoostTab Focus => _focus;

    /// <summary>The user's tabs, in strip order (Focus not included).</summary>
    public IReadOnlyList<RoostTab> Tabs => _tabs;

    /// <summary>Focus, then the user's tabs.</summary>
    public IEnumerable<RoostTab> All => _tabs.Prepend(_focus);

    public string ActiveId { get; private set; } = FocusId;

    public RoostTab Active => Find(ActiveId) ?? _focus;

    public RoostTab? Find(string id) => id == FocusId ? _focus : _tabs.FirstOrDefault(t => t.Id == id);

    /// <summary>Every place <paramref name="key"/> is shown (at most one while <see cref="UniquePlacement"/>).</summary>
    public IReadOnlyList<RoostPlacement> Locate(string key)
    {
        var list = new List<RoostPlacement>();
        foreach (var tab in All)
            foreach (var (region, k) in tab.CellMap)
                if (k == key) list.Add(new RoostPlacement(tab.Id, region));
        return list;
    }

    public bool IsPlaced(string key) => All.Any(t => t.CellMap.ContainsValue(key));

    // ── Tabs ──────────────────────────────────────────────────────────────────

    /// <summary>Switches to tab <paramref name="id"/>. Returns false for an unknown tab or the one already active.</summary>
    public bool Activate(string id)
    {
        if (Find(id) is null) return false;
        _shown[id] = ++_tick;
        if (ActiveId == id) return false;
        ActiveId = id;
        RaiseIfChanged();
        return true;
    }

    /// <summary>Adds a tab at the end ("Tab N" when unnamed; a single region unless given a layout). Null past
    /// <see cref="MaxTabs"/>. Doesn't switch to it.</summary>
    public RoostTab? AddTab(string? name = null, RoostGridLayout? layout = null)
    {
        if (_tabs.Count >= MaxTabs) return null;
        var tab = new RoostTab(NewId(), Clean(name) ?? NextDefaultName(), layout ?? RoostGridLayout.Full);
        _tabs.Add(tab);
        RaiseIfChanged();
        return tab;
    }

    /// <summary>Renames a tab (trimmed, capped at <see cref="MaxNameLength"/>). Focus can't be renamed; a blank name
    /// is refused.</summary>
    public bool RenameTab(string id, string name)
    {
        if (id == FocusId || Find(id) is not { } tab || Clean(name) is not { } clean || clean == tab.Name) return false;
        tab.Name = clean;
        RaiseIfChanged();
        return true;
    }

    /// <summary>Closes a tab; its sessions go back to the rail and the tab before it (or Focus) becomes active.</summary>
    public bool CloseTab(string id)
    {
        int at = _tabs.FindIndex(t => t.Id == id);
        if (at < 0) return false;
        _tabs.RemoveAt(at);
        _shown.Remove(id);
        if (ActiveId == id) ActiveId = at > 0 ? _tabs[at - 1].Id : FocusId;
        RaiseIfChanged();
        return true;
    }

    /// <summary>A copy of a tab's layout, right after it ("Name copy"). Its regions start empty while
    /// <see cref="UniquePlacement"/> holds.</summary>
    public RoostTab? DuplicateTab(string id)
    {
        if (_tabs.Count >= MaxTabs || id == FocusId || Find(id) is not { } source) return null;
        var copy = new RoostTab(NewId(), Clean($"{source.Name} copy")!, source.Layout);
        if (!UniquePlacement) foreach (var (r, k) in source.CellMap) copy.CellMap[r] = k;
        _tabs.Insert(_tabs.IndexOf(source) + 1, copy);
        RaiseIfChanged();
        return copy;
    }

    /// <summary>Moves a tab to position <paramref name="index"/> among the user's tabs (clamped).</summary>
    public bool MoveTab(string id, int index)
    {
        int from = _tabs.FindIndex(t => t.Id == id);
        if (from < 0) return false;
        index = Math.Clamp(index, 0, _tabs.Count - 1);
        if (index == from) return false;
        var tab = _tabs[from];
        _tabs.RemoveAt(from);
        _tabs.Insert(index, tab);
        RaiseIfChanged();
        return true;
    }

    /// <summary>
    /// Puts a new layout on a tab. Region ids carry the sessions across: a region that survived keeps its session,
    /// one that's gone sends its session back to the rail. (A preset or saved layout is first renumbered with
    /// <see cref="RoostGridLayout.AdoptIds"/>, so sessions follow it in reading order.) Returns the sessions that are
    /// now in no tab. The Focus tab's layout is fixed.
    /// </summary>
    public IReadOnlyList<string> ApplyLayout(string id, RoostGridLayout layout)
    {
        if (id == FocusId || Find(id) is not { } tab) return [];
        tab.Layout = layout;
        var dropped = tab.CellMap.Where(c => layout.Find(c.Key) is null).ToList();
        foreach (var (region, _) in dropped) tab.CellMap.Remove(region);
        if (tab.Zoomed is { } z && layout.Find(z) is null) tab.Zoomed = null;
        RaiseIfChanged();
        return dropped.Select(c => c.Value).Distinct().Where(k => !IsPlaced(k)).ToList();
    }

    // ── Sessions ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Puts <paramref name="key"/> in a region (a drop, the empty-region picker). While <see cref="UniquePlacement"/>
    /// holds it leaves wherever it was, and the region's occupant takes its old place (a swap) — or, if it came from
    /// the rail, goes back to the rail. Returns the session that's now in no tab, if any.
    /// </summary>
    public string? Assign(string tabId, int regionId, string key)
    {
        if (Find(tabId) is not { } tab || tab.Layout.Find(regionId) is null) return null;
        var occupant = tab.At(regionId);
        tab.LastRegion = regionId;
        if (occupant == key) return null;
        string? unplaced = null;
        if (UniquePlacement)
        {
            var from = Locate(key);
            foreach (var p in from) Find(p.TabId)!.CellMap.Remove(p.RegionId);
            tab.CellMap[regionId] = key;
            if (occupant is not null)
            {
                if (from.Count == 1) Find(from[0].TabId)!.CellMap[from[0].RegionId] = occupant;
                else unplaced = occupant;
            }
        }
        else
        {
            tab.CellMap[regionId] = key;
            if (occupant is not null && !IsPlaced(occupant)) unplaced = occupant;
        }
        RaiseIfChanged();
        return unplaced;
    }

    /// <summary>Takes <paramref name="key"/> out of every region ("Remove from tab"). Returns whether it was placed.</summary>
    public bool Unassign(string key)
    {
        bool any = false;
        foreach (var tab in All)
            foreach (var region in tab.CellMap.Where(c => c.Value == key).Select(c => c.Key).ToList())
            {
                tab.CellMap.Remove(region);
                if (tab.Zoomed == region) tab.Zoomed = null;
                any = true;
            }
        if (any) RaiseIfChanged();
        return any;
    }

    /// <summary>
    /// A rail click: go to <paramref name="key"/>. Placed → its tab (the most recently shown, if it's in several);
    /// not placed → the Focus tab, replacing any one-off there. Un-zooms the tab if another region was zoomed.
    /// Returns where it now is.
    /// </summary>
    public RoostPlacement Show(string key)
    {
        var at = Locate(key);
        var p = at.Count > 0
            ? at.OrderByDescending(x => _shown.TryGetValue(x.TabId, out var t) ? t : 0).First()
            : new RoostPlacement(FocusId, _focus.Layout.Regions[0].Id);
        if (at.Count == 0) Assign(FocusId, p.RegionId, key);
        var tab = Find(p.TabId)!;
        if (tab.Zoomed is { } z && z != p.RegionId) tab.Zoomed = null;
        tab.LastRegion = p.RegionId;
        Activate(p.TabId);
        return p;
    }

    /// <summary>The user focused <paramref name="key"/>'s pane (a click inside it, Alt+N): remember its region as its
    /// tab's <see cref="RoostTab.LastRegion"/>.</summary>
    public void NoteFocus(string key)
    {
        foreach (var p in Locate(key)) Find(p.TabId)!.LastRegion = p.RegionId;
    }

    /// <summary>
    /// A session dropped on a tab's header: it goes into the tab's first empty region (reading order), or — when
    /// there's none — takes the tab's <see cref="RoostTab.LastRegion"/> (its first region if that's gone), the
    /// occupant swapping to where the session came from (<see cref="Assign"/>). A session already in the tab stays
    /// put. Returns where it now is, or null for an unknown tab.
    /// </summary>
    public RoostPlacement? DropOnTab(string tabId, string key)
    {
        if (Find(tabId) is not { } tab) return null;
        if (Locate(key).FirstOrDefault(p => p.TabId == tabId) is { TabId: not null } here) return here;
        var order = tab.Layout.ReadingOrder;
        int region = order.Where(r => tab.At(r.Id) is null).Select(r => (int?)r.Id).FirstOrDefault()
            ?? (tab.LastRegion is { } last && tab.Layout.Find(last) is not null ? last : order[0].Id);
        Assign(tabId, region, key);
        return new RoostPlacement(tabId, region);
    }

    /// <summary>Zooms a tab's region to fill the tab, or (null, or the zoomed one again) restores it. Returns the
    /// zoomed region.</summary>
    public int? ToggleZoom(string tabId, int? regionId)
    {
        if (Find(tabId) is not { } tab) return null;
        tab.Zoomed = regionId is { } r && tab.Zoomed != r && tab.Layout.Find(r) is not null ? r : null;
        return tab.Zoomed;
    }

    /// <summary>The panes in no tab, in roster order.</summary>
    public IReadOnlyList<RoostPane> Unplaced(IReadOnlyList<RoostPane> panes) => panes.Where(p => !IsPlaced(p.Key)).ToList();

    /// <summary>The light across every tab but the active one: what "need you in other tabs" counts.</summary>
    public RoostTabLight Elsewhere(IReadOnlyList<RoostPane> panes)
    {
        var active = Active.CellMap.Values.ToHashSet(StringComparer.Ordinal);
        return RoostTabStatus.Of(All.Where(t => t.Id != ActiveId).SelectMany(t => t.CellMap.Values)
            .Where(k => !active.Contains(k)), panes);
    }

    /// <summary>
    /// Folds a roster update in: follows re-keyed panes (<see cref="RoostRoster.Adopted"/>), resolves a persisted
    /// state's cells on the first call (a token places its session only if both pid and session id match), and
    /// empties regions whose session left the roster (ended and dropped, or closed).
    /// </summary>
    public void Sync(IReadOnlyList<RoostPane> panes, IReadOnlyDictionary<string, string>? adopted = null)
    {
        if (adopted is { Count: > 0 })
            foreach (var tab in All)
                foreach (var (region, key) in tab.CellMap.ToList())
                {
                    if (!adopted.TryGetValue(key, out var now)) continue;
                    if (UniquePlacement && IsPlaced(now)) tab.CellMap.Remove(region);   // the user already placed it
                    else tab.CellMap[region] = now;
                }

        var live = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var p in panes) live[p.Key] = p.Session.SessionId;
        _sessionIds.Clear();
        foreach (var (k, sid) in live) _sessionIds[k] = sid;

        if (_seeds is { } seeds)
        {
            _seeds = null;
            foreach (var (tabId, region, pid, sid) in seeds)
            {
                if (!live.TryGetValue(pid, out var liveSid) || liveSid != sid) continue;
                if (Find(tabId) is not { } tab || tab.Layout.Find(region) is null || tab.CellMap.ContainsKey(region)) continue;
                if (UniquePlacement && IsPlaced(pid)) continue;
                tab.CellMap[region] = pid;
            }
        }

        foreach (var tab in All)
            foreach (var region in tab.CellMap.Where(c => !live.ContainsKey(c.Value)).Select(c => c.Key).ToList())
            {
                tab.CellMap.Remove(region);
                if (tab.Zoomed == region) tab.Zoomed = null;
            }
        RaiseIfChanged();
    }

    /// <summary>
    /// The first-run tab (D8): "Main", shaped for the live sessions (up to <see cref="RoostTemplates.AutoMaxCells"/>)
    /// and filled with them in roster order. Only for a set that has never had tabs — the app calls it when nothing
    /// was ever persisted.
    /// </summary>
    public RoostTab? CreateDefault(IReadOnlyList<RoostPane> panes, double aspect)
    {
        if (_tabs.Count > 0) return null;
        var live = panes.Where(p => !p.Ended && !IsPlaced(p.Key)).Take(RoostTemplates.AutoMaxCells).ToList();
        var layout = RoostGridLayout.FromTemplate(RoostTemplates.ForCount(Math.Max(1, live.Count), aspect));
        var tab = AddTab("Main", layout)!;
        var regions = layout.ReadingOrder;
        for (int i = 0; i < live.Count && i < regions.Count; i++) tab.CellMap[regions[i].Id] = live[i].Key;
        Activate(tab.Id);
        RaiseIfChanged();
        return tab;
    }

    // ── Persistence ───────────────────────────────────────────────────────────

    /// <summary>The persisted form. Cells become <c>pid/sessionId</c> tokens; unresolved persisted cells are kept
    /// until the first <see cref="Sync"/> settles them.</summary>
    public RoostTabsState ToState()
    {
        var state = new RoostTabsState { Active = ActiveId, Focus = Token(_focus.At(_focus.Layout.Regions[0].Id)) };
        foreach (var tab in _tabs)
        {
            var cells = new Dictionary<int, string>();
            foreach (var (region, key) in tab.CellMap) if (Token(key) is { } t) cells[region] = t;
            if (_seeds is not null)
                foreach (var s in _seeds.Where(s => s.TabId == tab.Id)) cells.TryAdd(s.RegionId, $"{s.Pid}/{s.SessionId}");
            state.Tabs.Add(new RoostTabState
            {
                Id = tab.Id, Name = tab.Name, Layout = tab.Layout.Regions.ToList(), Cells = cells.Count > 0 ? cells : null,
            });
        }
        if (state.Focus is null && _seeds?.FirstOrDefault(s => s.TabId == FocusId) is { Pid: not null } f)
            state.Focus = $"{f.Pid}/{f.SessionId}";
        return state;
    }

    /// <summary>
    /// Restores a persisted state (replacing any tabs). Anything malformed is repaired rather than trusted: a bad
    /// layout reads as one region, a blank or over-long name is renamed, duplicate or reserved ids are dropped, and a
    /// cell whose token is malformed or names a missing region is ignored. Cells are placed by the first
    /// <see cref="Sync"/>. Doesn't raise <see cref="Changed"/>.
    /// </summary>
    public void Seed(RoostTabsState? state)
    {
        _tabs.Clear();
        _focus.CellMap.Clear();
        _shown.Clear();
        _seeds = [];
        ActiveId = FocusId;
        if (state is not null)
        {
            foreach (var t in state.Tabs ?? [])
            {
                if (_tabs.Count >= MaxTabs || string.IsNullOrWhiteSpace(t.Id) || t.Id == FocusId || Find(t.Id) is not null) continue;
                var tab = new RoostTab(t.Id, Clean(t.Name) ?? NextDefaultName(), RoostGridLayout.FromPersisted(t.Layout));
                _tabs.Add(tab);
                foreach (var (region, token) in t.Cells ?? [])
                    if (tab.Layout.Find(region) is not null && Parse(token) is { } p) _seeds.Add((tab.Id, region, p.Pid, p.SessionId));
            }
            if (Parse(state.Focus) is { } focus) _seeds.Add((FocusId, _focus.Layout.Regions[0].Id, focus.Pid, focus.SessionId));
            if (state.Active is { } active && Find(active) is not null) ActiveId = active;
        }
        _nextId = 1 + _tabs.Select(t => t.Id.StartsWith('t') && int.TryParse(t.Id.AsSpan(1), out var n) ? n : 0).DefaultIfEmpty(0).Max();
        _sig = Signature();
    }

    private string? Token(string? key) =>
        key is not null && _sessionIds.TryGetValue(key, out var sid) && sid.Length > 0 ? $"{key}/{sid}" : null;

    private static (string Pid, string SessionId)? Parse(string? token)
    {
        if (token is null) return null;
        int slash = token.IndexOf('/');
        return slash <= 0 || slash == token.Length - 1 ? null : (token[..slash], token[(slash + 1)..]);
    }

    private string NewId()
    {
        string id;
        do id = $"t{_nextId++}"; while (Find(id) is not null);
        return id;
    }

    private string NextDefaultName()
    {
        for (int n = 1; ; n++)
        {
            var name = $"Tab {n}";
            if (!_tabs.Any(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase))) return name;
        }
    }

    private static string? Clean(string? name)
    {
        var t = name?.Trim();
        if (string.IsNullOrEmpty(t)) return null;
        return t.Length > MaxNameLength ? t[..MaxNameLength].TrimEnd() : t;
    }

    private string Signature()
    {
        var s = ToState();
        return $"{s.Active}|{s.Focus}|" + string.Join("¦", s.Tabs.Select(t =>
            $"{t.Id}:{t.Name}:{RoostGridLayout.FromPersisted(t.Layout).Signature}:"
            + string.Join(",", (t.Cells ?? []).OrderBy(c => c.Key).Select(c => $"{c.Key}={c.Value}"))));
    }

    private void RaiseIfChanged()
    {
        var sig = Signature();
        if (sig == _sig) return;
        _sig = sig;
        Changed?.Invoke();
    }
}

/// <summary>A tab's status light, most urgent last.</summary>
public enum RoostLight
{
    None = 0,
    Quiet = 1,
    Working = 2,
    Done = 3,
    Awaiting = 4,
    Error = 5,
}

/// <summary>A tab's light and its number: the sessions in it that want the user (Needs you + Done · review).</summary>
public readonly record struct RoostTabLight(RoostLight Light, int Count);

/// <summary>The tab light rule (D7) — the overlay's Roost badge (an API error outranks awaiting input) plus the
/// rail's Done · review state. UI-free.</summary>
public static class RoostTabStatus
{
    public static RoostTabLight For(RoostTab tab, IReadOnlyList<RoostPane> panes) => Of(tab.CellMap.Values, panes);

    public static RoostTabLight Of(IEnumerable<string> keys, IReadOnlyList<RoostPane> panes)
    {
        var wanted = keys.ToHashSet(StringComparer.Ordinal);
        var light = RoostLight.None;
        int count = 0;
        foreach (var p in panes)
        {
            if (!wanted.Contains(p.Key)) continue;
            var l = LightOf(p);
            if (l > light) light = l;
            if (!p.Ended && p.Group is RoostGroup.NeedsYou or RoostGroup.DoneReview) count++;
        }
        return new RoostTabLight(light, count);
    }

    public static RoostLight LightOf(RoostPane p) => p.Ended ? RoostLight.Quiet : p.Session.Status switch
    {
        SessionStatus.ApiError => RoostLight.Error,
        SessionStatus.AwaitingInput => RoostLight.Awaiting,
        SessionStatus.NeedsAttention => RoostLight.Done,
        SessionStatus.Running => RoostLight.Working,
        _ => RoostLight.Quiet,
    };
}
