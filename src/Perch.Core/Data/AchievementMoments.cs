namespace Perch.Data;

/// <summary>
/// Ids for things that happen in Perch itself rather than in a transcript, so no stats scan can find them
/// after the fact. The app reports one through <see cref="AchievementService.RecordMoment"/> the first time it
/// happens; the store keeps it and <see cref="AchievementCatalog.Evaluate"/> reads it like any other metric.
/// </summary>
internal static class AchievementMoments
{
    /// <summary>The overlay had more session rows than its monitor could fit, so the list became a scroller.</summary>
    public const string SessionListOverflowed = "session-list-overflowed";
}
