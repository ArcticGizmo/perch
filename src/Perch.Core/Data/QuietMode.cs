namespace Perch.Data;

/// <summary>How long a Quiet-mode window lasts, chosen from the overlay header's right-click menu.</summary>
internal enum QuietDuration
{
    /// <summary>Turn Quiet mode off now.</summary>
    Off,
    /// <summary>A 1-minute window, offered only in DEBUG builds so the "comes back online" path is quick to test.</summary>
    Minute1,
    Minutes30,
    Hour1,
    Hours2,
    /// <summary>Until the next 7&#160;am (local wall-clock).</summary>
    UntilMorning,
}

/// <summary>
/// The single "effective settings" resolution layer for Quiet mode. Rather than every fun/social/silly
/// feature checking a quiet flag at its use-site (unmanageable across 50+ toggles), a feature opts in once
/// by marking its <see cref="SettingsRegistry"/> entry <see cref="SettingDescriptor.Playful"/>, and
/// <see cref="Resolve"/> produces a masked copy of <see cref="AppSettings"/> with every playful toggle
/// forced off while a quiet window is active. Everything downstream keeps reading settings exactly as before
/// — it just reads the resolved copy and never knows Quiet mode exists.
///
/// <para>All times are local wall-clock (<c>DateTime.Now</c>): the presets are pure offsets and
/// "until morning" is a wall-clock hour, so the whole class is timezone-independent and unit-testable.</para>
/// </summary>
internal static class QuietMode
{
    /// <summary>The wall-clock hour "until morning" resolves to.</summary>
    public const int MorningHour = 7;

    /// <summary>Whether a quiet window is currently active (a deadline in the future).</summary>
    public static bool IsActive(DateTime? until, DateTime now) => until is { } u && now < u;

    /// <summary>
    /// A masked copy of <paramref name="raw"/> with every <see cref="SettingDescriptor.Playful"/> toggle
    /// forced off when <paramref name="quietActive"/>; the untouched instance when it isn't. The copy keeps
    /// <see cref="AppSettings.QuietUntil"/> intact so callers can still show the remaining time off it.
    /// </summary>
    public static AppSettings Resolve(AppSettings raw, bool quietActive)
    {
        if (!quietActive) return raw;
        var eff = raw.Clone();
        foreach (var d in SettingsRegistry.All)
            if (d.Playful && d.SetBool is { } set)
                set(eff, false);
        return eff;
    }

    /// <summary>The deadline a chosen <paramref name="duration"/> maps to from <paramref name="now"/>,
    /// or null for <see cref="QuietDuration.Off"/>.</summary>
    public static DateTime? DeadlineFor(QuietDuration duration, DateTime now) => duration switch
    {
        QuietDuration.Off          => null,
        QuietDuration.Minute1      => now.AddMinutes(1),
        QuietDuration.Minutes30    => now.AddMinutes(30),
        QuietDuration.Hour1        => now.AddHours(1),
        QuietDuration.Hours2       => now.AddHours(2),
        QuietDuration.UntilMorning => NextMorning(now),
        _                          => null,
    };

    // The next occurrence of MorningHour:00 — today's if it hasn't passed yet, else tomorrow's.
    private static DateTime NextMorning(DateTime now)
    {
        var morning = now.Date.AddHours(MorningHour);
        return now < morning ? morning : morning.AddDays(1);
    }
}
