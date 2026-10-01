using System.Runtime.InteropServices;
using Perch.Platform;

namespace Perch.Platform.Windows;

/// <summary>
/// Windows <see cref="IPathInstaller"/>: adds (and removes) the install directory to the per-user PATH
/// so the perch plugin — and the user — can invoke <c>perch</c> from any terminal. Per-user PATH needs
/// no elevation. Run from the installer's install/update/uninstall hooks; existing shells must be
/// restarted to see the change. (Moved from the WinForms app's PathRegistration.)
/// </summary>
public sealed class PathInstaller : IPathInstaller
{
    public void Register() => Edit(current => PathList.WithEntry(current, InstallDir(), Environment.ExpandEnvironmentVariables));

    public void Unregister() => Edit(current => PathList.WithoutEntry(current, InstallDir(), Environment.ExpandEnvironmentVariables));

    private static string InstallDir() => AppContext.BaseDirectory.TrimEnd('\\', '/');

    // Reads and writes HKCU\Environment\Path directly (review fixes CP19). Environment.Get/SetEnvironmentVariable
    // would expand every %VAR% entry on the read and write the result back as REG_SZ, permanently baking the
    // user's variables into literal paths on every install and uninstall. Here the value is read unexpanded,
    // written back with the type it had (REG_EXPAND_SZ for a new value, as Windows itself creates it), and only
    // when the edit changed it.
    private static void Edit(Func<string, string?> edit)
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey("Environment", writable: true);
        if (key is null) return;
        var current = key.GetValue(PathValue, null, Microsoft.Win32.RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
        if (edit(current ?? "") is not { } updated) return;
        var kind = current is not null && key.GetValueKind(PathValue) == Microsoft.Win32.RegistryValueKind.String
            ? Microsoft.Win32.RegistryValueKind.String
            : Microsoft.Win32.RegistryValueKind.ExpandString;
        key.SetValue(PathValue, updated, kind);
        Broadcast();
    }

    private const string PathValue = "Path";

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
