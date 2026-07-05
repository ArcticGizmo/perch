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

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

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
}
