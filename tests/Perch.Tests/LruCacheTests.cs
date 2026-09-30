using Perch.Data;
using Xunit;

namespace Perch.Tests;

/// <summary><see cref="LruCache{TKey, TValue}"/> (review fixes CP23): the overlay's shaped-text and truncation
/// caches.</summary>
public sealed class LruCacheTests
{
    [Fact]
    public void A_hit_returns_the_cached_value_without_creating_again()
    {
        int made = 0;
        var cache = new LruCache<string, int>(4);
        Assert.Equal(1, cache.GetOrAdd("a", _ => ++made));
        Assert.Equal(1, cache.GetOrAdd("a", _ => ++made));
        Assert.Equal(1, made);
    }

    [Fact]
    public void Past_capacity_the_least_recently_used_entry_is_evicted()
    {
        int made = 0;
        var cache = new LruCache<string, int>(2);
        cache.GetOrAdd("a", _ => ++made);
        cache.GetOrAdd("b", _ => ++made);
        cache.GetOrAdd("a", _ => ++made);   // touch a: b is now the oldest
        cache.GetOrAdd("c", _ => ++made);   // evicts b

        Assert.Equal(2, cache.Count);
        Assert.Equal(1, cache.GetOrAdd("a", _ => ++made));   // still cached
        Assert.Equal(4, cache.GetOrAdd("b", _ => ++made));   // was evicted, made again
    }

    [Fact]
    public void Concurrent_callers_all_get_a_value_and_the_cache_stays_bounded()
    {
        var cache = new LruCache<int, int>(64);
        Parallel.For(0, 10_000, i => Assert.Equal((i % 200) * 2, cache.GetOrAdd(i % 200, k => k * 2)));
        Assert.InRange(cache.Count, 1, 64);
    }
}
