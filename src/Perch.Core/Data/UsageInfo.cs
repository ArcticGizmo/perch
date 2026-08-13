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
/// The account's monthly "extra usage" (overage) window — the <c>extra_usage</c> block of the /usage
/// endpoint. It is a <b>dollar</b> spend against a user-set monthly cap, unlike the percentage rate-limit
/// windows around it. <paramref name="Used"/> and <paramref name="Limit"/> are in <paramref name="Currency"/>'s
/// major units (e.g. AUD dollars), to <paramref name="DecimalPlaces"/> places. <paramref name="Enabled"/> is
/// the account's own toggle — false when extra usage is switched off, in which case there is nothing to show.
/// The endpoint reports no reset boundary for this window (it rolls on the billing month), so there is no
/// pace marker as there is for the timed windows.
/// </summary>
internal sealed record ExtraUsageInfo(
    bool Enabled, decimal Used, decimal Limit, string Currency, int DecimalPlaces, bool LimitReached)
{
    /// <summary>Spent as a percentage of the limit (0–100), or null when the limit is zero/unknown.</summary>
    public double? Percent => Limit > 0 ? (double)(Used / Limit) * 100.0 : null;

    /// <summary>A short currency symbol for common currencies, else the ISO code (e.g. "kr ").</summary>
    public string Symbol => Currency.ToUpperInvariant() switch
    {
        "AUD" or "USD" or "CAD" or "NZD" or "SGD" or "HKD" or "MXN" => "$",
        "EUR" => "€",
        "GBP" => "£",
        "JPY" or "CNY" => "¥",
        "INR" => "₹",
        _ => Currency + " ",
    };

    // Whole amounts drop the ".00" so the compact bar caption stays tight ("$0/$100"); fractional
    // amounts keep up to DecimalPlaces of precision ("$12.50/$100").
    private string Amount(decimal v) =>
        v == decimal.Truncate(v) ? $"{Symbol}{v:0}" : $"{Symbol}{v.ToString("0." + new string('#', Math.Max(1, DecimalPlaces)))}";

    /// <summary>The right-column caption for the spend bar, e.g. <c>$0/$100</c>.</summary>
    public string Compact => $"{Amount(Used)}/{Amount(Limit)}";

    /// <summary>The fuller phrasing for the tooltip, e.g. <c>$0.00 of $100.00 AUD</c>.</summary>
    public string Detailed =>
        $"{Symbol}{Used.ToString("N" + DecimalPlaces)} of {Symbol}{Limit.ToString("N" + DecimalPlaces)} {Currency.ToUpperInvariant()}";
}

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

    /// <summary>
    /// The monthly extra-usage (overage) spend window, or null when the endpoint omits the block. Kept off
    /// the positional list (and defaulted) so existing construction sites and the <c>with</c>-expressions in
    /// <see cref="Perch.Data.UsageMonitor"/> are unaffected. Its own <see cref="ExtraUsageInfo.Enabled"/> flag
    /// says whether the account has extra usage switched on; the spend bar checks it before drawing.
    /// </summary>
    public ExtraUsageInfo? ExtraUsage { get; init; }

    // Past this age a successful reading is considered stale (we poll every 5 minutes,
    // so anything older than 6 means at least one poll was missed or failed).
    private static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(6);

    /// <summary>The "no data yet" placeholder shown before the first fetch completes.</summary>
    public static UsageInfo Empty { get; } =
        new(null, null, null, null, DateTime.MinValue, false, "No usage data yet");

    public bool IsStale(DateTime now) => !Ok || now - LastUpdated > StaleAfter;
}
