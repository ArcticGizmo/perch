using System.Text.RegularExpressions;

namespace Perch.Data;

/// <summary>
/// The icons users import for their quick links. Each is a PNG Perch made itself (scaled to <see cref="PixelSize"/>
/// on import), stored under the profile's roaming data folder beside <c>settings.json</c>, because it's the user's
/// data rather than a cache. A link refers to its icon by bare file name only (<see cref="QuickLink.CustomIcon"/>),
/// so the profile stays self-contained and a hand-edited settings file can't point Perch at some other file. Pure
/// apart from the file system calls, which take a directory so tests can use a temp one. Never throws.
/// </summary>
internal static partial class QuickLinkIcons
{
    /// <summary>The stored size: the strip draws at 16 DIP, and 64px stays sharp up to 4× display scale.</summary>
    public const int PixelSize = 64;

    public static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppProfile.DataFolderName, "quick-link-icons");

    // Exactly what NewName produces; anything else is not ours.
    [GeneratedRegex("^[0-9a-f]{32}\\.png$")]
    private static partial Regex NamePattern();

    public static bool IsValidName(string? name) => name is not null && NamePattern().IsMatch(name);

    public static string NewName() => Guid.NewGuid().ToString("N") + ".png";

    /// <summary>The full path of a link's imported icon when it names one of ours and the file is there, else null
    /// (the link then shows its kind's own icon).</summary>
    public static string? PathFor(string? customIcon, string directory)
    {
        if (!IsValidName(customIcon)) return null;
        var path = Path.Combine(directory, customIcon!);
        return File.Exists(path) ? path : null;
    }

    /// <summary>Writes an imported icon's PNG bytes under a fresh name and returns the name, or null on failure.</summary>
    public static string? Save(byte[] png, string directory)
    {
        try
        {
            var name = NewName();
            AtomicFile.Write(Path.Combine(directory, name), s => s.Write(png, 0, png.Length));
            return name;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Deletes an icon by name; ignores anything that isn't one of ours.</summary>
    public static void Delete(string? name, string directory)
    {
        if (!IsValidName(name)) return;
        try { File.Delete(Path.Combine(directory, name!)); } catch { }
    }

    /// <summary>Deletes every icon file no link refers to (a link removed while Perch was down, a crash between
    /// writing an icon and saving settings). Leaves anything not named like ours, and any subfolder, alone.
    /// Returns how many it deleted.</summary>
    public static int Sweep(IEnumerable<string?> referenced, string directory)
    {
        try
        {
            if (!Directory.Exists(directory)) return 0;
            var keep = new HashSet<string>(referenced.Where(IsValidName)!, StringComparer.OrdinalIgnoreCase);
            int deleted = 0;
            foreach (var path in Directory.EnumerateFiles(directory, "*.png"))
            {
                var name = Path.GetFileName(path);
                if (!IsValidName(name) || keep.Contains(name)) continue;
                try { File.Delete(path); deleted++; } catch { }
            }
            return deleted;
        }
        catch
        {
            return 0;
        }
    }
}
