using System.Runtime.InteropServices;

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
}
