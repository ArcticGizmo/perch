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
    // own entries) if Perch is removed without running the uninstaller. Refreshed on every launch.
    private static string MarkerPath => Path.Combine(BinDir, "perch.path");

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
                ClaudeUserSettings.ReconcileHooks(HookBinaryPath, AppInfo.Version, AppProfile.IsDev);
        }
        catch
        {
            // Best-effort: never break startup over hook wiring.
        }
    }

    /// <summary>
    /// Removes the managed hook block from <c>~/.claude/settings.json</c> and deletes the stable bin
    /// dir. Called from the Velopack uninstall callback (see <c>Program</c>).
    /// </summary>
    public static void Uninstall()
    {
        try { ClaudeUserSettings.RemoveManagedHooks(AppProfile.IsDev, HookBinaryPath); } catch { }
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
