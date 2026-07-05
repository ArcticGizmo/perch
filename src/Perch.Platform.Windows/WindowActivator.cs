using System.Runtime.InteropServices;
using System.Text;
using Perch.Platform;

namespace Perch.Platform.Windows;

/// <summary>
/// Windows <see cref="IWindowActivator"/>: brings a session's hosting terminal/IDE window to the
/// foreground by walking the process ancestry. Moved verbatim from the WinForms app's NativeMethods so
/// both UIs can share it — the logic is pure Win32 with no WinForms dependency.
/// </summary>
public sealed class WindowActivator : IWindowActivator
{
    // Focuses the host window of a Claude Code session. The session's claude.exe runs inside some
    // host's terminal — a standalone emulator (Windows Terminal), or an IDE's integrated terminal
    // (VSCode, Rider). In every case the host's window is a process *ancestor* of claude.exe, so we
    // walk the parent chain and bring the closest ancestor's real window forward.
    //
    // The key to *which* window is GA_ROOTOWNER. Under ConPTY (Win11 26100+) each shell owns a 0×0
    // "PseudoConsoleWindow"; that window is window-*owned* by the exact terminal window hosting this
    // session, so its root owner is the one terminal we want — the only way to tell apart several
    // Windows Terminal windows that share a single process. For a plain top-level window (an IDE) the
    // root owner is the window itself; there the title (carrying the folder name, e.g.
    // "… - perch - Visual Studio Code") disambiguates projectHint among windows sharing a pid.
    // Focus is best-effort for Windows Terminal, whose title follows the *active tab* and can't be
    // steered to a background tab via Win32.
    public void FocusTerminalForProcess(int pid, string? projectHint = null)
    {
        // Build ancestor list closest-first: claude → cmd → WindowsTerminal → explorer …
        var ancestors = new List<int>();
        int current = pid;
        for (int depth = 0; depth < 10; depth++)
        {
            if (current <= 0) break;
            ancestors.Add(current);
            current = GetParentPid(current);
        }

        // Assign a depth score to each PID (0 = the Claude process itself)
        var depthByPid = ancestors
            .Select((p, i) => (p, i))
            .ToDictionary(x => x.p, x => x.i);

        // For every visible window owned by an ancestor, resolve it to its root owner and keep that,
        // grouped by the *owning ancestor's* depth. There is deliberately NO size/title/cloak gate:
        // any such heuristic can wrongly drop a real terminal window (an untitled one, a minimized
        // one reporting a tiny rect, one cloaked onto another virtual desktop), and focusing the
        // terminal correctly every time is the priority. Junk windows (explorer's title-less
        // thumbnail/DWM helpers) don't interfere because they live at explorer's depth — deeper than
        // the terminal/IDE — and the closest-depth rule below reaches the real host first. The 0×0
        // ConPTY pseudo-console isn't junk: GA_ROOTOWNER maps it to the exact terminal window.
        var byDepth = new SortedDictionary<int, List<(IntPtr hWnd, string title)>>();

        EnumWindows((hWnd, _) =>
        {
            if (!IsWindowVisible(hWnd)) return true;
            GetWindowThreadProcessId(hWnd, out uint windowPid);
            if (!depthByPid.TryGetValue((int)windowPid, out int d)) return true;

            IntPtr owner = GetAncestor(hWnd, GA_ROOTOWNER);
            if (owner == IntPtr.Zero) owner = hWnd;

            if (!byDepth.TryGetValue(d, out var list))
                byDepth[d] = list = new List<(IntPtr, string)>();
            list.Add((owner, GetWindowTitle(owner)));
            return true;
        }, IntPtr.Zero);

        if (byDepth.Count == 0) return;

        // Prefer the *closest* ancestor — explorer is a distant ancestor of every process and owns
        // the taskbar, so it would otherwise win. SortedDictionary keeps depths ascending.
        var atClosest = byDepth.First().Value;

        // Among that host's windows, prefer the one whose title mentions the session's project; this
        // distinguishes two VSCode/Rider project windows sharing a single host pid.
        var chosen = atClosest.FirstOrDefault(
            c => !string.IsNullOrEmpty(projectHint)
                 && c.title.Contains(projectHint!, StringComparison.OrdinalIgnoreCase));
        if (chosen.hWnd == IntPtr.Zero)
            chosen = atClosest[0];

        // FocusWindow (not the old unconditional-restore path) so a maximized IDE window isn't
        // un-maximized on the way to the foreground.
        FocusWindow(chosen.hWnd);
    }

    public void FocusProcessMainWindow(int pid)
    {
        try
        {
            using var p = System.Diagnostics.Process.GetProcessById(pid);
            var hwnd = p.MainWindowHandle;
            if (hwnd != IntPtr.Zero) FocusWindow(hwnd);
        }
        catch { /* process gone or inaccessible — best-effort */ }
    }

    // Brings a window to the foreground for a tray/notification click. Only SW_RESTOREs when the
    // window is actually minimized — an unconditional SW_RESTORE on a non-minimized window
    // (a maximized IDE, or an Electron window like GitKraken) triggers an unwanted un-maximize /
    // minimize-and-restore cycle instead of a simple bring-to-front. Briefly attaching our input
    // queue to the current foreground thread lifts Windows' foreground lock, which otherwise
    // silently ignores SetForegroundWindow when the caller doesn't already own the foreground.
    private static void FocusWindow(IntPtr hWnd)
    {
        if (IsIconic(hWnd))
            ShowWindow(hWnd, SW_RESTORE);

        uint foreThread = GetWindowThreadProcessId(GetForegroundWindow(), out _);
        uint thisThread = GetCurrentThreadId();

        if (foreThread != 0 && foreThread != thisThread)
        {
            AttachThreadInput(foreThread, thisThread, true);
            SetForegroundWindow(hWnd);
            AttachThreadInput(foreThread, thisThread, false);
        }
        else
        {
            SetForegroundWindow(hWnd);
        }
    }

    private static string GetWindowTitle(IntPtr hWnd)
    {
        int len = GetWindowTextLength(hWnd);
        if (len <= 0) return "";
        var sb = new StringBuilder(len + 1);
        GetWindowText(hWnd, sb, sb.Capacity);
        return sb.ToString();
    }

    private static int GetParentPid(int pid)
    {
        var snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snapshot == IntPtr.Zero) return -1;
        try
        {
            var entry = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>() };
            if (!Process32First(snapshot, ref entry)) return -1;
            do
            {
                if ((int)entry.th32ProcessID == pid)
                    return (int)entry.th32ParentProcessID;
            }
            while (Process32Next(snapshot, ref entry));
            return -1;
        }
        finally
        {
            CloseHandle(snapshot);
        }
    }

    // ── Interop ──────────────────────────────────────────────────────────────
    private const int SW_RESTORE = 9;
    private const uint TH32CS_SNAPPROCESS = 0x00000002;
    private const uint GA_ROOTOWNER = 3;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hWnd, uint gaFlags);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

    [DllImport("kernel32.dll")]
    private static extern bool Process32First(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

    [DllImport("kernel32.dll")]
    private static extern bool Process32Next(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr hObject);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct PROCESSENTRY32
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }
}
