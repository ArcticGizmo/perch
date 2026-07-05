using System.Diagnostics;
using Perch.Platform;

namespace Perch.Platform.Mac;

/// <summary>
/// macOS <see cref="IAppIconProvider"/> for the overlay's quick-links strip. Deliberately shell-based
/// rather than AppKit: launching goes through <c>open -a</c>, and an app's icon is produced by locating
/// its <c>.app</c> bundle, finding the bundle's <c>.icns</c>, and converting it to a cached PNG with
/// <c>sips</c> (both ship with macOS). This honours the seam's on-disk-PNG contract while avoiding the
/// fragile <c>NSImage → NSBitmapImageRep → PNG</c> Objective-C chain — a pragmatic trade given the port is
/// developed without a Mac to verify interop against. Everything is best-effort; a null return just means
/// the strip draws name-derived initials.
///
/// NOTE (Phase 3): the tool invocations follow documented CLI behaviour but aren't yet verified on a Mac.
/// A future refinement is <c>NSWorkspace.iconForFile</c> for pixel-exact icons and Store-style launches.
/// </summary>
public sealed class AppIconProvider : IAppIconProvider
{
    private static readonly string CacheDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Perch", "QuickLinkIcons");

    public string? GetIconFile(string name, string? explicitPath, string? resolvedPath, int pixelSize)
    {
        try
        {
            string? appBundle = FindAppBundle(name, explicitPath, resolvedPath);
            if (appBundle is null) return null;

            string? icns = FindIcns(appBundle);
            if (icns is null) return null;

            Directory.CreateDirectory(CacheDir);
            string outPath = Path.Combine(CacheDir, $"{SafeName(appBundle)}_{pixelSize}.png");
            // Reuse a cached render unless the source .icns is newer.
            if (File.Exists(outPath) && File.GetLastWriteTimeUtc(outPath) >= File.GetLastWriteTimeUtc(icns))
                return outPath;

            // sips scales the .icns into a PNG no larger than pixelSize in either dimension.
            bool ok = RunTool("/usr/bin/sips",
                "-s", "format", "png", "-Z", pixelSize.ToString(), icns, "--out", outPath);
            return ok && File.Exists(outPath) ? outPath : null;
        }
        catch { return null; }
    }

    public bool TryLaunchByName(string name)
    {
        // `open -a <name>` launches (or foregrounds) an app by display name; exit 0 means it started.
        try { return RunTool("/usr/bin/open", "-a", name); }
        catch { return false; }
    }

    // Resolve to a .app bundle: prefer a pinned/resolved path that is (or sits inside) one, else locate by
    // display name with Spotlight. Returns null when nothing plausible is found.
    private static string? FindAppBundle(string name, string? explicitPath, string? resolvedPath)
    {
        foreach (var p in new[] { explicitPath, resolvedPath })
        {
            if (string.IsNullOrWhiteSpace(p)) continue;
            if (AppBundleOf(p!) is { } bundle) return bundle;
        }

        // Spotlight by display name; take the first matching .app.
        string query = $"kMDItemContentType == 'com.apple.application-bundle' && kMDItemDisplayName == \"{name}\"c";
        string? hits = RunToolOutput("/usr/bin/mdfind", query);
        if (hits is not null)
        {
            foreach (var line in hits.Split('\n'))
            {
                string t = line.Trim();
                if (t.EndsWith(".app", StringComparison.OrdinalIgnoreCase) && Directory.Exists(t))
                    return t;
            }
        }
        return null;
    }

    // If path is (or is nested inside) a .app bundle, return the bundle root; else null.
    private static string? AppBundleOf(string path)
    {
        string p = path;
        while (!string.IsNullOrEmpty(p))
        {
            if (p.EndsWith(".app", StringComparison.OrdinalIgnoreCase) && Directory.Exists(p)) return p;
            string? parent = Path.GetDirectoryName(p.TrimEnd('/'));
            if (string.IsNullOrEmpty(parent) || parent == p) break;
            p = parent;
        }
        return null;
    }

    private static string? FindIcns(string appBundle)
    {
        string resources = Path.Combine(appBundle, "Contents", "Resources");
        if (!Directory.Exists(resources)) return null;

        // Prefer the icon named in Info.plist's CFBundleIconFile; else the first .icns in Resources.
        if (ReadBundleIconName(appBundle) is { } named)
        {
            string file = named.EndsWith(".icns", StringComparison.OrdinalIgnoreCase) ? named : named + ".icns";
            string cand = Path.Combine(resources, file);
            if (File.Exists(cand)) return cand;
        }
        var icns = Directory.GetFiles(resources, "*.icns");
        return icns.Length > 0 ? icns[0] : null;
    }

    // Reads CFBundleIconFile via `defaults read` to avoid bundling a plist parser. Best-effort.
    private static string? ReadBundleIconName(string appBundle)
    {
        string infoNoExt = Path.Combine(appBundle, "Contents", "Info"); // `defaults` wants the path sans .plist
        string? val = RunToolOutput("/usr/bin/defaults", "read", infoNoExt, "CFBundleIconFile")?.Trim();
        return string.IsNullOrEmpty(val) ? null : val;
    }

    private static string SafeName(string s)
    {
        foreach (char c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        return s;
    }

    private static bool RunTool(string exe, params string[] args)
    {
        var p = StartTool(exe, args);
        if (p is null) return false;
        using (p)
        {
            if (!p.WaitForExit(10_000)) { try { p.Kill(true); } catch { } return false; }
            return p.ExitCode == 0;
        }
    }

    private static string? RunToolOutput(string exe, params string[] args)
    {
        var p = StartTool(exe, args);
        if (p is null) return null;
        using (p)
        {
            string outp = p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(10_000)) { try { p.Kill(true); } catch { } return null; }
            return p.ExitCode == 0 ? outp : null;
        }
    }

    private static Process? StartTool(string exe, string[] args)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        return Process.Start(psi);
    }
}
