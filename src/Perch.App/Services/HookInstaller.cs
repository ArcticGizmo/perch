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
/// path that survives updates. The stable location lives under the app's own profile dir
/// (<c>%APPDATA%\Perch[ (Dev)]\bin</c> on Windows, <c>~/.config/Perch[ (Dev)]/bin</c> on Unix — the
/// same base as <c>AppSettings</c>, which <c>perch-hook</c>'s own profile logic also reads).
///
/// Everything here is best-effort and must never throw out of startup: a missing binary, an unreadable
/// settings file, or a locked directory all collapse to "hooks simply don't get wired this launch".
/// A missing hook command is non-blocking in Claude Code (only exit code 2 blocks, which perch-hook
/// never emits), so a stale path can never wedge a session.
/// </summary>
internal static class HookInstaller
{
    private static string HookFileName => OperatingSystem.IsWindows() ? "perch-hook.exe" : "perch-hook";

    /// <summary>The stable per-user bin dir the hooks point at (profile-aware).</summary>
    public static string BinDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        AppProfile.DataFolderName, "bin");

    /// <summary>Absolute path to the installed (stable) <c>perch-hook</c> binary.</summary>
    public static string HookBinaryPath => Path.Combine(BinDir, HookFileName);

    // A breadcrumb recording where the tray executable lives, so perch-hook can self-heal (strip its
    // own entries) if Perch is removed without running the uninstaller — and, on macOS, so it can find the
    // .app bundle to launch. Refreshed on every launch.
    private static string MarkerPath => Path.Combine(BinDir, "perch.path");

    /// <summary>
    /// Copy-if-newer the shipped binary to <see cref="HookBinaryPath"/>, record the tray location, then
    /// reconcile the managed hook block in every config dir. Safe to call on every launch.
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
            ReconcileAll();
            WatchForNewConfigDirs();
        }
        catch
        {
            // Best-effort: never break startup over hook wiring.
        }
    }

    /// <summary>
    /// Reconciles the managed hook block in <em>every</em> config dir. The hook only fires for a
    /// session if it is registered in that session's own config dir, and nothing else would repair a
    /// dir left with stale entries — the hook's own self-heal only ever strips.
    /// </summary>
    private static void ReconcileAll()
    {
        // A dev run without a published perch-hook beside it has nothing to point at; skip rather than
        // write a dangling command.
        if (!File.Exists(HookBinaryPath)) return;

        // Serialised: the config-dir watcher can fire while a reconcile is still running, and two
        // passes must not interleave writes to one settings file.
        lock (ReconcileGate)
        {
            foreach (var dir in ClaudeConfigSet.All)
            {
                try
                {
                    ClaudeUserSettings.ReconcileHooks(
                        dir.UserSettingsFile, HookBinaryPath, AppInfo.Version, AppProfile.IsDev);
                }
                catch { /* one unwritable dir must not stop the rest */ }
            }
        }
    }

    /// <summary>
    /// Re-reconciles when the config-dir set changes. A config dir can appear long after start-up — an
    /// environment signed into for the first time materialises its directory then — and a once-at-launch
    /// install would leave it hook-less until the next Perch launch. Its sessions would still be listed
    /// (Claude Code writes the session file itself, not the hook), but with no permission mode, no
    /// sub-agent/teammate alerts and no sidecar cleanup.
    ///
    /// <para>Note the irreducible race: Claude Code reads its settings when a session starts, so a
    /// session already running in a brand-new config dir when the hooks land does not pick them up. The
    /// next session in that dir does.</para>
    /// </summary>
    private static void WatchForNewConfigDirs()
    {
        if (_watching) return;
        _watching = true;
        // Changed is raised from the discovery probe, which runs on the caller's thread (the scan, on
        // the UI thread) — so the file IO goes to the pool.
        ClaudeConfigSet.Changed += () => Task.Run(ReconcileAll);
    }

    private static readonly object ReconcileGate = new();
    private static bool _watching;

    /// <summary>
    /// Removes the managed hook block from every config dir's <c>settings.json</c> and deletes the
    /// stable bin dir. Called from the Velopack uninstall callback (see <c>Program</c>). Fans out over
    /// the same set <see cref="Install"/> writes to, or uninstalling would leave the other config dirs
    /// invoking a deleted binary.
    /// </summary>
    public static void Uninstall()
    {
        foreach (var dir in ClaudeConfigSet.All)
        {
            try { ClaudeUserSettings.RemoveManagedHooks(dir.UserSettingsFile, AppProfile.IsDev, HookBinaryPath); }
            catch { }
        }
        try { if (Directory.Exists(BinDir)) Directory.Delete(BinDir, recursive: true); } catch { }
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
