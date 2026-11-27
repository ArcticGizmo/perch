using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Security.Cryptography;
using System.Text;
using Perch.Platform;

namespace Perch.Platform.Windows;

/// <summary>
/// Windows <see cref="IAppIconProvider"/>: resolves a quick link's icon through <see cref="ShellIcon"/>
/// and materialises it as a PNG in a per-user cache directory, returning the file path. Launching by
/// name goes through the Start-Menu AUMID and the AppsFolder shell verb (for Store/MSIX apps that have
/// no plain exe). Icon resolution and encoding are memoised for the process lifetime — the overlay
/// re-requests the whole strip on every settings edit, so an unchanged link is a cheap cache hit and its
/// PNG is written at most once per run.
/// </summary>
public sealed class WindowsAppIconProvider : IAppIconProvider
{
    private static readonly string CacheDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Perch", "IconCache");

    // key → cached PNG path (or null when no icon resolved), so a repeat request neither re-renders the
    // shell icon nor re-writes the file. Guards concurrent access from the (rare) off-thread caller.
    private readonly Dictionary<string, string?> _resolved = new();
    private readonly object _gate = new();

    public string? GetIconFile(string name, string? explicitPath, string? resolvedPath, int pixelSize)
    {
        string key = $"{name}{explicitPath}{resolvedPath}{pixelSize}";
        lock (_gate)
        {
            if (_resolved.TryGetValue(key, out var cached)) return cached;
            string? path = RenderOnSta(name, explicitPath, resolvedPath, pixelSize, key);
            _resolved[key] = path;
            return path;
        }
    }

    // Generous enough for the first call, which also enumerates the whole AppsFolder (~1s+).
    private static readonly TimeSpan RenderTimeout = TimeSpan.FromSeconds(10);

    // The shell image factory is apartment-threaded: called from a thread-pool (MTA) thread, a file whose
    // icon comes from a packaged handler (e.g. a .png owned by Photos) can block forever, and holding _gate
    // would then starve every later icon request. So each render gets its own STA thread and a deadline; a
    // render that misses it is abandoned (a background thread can't keep the process alive) and the link
    // falls back to initials.
    private static string? RenderOnSta(string name, string? explicitPath, string? resolvedPath, int size, string key)
    {
        string? result = null;
        var thread = new Thread(() => result = Render(name, explicitPath, resolvedPath, size, key))
        {
            IsBackground = true,
            Name = "Perch icon render",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return thread.Join(RenderTimeout) ? result : null;
    }

    // Resolves the icon bitmap (explicit path wins; then the Start-Menu logo; then a discovered exe),
    // writes it to the cache as PNG, and returns the path. Null when nothing resolves or the write fails.
    private static string? Render(string name, string? explicitPath, string? resolvedPath, int size, string key)
    {
        Bitmap? bmp = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(explicitPath))
            {
                bmp = LoadAppIcon(explicitPath, size);
            }
            else
            {
                int px = Math.Max(size, 32); // render larger than the strip, then downscale for crispness
                using var fromStartMenu = ShellIcon.LoadStartMenuByName(name, px);
                bmp = fromStartMenu != null ? ScaleTo(fromStartMenu, size) : LoadAppIcon(resolvedPath, size);
            }

            if (bmp == null) return null;

            Directory.CreateDirectory(CacheDir);
            string file = Path.Combine(CacheDir, Hash(key) + ".png");
            bmp.Save(file, ImageFormat.Png);
            return file;
        }
        catch { return null; }
        finally { bmp?.Dispose(); }
    }

    private static Bitmap? LoadAppIcon(string? exePath, int size)
    {
        if (string.IsNullOrWhiteSpace(exePath)) return null;

        // The shell image factory renders a crisp, transparent icon for an ordinary exe; fall back to
        // the classic exe-icon extraction if it can't.
        using var src = ShellIcon.Load(exePath, Math.Max(size, 32)) ?? ExtractClassicIcon(exePath);
        return src == null ? null : ScaleTo(src, size);
    }

    private static Bitmap? ExtractClassicIcon(string exePath)
    {
        try
        {
            using var icon = Icon.ExtractAssociatedIcon(exePath);
            return icon?.ToBitmap();
        }
        catch { return null; }
    }

    private static Bitmap ScaleTo(Bitmap src, int size)
    {
        var result = new Bitmap(size, size);
        using var ig = Graphics.FromImage(result);
        ig.InterpolationMode = InterpolationMode.HighQualityBicubic;
        ig.DrawImage(src, 0, 0, size, size);
        return result;
    }

    public bool TryLaunchByName(string name)
    {
        try
        {
            var appId = ShellIcon.StartMenuAppId(name);
            if (appId == null) return false;
            Process.Start(new ProcessStartInfo(Perch.Data.ExecutableResolver.WindowsTool("explorer.exe"), $"shell:AppsFolder\\{appId}"));
            return true;
        }
        catch { return false; }
    }

    // A stable, filesystem-safe filename stem for a cache key (SHA-1 hex; deterministic within and
    // across runs, so a re-request reuses the same file).
    private static string Hash(string key)
    {
        byte[] bytes = SHA1.HashData(Encoding.UTF8.GetBytes(key));
        var sb = new StringBuilder(bytes.Length * 2);
        foreach (var b in bytes) sb.Append(b.ToString("x2"));
        return sb.ToString();
    }
}
