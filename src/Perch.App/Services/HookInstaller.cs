using Perch.Data;

namespace Perch.Avalonia.Services;

/// <summary>
/// Installs and reconciles Perch's self-managed Claude Code hooks. On every launch it copies the
/// shipped <c>perch-hook</c> binary to a stable per-user location and points
/// <c>~/.claude/settings.json</c> at it (see <see cref="ClaudeUserSettings.ReconcileHooks"/>), so live
/// session state reaches the tray without a marketplace plugin.
///
/// The copy is needed because the Velopack install dir is <em>versioned</em> —
/// <see cref="AppContext.BaseDirectory"/> changes on every update — so the hooks must reference a
/// path that survives updates. The stable location is <c>%LOCALAPPDATA%\Perch[ (Dev)]\bin</c> on Windows
/// and <c>~/.config/Perch[ (Dev)]/bin</c> on Unix. It used to be the <em>roaming</em> <c>%APPDATA%</c> on
/// Windows too (review fixes CP16), where an executable follows a roaming profile between machines and looks
/// like persistence to endpoint security. <see cref="MigrateFromLegacyBin"/> moves an existing install over.
///
/// Everything here is best-effort and must never throw out of startup: a missing binary, an unreadable
/// settings file, or a locked directory all collapse to "hooks simply don't get wired this launch".
/// A missing hook command is non-blocking in Claude Code (only exit code 2 blocks, which perch-hook
/// never emits), so a stale path can never wedge a session.
/// </summary>
internal static class HookInstaller
{
    private static string HookFileName => OperatingSystem.IsWindows() ? "perch-hook.exe" : "perch-hook";

    /// <summary>The stable per-user bin dir the hooks point at (profile-aware): local, never roaming.</summary>
    public static string BinDir { get; } = Path.Combine(
        Environment.GetFolderPath(OperatingSystem.IsWindows()
            ? Environment.SpecialFolder.LocalApplicationData
            : Environment.SpecialFolder.ApplicationData),
        AppProfile.DataFolderName, "bin");

    // Where the bin dir lived before CP16 (the roaming %APPDATA% on Windows; the same as BinDir elsewhere).
    private static string LegacyBinDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppProfile.DataFolderName, "bin");

    // How long the old copy stays after the hooks are moved off it: a Claude session reads its hook commands
    // when it starts, so one already running keeps calling the old path until it's restarted.
    private static readonly TimeSpan LegacyGrace = TimeSpan.FromDays(7);
    private const string LegacyMovedStamp = "moved-from-roaming";

    /// <summary>Absolute path to the installed (stable) <c>perch-hook</c> binary.</summary>
    public static string HookBinaryPath => Path.Combine(BinDir, HookFileName);

    // A breadcrumb recording where the tray executable lives, so perch-hook can self-heal (strip its
    // own entries) if Perch is removed without running the uninstaller — and, on macOS, so it can find the
    // .app bundle to launch. Refreshed on every launch.
    private static string MarkerPath => Path.Combine(BinDir, "perch.path");

    // Serialises hook reconciliation so the startup install and a ClaudeConfigSet.Changed-driven
    // re-reconcile (WatchForNewConfigDirs) can't interleave writes to the same settings files.
    private static readonly object ReconcileGate = new();
    private static bool _watching;

    /// <summary>
    /// Source of the config dirs the user turned hooks OFF for (resolved real paths), wired from
    /// <c>AppSettings.HooksDisabledDirs</c> — the opt-OUT for directories that default on. Read live on every
    /// <see cref="ReconcileAll"/> so a settings change takes effect on the next reconcile. Null/throwing means
    /// "nothing disabled". See <see cref="HookPolicy"/>.
    /// </summary>
    public static Func<IReadOnlyList<string>>? DisabledRealRootsProvider { get; set; }

    /// <summary>
    /// Source of the config dirs the user turned hooks ON for (resolved real paths), wired from
    /// <c>AppSettings.HooksEnabledDirs</c> — the opt-IN for auto-discovered directories that default off.
    /// Read live on every <see cref="ReconcileAll"/>. Null/throwing means "no opt-ins". See <see cref="HookPolicy"/>.
    /// </summary>
    public static Func<IReadOnlyList<string>>? EnabledRealRootsProvider { get; set; }

    /// <summary>
    /// Copy-if-newer the shipped binary to <see cref="HookBinaryPath"/>, record the tray location, then
    /// reconcile the managed hook block in <c>~/.claude/settings.json</c>. Safe to call on every launch.
    /// </summary>
    public static void Install()
    {
        try
        {
            var source = Path.Combine(AppContext.BaseDirectory, HookFileName);
            if (File.Exists(source) && IsNewer(source, HookBinaryPath))
            {
                Directory.CreateDirectory(BinDir);
                File.Copy(source, HookBinaryPath, overwrite: true);
                MakeExecutable(HookBinaryPath);
            }

            WriteMarker();

            // Only wire hooks once a binary actually exists at the stable path (a dev run without a
            // published perch-hook alongside it has nothing to point at — skip rather than write a
            // dangling command).
            if (File.Exists(HookBinaryPath))
            {
                MigrateFromLegacyBin();   // before the reconcile: see there
                ReconcileAll();
            }

            // Keep the hooks in step as config dirs appear/vanish (declared, self-reported). Idempotent
            // to arm; the reconcile it triggers is serialised with the startup one.
            WatchForNewConfigDirs();
        }
        catch
        {
            // Best-effort: never break startup over hook wiring.
        }
    }

    /// <summary>
    /// Reconciles Perch's managed hooks across the whole config-dir set per <see cref="HookPolicy"/>: install
    /// into every directory hooks are on for, strip our block from every one they're off for. The primary is
    /// always hooked; declared / self-reported dirs default on (opt-out); an auto-discovered (convention) dir
    /// defaults off but can be opted in. Stripping a directory that has none of our hooks is a no-op that
    /// never touches its file, so a merely pattern-matched dir is never silently written. Best-effort per dir.
    /// </summary>
    public static void ReconcileAll()
    {
        if (!File.Exists(HookBinaryPath)) return;
        lock (ReconcileGate)
        {
            var disabled = RealRootSet(DisabledRealRootsProvider);
            var enabled = RealRootSet(EnabledRealRootsProvider);
            foreach (var dir in ClaudeConfigSet.Instance.All)
            {
                try
                {
                    if (HookPolicy.IsEnabled(dir.Provenance, dir.RealRoot, disabled, enabled))
                        ClaudeUserSettings.ReconcileHooks(dir.UserSettingsFile, HookBinaryPath, AppInfo.Version, AppProfile.IsDev);
                    else
                        ClaudeUserSettings.RemoveManagedHooks(dir.UserSettingsFile, AppProfile.IsDev, HookBinaryPath);
                }
                catch { /* one bad dir must not stop the rest */ }
            }
        }
    }

    // A provider's real-path list as a set (OS-appropriate comparer), best-effort — a missing or throwing
    // provider yields the empty set.
    private static HashSet<string> RealRootSet(Func<IReadOnlyList<string>>? provider)
    {
        var set = new HashSet<string>(ClaudeConfigDir.PathComparer);
        try
        {
            foreach (var p in provider?.Invoke() ?? [])
                if (!string.IsNullOrWhiteSpace(p))
                    set.Add(Path.TrimEndingDirectorySeparator(p));
        }
        catch { /* best-effort: treat as empty */ }
        return set;
    }

    /// <summary>Strips Perch's managed hook block from one config dir — used when a dir is being removed from
    /// the set, so its hooks don't orphan (once it's out of the set, <see cref="ReconcileAll"/> can no longer
    /// reach it). A no-op when the dir has none of our hooks. Serialised with the other reconcile writes.</summary>
    public static void RemoveFromDir(ClaudeConfigDir dir)
    {
        lock (ReconcileGate)
        {
            try { ClaudeUserSettings.RemoveManagedHooks(dir.UserSettingsFile, AppProfile.IsDev, HookBinaryPath); }
            catch { /* best-effort */ }
        }
    }

    /// <summary>
    /// Moves an install off the pre-CP16 roaming bin dir (Windows). The reconcile that follows rewrites every
    /// hook onto <see cref="HookBinaryPath"/> — a release reconcile recognises its entries by the binary's name
    /// wherever it lives. On the first launch after the move a dev instance also strips hooks that name the old
    /// copy by path, which is the only way to recognise its entries once Claude Code has dropped their
    /// <c>_perch</c> marker; it has to run before the reconcile, because a dev strip also takes entries carrying
    /// the dev marker, which the reconcile is about to write. The old folder itself stays for
    /// <see cref="LegacyGrace"/> (sessions already running still call it) and is deleted on a later launch.
    /// </summary>
    private static void MigrateFromLegacyBin()
    {
        if (!OperatingSystem.IsWindows()
            || string.Equals(LegacyBinDir, BinDir, StringComparison.OrdinalIgnoreCase)
            || !Directory.Exists(LegacyBinDir))
            return;
        try
        {
            var stamp = Path.Combine(BinDir, LegacyMovedStamp);
            if (!File.Exists(stamp))
            {
                // Only a dev instance needs this: a release reconcile already owns every Perch entry.
                var legacyHook = Path.Combine(LegacyBinDir, HookFileName);
                if (AppProfile.IsDev)
                    lock (ReconcileGate)
                        foreach (var dir in ClaudeConfigSet.Instance.All)
                            try { ClaudeUserSettings.RemoveManagedHooks(dir.UserSettingsFile, true, legacyHook); }
                            catch { /* one bad dir must not stop the rest */ }
                File.WriteAllText(stamp, DateTime.UtcNow.ToString("O"));
                return;
            }
            if (DateTime.TryParse(File.ReadAllText(stamp), null, System.Globalization.DateTimeStyles.RoundtripKind, out var movedAt)
                && DateTime.UtcNow - movedAt.ToUniversalTime() >= LegacyGrace)
                Directory.Delete(LegacyBinDir, recursive: true);
        }
        catch { /* best-effort: retried next launch */ }
    }

    /// <summary>Re-reconciles hooks whenever the config-dir set changes (a dir was declared or a session
    /// self-reported one), on the thread pool. Idempotent — arms the subscription at most once.</summary>
    public static void WatchForNewConfigDirs()
    {
        lock (ReconcileGate)
        {
            if (_watching) return;
            _watching = true;
        }
        ClaudeConfigSet.Changed += () => System.Threading.Tasks.Task.Run(ReconcileAll);
    }

    /// <summary>
    /// Removes the managed hook block from every config dir in the set and deletes the stable bin dir.
    /// Called from the Velopack uninstall callback (see <c>Program</c>). Removal is safe in any dir (it
    /// only ever strips Perch's own entries), so it fans over the whole set, not just the writable ones.
    /// </summary>
    public static void Uninstall()
    {
        lock (ReconcileGate)
        {
            foreach (var dir in ClaudeConfigSet.Instance.All)
                try { ClaudeUserSettings.RemoveManagedHooks(dir.UserSettingsFile, AppProfile.IsDev, HookBinaryPath); }
                catch { }
        }
        try { if (Directory.Exists(BinDir)) Directory.Delete(BinDir, recursive: true); } catch { }
        try { if (Directory.Exists(LegacyBinDir)) Directory.Delete(LegacyBinDir, recursive: true); } catch { }
    }

    // Copy when the destination is missing, a different size, or older than the source — so the stable
    // copy self-updates after an app update without a version lookup into the AOT binary.
    private static bool IsNewer(string source, string dest)
    {
        if (!File.Exists(dest)) return true;
        var s = new FileInfo(source);
        var d = new FileInfo(dest);
        return s.Length != d.Length || s.LastWriteTimeUtc > d.LastWriteTimeUtc;
    }

    // The breadcrumb perch-hook uses to find (or launch) the tray. A Velopack install already lives at a
    // stable path — …\Perch\current\perch.exe — that survives updates, so the running path is recorded
    // as-is. A portable copy the user later moves invalidates it, and perch-hook self-heals by stripping
    // our hooks when the recorded binary is gone.
    private static void WriteMarker()
    {
        try
        {
            Directory.CreateDirectory(BinDir);
            File.WriteAllText(MarkerPath, Environment.ProcessPath ?? "");
        }
        catch { }
    }

    // chmod +x on Unix so Claude Code can spawn the copied binary. No-op on Windows.
    private static void MakeExecutable(string path)
    {
        if (OperatingSystem.IsWindows()) return;
        try
        {
            File.SetUnixFileMode(path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
        catch { }
    }
}
