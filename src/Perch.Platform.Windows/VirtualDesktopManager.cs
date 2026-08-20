using System.Runtime.InteropServices;
using System.Text;
using Perch.Platform;

namespace Perch.Platform.Windows;

/// <summary>
/// Windows <see cref="IVirtualDesktopManager"/>, over the shell's public COM
/// <c>IVirtualDesktopManager</c> (CLSID <c>aa509086-…</c>). Moving a window between desktops is the shell's
/// own <c>MoveWindowToDesktop</c>, which the shell only honours for a window owned by the <em>calling</em>
/// process — which is exactly our case (our own Avalonia windows), so no undocumented internal interface is
/// needed. Best-effort throughout: any HRESULT failure or missing shell support degrades to "did nothing".
/// <para>
/// The one hard part is naming the <em>destination</em>: the public API can move a window to a desktop id,
/// but exposes no "id of the desktop currently on screen". We derive it from a window we can prove is on
/// the current desktop (<see cref="FindCurrentDesktopId"/>): the foreground window when it's a normal
/// per-desktop window, else the first enumerated top-level window the shell reports as on the current
/// desktop. This sidesteps the version-unstable <c>IVirtualDesktopManagerInternal</c> entirely.
/// </para>
/// Set <c>PERCH_VDM_DEBUG=1</c> to append a line per call to <c>%TEMP%\perch-vdm.log</c>.
/// </summary>
public sealed class VirtualDesktopManager : IVirtualDesktopManager
{
    public bool MoveWindowToCurrentDesktop(nint windowHandle)
    {
        if (windowHandle == 0 || !OperatingSystem.IsWindows()) return Log("no-handle-or-not-windows", false);

        IVirtualDesktopManagerCom? vdm = null;
        try
        {
            var type = Type.GetTypeFromCLSID(CLSID_VirtualDesktopManager);
            if (type is null) return Log("no-clsid-type", false);
            vdm = Activator.CreateInstance(type) as IVirtualDesktopManagerCom;
            if (vdm is null) return Log("cocreate-or-qi-failed", false);

            // Already on the current desktop → nothing to do (and don't drag any other window around).
            int hrIsOn = vdm.IsWindowOnCurrentVirtualDesktop((IntPtr)windowHandle, out int onCurrent);
            if (hrIsOn == 0 && onCurrent != 0) return Log("already-on-current", false);

            Guid destination = FindCurrentDesktopId(vdm);
            if (destination == Guid.Empty) return Log($"no-current-desktop-id (isOnHr=0x{hrIsOn:X8})", false);

            int hrMove = vdm.MoveWindowToDesktop((IntPtr)windowHandle, ref destination);
            return Log($"move->{destination:B} hr=0x{hrMove:X8}", hrMove == 0);
        }
        catch (Exception ex)
        {
            return Log("exception: " + ex.Message, false);
        }
        finally
        {
            if (vdm is not null) Marshal.FinalReleaseComObject(vdm);
        }
    }

    // The id of the desktop currently on screen. First choice is the foreground window: whatever the user is
    // looking at is on the current desktop by definition. But that can be a transient menu popup, or a window
    // pinned to all desktops — both of which report GUID_NULL — so fall back to the first enumerated top-level
    // window the shell says is on the current desktop and carries a real (non-null) per-desktop id.
    private static Guid FindCurrentDesktopId(IVirtualDesktopManagerCom vdm)
    {
        IntPtr foreground = GetForegroundWindow();
        if (foreground != IntPtr.Zero &&
            vdm.GetWindowDesktopId(foreground, out Guid fg) == 0 && fg != Guid.Empty)
            return fg;

        Guid found = Guid.Empty;
        EnumWindows((hWnd, _) =>
        {
            if (!IsWindowVisible(hWnd)) return true;
            if (vdm.IsWindowOnCurrentVirtualDesktop(hWnd, out int onCurrent) != 0 || onCurrent == 0) return true;
            if (vdm.GetWindowDesktopId(hWnd, out Guid id) != 0 || id == Guid.Empty) return true;
            found = id;
            return false; // stop at the first match
        }, IntPtr.Zero);
        return found;
    }

    // ── Diagnostics ────────────────────────────────────────────────────────────
    // Opt-in, so it's inert in normal runs. Returns `result` so call sites read as one expression.
    private static bool Log(string message, bool result)
    {
        if (Environment.GetEnvironmentVariable("PERCH_VDM_DEBUG") is not "1") return result;
        try
        {
            var line = $"{DateTime.Now:HH:mm:ss.fff} [{(result ? "moved" : "noop ")}] {message}{Environment.NewLine}";
            File.AppendAllText(Path.Combine(Path.GetTempPath(), "perch-vdm.log"), line, Encoding.UTF8);
        }
        catch { /* diagnostics must never affect behaviour */ }
        return result;
    }

    // ── Interop ──────────────────────────────────────────────────────────────
    // The shell's Virtual Desktop Manager coclass. This is the *public* interface (documented on MSDN) —
    // unlike the internal IVirtualDesktopManagerInternal that shifts IID every Windows build, this one is
    // stable, which is why the whole feature is built on it alone.
    private static readonly Guid CLSID_VirtualDesktopManager = new("aa509086-5ca9-4c25-8f95-589d3c07b48a");

    [ComImport]
    [Guid("a5cd92ff-29be-454c-8d04-d82879fb3f1b")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IVirtualDesktopManagerCom
    {
        [PreserveSig] int IsWindowOnCurrentVirtualDesktop(IntPtr topLevelWindow, out int onCurrentDesktop);
        [PreserveSig] int GetWindowDesktopId(IntPtr topLevelWindow, out Guid desktopId);
        [PreserveSig] int MoveWindowToDesktop(IntPtr topLevelWindow, ref Guid desktopId);
    }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
}
