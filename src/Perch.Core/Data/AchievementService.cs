namespace Perch.Data;

/// <summary>A newly-reached achievement rung, ready to announce — the level's name/emoji/tier plus a
/// short detail line ("Tokens · Lvl 3" for a levelled family, or the criteria for a one-off).</summary>
internal sealed record AchievementUnlock(string Name, string Emoji, string Detail, AchievementTier Tier);

/// <summary>
/// Ties the (stateless) <see cref="AchievementCatalog"/> to the durable <see cref="AchievementStore"/>:
/// evaluates the families, commits any newly-reached <b>levels</b>, and hands back the rungs to announce.
/// The once-only guarantee lives here — a rung already in the store is never returned again, and crossing
/// several rungs at once returns each.
///
/// It does <b>not</b> decide how to present them. The first sync on a fresh install (or after the store's
/// id scheme changes) legitimately returns everything you've ever earned — dozens of rungs — so the caller
/// collapses a large batch into a single summary toast rather than a wall of pop-ups. See the App's
/// achievement check.
///
/// Not thread-safe; call <see cref="Sync"/> from one owner (the app marshals the disk scan off the UI
/// thread but invokes this from a single place).
/// </summary>
internal sealed class AchievementService
{
    private readonly AchievementStore _store;

    public AchievementService(AchievementStore store) => _store = store;

    /// <summary>Evaluates the families, commits newly-reached levels to the store, and returns the rungs
    /// crossed since the last sync (empty when nothing is new).</summary>
    public IReadOnlyList<AchievementUnlock> Sync(StatsReport report, RangeReport? range, bool includeCost)
    {
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

        return newlyUnlocked;
    }
}
