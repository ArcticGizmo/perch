using System.Globalization;

namespace Perch.Data.Resources;

/// <summary>Number formatting shared by the findings text and the viewer, so both say "1.2 GB" the same way.</summary>
public static class ResourceFormat
{
    public static string Bytes(long bytes)
    {
        const double K = 1024, M = K * 1024, G = M * 1024;
        double b = Math.Max(0, bytes);
        return b switch
        {
            >= 10 * G => (b / G).ToString("0", CultureInfo.CurrentCulture) + " GB",
            >= G => (b / G).ToString("0.0", CultureInfo.CurrentCulture) + " GB",
            >= M => (b / M).ToString("0", CultureInfo.CurrentCulture) + " MB",
            >= K => (b / K).ToString("0", CultureInfo.CurrentCulture) + " KB",
            _ => ((long)b).ToString(CultureInfo.CurrentCulture) + " B",
        };
    }

    public static string Rate(long bytesPerSecond) => Bytes(bytesPerSecond) + "/s";

    public static string Duration(TimeSpan span)
    {
        if (span < TimeSpan.Zero) span = TimeSpan.Zero;
        if (span.TotalHours >= 1) return $"{(int)span.TotalHours}h {span.Minutes:00}m";
        if (span.TotalMinutes >= 1) return span.Seconds == 0 ? $"{span.Minutes}m" : $"{span.Minutes}m {span.Seconds:00}s";
        return $"{span.Seconds}s";
    }

    public static string Percent(double value) =>
        value is > 0 and < 1 ? "<1%" : value.ToString("0", CultureInfo.CurrentCulture) + "%";
}
