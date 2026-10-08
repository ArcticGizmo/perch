namespace Perch.Feeds;

/// <summary>One subscription's read markers: entry id → when it was read (or primed). Persisted in
/// <c>feeds/read.json</c> by <see cref="FeedStore"/>.</summary>
internal sealed class FeedReadRecord
{
    public Dictionary<string, DateTime> Read { get; set; } = new(StringComparer.Ordinal);
    public DateTime? PrimedUtc { get; set; }
}

/// <summary>
/// The rules for "unread" (docs/feeds-plan.md §3.3). Pure.
/// <list type="bullet">
///   <item><b>Priming:</b> the first successful fetch marks everything already in the feed read, so adding a feed
///   is quiet. Only entries that appear after that are unread.</item>
///   <item><b>Edits don't re-light:</b> unread is "id not in the read set"; a bumped <c>updated</c> is ignored.</item>
///   <item><b>Pruning is conservative:</b> a read marker is only dropped once its entry has left the feed
///   <em>and</em> it's older than <see cref="Retention"/>, with a hard cap. A feed that briefly serves an empty
///   or truncated document therefore can't wipe the markers and re-light everything when it recovers.</item>
/// </list>
/// </summary>
internal static class FeedReadState
{
    public static readonly TimeSpan Retention = TimeSpan.FromDays(30);
    public const int MaxMarkers = 2000;

    /// <summary>
    /// Applies a successful fetch. Returns the entries that are <em>newly</em> unread — unread now and absent from
    /// <paramref name="previous"/> — for notifications. Priming returns none.
    /// </summary>
    public static IReadOnlyList<FeedEntry> ApplyFetch(FeedReadRecord rec, FeedDoc? previous, FeedDoc doc, DateTime nowUtc)
    {
        if (rec.PrimedUtc is null)
        {
            foreach (var e in doc.Entries) rec.Read[e.Id] = nowUtc;
            rec.PrimedUtc = nowUtc;
            return [];
        }

        var before = previous is null ? null : new HashSet<string>(previous.Entries.Select(e => e.Id), StringComparer.Ordinal);
        var fresh = doc.Entries.Where(e => !rec.Read.ContainsKey(e.Id) && (before is null || !before.Contains(e.Id))).ToList();
        Prune(rec, doc, nowUtc);
        return fresh;
    }

    public static bool IsUnread(FeedReadRecord rec, FeedEntry e) => rec.PrimedUtc is not null && !rec.Read.ContainsKey(e.Id);

    public static int UnreadCount(FeedReadRecord rec, FeedDoc? doc) =>
        doc is null ? 0 : doc.Entries.Count(e => IsUnread(rec, e));

    public static void MarkRead(FeedReadRecord rec, string entryId, DateTime nowUtc) => rec.Read[entryId] = nowUtc;

    public static void MarkAllRead(FeedReadRecord rec, FeedDoc? doc, DateTime nowUtc)
    {
        if (doc is null) return;
        foreach (var e in doc.Entries) rec.Read.TryAdd(e.Id, nowUtc);
        rec.PrimedUtc ??= nowUtc;
    }

    private static void Prune(FeedReadRecord rec, FeedDoc doc, DateTime nowUtc)
    {
        var present = new HashSet<string>(doc.Entries.Select(e => e.Id), StringComparer.Ordinal);
        foreach (var (id, at) in rec.Read.ToList())
            if (!present.Contains(id) && nowUtc - at > Retention) rec.Read.Remove(id);

        if (rec.Read.Count > MaxMarkers)
            foreach (var (id, _) in rec.Read.Where(kv => !present.Contains(kv.Key)).OrderBy(kv => kv.Value)
                         .Take(rec.Read.Count - MaxMarkers).ToList())
                rec.Read.Remove(id);
    }
}
