namespace Perch.Data;

/// <summary>
/// One model-scoped weekly window (the endpoint's <c>weekly_scoped</c> limits — e.g. Fable's own
/// allowance, separate from the account-wide weekly pool). <paramref name="Label"/> is the model's
/// display name, used verbatim as the bar caption.
/// </summary>
/// <param name="ResetsAt">The endpoint reports this as null for scoped buckets, so the monitor
/// substitutes the account-wide weekly reset — weekly windows share a reset boundary — giving the bar
/// a pace marker like the two above it.</param>
internal sealed record ScopedUsage(string Label, double? Percent, DateTime? ResetsAt);

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
    /// <summary>
    /// The model-scoped weekly windows, in the order the endpoint listed them; empty when the account
    /// has none. Kept off the positional list (and defaulted) so existing construction sites and the
    /// <c>with</c>-expressions in <see cref="Perch.Data.UsageMonitor"/> are unaffected. A list rather
    /// than a Fable-shaped field: the endpoint names the model in the payload, so a second scoped
    /// bucket or a rename needs no code change.
    /// </summary>
    public IReadOnlyList<ScopedUsage> Scoped { get; init; } = [];

    // Past this age a successful reading is considered stale (we poll every 5 minutes,
    // so anything older than 6 means at least one poll was missed or failed).
    private static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(6);

    /// <summary>The "no data yet" placeholder shown before the first fetch completes.</summary>
    public static UsageInfo Empty { get; } =
        new(null, null, null, null, DateTime.MinValue, false, "No usage data yet");

    public bool IsStale(DateTime now) => !Ok || now - LastUpdated > StaleAfter;
}
