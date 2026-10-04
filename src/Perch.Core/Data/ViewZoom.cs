namespace Perch.Data;

/// <summary>
/// The zoom levels the session window and the Roost step through on Ctrl+= / Ctrl+− (and Ctrl+wheel): a
/// browser-like ladder around 100%. The level is a plain scale factor (1.0 = 100%) persisted per surface in
/// <see cref="AppSettings"/>; <see cref="Normalize"/> keeps a hand-edited or corrupt value in range.
/// </summary>
public static class ViewZoom
{
    public const double Default = 1.0;

    public static IReadOnlyList<double> Steps { get; } = [0.75, 0.8, 0.9, 1.0, 1.1, 1.25, 1.5, 1.75, 2.0];

    public static double Min => Steps[0];
    public static double Max => Steps[^1];

    // Comparisons tolerate float noise, so a stored 1.1 still counts as sitting on the 1.1 step.
    private const double Epsilon = 0.001;

    /// <summary>The next step above (<paramref name="direction"/> &gt; 0) or below (&lt; 0) <paramref name="current"/>,
    /// pinned at the ends. A value between steps moves to the neighbouring step in that direction.</summary>
    public static double Step(double current, int direction)
    {
        current = Normalize(current);
        if (direction > 0)
        {
            foreach (var s in Steps) if (s > current + Epsilon) return s;
            return Max;
        }
        if (direction < 0)
        {
            for (int i = Steps.Count - 1; i >= 0; i--) if (Steps[i] < current - Epsilon) return Steps[i];
            return Min;
        }
        return current;
    }

    /// <summary>A usable zoom: NaN, infinities and non-positive values fall back to 100%, the rest clamp to the ladder's range.</summary>
    public static double Normalize(double zoom) =>
        double.IsFinite(zoom) && zoom > 0 ? Math.Clamp(zoom, Min, Max) : Default;

    /// <summary>"110%".</summary>
    public static string Label(double zoom) => $"{Math.Round(Normalize(zoom) * 100):0}%";
}
