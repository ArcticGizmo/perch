namespace Perch.Feeds;

/// <summary>A feed as the story player sees it: its entries newest-first, which are unread, and its last error.
/// Passed in overlay row order.</summary>
internal sealed record StoryFeed(string SubId, IReadOnlyList<FeedEntry> Entries, IReadOnlySet<string> UnreadIds, string? Error);

internal enum StoryCardKind { Entry, Error, CaughtUp, Empty }

/// <summary>One card. <see cref="Entry"/> is set for <see cref="StoryCardKind.Entry"/> cards; <see cref="IsNew"/>
/// marks an entry that was unread when it joined the plan (it stays marked once watched, so the progress segments
/// keep showing what was new).</summary>
internal sealed record StoryCard(StoryCardKind Kind, string SubId, FeedEntry? Entry = null, string? Error = null, bool IsNew = false);

/// <summary>One feed's run of cards in the plan. The progress segments are the cards of the current run;
/// <see cref="StartIndex"/> is where playback enters it (its first new entry, or its error card).</summary>
internal sealed class StoryRun(string subId, List<StoryCard> cards, int startIndex = 0)
{
    public string SubId { get; } = subId;
    public List<StoryCard> Cards { get; } = cards;
    public int StartIndex { get; } = startIndex;
}

internal enum StoryMove { None, Moved, FeedChanged, Ended }

/// <summary>
/// What plays, in what order, and where next/previous go (docs/feeds-plan.md §3.6). Pure; the window just renders
/// <see cref="Current"/> and calls <see cref="Next"/>/<see cref="Prev"/>/<see cref="JumpTo"/>.
/// <list type="bullet">
///   <item><b>A head with unread entries:</b> its entries in time order, entered at the first unread one (marked
///   <see cref="StoryCard.IsNew"/>) with up to <see cref="HistoryCount"/> already-read entries behind it, so ←
///   walks back in time. Then every other feed with unread entries (the ones after it in row order, then wrapping
///   round), each entered at its own first new entry, then an "All caught up" card.</item>
///   <item><b>A head with nothing unread:</b> a replay of its newest <see cref="ReplayCount"/>, opened on the
///   newest; ← walks back. A replay doesn't chain into other feeds.</item>
///   <item><b>A failing feed</b> leads its run with an error card.</item>
/// </list>
/// A plan is a snapshot: <see cref="Merge"/> adds entries that arrive mid-story only <em>ahead</em> of the
/// cursor, never reshuffling what's behind it.
/// </summary>
internal sealed class StoryPlan
{
    public const int ReplayCount = 10;

    /// <summary>Read entries kept behind a feed's first new one, to go back to.</summary>
    public const int HistoryCount = 10;

    private readonly List<StoryRun> _runs;

    private StoryPlan(List<StoryRun> runs, int run, int card, bool replay)
    {
        _runs = runs;
        RunIndex = run;
        CardIndex = card;
        IsReplay = replay;
    }

    public IReadOnlyList<StoryRun> Runs => _runs;
    public int RunIndex { get; private set; }
    public int CardIndex { get; private set; }
    public bool IsReplay { get; }

    public StoryRun CurrentRun => _runs[RunIndex];
    public StoryCard Current => CurrentRun.Cards[CardIndex];

    // ── Building ────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The plan for a click on <paramref name="subId"/>'s head.</summary>
    public static StoryPlan ForHead(IReadOnlyList<StoryFeed> feeds, string subId)
    {
        int at = IndexOf(feeds, subId);
        if (at < 0) return Empty(subId);
        var feed = feeds[at];

        if (Unread(feed).Count > 0)
        {
            var runs = new List<StoryRun> { UnreadRun(feed, withError: true) };
            for (int k = 1; k < feeds.Count; k++)
            {
                var other = feeds[(at + k) % feeds.Count];
                if (Unread(other).Count > 0) runs.Add(UnreadRun(other, withError: false));
            }
            runs.Add(new StoryRun("", [new StoryCard(StoryCardKind.CaughtUp, "")]));
            return new StoryPlan(runs, 0, runs[0].StartIndex, replay: false);
        }

        // Replay: newest N, shown oldest → newest, opened on the newest.
        var cards = new List<StoryCard>();
        if (feed.Error is not null) cards.Add(new StoryCard(StoryCardKind.Error, feed.SubId, Error: feed.Error));
        cards.AddRange(feed.Entries.Take(ReplayCount).Reverse().Select(e => new StoryCard(StoryCardKind.Entry, feed.SubId, e)));
        if (cards.Count == 0) return Empty(subId);
        return new StoryPlan([new StoryRun(feed.SubId, cards)], 0, cards.Count - 1, replay: true);
    }

    /// <summary>The plan for the overflow chip: the first feed with unread entries, else a replay of the first.</summary>
    public static StoryPlan ForOverflow(IReadOnlyList<StoryFeed> feeds)
    {
        if (feeds.Count == 0) return Empty("");
        var first = feeds.FirstOrDefault(f => Unread(f).Count > 0) ?? feeds[0];
        return ForHead(feeds, first.SubId);
    }

    private static StoryPlan Empty(string subId) =>
        new([new StoryRun(subId, [new StoryCard(StoryCardKind.Empty, subId)])], 0, 0, replay: true);

    private static int IndexOf(IReadOnlyList<StoryFeed> feeds, string subId)
    {
        for (int i = 0; i < feeds.Count; i++) if (feeds[i].SubId == subId) return i;
        return -1;
    }

    // Unread entries, oldest first.
    private static List<FeedEntry> Unread(StoryFeed f) =>
        f.Entries.Where(e => f.UnreadIds.Contains(e.Id)).Reverse().ToList();

    // A feed with news: its entries oldest → newest, from up to HistoryCount read ones before the first unread to the
    // newest (a read entry newer than the first unread one stays in its place in time). Entered at the first unread,
    // or at the error card leading it.
    private static StoryRun UnreadRun(StoryFeed f, bool withError)
    {
        var ordered = f.Entries.Reverse().ToList();
        int firstNew = ordered.FindIndex(e => f.UnreadIds.Contains(e.Id));
        int from = Math.Max(0, firstNew - HistoryCount);
        var cards = ordered.Skip(from)
            .Select(e => new StoryCard(StoryCardKind.Entry, f.SubId, e, IsNew: f.UnreadIds.Contains(e.Id)))
            .ToList();
        int start = firstNew - from;
        if (withError && f.Error is not null) cards.Insert(start, new StoryCard(StoryCardKind.Error, f.SubId, Error: f.Error));
        return new StoryRun(f.SubId, cards, start);
    }

    // ── Navigation ──────────────────────────────────────────────────────────────────────────────────────────

    public StoryMove Next()
    {
        if (CardIndex + 1 < CurrentRun.Cards.Count) { CardIndex++; return StoryMove.Moved; }
        if (RunIndex + 1 < _runs.Count) { RunIndex++; CardIndex = CurrentRun.StartIndex; return StoryMove.FeedChanged; }
        return StoryMove.Ended;
    }

    public StoryMove Prev()
    {
        if (CardIndex > 0) { CardIndex--; return StoryMove.Moved; }
        if (RunIndex > 0) { RunIndex--; CardIndex = CurrentRun.Cards.Count - 1; return StoryMove.FeedChanged; }
        return StoryMove.None;
    }

    /// <summary>Jumps to a card of the current run (a click on a progress segment).</summary>
    public StoryMove JumpTo(int card)
    {
        if (card < 0 || card >= CurrentRun.Cards.Count || card == CardIndex) return StoryMove.None;
        CardIndex = card;
        return StoryMove.Moved;
    }

    /// <summary>
    /// Folds a fresh snapshot in without disturbing what's been watched: new unread entries are appended to the
    /// current run and to later runs, and feeds that newly have unread entries get a run before the end card.
    /// Nothing at or behind the cursor moves. A replay is left alone.
    /// </summary>
    public void Merge(IReadOnlyList<StoryFeed> feeds)
    {
        if (IsReplay) return;
        int endCard = _runs.FindIndex(r => r.Cards.Count == 1 && r.Cards[0].Kind == StoryCardKind.CaughtUp);
        var planned = new HashSet<string>(_runs.Select(r => r.SubId), StringComparer.Ordinal);

        foreach (var feed in feeds)
        {
            var unread = Unread(feed);
            if (unread.Count == 0) continue;

            int r = _runs.FindIndex(x => x.SubId == feed.SubId);
            if (r >= 0)
            {
                if (r < RunIndex) continue;   // already watched past it
                var cards = _runs[r].Cards;
                var newIds = unread.Select(e => e.Id).ToHashSet(StringComparer.Ordinal);
                // A card ahead of the cursor whose entry has since turned unread is marked new; behind it, nothing moves.
                for (int i = r == RunIndex ? CardIndex + 1 : 0; i < cards.Count; i++)
                    if (cards[i] is { Entry: { } ce, IsNew: false } c && newIds.Contains(ce.Id)) cards[i] = c with { IsNew = true };
                var have = new HashSet<string>(cards.Where(c => c.Entry is not null).Select(c => c.Entry!.Id), StringComparer.Ordinal);
                foreach (var e in unread)
                    if (!have.Contains(e.Id)) cards.Add(new StoryCard(StoryCardKind.Entry, feed.SubId, e, IsNew: true));
            }
            else if (!planned.Contains(feed.SubId))
            {
                var run = UnreadRun(feed, withError: false);
                int insertAt = endCard >= 0 ? endCard : _runs.Count;
                if (insertAt <= RunIndex) insertAt = RunIndex + 1;
                _runs.Insert(insertAt, run);
                if (endCard >= 0 && insertAt <= endCard) endCard++;
                planned.Add(feed.SubId);
            }
        }
    }
}
