using System.Runtime.InteropServices;
using Perch.Platform;

namespace Perch.Platform.Windows;

/// <summary>
/// Windows <see cref="IWindowChrome"/>: applies the overlay windows' Win32 extended styles that Avalonia
/// doesn't expose directly. A tool window (no Alt+Tab entry) that never takes activation (so showing it
/// never steals focus from the terminal the user is typing in), plus a click-through variant for the
/// ambient overlays (confetti, glow, dense drop-zone). The WinForms overlay got the tool-window bit from
/// <c>CreateParams</c>; here we set the bits on the window handle once it exists.
/// </summary>
public sealed class WindowChrome : IWindowChrome
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW  = 0x00000080;
    private const int WS_EX_NOACTIVATE  = 0x08000000;
    private const int WS_EX_TRANSPARENT = 0x00000020; // clicks fall through to the window beneath
    private const int WS_EX_LAYERED     = 0x00080000; // required alongside TRANSPARENT for click-through

    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOSIZE     = 0x0001;
    private const uint SWP_NOMOVE     = 0x0002;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const int  SW_SHOW        = 5;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] private static extern IntPtr SetFocus(IntPtr hWnd);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();

    /// <summary>Marks the window as a no-activate tool window. Best-effort; a zero handle is ignored.</summary>
    public void MakeToolWindowNoActivate(IntPtr handle)
    {
        if (handle == IntPtr.Zero) return;
        int ex = GetWindowLong(handle, GWL_EXSTYLE);
        SetWindowLong(handle, GWL_EXSTYLE, ex | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
    }

    /// <summary>Marks the window click-through (transparent to mouse), plus tool-window + no-activate —
    /// for the ambient overlays (confetti, glow) that must never intercept input or take focus.
    /// Best-effort; a zero handle is ignored.
    /// <para>
    /// Both <c>WS_EX_LAYERED</c> and <c>WS_EX_TRANSPARENT</c> are required for click-through: on a
    /// DWM-composited (DirectComposition-backed) Avalonia window — which is how a transparent, topmost
    /// window is realised — <c>WS_EX_TRANSPARENT</c> alone does <em>not</em> pass mouse input through;
    /// the window keeps eating clicks until <c>WS_EX_LAYERED</c> is also set. Adding the layered bit does
    /// not disturb the DirectComposition per-pixel content (verified: the glow still renders), and no
    /// <c>SetLayeredWindowAttributes</c> call is needed — the compositor supplies the alpha.
    /// </para></summary>
    public void MakeClickThroughNoActivate(IntPtr handle)
    {
        if (handle == IntPtr.Zero) return;
        int ex = GetWindowLong(handle, GWL_EXSTYLE);
        SetWindowLong(handle, GWL_EXSTYLE, ex | WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
    }

    /// <summary>Re-asserts topmost and lifts the window to the top of the topmost band via
    /// <c>SetWindowPos(HWND_TOPMOST, SWP_NOACTIVATE)</c>, so a hint drawn over another always-on-top
    /// window (the overlay) is visible rather than buried. Best-effort; a zero handle is ignored.</summary>
    public void BringToTopNoActivate(IntPtr handle)
    {
        if (handle == IntPtr.Zero) return;
        SetWindowPos(handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    /// <summary>Forces the window to the foreground and hands it keyboard focus. Windows blocks a process
    /// that isn't already the foreground process from calling <c>SetForegroundWindow</c> outright, so we
    /// briefly attach our input queue to the current foreground thread's — while attached, the foreground
    /// restriction doesn't apply — then set focus and detach. Used by the session switcher, which a global
    /// hotkey summons and which must take typing immediately. Best-effort; a zero handle is ignored.</summary>
    public void ForceForeground(IntPtr handle)
    {
        if (handle == IntPtr.Zero) return;

        IntPtr fg = GetForegroundWindow();
        uint fgThread = fg == IntPtr.Zero ? 0 : GetWindowThreadProcessId(fg, out _);
        uint thisThread = GetCurrentThreadId();

        bool attached = fgThread != 0 && fgThread != thisThread && AttachThreadInput(thisThread, fgThread, true);
        try
        {
            ShowWindow(handle, SW_SHOW);
            BringWindowToTop(handle);
            SetForegroundWindow(handle);
            SetFocus(handle);
        }
        finally
        {
            if (attached) AttachThreadInput(thisThread, fgThread, false);
        }
    }
}
