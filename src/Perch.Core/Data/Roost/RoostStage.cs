namespace Perch.Data.Roost;

/// <summary>What protects a pane from being bumped off the stage: the focused pane and the one being typed in
/// (a pane pinned with <see cref="RoostPin.Expanded"/> — "Keep on stage" — is protected by its pin).</summary>
public readonly record struct RoostStageGuard(string? Focused, string? TypingIn);

/// <summary>
/// Which panes the Tiled stage shows — at most <see cref="Capacity"/>, each a full thread — and in which cells.
/// Every other pane stays in the rail. UI-free, unit-tested.
///
/// <para>Places are sticky: a pane keeps its cell until it leaves the roster, the user moves it, or a more urgent
/// pane needs the place. With a free place, the most urgent waiting pane goes in at its position in the user's
/// order. With none, a waiting pane takes the place of the <em>weakest</em> pane on stage (least urgent, then
/// least recently viewed) only when it is strictly more urgent — and it takes that pane's cell, so nothing else
/// moves. The focused pane, the one being typed in and pinned panes are never bumped automatically; a pane the user
/// put on stage themselves (<see cref="Bring"/>, <see cref="Place"/>) counts as at least Working, so only a pane that
/// needs them or is done for review bumps it. While the user is typing nothing is admitted or bumped at all (only
/// departures apply).</para>
/// </summary>
public sealed class RoostStage
{
    /// <summary>The most panes on stage.</summary>
    public const int Capacity = RoostTemplates.AutoMaxCells;

    private readonly List<string> _slots = [];
    private readonly Dictionary<string, long> _viewed = new(StringComparer.Ordinal);
    private readonly HashSet<string> _everOn = new(StringComparer.Ordinal);
    private readonly HashSet<string> _chosen = new(StringComparer.Ordinal);   // put on stage by the user
    private long _tick;

    /// <summary>The panes on stage, in cell order.</summary>
    public IReadOnlyList<string> Slots => _slots;

    public bool IsOn(string key) => _slots.Contains(key);

    /// <summary>Whether the pane has been on stage since it appeared (an off-stage pane that never was is "new").</summary>
    public bool WasEverOn(string key) => _everOn.Contains(key);

    /// <summary>0 = most urgent: Needs you, Done · review, Working, Quiet, ended.</summary>
    public static int Urgency(RoostPane p) => p.Ended ? 4 : p.Group switch
    {
        RoostGroup.NeedsYou => 0,
        RoostGroup.DoneReview => 1,
        RoostGroup.Working => 2,
        _ => 3,
    };

    /// <summary>
    /// Folds the roster into the stage. <paramref name="panes"/> is every pane in the user's order. Panes that
    /// left drop out; unless <paramref name="hold"/> (the user is typing), waiting panes fill free places and
    /// bump weaker ones.
    /// </summary>
    public void Sync(IReadOnlyList<RoostPane> panes, RoostStageGuard guard, bool hold)
    {
        var byKey = panes.ToDictionary(p => p.Key, StringComparer.Ordinal);
        _slots.RemoveAll(k => !byKey.ContainsKey(k));
        _everOn.IntersectWith(byKey.Keys);
        _chosen.IntersectWith(_slots);
        foreach (var gone in _viewed.Keys.Where(k => !byKey.ContainsKey(k)).ToList()) _viewed.Remove(gone);
        if (hold) return;

        var order = Order(panes);
        var waiting = panes.Where(p => !_slots.Contains(p.Key))
            .OrderBy(Urgency).ThenBy(p => order[p.Key]).ToList();
        foreach (var p in waiting)
        {
            if (_slots.Count < Capacity) { Insert(p.Key, order); continue; }
            // Waiting panes come most urgent first, so once one can't outrank the weakest, none after it can.
            if (Weakest(byKey, guard, strict: true) is not { } victim || Urgency(p) >= Standing(byKey[victim])) break;
            Replace(victim, p.Key);
        }
    }

    /// <summary>The user asked for <paramref name="key"/> (the rail, Ctrl+1–9, a new session they started): put it
    /// on stage, into a free place or the weakest pane's cell. The weakest excludes the guard's panes and pinned
    /// panes; if every pane is protected, the one being typed in is still spared.</summary>
    public void Bring(string key, IReadOnlyList<RoostPane> panes, RoostStageGuard guard)
    {
        var byKey = panes.ToDictionary(p => p.Key, StringComparer.Ordinal);
        if (!byKey.ContainsKey(key)) return;
        if (!_slots.Contains(key))
        {
            if (_slots.Count < Capacity) Insert(key, Order(panes));
            else if ((Weakest(byKey, guard, strict: true) ?? Weakest(byKey, guard, strict: false)) is { } victim)
                Replace(victim, key);
        }
        if (_slots.Contains(key)) _chosen.Add(key);
        Viewed(key);
    }

    /// <summary>
    /// Puts <paramref name="key"/> in cell <paramref name="slot"/> (a drag, or a flyout cell). A pane already on
    /// stage swaps cells with the one there; one from the rail takes the cell and its occupant returns to the rail.
    /// A slot past the last pane means "the end". Returns the pane displaced from that cell, if any.
    /// </summary>
    public string? Place(string key, int slot, IReadOnlyList<RoostPane> panes)
    {
        if (!panes.Any(p => p.Key == key)) return null;
        int from = _slots.IndexOf(key);
        string? there = slot >= 0 && slot < _slots.Count ? _slots[slot] : null;
        if (there == key) return null;
        if (from >= 0)
        {
            if (there is null) { _slots.RemoveAt(from); _slots.Add(key); }
            else (_slots[from], _slots[slot]) = (there, key);
        }
        else if (there is not null) Replace(there, key);
        else if (_slots.Count < Capacity) Admit(key, _slots.Count);
        if (_slots.Contains(key)) _chosen.Add(key);
        Viewed(key);
        return there;
    }

    /// <summary>The user looked at a pane (focused it) — the recency that picks who gets bumped.</summary>
    public void Viewed(string key) => _viewed[key] = ++_tick;

    /// <summary>Without sticky places (a filter chip is on): the <see cref="Capacity"/> most urgent of
    /// <paramref name="panes"/>, in the user's order.</summary>
    public static IReadOnlyList<string> Strict(IReadOnlyList<RoostPane> panes)
    {
        var order = Order(panes);
        return panes.OrderBy(Urgency).ThenBy(p => order[p.Key]).Take(Capacity)
            .OrderBy(p => order[p.Key]).Select(p => p.Key).ToList();
    }

    private static Dictionary<string, int> Order(IReadOnlyList<RoostPane> panes)
    {
        var order = new Dictionary<string, int>(panes.Count, StringComparer.Ordinal);
        for (int i = 0; i < panes.Count; i++) order[panes[i].Key] = i;
        return order;
    }

    // Into a free place at its position in the user's order: before the first pane on stage that comes after it.
    private void Insert(string key, Dictionary<string, int> order)
    {
        int rank = order[key];
        int at = _slots.FindIndex(k => order.TryGetValue(k, out var r) && r > rank);
        Admit(key, at < 0 ? _slots.Count : at);
    }

    private void Replace(string victim, string key)
    {
        _chosen.Remove(victim);
        Admit(key, _slots.IndexOf(victim), replace: true);
    }

    // How hard a pane on stage is to bump: its urgency, but a live pane the user chose counts as at least Working.
    private int Standing(RoostPane p) => _chosen.Contains(p.Key) && !p.Ended ? Math.Min(Urgency(p), 2) : Urgency(p);

    private void Admit(string key, int at, bool replace = false)
    {
        if (replace) _slots[at] = key;
        else _slots.Insert(at, key);
        _everOn.Add(key);
        _viewed.TryAdd(key, ++_tick);   // arriving counts as a view, so a newcomer isn't the first to go
    }

    // The least urgent pane on stage, then the least recently viewed. Strict spares the focused pane, the one
    // being typed in and pinned panes; otherwise only the one being typed in is spared.
    private string? Weakest(Dictionary<string, RoostPane> byKey, RoostStageGuard guard, bool strict) =>
        _slots.Where(k => k != guard.TypingIn
                          && (!strict || (k != guard.Focused && byKey[k].Pin != RoostPin.Expanded)))
            .OrderByDescending(k => Standing(byKey[k]))
            .ThenBy(k => _viewed.TryGetValue(k, out var t) ? t : 0)
            .FirstOrDefault();
}
