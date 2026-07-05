namespace Perch.Data;

/// <summary>
/// A user-configured shortcut shown in the overlay's quick-links row (the icon strip below the
/// usage bars). Clicking it focuses the app's window if it's already running, or launches it.
/// <para>
/// <see cref="ExePath"/> may be left empty for the well-known apps (GitKraken, Slack), whose
/// install location is discovered live via <see cref="KnownApps"/> so a version bump doesn't strand
/// the link. Custom links always carry an explicit executable path.
/// </para>
/// </summary>
internal sealed class QuickLink
{
    public string Name    { get; set; } = "";
    public string ExePath { get; set; } = "";
    public bool   Enabled { get; set; } = true;

    // The executable to launch: the configured path when it's set and still exists, otherwise a live
    // lookup for the well-known apps (handles version-bumped install dirs). Null if nothing resolves.
    public string? ResolveExe()
    {
        if (!string.IsNullOrWhiteSpace(ExePath))
        {
            if (File.Exists(ExePath)) return ExePath;
            // Set but unverifiable — a stale preset path, or a real file in a permission-locked
            // location (e.g. WindowsApps) where File.Exists can come back false. Re-discover known
            // apps; otherwise hand back the configured path and let icon/launch try it anyway.
            return KnownApps.FindByName(Name) ?? ExePath;
        }
        return KnownApps.FindByName(Name);
    }

    // Process name used to focus an already-running window: the resolved exe's filename without its
    // extension, falling back to the link name with whitespace stripped so focusing still has a shot
    // even when the exe can't be resolved.
    public string ProcessName()
    {
        var exe = ResolveExe();
        if (exe != null)
            return Path.GetFileNameWithoutExtension(exe);
        return new string(Name.Where(c => !char.IsWhiteSpace(c)).ToArray()).ToLowerInvariant();
    }

    public QuickLink Clone() => new() { Name = Name, ExePath = ExePath, Enabled = Enabled };
}

/// <summary>
/// Discovery for the apps Perch ships first-class support for. GitKraken and Slack ship as
/// per-user Squirrel/Electron installs (a flat "Programs" layout or a versioned "app-x.y.z" one) and
/// Slack is also distributed through the Microsoft Store, so we additionally probe the per-user
/// WindowsApps folder. We return the first hit, preferring the newest versioned dir.
/// </summary>
internal static class KnownApps
{
    // The well-known link names offered as one-click presets in Settings, in display order.
    public static readonly string[] PresetNames = ["GitKraken", "Slack", "Microsoft Teams", "Outlook"];

    // Resolves a well-known app's executable by its (case-insensitive) link name. Null for custom
    // names or when the app isn't installed.
    public static string? FindByName(string name) => name.Trim().ToLowerInvariant() switch
    {
        "gitkraken"       => FindGitKraken(),
        "slack"           => FindSlack(),
        "microsoft teams" => FindTeams(),
        "outlook"         => FindOutlook(),
        _                 => null,
    };

    public static string? FindGitKraken() => FindElectronApp("GitKraken", "gitkraken", "gitkraken.exe");
    public static string? FindSlack()     => FindElectronApp("Slack",     "slack",     "slack.exe");

    // Teams doesn't follow the Electron/Squirrel layout. Prefer "new" Teams (an MSIX package launched
    // via its per-user execution alias), then fall back to the classic per-user install, which keeps a
    // stable "current" junction pointing at the active version.
    public static string? FindTeams()
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        var windowsApps = Path.Combine(local, "Microsoft", "WindowsApps");
        var alias = Path.Combine(windowsApps, "ms-teams.exe");
        if (File.Exists(alias)) return alias;
        if (Directory.Exists(windowsApps))
        {
            foreach (var pkg in Directory.GetDirectories(windowsApps, "*MSTeams*"))
            {
                var exe = Path.Combine(pkg, "ms-teams.exe");
                if (File.Exists(exe)) return exe;
            }
        }

        var classic = Path.Combine(local, "Microsoft", "Teams", "current", "Teams.exe");
        if (File.Exists(classic)) return classic;

        return null;
    }

    // Outlook, like Teams, comes in two flavours. Prefer "new" Outlook (an MSIX package launched via
    // its per-user execution alias "olk.exe"), then fall back to classic Outlook, a Click-to-Run / MSI
    // install of the Office suite that lives under Program Files in a versioned "OfficeNN" folder.
    public static string? FindOutlook()
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        var windowsApps = Path.Combine(local, "Microsoft", "WindowsApps");
        var alias = Path.Combine(windowsApps, "olk.exe");
        if (File.Exists(alias)) return alias;
        if (Directory.Exists(windowsApps))
        {
            foreach (var pkg in Directory.GetDirectories(windowsApps, "*OutlookForWindows*"))
            {
                var exe = Path.Combine(pkg, "olk.exe");
                if (File.Exists(exe)) return exe;
            }
        }

        // Classic Outlook ships inside the Office suite. Click-to-Run keeps a stable "root" junction;
        // MSI installs land directly in a versioned OfficeNN folder. Probe both 64- and 32-bit Program
        // Files, newest Office version first.
        foreach (var programFiles in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                 })
        {
            if (string.IsNullOrEmpty(programFiles)) continue;
            var office = Path.Combine(programFiles, "Microsoft Office");
            foreach (var baseDir in new[] { Path.Combine(office, "root"), office })
            {
                if (!Directory.Exists(baseDir)) continue;
                foreach (var ver in Directory.GetDirectories(baseDir, "Office*")
                                             .OrderByDescending(x => x, StringComparer.OrdinalIgnoreCase))
                {
                    var exe = Path.Combine(ver, "OUTLOOK.EXE");
                    if (File.Exists(exe)) return exe;
                }
            }
        }

        return null;
    }

    // programsDir: the folder name under LocalAppData\Programs for a flat install.
    // squirrelDir: the lowercase folder name under LocalAppData for a versioned (app-*) install.
    private static string? FindElectronApp(string programsDir, string squirrelDir, string exeName)
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        var standalone = Path.Combine(local, "Programs", programsDir, exeName);
        if (File.Exists(standalone)) return standalone;

        var root = Path.Combine(local, squirrelDir);
        if (Directory.Exists(root))
        {
            foreach (var sub in Directory.GetDirectories(root, "app-*")
                                         .OrderByDescending(x => x, StringComparer.OrdinalIgnoreCase))
            {
                var exe = Path.Combine(sub, exeName);
                if (File.Exists(exe)) return exe;
            }
        }

        // Microsoft Store (MSIX) install. The per-user execution alias under WindowsApps launches the
        // packaged app reliably and still yields its icon, so prefer it; otherwise fall back to the
        // real exe inside the package family folder.
        var windowsApps = Path.Combine(local, "Microsoft", "WindowsApps");
        var alias = Path.Combine(windowsApps, exeName);
        if (File.Exists(alias)) return alias;
        if (Directory.Exists(windowsApps))
        {
            var stem = Path.GetFileNameWithoutExtension(exeName);
            foreach (var pkg in Directory.GetDirectories(windowsApps, $"*{stem}*"))
            {
                var exe = Path.Combine(pkg, exeName);
                if (File.Exists(exe)) return exe;
            }
        }
        return null;
    }
}
