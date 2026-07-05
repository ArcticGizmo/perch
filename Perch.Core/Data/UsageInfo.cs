namespace Perch.Data;

/// <summary>
/// A snapshot of the account-wide rate-limit usage as reported by Claude Code's
/// /usage endpoint: the 5-hour ("session") and 7-day ("weekly") windows.
/// Percentages are 0–100; null when the value is unknown. <see cref="Ok"/> is false
/// when the most recent fetch failed, in which case the percentages (if any) are the
/// last successfully-read values and should be shown dimmed.
/// </summary>
internal sealed record UsageInfo(
    double? FiveHourPercent,
    double? SevenDayPercent,
    DateTime? FiveHourResetsAt,
    DateTime? SevenDayResetsAt,
    DateTime LastUpdated,
    bool Ok,
    string? Error)
{
    // Past this age a successful reading is considered stale (we poll every 5 minutes,
    // so anything older than 6 means at least one poll was missed or failed).
    private static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(6);

    /// <summary>The "no data yet" placeholder shown before the first fetch completes.</summary>
    public static UsageInfo Empty { get; } =
        new(null, null, null, null, DateTime.MinValue, false, "No usage data yet");

    public bool IsStale(DateTime now) => !Ok || now - LastUpdated > StaleAfter;
}
