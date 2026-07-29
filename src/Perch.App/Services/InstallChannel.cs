using Perch.Data;
using Velopack;
using Velopack.Sources;

namespace Perch.Avalonia.Services;

/// <summary>How this copy of Perch got onto the machine — which decides who owns updates.</summary>
internal enum InstallChannelKind
{
    /// <summary>A normal <c>Perch-win-Setup.exe</c> (Velopack) install, however it was fetched — by hand or
    /// through <c>install.ps1</c>. Perch updates itself.</summary>
    Setup,
    /// <summary>The portable zip, extracted by hand. The user owns updates.</summary>
    Portable,
    /// <summary>Not an installed build at all — a dev <c>dotnet run</c>. Nothing to update.</summary>
    Unpackaged,
}

/// <summary>
/// Works out which distribution channel this process came from, so the updater never rewrites a directory
/// it doesn't own.
///
/// Every supported install route now ends at the same place — the Velopack installer, which self-updates.
/// <c>install.ps1</c> is only a verified download-and-run wrapper around it (see
/// <c>docs/distribution-plan.md</c>), so it produces an ordinary <see cref="InstallChannelKind.Setup"/>
/// install with the normal in-app update path. That leaves the portable zip as the one shape Perch can see
/// but not update.
///
/// Checking for updates works on *every* installed shape (Velopack's portable layout resolves a version and
/// an update feed just fine), so a portable copy is still told a new version exists — only the apply step is
/// withheld. See <see cref="UpdateService"/>.
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

    /// <summary>What to do about an update that's already been detected. Reads as the second half of
    /// "Version 1.2.3 is available. …".</summary>
    public static string Instruction => Kind switch
    {
        InstallChannelKind.Portable   => "This is a portable copy of Perch — download the new release and replace it.",
        InstallChannelKind.Unpackaged => "This build isn't installed, so it can't update itself.",
        _                             => "Click Update now to apply it and restart.",
    };

    /// <summary>Who installs updates on this channel, for the idle (nothing-pending) state.</summary>
    public static string OwnershipNote => Kind switch
    {
        InstallChannelKind.Portable   => "Perch watches for new versions in the background; this portable copy is updated by replacing it with the latest release.",
        InstallChannelKind.Unpackaged => "This build isn't installed, so update checks are disabled.",
        _                             => "Perch checks for updates in the background and applies new versions on the next launch.",
    };

    /// <summary>
    /// A fresh <see cref="UpdateManager"/> over the GitHub release feed. Constructing one is cheap and does
    /// no I/O — the network only happens on the check/download calls — so every caller here builds its own
    /// rather than sharing mutable state.
    /// </summary>
    public static UpdateManager CreateManager() => new(new GithubSource(AppInfo.RepoUrl, null, false));

    private static InstallChannelKind Detect()
    {
        try
        {
            var mgr = CreateManager();
            if (!mgr.IsInstalled) return InstallChannelKind.Unpackaged;
            return mgr.IsPortable ? InstallChannelKind.Portable : InstallChannelKind.Setup;
        }
        catch
        {
            return InstallChannelKind.Unpackaged;
        }
    }
}
