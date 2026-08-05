using System.Diagnostics;

namespace Perch.Data.Hypertree;

/// <summary>What a jump did, mapped from <c>htree</c>'s documented exit codes.</summary>
internal enum HypertreeJump
{
    Ok = 0,
    /// <summary>No Hypertree tray is running in this session.</summary>
    NoTray = 1,
    /// <summary>The branch no longer exists — it was removed between our read and the click.</summary>
    UnknownTarget = 2,
    /// <summary>We built a command line <c>htree</c> didn't accept. A bug on our side.</summary>
    BadUsage = 3,
    /// <summary>The tray was reached but couldn't carry out the jump.</summary>
    Failed = 4,
    /// <summary>We couldn't run <c>htree</c> at all (missing, or the launch threw).</summary>
    Unavailable = 5,
}

/// <summary>Where <c>htree</c> is and what it reports itself to be — the Settings status line.</summary>
/// <param name="Path">Absolute path to <c>htree.exe</c>, or null when it can't be found.</param>
/// <param name="Version">Hypertree's version, or null when it couldn't be determined.</param>
/// <param name="Running">Whether a Hypertree tray is live right now.</param>
internal sealed record HypertreeInstall(string? Path, string? Version, bool Running)
{
    public bool Installed => Path is not null;

    public static readonly HypertreeInstall Missing = new(null, null, false);
}

/// <summary>
/// The one thing Perch asks Hypertree to <em>do</em>: jump to a branch. Everything Perch reads comes from
/// the status file (see <see cref="HypertreeStatusReader"/>); only this mutating action needs the CLI.
/// </summary>
/// <remarks>
/// <para>The jump can't be performed in-process. Virtual-desktop control is per-session COM plus the
/// Hypertree tray's own in-memory bookkeeping, guarded by a single-instance mutex — so the request has to
/// be handed to the running tray, which is exactly what <c>htree goto</c> does over Hypertree's control
/// pipe. Perch shells the CLI rather than speaking that pipe itself so the protocol stays Hypertree's to
/// change.</para>
///
/// <para>Branches are addressed by their stable id, never by list position: the user could reorder the
/// stack between the status read that painted the strip and the click on it, and a positional jump would
/// then land somewhere else. Main has no id and is addressed by the literal <c>main</c>.</para>
///
/// <para><b>Blocking.</b> <see cref="GoTo"/> waits on another process, which in turn waits on the tray
/// (up to ~2s to connect, ~5s for a reply). Call it off the UI thread.</para>
/// </remarks>
internal static class HypertreeBridge
{
    // The tray publishes htree's absolute path, so this is only the fallback for locating it while no
    // tray is running (the Settings "is it installed" line). Velopack packId "Hypertree" → this layout.
    private static string InstalledCliPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Hypertree", "current", "htree.exe");

    /// <summary>
    /// Locate Hypertree and describe it. Prefers the path the running tray published — no guessing at an
    /// install layout — and falls back to the known install location when nothing is running.
    /// </summary>
    /// <remarks>Spawns <c>htree --version</c> only when no tray is running (there's no published version
    /// to read in that case). Blocking; call off the UI thread.</remarks>
    public static HypertreeInstall Describe()
    {
        var status = HypertreeStatusReader.Read();
        if (status is not null)
        {
            var live = status.Cli is not null && File.Exists(status.Cli) ? status.Cli : FallbackPath();
            return new HypertreeInstall(live, NullIfBlank(status.Version), Running: true);
        }

        var path = FallbackPath();
        if (path is null) return HypertreeInstall.Missing;
        return new HypertreeInstall(path, QueryVersion(path), Running: false);
    }

    private static string? FallbackPath() => File.Exists(InstalledCliPath) ? InstalledCliPath : null;

    /// <summary>
    /// Jump to a row — by default its resume desktop, which is what a bare "go to this branch" means to
    /// Hypertree, or to a specific desktop on it.
    /// </summary>
    /// <param name="target">The row's <see cref="HypertreeRow.Target"/>: a branch id, or <c>main</c>.</param>
    /// <param name="cliPath">The path the status file published, if we have it.</param>
    /// <param name="desktopIndex">A 0-based index into the row's desktops, or <c>-1</c> for its resume
    /// point.</param>
    /// <remarks>See <see cref="Address"/> for how a desktop is spelled on the command line.</remarks>
    public static HypertreeJump GoTo(string target, string? cliPath, int desktopIndex = -1)
    {
        var exe = cliPath is not null && File.Exists(cliPath) ? cliPath : FallbackPath();
        if (exe is null) return HypertreeJump.Unavailable;

        var addressed = Address(target, desktopIndex);

        try
        {
            using var p = Process.Start(new ProcessStartInfo(exe)
            {
                // Passed as a separate argument rather than a joined string so a branch name can never be
                // re-split by the shell — though in practice we always send a GUID or "main".
                ArgumentList = { "goto", addressed },
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (p is null) return HypertreeJump.Unavailable;

            // Drain both pipes while we wait. We don't use the text — the exit code is the contract — but
            // a redirected stream nobody reads will block the child once its buffer fills, which would
            // turn a failure message into a hang. Start the reads before waiting, never after.
            _ = p.StandardOutput.ReadToEndAsync();
            _ = p.StandardError.ReadToEndAsync();

            // Bounded so a wedged tray can't hold the worker thread forever. Comfortably past htree's own
            // 2s connect + 5s reply timeouts, so we only trip when the CLI itself has failed to return.
            if (!p.WaitForExit(10_000))
            {
                try { p.Kill(entireProcessTree: true); } catch { /* already gone */ }
                return HypertreeJump.Failed;
            }

            return Enum.IsDefined(typeof(HypertreeJump), p.ExitCode)
                ? (HypertreeJump)p.ExitCode
                : HypertreeJump.Failed;
        }
        catch
        {
            return HypertreeJump.Unavailable;
        }
    }

    /// <summary>
    /// Spells a jump target for <c>htree goto</c>: the row on its own, or <c>&lt;row&gt;/&lt;n&gt;</c> for a
    /// specific desktop on it.
    /// </summary>
    /// <remarks>
    /// The desktop is given by 1-based position rather than by label. Positions come straight from the
    /// array the strip was painted from, whereas labels are Hypertree's to duplicate — and main can't be
    /// addressed by id at all (it has none), so a name-based form would have no fallback there.
    /// <para><c>htree</c> resolves the segment as a label first and only then as a position, so a desktop
    /// literally named "2" would win over the second slot. That's the one case this gets wrong, and it
    /// still lands on a desktop of the right row.</para>
    /// </remarks>
    internal static string Address(string target, int desktopIndex)
        => desktopIndex >= 0 ? $"{target}/{desktopIndex + 1}" : target;

    // htree prints its version and exits 0. Best-effort: a version we can't read is cosmetic.
    private static string? QueryVersion(string exe)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(exe)
            {
                ArgumentList = { "--version" },
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (p is null) return null;

            var text = p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(3_000))
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                return null;
            }
            return p.ExitCode == 0 ? NullIfBlank(text.Trim()) : null;
        }
        catch { return null; }
    }

    private static string? NullIfBlank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
}
