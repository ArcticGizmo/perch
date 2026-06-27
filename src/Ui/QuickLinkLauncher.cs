using System.Drawing.Drawing2D;

using Perch.Data;
namespace Perch.Ui;

/// <summary>
/// Resolves a <see cref="QuickLink"/>'s icon and launches/focuses its app — the icon-loading and
/// shell-launch logic the overlay's quick-links strip used to carry inline. Icons come from a
/// process-wide cache keyed by (name, path, size): resolving an app's icon can be costly (a Start
/// Menu lookup), and the overlay re-resolves the whole strip on every settings edit, so unchanged
/// links must be a cheap cache hit. The cache owns the bitmaps for the process lifetime, so callers
/// must not dispose what they get back.
/// </summary>
internal static class QuickLinkLauncher
{
    private static readonly Dictionary<string, Bitmap?> _iconCache = new();

    /// <summary>The cached icon for a link at the given pixel size, loading it on first request.
    /// Returns null when no icon resolves (the strip then draws name-derived initials).</summary>
    public static Bitmap? CachedIcon(QuickLink link, int size)
    {
        // A 0x01 control-char separator (it cannot appear in a name or path) keeps the three
        // (name, path, size) parts from colliding: ("ab","c") and ("a","bc") must key apart.
        string key = $"{link.Name}{link.ExePath}{size}";
        if (_iconCache.TryGetValue(key, out var cached)) return cached;
        var icon = LoadQuickLinkIcon(link, size);
        _iconCache[key] = icon;
        return icon;
    }

    // The icon for a quick link. An explicit path takes precedence: we show that program's own icon,
    // even if it's a placeholder. With no path, prefer the icon Windows shows for the app in the Start
    // Menu (matched by name) — for Store / MSIX apps that's the real package logo, which the bare alias
    // exe doesn't carry — then a discovered well-known exe, else null (the strip draws initials).
    private static Bitmap? LoadQuickLinkIcon(QuickLink link, int size)
    {
        if (!string.IsNullOrWhiteSpace(link.ExePath))
            return LoadAppIcon(link.ExePath, size);

        int px = Math.Max(size, 32);  // render larger than the strip, then downscale for crispness
        using var fromStartMenu = ShellIcon.LoadStartMenuByName(link.Name, px);
        if (fromStartMenu != null) return ScaleTo(fromStartMenu, size);

        return LoadAppIcon(link.ResolveExe(), size);
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

    // Focuses the app's existing window if it's running, otherwise launches it. An explicit path is
    // launched directly; with no path we launch by Start Menu identity (reliable for Store / MSIX apps
    // that have no real exe to point at), falling back to a discovered well-known exe.
    public static void LaunchOrFocus(QuickLink link)
    {
        try
        {
            foreach (var p in System.Diagnostics.Process.GetProcessesByName(link.ProcessName()))
            {
                if (p.MainWindowHandle != IntPtr.Zero)
                {
                    NativeMethods.FocusWindow(p.MainWindowHandle);
                    return;
                }
            }

            if (!string.IsNullOrWhiteSpace(link.ExePath)) { StartFile(link.ExePath); return; }

            var appId = ShellIcon.StartMenuAppId(link.Name);
            if (appId != null)
            {
                System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo("explorer.exe", $"shell:AppsFolder\\{appId}"));
                return;
            }

            var exe = link.ResolveExe();
            if (exe != null) StartFile(exe);
        }
        catch { }
    }

    private static void StartFile(string path) =>
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
}
