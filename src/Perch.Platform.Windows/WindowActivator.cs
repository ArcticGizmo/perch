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
    //
    // Returns false when no host window could be resolved at all, so the caller can say so instead of
    // leaving a click that appears to do nothing.
    public bool FocusTerminalForProcess(int pid, string? projectHint = null)
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

        // Visible windows only on the first pass — that's the healthy case and the narrowest net. If it
        // comes up empty the host window is itself hidden (see below), so we retry accepting those too.
        var byDepth = CollectHostWindows(depthByPid, includeHidden: false);
        if (byDepth.Count == 0)
        {
            // A live session whose whole host window has been hidden (WS_VISIBLE cleared — not
            // minimized, not cloaked onto another virtual desktop). It is still perfectly focusable
            // once shown, so don't give up on it; FocusWindow below un-hides whatever we pick. The
            // hidden pass runs *second* so the common path keeps the tighter visible-only net.
            byDepth = CollectHostWindows(depthByPid, includeHidden: true);
            if (byDepth.Count == 0) return false;
        }

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
        return true;
    }

    // For every window owned by an ancestor, resolve it to its root owner and keep that, grouped by the
    // *owning ancestor's* depth. There is deliberately NO size/title/cloak gate: any such heuristic can
    // wrongly drop a real terminal window (an untitled one, a minimized one reporting a tiny rect, one
    // cloaked onto another virtual desktop), and focusing the terminal correctly every time is the
    // priority. Junk windows (explorer's title-less thumbnail/DWM helpers) don't interfere because they
    // live at explorer's depth — deeper than the terminal/IDE — and the closest-depth rule in the caller
    // reaches the real host first. The 0×0 ConPTY pseudo-console isn't junk: GA_ROOTOWNER maps it to the
    // exact terminal window.
    //
    // Note the visibility test is on the *enumerated* window, while what we keep is its root owner. So a
    // visible pseudo-console owning a hidden terminal window still gets through the visible-only pass —
    // that case is handled by FocusWindow un-hiding what it's given. includeHidden is for the narrower
    // case where the ancestor has no visible window of its own to enumerate.
    private static SortedDictionary<int, List<(IntPtr hWnd, string title)>> CollectHostWindows(
        Dictionary<int, int> depthByPid, bool includeHidden)
    {
        var byDepth = new SortedDictionary<int, List<(IntPtr hWnd, string title)>>();

        EnumWindows((hWnd, _) =>
        {
            if (!includeHidden && !IsWindowVisible(hWnd)) return true;
            GetWindowThreadProcessId(hWnd, out uint windowPid);
            if (!depthByPid.TryGetValue((int)windowPid, out int d)) return true;

            IntPtr owner = GetAncestor(hWnd, GA_ROOTOWNER);
            if (owner == IntPtr.Zero) owner = hWnd;

            if (!byDepth.TryGetValue(d, out var list))
                byDepth[d] = list = new List<(IntPtr, string)>();
            list.Add((owner, GetWindowTitle(owner)));
            return true;
        }, IntPtr.Zero);

        return byDepth;
    }

    // Focuses the app that owns pid, where pid may well be a windowless helper process — the case this
    // exists for is a microphone capture session, which on Teams (and any Electron/WebView2 app) belongs to
    // a media child process while the windows live in the main one.
    //
    // Candidates are therefore the pid, its ancestors, and every process running the *same executable*. That
    // last set is what actually resolves the Teams case — the capture pid and the UI pid are two instances
    // of the same ms-teams.exe — and it survives a helper being reparented, which an ancestor walk alone
    // would not. Ancestors are still walked (and scored closer) so a conventional app with a parent-owned
    // window works too; explorer.exe is an ancestor of nearly everything, hence the depth cap and the
    // closest-depth-wins rule below, which keeps a File Explorer window from ever being the answer.
    //
    // Unlike FocusTerminalForProcess this requires a *titled, visible* window: we're looking for a real app
    // window a user would recognise, not a 0×0 pseudo-console, and every titleless helper window in the
    // candidate processes would otherwise be a candidate.
    public bool FocusAppWindowForProcess(int pid, string? titleHint = null)
    {
        try
        {
            var processes = SnapshotProcesses();
            var depthByPid = new Dictionary<int, int>();

            // Depth 0..MaxDepth: the process itself, then its ancestors.
            const int MaxDepth = 5;
            int current = pid;
            for (int depth = 0; depth <= MaxDepth && current > 0; depth++)
            {
                if (!depthByPid.TryAdd(current, depth)) break; // a cycle in a torn snapshot
                current = processes.TryGetValue(current, out var info) ? info.ParentPid : 0;
            }

            // Same-executable siblings, scored at depth 1: they are the same application, and closer to the
            // truth than any real ancestor beyond the parent.
            if (processes.TryGetValue(pid, out var self) && !string.IsNullOrEmpty(self.ExeFile))
            {
                foreach (var (otherPid, info) in processes)
                {
                    if (otherPid == pid) continue;
                    if (string.Equals(info.ExeFile, self.ExeFile, StringComparison.OrdinalIgnoreCase))
                        depthByPid.TryAdd(otherPid, 1);
                }
            }

            var byDepth = CollectAppWindows(depthByPid);
            if (byDepth.Count == 0) return false;

            // Closest relative first; within it, EnumWindows order is Z-order, so the first entry is the
            // app's most recently used window — the right default for "take me back".
            var atClosest = byDepth.First().Value;
            var chosen = atClosest.FirstOrDefault(
                c => !string.IsNullOrEmpty(titleHint)
                     && c.title.Contains(titleHint!, StringComparison.OrdinalIgnoreCase));
            if (chosen.hWnd == IntPtr.Zero) chosen = atClosest[0];

            // A window on another virtual desktop stays WS_VISIBLE (it's DWM-cloaked, not hidden), so it
            // arrives here like any other; foregrounding it makes Windows switch desktop, which is exactly
            // the "jump back to the meeting from wherever I am" behaviour.
            FocusWindow(chosen.hWnd);
            return true;
        }
        catch
        {
            return false; // best-effort
        }
    }

    // Visible, titled top-level windows belonging to any candidate process, grouped by that process's depth
    // and kept in EnumWindows (Z-order) order within each group.
    private static SortedDictionary<int, List<(IntPtr hWnd, string title)>> CollectAppWindows(
        Dictionary<int, int> depthByPid)
    {
        var byDepth = new SortedDictionary<int, List<(IntPtr hWnd, string title)>>();

        EnumWindows((hWnd, _) =>
        {
            if (!IsWindowVisible(hWnd)) return true;
            GetWindowThreadProcessId(hWnd, out uint windowPid);
            if (!depthByPid.TryGetValue((int)windowPid, out int depth)) return true;

            // Top-level only: an owned dialog resolves to its owner, and we keep the owner.
            IntPtr owner = GetAncestor(hWnd, GA_ROOTOWNER);
            if (owner == IntPtr.Zero) owner = hWnd;

            var title = GetWindowTitle(owner);
            if (title.Length == 0) return true;

            if (!byDepth.TryGetValue(depth, out var list))
                byDepth[depth] = list = new List<(IntPtr, string)>();
            if (!list.Any(e => e.hWnd == owner)) list.Add((owner, title));
            return true;
        }, IntPtr.Zero);

        return byDepth;
    }

    // One Toolhelp pass giving both the parent map and each process's executable name — the two things the
    // candidate search needs, without a second snapshot or a per-process OpenProcess.
    private static Dictionary<int, (int ParentPid, string ExeFile)> SnapshotProcesses()
    {
        var map = new Dictionary<int, (int, string)>();
        var snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snapshot == IntPtr.Zero) return map;
        try
        {
            var entry = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>() };
            if (!Process32First(snapshot, ref entry)) return map;
            do
            {
                map[(int)entry.th32ProcessID] = ((int)entry.th32ParentProcessID, entry.szExeFile ?? "");
            }
            while (Process32Next(snapshot, ref entry));
            return map;
        }
        finally
        {
            CloseHandle(snapshot);
        }
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
        // A hidden window (WS_VISIBLE cleared) must be shown before it can take the foreground —
        // SetForegroundWindow on one is silently a no-op, which is exactly how a live session with a
        // hidden terminal ends up looking like a dead row you can't click. This is a *distinct* state
        // from minimized (WS_MINIMIZE) and from DWM-cloaked (a window on another virtual desktop stays
        // WS_VISIBLE), so it needs its own SW_SHOW and neither branch below covers it.
        //
        // Deliberately not checking ShowWindow's return value: it reports the window's *previous*
        // visibility, not success, so un-hiding a hidden window correctly returns false.
        if (!IsWindowVisible(hWnd))
            ShowWindow(hWnd, SW_SHOW);

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
    private const int SW_SHOW = 5;
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
