namespace Perch.Data;

/// <summary>
/// A small bounded least-recently-used cache. The overlay keeps its shaped text and its truncated labels in
/// one (review fixes CP23), so a repaint reuses the previous frame's work instead of re-shaping every string.
/// Thread-safe (one lock); a hit moves the entry to the front, and an insert past <see cref="Capacity"/>
/// evicts the least recently used entry.
/// </summary>
internal sealed class LruCache<TKey, TValue> where TKey : notnull
{
    private readonly Dictionary<TKey, LinkedListNode<(TKey Key, TValue Value)>> _map;
    private readonly LinkedList<(TKey Key, TValue Value)> _order = new();
    private readonly object _gate = new();

    public LruCache(int capacity, IEqualityComparer<TKey>? comparer = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        Capacity = capacity;
        _map = new Dictionary<TKey, LinkedListNode<(TKey, TValue)>>(capacity, comparer);
    }

    public int Capacity { get; }

    public int Count
    {
        get { lock (_gate) return _map.Count; }
    }

    /// <summary>The cached value for <paramref name="key"/>, or the one <paramref name="create"/> makes (which
    /// is then cached). <paramref name="create"/> runs outside the lock; if two callers race on one key, the
    /// first value stored wins and both get it.</summary>
    public TValue GetOrAdd(TKey key, Func<TKey, TValue> create)
    {
        lock (_gate)
        {
            if (_map.TryGetValue(key, out var hit))
            {
                _order.Remove(hit);
                _order.AddFirst(hit);
                return hit.Value.Value;
            }
        }

        var value = create(key);
        lock (_gate)
        {
            if (_map.TryGetValue(key, out var raced))
                return raced.Value.Value;
            _map[key] = _order.AddFirst((key, value));
            if (_map.Count > Capacity)
            {
                var last = _order.Last!;
                _order.RemoveLast();
                _map.Remove(last.Value.Key);
            }
            return value;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _map.Clear();
            _order.Clear();
        }
    }
}
