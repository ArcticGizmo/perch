using System.Runtime.InteropServices;
using Perch.Platform;

namespace Perch.Platform.Mac;

/// <summary>
/// macOS <see cref="IWindowActivator"/>: brings a session's hosting terminal/IDE to the foreground. Walks
/// the process tree up from the session's pid (via <c>libproc</c>) to the nearest ancestor that is a
/// regular GUI app — the terminal emulator / IDE hosting the shell — and activates it through
/// <c>NSRunningApplication</c>. Best-effort, and coarser than the Windows implementation: it focuses the
/// hosting <em>app</em>, not a specific window/tab (that needs the Accessibility API), so
/// <paramref name="projectHint"/> is not yet used. Never throws.
///
/// NOTE (Phase 3): written against AppKit/libproc docs but not yet verified on a Mac. Raising the exact
/// window/tab (honouring projectHint) and any needed Accessibility-permission prompt are Phase-6 follow-ups.
/// </summary>
public sealed class WindowActivator : IWindowActivator
{
    private const int NSApplicationActivateAllWindows = 1 << 0;        // 1
    private const int NSApplicationActivateIgnoringOtherApps = 1 << 1; // 2
    private const nint NSApplicationActivationPolicyRegular = 0;

    private const int PROC_PIDTBSDINFO = 3;
    private const int PpidOffset = 16; // pbi_ppid is the 5th uint32 in proc_bsdinfo

    public void FocusTerminalForProcess(int pid, string? projectHint = null)
    {
        try
        {
            int current = pid;
            for (int depth = 0; depth < 12 && current > 1; depth++)
            {
                if (Activate(current, requireRegular: true)) return;
                current = ParentPid(current);
            }
        }
        catch { /* best-effort */ }
    }

    public void FocusProcessMainWindow(int pid)
    {
        try { Activate(pid, requireRegular: false); }
        catch { /* best-effort */ }
    }

    // Activate the app owning pid. When requireRegular, only a Dock-visible ("regular") app qualifies —
    // used while walking ancestors so we skip the shell/claude processes and land on the terminal app.
    private static bool Activate(int pid, bool requireRegular)
    {
        IntPtr app = ObjC.SendGet(ObjC.Class("NSRunningApplication"),
            ObjC.Sel("runningApplicationWithProcessIdentifier:"), pid);
        if (app == IntPtr.Zero) return false;

        if (requireRegular &&
            ObjC.SendNint(app, ObjC.Sel("activationPolicy")) != NSApplicationActivationPolicyRegular)
            return false;

        ObjC.SendVoid(app, ObjC.Sel("activateWithOptions:"),
            (nuint)(NSApplicationActivateIgnoringOtherApps | NSApplicationActivateAllWindows));
        return true;
    }

    private static int ParentPid(int pid)
    {
        const int bufSize = 256; // >= sizeof(proc_bsdinfo)
        IntPtr buf = Marshal.AllocHGlobal(bufSize);
        try
        {
            int r = proc_pidinfo(pid, PROC_PIDTBSDINFO, 0, buf, bufSize);
            return r >= PpidOffset + sizeof(int) ? Marshal.ReadInt32(buf, PpidOffset) : 0;
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    [DllImport("libSystem.dylib")]
    private static extern int proc_pidinfo(int pid, int flavor, ulong arg, IntPtr buffer, int buffersize);
}
