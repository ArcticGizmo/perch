namespace Perch.Avalonia.Rendering;

/// <summary>
/// The one breathing curve behind Perch's pulsing attention cues — the overlay's wrong-account outline and the
/// Roost's needs-you ring — so every pulse reads the same, and the OS reduce-motion preference
/// (<see cref="Perch.Platform.IMotionPreference"/>) is honoured in one place: when it's set,
/// <see cref="Intensity"/> holds at full and callers draw their steady, thicker fallback instead
/// (<see cref="ReduceMotion"/>), and should stop their repaint timers.
/// </summary>
internal static class Pulse
{
    /// <summary>The default breathing period.</summary>
    public const double PeriodMs = 1100;

    private static readonly TimeSpan RecheckEvery = TimeSpan.FromSeconds(2);
    private static bool _cached;
    private static DateTime _checkedUtc = DateTime.MinValue;

    /// <summary>Forces the preference for the headless renderer (deterministic captures of both states);
    /// null = read the OS.</summary>
    public static bool? Override { get; set; }

    /// <summary>True when the OS asks for reduced motion. Re-read at most every couple of seconds, since it's
    /// consulted per painted frame while a pulse is on screen.</summary>
    public static bool ReduceMotion
    {
        get
        {
            if (Override is { } forced) return forced;
            var now = DateTime.UtcNow;
            if (now - _checkedUtc >= RecheckEvery)
            {
                _cached = PlatformServices.MotionPreference.ReduceMotion;
                _checkedUtc = now;
            }
            return _cached;
        }
    }

    /// <summary>A smooth 0→1→0 breathing value off the wall clock (<paramref name="periodMs"/> per cycle), or a
    /// constant 1 under reduced motion.</summary>
    public static double Intensity(double periodMs = PeriodMs)
    {
        if (ReduceMotion) return 1;
        double phase = (DateTime.Now.TimeOfDay.TotalMilliseconds % periodMs) / periodMs;
        return 0.5 - 0.5 * Math.Cos(phase * 2 * Math.PI);
    }
}
