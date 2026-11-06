using System.Globalization;

namespace Perch.Data.Resources;

/// <summary>Where resource recordings live (<c>&lt;settings dir&gt;/diagnostics/</c>, beside <c>logs/</c>) and how many
/// are kept.</summary>
public static class ResourceRecordingStore
{
    public const int Keep = 20;
    private const string Prefix = "resources-";
    private const string Extension = ".jsonl";

    public static string Directory =>
        Path.Combine(Path.GetDirectoryName(AppSettings.SettingsFilePath) ?? Path.GetTempPath(), "diagnostics");

    public static string NewFilePath(DateTime localNow) =>
        Path.Combine(Directory, Prefix + localNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + Extension);

    /// <summary>Saved recordings, newest first.</summary>
    public static IReadOnlyList<FileInfo> List(string? directory = null)
    {
        try
        {
            var dir = new DirectoryInfo(directory ?? Directory);
            if (!dir.Exists) return [];
            return dir.GetFiles(Prefix + "*" + Extension)
                .OrderByDescending(f => f.Name, StringComparer.Ordinal)
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    /// <summary>Deletes all but the newest <paramref name="keep"/> recordings.</summary>
    public static void Prune(int keep = Keep, string? directory = null)
    {
        foreach (var old in List(directory).Skip(keep))
        {
            try { old.Delete(); } catch { }
        }
    }

    /// <summary>The start time a recording's file name carries, for listing without opening it.</summary>
    public static DateTime? StartedAt(FileInfo file)
    {
        var stem = Path.GetFileNameWithoutExtension(file.Name);
        if (!stem.StartsWith(Prefix, StringComparison.Ordinal)) return null;
        return DateTime.TryParseExact(stem[Prefix.Length..], "yyyyMMdd-HHmmss", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeLocal, out var at) ? at : null;
    }
}
