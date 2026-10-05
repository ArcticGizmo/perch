namespace Perch.Data;

/// <summary>
/// Relative-time formatting: due dates both ways ("in 2h", "due now", "overdue 5m"), and how long ago a session
/// ended (<see cref="Ago"/>, <see cref="Span"/>). The overlay feed's <c>FormatAgo</c> is UI-private; this is a pure,
/// toolkit-neutral helper (the clock passed in) so it lives in Core and is deterministically testable.
/// </summary>
internal static class RelativeTime
{
    /// <summary>
    /// A short label for how far <paramref name="dueUtc"/> is from <paramref name="nowUtc"/>. Within a minute
    /// either way reads "due now"; otherwise "in Xm/h/d" for the future and "overdue Xm/h/d" for the past.
    /// Both inputs are UTC.
    /// </summary>
    public static string DueLabel(DateTime nowUtc, DateTime dueUtc)
    {
        var delta = dueUtc - nowUtc;
        var mag = delta < TimeSpan.Zero ? -delta : delta;

        if (mag < TimeSpan.FromMinutes(1)) return "due now";

        var span = Magnitude(mag);
        return delta >= TimeSpan.Zero ? $"in {span}" : $"overdue {span}";
    }

    /// <summary>How long ago, short: "just now", "12m ago", "5h ago" (up to two days), "3d ago".</summary>
    public static string Ago(DateTime now, DateTime at) =>
        now - at < TimeSpan.FromMinutes(1) ? "just now" : $"{Elapsed(now - at)} ago";

    /// <summary>The bare span <see cref="Ago"/> names, for a label that carries the "when" itself ("interrupted ·
    /// 14h"): "just now", "12m", "5h", "3d".</summary>
    public static string Span(DateTime now, DateTime at) =>
        now - at < TimeSpan.FromMinutes(1) ? "just now" : Elapsed(now - at);

    // Hours run to two days, so "yesterday evening" still reads in hours.
    private static string Elapsed(TimeSpan d) =>
        d < TimeSpan.FromHours(1) ? $"{(int)d.TotalMinutes}m"
        : d < TimeSpan.FromHours(48) ? $"{(int)d.TotalHours}h"
        : $"{(int)d.TotalDays}d";

    private static string Magnitude(TimeSpan mag)
    {
        if (mag < TimeSpan.FromHours(1)) return $"{(int)mag.TotalMinutes}m";
        if (mag < TimeSpan.FromDays(1)) return $"{(int)mag.TotalHours}h";
        return $"{(int)mag.TotalDays}d";
    }
}
