using System.Runtime.InteropServices;
using System.Text;

namespace Perch.Ui;

internal static class NativeMethods
{
    // ── Tray icon ────────────────────────────────────────────────────────────

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DestroyIcon(IntPtr hIcon);

    // ── Dark title bar ─────────────────────────────────────────────────────────
    // Opts a window's non-client area (title bar, border) into the system dark theme so a
    // WinForms form matches the app's dark content. Best-effort: silently no-ops on builds
    // that don't support the attribute.

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    internal static void UseDarkTitleBar(IntPtr hwnd)
    {
        int enabled = 1;
        // 20 = DWMWA_USE_IMMERSIVE_DARK_MODE on Win10 20H1+/Win11; 19 on earlier 20xx builds.
        if (DwmSetWindowAttribute(hwnd, 20, ref enabled, sizeof(int)) != 0)
            DwmSetWindowAttribute(hwnd, 19, ref enabled, sizeof(int));
    }

    // ── Dark scrollbars ────────────────────────────────────────────────────────
    // Opting the app into dark mode (uxtheme ordinal #135) then applying the explorer dark
    // theme to a scrolling control gives it the dark non-client scrollbar instead of the
    // default light one. Best-effort: unsupported on builds older than Win10 1809.

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    private static extern int SetWindowTheme(IntPtr hWnd, string? pszSubAppName, string? pszSubIdList);

    [DllImport("uxtheme.dll", EntryPoint = "#135", SetLastError = true)]
    private static extern int SetPreferredAppMode(int appMode);

    private static bool _darkAppModeSet;

    internal static void UseDarkScrollBars(IntPtr hWnd)
    {
        try
        {
            if (!_darkAppModeSet)
            {
                SetPreferredAppMode(1); // PreferredAppMode.AllowDark
                _darkAppModeSet = true;
            }
            SetWindowTheme(hWnd, "DarkMode_Explorer", null);
        }
        catch { }
    }

    // ── Global hot key ───────────────────────────────────────────────────────
    // System-wide hotkey registration: Windows posts WM_HOTKEY to the registering window
    // regardless of which application currently has focus.

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    // ── Borderless window drag / bulk control updates ───────────────────────────
    // ReleaseCapture + WM_NCLBUTTONDOWN(HTCAPTION) lets a borderless window be dragged from a
    // custom title bar; SendMessage(WM_SETREDRAW) brackets bulk RichTextBox appends to kill flicker.

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ReleaseCapture();

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    internal static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

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

    private const uint GA_ROOTOWNER = 3;

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

    private const int SW_RESTORE = 9;
    private const uint TH32CS_SNAPPROCESS = 0x00000002;

    // ── Per-pixel-alpha layered window (the ambient screen-edge glow) ─────────────
    // A layered window whose content is pushed as a premultiplied 32bpp bitmap via
    // UpdateLayeredWindow, giving a soft anti-aliased glow the 1-bit TransparencyKey path can't. The
    // pulse re-blits with a varying SourceConstantAlpha, so only the opacity changes — no re-render.

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UpdateLayeredWindow(
        IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize,
        IntPtr hdcSrc, ref POINT pptSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);

    [DllImport("user32.dll")]
    internal static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    internal static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll")]
    internal static extern IntPtr CreateCompatibleDC(IntPtr hDC);

    [DllImport("gdi32.dll")]
    internal static extern IntPtr SelectObject(IntPtr hDC, IntPtr hObject);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DeleteDC(IntPtr hDC);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DeleteObject(IntPtr hObject);

    [StructLayout(LayoutKind.Sequential)]
    internal struct POINT
    {
        public int X;
        public int Y;
        public POINT(int x, int y) { X = x; Y = y; }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SIZE
    {
        public int Cx;
        public int Cy;
        public SIZE(int cx, int cy) { Cx = cx; Cy = cy; }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal struct BLENDFUNCTION
    {
        public byte BlendOp;
        public byte BlendFlags;
        public byte SourceConstantAlpha;
        public byte AlphaFormat;
    }

    internal const int  ULW_ALPHA        = 0x02;
    internal const byte AC_SRC_OVER      = 0x00;
    internal const byte AC_SRC_ALPHA     = 0x01;

    // Extended styles for a click-through, non-activating, always-on-top layered overlay.
    internal const int WS_EX_LAYERED     = 0x00080000;
    internal const int WS_EX_TRANSPARENT = 0x00000020;
    internal const int WS_EX_TOOLWINDOW  = 0x00000080;
    internal const int WS_EX_NOACTIVATE  = 0x08000000;
    internal const int WS_EX_TOPMOST     = 0x00000008;

    // Brings a window to the foreground for a tray/notification click. Only SW_RESTOREs when the
    // window is actually minimized — an unconditional SW_RESTORE on a non-minimized window
    // (a maximized IDE, or an Electron window like GitKraken) triggers an unwanted un-maximize /
    // minimize-and-restore cycle instead of a simple bring-to-front. Briefly attaching our input
    // queue to the current foreground thread lifts Windows' foreground lock, which otherwise
    // silently ignores SetForegroundWindow when the caller doesn't already own the foreground.
    internal static void FocusWindow(IntPtr hWnd)
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
    internal static void FocusTerminalForProcess(int pid, string? projectHint = null)
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
}
