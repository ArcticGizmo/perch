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
    // (VSCode, Rider). Usually the host's window is a process *ancestor* of claude.exe, so we walk the
    // parent chain and bring the closest ancestor's real window forward. When it isn't — chiefly the
    // Windows Terminal DefTerm handoff, where a COM-activated OpenConsole.exe outside the ancestry owns
    // the console — we fall back to ResolveTerminalViaConsole, which reads the session→terminal link from
    // the kernel console object. See docs/terminal-focus-correlation.md.
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
        // Build the ancestor chain closest-first (claude → cmd → WindowsTerminal → explorer …) from a
        // single process snapshot, assigning each a depth score (0 = the Claude process itself).
        var processes = SnapshotProcesses();
        var depthByPid = new Dictionary<int, int>();

        // explorer.exe is an ancestor of virtually every interactive session yet never *hosts* a
        // terminal window, so it must never be chosen as the host. The closest-depth rule below usually
        // keeps it from winning — but only while the real terminal is somewhere in the chain. It isn't
        // always: a Windows Terminal DefTerm console can be handed off to a COM-activated
        // OpenConsole.exe (parented to svchost, not the shell), whose PseudoConsoleWindow — the only
        // route to the WT window — lives outside this ancestry. With no terminal in the chain, explorer
        // becomes the closest match and we'd silently focus the desktop / Program Manager (a dead click
        // that even suppresses the "no window" toast). Excluding it makes that case fail honestly.
        var excludedPids = new HashSet<int>();

        int current = pid;
        for (int depth = 0; depth < 10 && current > 0; depth++)
        {
            if (!depthByPid.TryAdd(current, depth)) break; // a cycle in a torn snapshot
            if (processes.TryGetValue(current, out var info))
            {
                if (string.Equals(info.ExeFile, "explorer.exe", StringComparison.OrdinalIgnoreCase))
                    excludedPids.Add(current);
                current = info.ParentPid;
            }
            else current = 0;
        }

        // Visible windows only on the first pass — that's the healthy case and the narrowest net. If it
        // comes up empty the host window is itself hidden (see below), so we retry accepting those too.
        var byDepth = CollectHostWindows(depthByPid, excludedPids, includeHidden: false);
        if (byDepth.Count == 0)
        {
            // A live session whose whole host window has been hidden (WS_VISIBLE cleared — not
            // minimized, not cloaked onto another virtual desktop). It is still perfectly focusable
            // once shown, so don't give up on it; FocusWindow below un-hides whatever we pick. The
            // hidden pass runs *second* so the common path keeps the tighter visible-only net.
            byDepth = CollectHostWindows(depthByPid, excludedPids, includeHidden: true);
            if (byDepth.Count == 0)
            {
                // Process ancestry can't reach the terminal at all. The case this rescues is the Windows
                // Terminal default-terminal (DefTerm) handoff, where a COM-activated OpenConsole.exe —
                // parented to svchost, not the shell — owns the session's PseudoConsoleWindow, so nothing
                // in claude's ancestry leads to the terminal window. Ask the kernel's console object
                // instead (ResolveTerminalViaConsole); it links the session to its terminal directly,
                // whichever way the console was allocated. If that too comes up empty the session has no
                // reachable window, so return false and let the caller show its "no window" notification.
                IntPtr viaConsole = ResolveTerminalViaConsole(pid);
                if (viaConsole == IntPtr.Zero) return false;
                FocusWindow(viaConsole);
                return true;
            }
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
        Dictionary<int, int> depthByPid, HashSet<int> excludedPids, bool includeHidden)
    {
        var byDepth = new SortedDictionary<int, List<(IntPtr hWnd, string title)>>();

        EnumWindows((hWnd, _) =>
        {
            if (!includeHidden && !IsWindowVisible(hWnd)) return true;
            GetWindowThreadProcessId(hWnd, out uint windowPid);
            if (excludedPids.Contains((int)windowPid)) return true; // never host on explorer's windows
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

    // Resolves a session's terminal window through the kernel's console object rather than the process
    // tree. This is the fallback for when FocusTerminalForProcess's ancestry walk finds nothing — most
    // importantly the Windows Terminal DefTerm handoff, where a COM-activated OpenConsole.exe (parented to
    // svchost, not the shell) owns the session's 0×0 PseudoConsoleWindow, leaving it outside claude's
    // ancestry. AttachConsole binds *this* process to the target's console; GetConsoleWindow then returns
    // that session's pseudo-console window whichever way it was allocated, and its GA_ROOTOWNER is the
    // hosting terminal window — the authoritative session→terminal link the ancestry can't provide. It is
    // read live at click time from the console object; nothing is persisted and no hook is involved.
    //
    // Console attachment is process-*global* state, so this serialises on a lock, does only three Win32
    // calls while attached, and always FreeConsole()s in a finally. Perch's Windows head is a WinExe (GUI,
    // no console of its own), so FreeConsole/AttachConsole neither allocate nor destroy a visible console
    // window; the leading FreeConsole only matters for a dev run that inherited the launching terminal's
    // console (AttachConsole fails with ERROR_ACCESS_DENIED while we already hold one).
    //
    // Returns IntPtr.Zero when there is nothing to focus: the process is gone, it has no Win32 console
    // (Git Bash/mintty — already covered by the ancestry walk), or its console is a higher integrity level
    // than Perch (access denied). The caller treats zero as "no window" and notifies the user.
    private static readonly object _consoleLock = new();

    private static IntPtr ResolveTerminalViaConsole(int pid)
    {
        lock (_consoleLock)
        {
            FreeConsole();
            if (!AttachConsole((uint)pid))
                return IntPtr.Zero;
            try
            {
                IntPtr consoleWnd = GetConsoleWindow();
                if (consoleWnd == IntPtr.Zero)
                    return IntPtr.Zero;
                IntPtr owner = GetAncestor(consoleWnd, GA_ROOTOWNER);
                return owner == IntPtr.Zero ? consoleWnd : owner;
            }
            finally
            {
                FreeConsole();
            }
        }
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

    // Console attach/detach for ResolveTerminalViaConsole. AttachConsole binds this process to a target's
    // console; GetConsoleWindow then returns that console's window (the ConPTY PseudoConsoleWindow).
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(uint dwProcessId);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FreeConsole();

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

    // CharSet.Auto MUST match the PROCESSENTRY32 struct's CharSet.Auto below: it selects the Unicode
    // (…W) entry points, so szExeFile is marshalled as Unicode. Without it these default to CharSet.Ansi
    // (…A), which fills szExeFile with ANSI bytes that the Auto/Unicode struct then reads back as garbage —
    // silently breaking any comparison against the exe name (e.g. the "explorer.exe" host exclusion).
    [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
    private static extern bool Process32First(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
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
