using System.Runtime.InteropServices;

namespace Perch.App;

/// <summary>
/// Adds (and removes) the install directory to the per-user PATH so the perch plugin — and the
/// user — can invoke <c>perch</c> from any terminal. Per-user PATH needs no elevation. Run from
/// Velopack's install/update/uninstall hooks; existing shells must be restarted to see the change.
/// </summary>
internal static class PathRegistration
{
    public static void Register()
    {
        var dir = InstallDir();
        var current = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User) ?? "";
        if (Contains(current, dir))
            return;
        var updated = string.IsNullOrEmpty(current) ? dir : current.TrimEnd(';') + ";" + dir;
        Environment.SetEnvironmentVariable("PATH", updated, EnvironmentVariableTarget.User);
        Broadcast();
    }

    public static void Unregister()
    {
        var dir = InstallDir();
        var current = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User);
        if (string.IsNullOrEmpty(current))
            return;
        var kept = current
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(p => !PathEquals(p, dir));
        Environment.SetEnvironmentVariable("PATH", string.Join(';', kept), EnvironmentVariableTarget.User);
        Broadcast();
    }

    private static string InstallDir() => AppContext.BaseDirectory.TrimEnd('\\', '/');

    private static bool Contains(string pathVar, string dir) => pathVar
        .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Any(p => PathEquals(p, dir));

    private static bool PathEquals(string a, string b) =>
        string.Equals(a.TrimEnd('\\', '/'), b.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

    // Notify the shell (and new processes) the environment changed, so freshly-launched terminals see
    // the updated PATH without a logoff.
    private const int HWND_BROADCAST = 0xffff;
    private const int WM_SETTINGCHANGE = 0x1A;
    private const int SMTO_ABORTIFHUNG = 0x2;

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern IntPtr SendMessageTimeout(
        IntPtr hWnd, int Msg, IntPtr wParam, string lParam, int fuFlags, int uTimeout, out IntPtr lpdwResult);

    private static void Broadcast()
    {
        try
        {
            SendMessageTimeout((IntPtr)HWND_BROADCAST, WM_SETTINGCHANGE, IntPtr.Zero, "Environment",
                SMTO_ABORTIFHUNG, 5000, out _);
        }
        catch { }
    }
}
