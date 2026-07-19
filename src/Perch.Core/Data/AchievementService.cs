namespace Perch.Data;

/// <summary>
/// Ties the (stateless) <see cref="AchievementCatalog"/> to the durable <see cref="AchievementStore"/>:
/// evaluates the lifetime badges, commits any newly-earned ones, and hands back the list the caller should
/// announce (a toast per badge). The once-only guarantee lives here — a badge already in the store is never
/// returned again.
///
/// <b>Silent first run.</b> On a fresh install the store file doesn't exist yet, so the first sync would
/// otherwise toast every badge the user has already earned across their whole history — a wall of pop-ups.
/// Instead the first sync seeds the store silently and announces nothing; every sync after that (this run
/// or a later launch, since the file now exists) announces only genuinely-new unlocks.
///
/// Not thread-safe; call <see cref="Sync"/> from one place (the app marshals the disk scan off the UI
/// thread but invokes this from a single owner).
/// </summary>
internal sealed class AchievementService
{
    private readonly AchievementStore _store;
    private bool _seeded;

    public AchievementService(AchievementStore store) => _store = store;

    /// <summary>Evaluates lifetime badges against the all-time report, commits newly-earned ones to the
    /// store, and returns the badges to announce (empty on the silent first-run seed).</summary>
    public IReadOnlyList<Achievement> Sync(StatsReport report, RangeReport? range, bool includeCost)
    {
        // Announce once we've either seeded this process or found a pre-existing store on disk.
        bool announce = _seeded || _store.Existed;
        _seeded = true;

        var newlyUnlocked = new List<Achievement>();
        foreach (var a in AchievementCatalog.Evaluate(report, range, includeCost))
        {
            if (a.Earned && _store.Add(a.Id))
                newlyUnlocked.Add(a);
        }

        if (newlyUnlocked.Count > 0)
            _store.Save();

        return announce ? newlyUnlocked : [];
    }
}
