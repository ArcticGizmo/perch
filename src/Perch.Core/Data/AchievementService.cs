namespace Perch.Data;

/// <summary>A newly-reached achievement rung, ready to announce — the level's name/emoji/tier plus a
/// short detail line ("Tokens · Lvl 3" for a levelled family, or the criteria for a one-off).</summary>
internal sealed record AchievementUnlock(string Name, string Emoji, string Detail, AchievementTier Tier);

/// <summary>
/// Ties the (stateless) <see cref="AchievementCatalog"/> to the durable <see cref="AchievementStore"/>:
/// evaluates the families, commits any newly-reached <b>levels</b>, and hands back the rungs to announce
/// (a toast each). The once-only guarantee lives here — a rung already in the store is never returned again,
/// and crossing several rungs at once announces each.
///
/// <b>Silent first run.</b> On a fresh install the store file doesn't exist yet, so the first sync would
/// otherwise toast every rung the user has already earned across their whole history — a wall of pop-ups.
/// Instead the first sync seeds the store silently and announces nothing; every sync after that (this run
/// or a later launch, since the file now exists) announces only genuinely-new unlocks.
///
/// Not thread-safe; call <see cref="Sync"/> from one owner (the app marshals the disk scan off the UI
/// thread but invokes this from a single place).
/// </summary>
internal sealed class AchievementService
{
    private readonly AchievementStore _store;
    private bool _seeded;

    public AchievementService(AchievementStore store) => _store = store;

    /// <summary>Evaluates the families, commits newly-reached levels to the store, and returns the rungs to
    /// announce (empty on the silent first-run seed).</summary>
    public IReadOnlyList<AchievementUnlock> Sync(StatsReport report, RangeReport? range, bool includeCost)
    {
        // Announce once we've either seeded this process or found a pre-existing store on disk.
        bool announce = _seeded || _store.Existed;
        _seeded = true;

        var newlyUnlocked = new List<AchievementUnlock>();
        foreach (var fam in AchievementCatalog.Evaluate(report, range, includeCost))
        {
            for (int i = 0; i < fam.Levels.Count; i++)
            {
                var level = fam.Levels[i];
                if (!level.Earned || !_store.Add(level.Id))
                    continue;
                string detail = fam.Category.Length > 0 ? $"{fam.Category} · Lvl {i + 1}" : level.Criteria;
                newlyUnlocked.Add(new AchievementUnlock(level.Name, level.Emoji, detail, level.Tier));
            }
        }

        if (newlyUnlocked.Count > 0)
            _store.Save();

        return announce ? newlyUnlocked : [];
    }
}
