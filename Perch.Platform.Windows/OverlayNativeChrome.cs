using System.Runtime.InteropServices;

namespace Perch.Platform.Windows;

/// <summary>
/// Applies the overlay window's Win32 extended styles that Avalonia doesn't expose directly: a tool
/// window (no Alt+Tab entry) that never takes activation (so showing it never steals focus from the
/// terminal the user is typing in). The WinForms overlay got the tool-window bit from
/// <c>CreateParams</c>; here we set both bits on the window handle once it exists.
/// </summary>
public static class OverlayNativeChrome
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    /// <summary>Marks the window as a no-activate tool window. Best-effort; a zero handle is ignored.</summary>
    public static void MakeToolWindowNoActivate(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero) return;
        int ex = GetWindowLong(hWnd, GWL_EXSTYLE);
        SetWindowLong(hWnd, GWL_EXSTYLE, ex | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
    }
}
