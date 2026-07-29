using Perch.Data;
using Velopack;
using Velopack.Sources;

namespace Perch.Avalonia.Services;

/// <summary>How this copy of Perch got onto the machine — which decides who owns updates.</summary>
internal enum InstallChannelKind
{
    /// <summary>A normal <c>PerchSetup.exe</c> (Velopack) install. Perch updates itself.</summary>
    Setup,
    /// <summary>A Scoop-managed copy: the Velopack portable payload extracted into
    /// <c>&lt;scoop&gt;\apps\perch\&lt;version&gt;\</c>. Scoop owns updates (<c>scoop update perch</c>).</summary>
    Scoop,
    /// <summary>The portable zip extracted by hand, or by a package manager we don't recognise. The user
    /// owns updates.</summary>
    Portable,
    /// <summary>Not an installed build at all — a dev <c>dotnet run</c>. Nothing to update.</summary>
    Unpackaged,
}

/// <summary>
/// Works out which distribution channel this process came from, so the updater never fights the tool that
/// installed it.
///
/// Perch ships through more than one channel now (see <c>docs/distribution-plan.md</c>): the Velopack
/// <c>PerchSetup.exe</c>, which self-updates, and a Scoop bucket, which serves Velopack's *portable* zip
/// and expects <c>scoop update perch</c> to be the only thing that ever rewrites the app directory. A
/// Velopack self-update inside a Scoop app dir would leave Scoop's recorded version/hash pointing at
/// contents that no longer match, so on those installs we surface the command instead of applying.
///
/// Checking for updates works on *every* installed shape (Velopack's portable layout resolves a version
/// and an update feed just fine), so Scoop users still get told a new version exists — only the apply step
/// is handed back to the package manager. See <see cref="UpdateService"/>.
///
/// Detection is best-effort and cached: a probe that throws collapses to <see cref="InstallChannelKind.Unpackaged"/>,
/// which disables the update UI rather than surfacing errors.
/// </summary>
internal static class InstallChannel
{
    private static readonly Lazy<InstallChannelKind> Detected = new(Detect);

    /// <summary>The shell of the current install. Probed once, on first use.</summary>
    public static InstallChannelKind Kind => Detected.Value;

    /// <summary>True when Perch may download and apply its own updates — i.e. a real Velopack install.</summary>
    public static bool SelfUpdates => Kind == InstallChannelKind.Setup;

    /// <summary>True when the update feed can be queried at all (false for a dev run).</summary>
    public static bool CanCheck => Kind != InstallChannelKind.Unpackaged;

    /// <summary>The command that installs an update on this channel, or null when there isn't one to type.</summary>
    public static string? UpdateCommand => Kind == InstallChannelKind.Scoop ? "scoop update perch" : null;

    /// <summary>What to do about an update that's already been detected. Reads as the second half of
    /// "Version 1.2.3 is available. …".</summary>
    public static string Instruction => Kind switch
    {
        InstallChannelKind.Scoop     => "Perch was installed with Scoop, so run \"scoop update perch\" to install it.",
        InstallChannelKind.Portable  => "This is a portable copy of Perch — download the new release and replace it.",
        InstallChannelKind.Unpackaged => "This build isn't installed, so it can't update itself.",
        _                            => "Click Update now to apply it and restart.",
    };

    /// <summary>Who installs updates on this channel, for the idle (nothing-pending) state.</summary>
    public static string OwnershipNote => Kind switch
    {
        InstallChannelKind.Scoop     => "Perch watches for new versions in the background; installing them is Scoop's job — run \"scoop update perch\".",
        InstallChannelKind.Portable  => "Perch watches for new versions in the background; this portable copy is updated by replacing it with the latest release.",
        InstallChannelKind.Unpackaged => "This build isn't installed, so update checks are disabled.",
        _                            => "Perch checks for updates in the background and applies new versions on the next launch.",
    };

    /// <summary>
    /// A fresh <see cref="UpdateManager"/> over the GitHub release feed. Constructing one is cheap and does
    /// no I/O — the network only happens on the check/download calls — so every caller here builds its own
    /// rather than sharing mutable state.
    /// </summary>
    public static UpdateManager CreateManager() => new(new GithubSource(AppInfo.RepoUrl, null, false));

    /// <summary>
    /// Rewrites a path inside a Scoop app directory to go through Scoop's stable <c>current</c> junction
    /// (<c>…\apps\perch\0.2.32\current\perch.exe</c> → <c>…\apps\perch\current\current\perch.exe</c>), so
    /// breadcrumbs we persist outside the app dir survive a <c>scoop update</c> — which installs into a new
    /// version directory and deletes the old one. Returns the path unchanged for every other channel, and
    /// whenever the rewritten path doesn't actually exist.
    /// </summary>
    public static string StableExePath(string exePath)
    {
        if (string.IsNullOrEmpty(exePath)) return exePath;
        try
        {
            var versionDir = FindScoopAppDir(Path.GetDirectoryName(exePath));
            if (versionDir is null) return exePath;

            // Already launched through the junction — nothing to rewrite.
            if (string.Equals(Path.GetFileName(versionDir), "current", StringComparison.OrdinalIgnoreCase))
                return exePath;

            var junction = Path.Combine(Path.GetDirectoryName(versionDir)!, "current");
            var candidate = Path.Combine(junction, Path.GetRelativePath(versionDir, exePath));
            return File.Exists(candidate) ? candidate : exePath;
        }
        catch { return exePath; }
    }

    private static InstallChannelKind Detect()
    {
        try
        {
            var mgr = CreateManager();
            if (!mgr.IsInstalled) return InstallChannelKind.Unpackaged;
            if (!mgr.IsPortable) return InstallChannelKind.Setup;
            return FindScoopAppDir(AppContext.BaseDirectory) is null
                ? InstallChannelKind.Portable
                : InstallChannelKind.Scoop;
        }
        catch
        {
            return InstallChannelKind.Unpackaged;
        }
    }

    /// <summary>
    /// Walks up from <paramref name="startDir"/> looking for Scoop's per-app install directory
    /// (<c>&lt;scoop&gt;\apps\&lt;app&gt;\&lt;version&gt;</c>), recognised by the <c>install.json</c> Scoop drops
    /// there *and* the tell-tale <c>…\apps\&lt;app&gt;\</c> shape above it — both, so a stray install.json in
    /// someone's portable extraction can't be mistaken for a Scoop install. The walk is short because the
    /// app content dir is at most one level below the app dir (Velopack's <c>current\</c>).
    /// </summary>
    private static string? FindScoopAppDir(string? startDir)
    {
        if (string.IsNullOrEmpty(startDir)) return null;
        var dir = new DirectoryInfo(startDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        for (int i = 0; i < 3 && dir is not null; i++, dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "install.json")) &&
                string.Equals(dir.Parent?.Parent?.Name, "apps", StringComparison.OrdinalIgnoreCase))
                return dir.FullName;
        }
        return null;
    }
}
