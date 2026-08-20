namespace Perch.Data;

/// <summary>
/// Two-directional relative-time formatting for due dates — "in 2h", "in 3d", "due now", "overdue 5m".
/// The overlay feed's <c>FormatAgo</c> only speaks the past and is UI-private; this is a pure, toolkit-neutral
/// helper (takes <c>nowUtc</c> explicitly) so it lives in Core and is deterministically testable.
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

    private static string Magnitude(TimeSpan mag)
    {
        if (mag < TimeSpan.FromHours(1)) return $"{(int)mag.TotalMinutes}m";
        if (mag < TimeSpan.FromDays(1)) return $"{(int)mag.TotalHours}h";
        return $"{(int)mag.TotalDays}d";
    }
}
