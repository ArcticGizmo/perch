using System.Runtime.InteropServices;
using Perch.Platform;

namespace Perch.Platform.Windows;

/// <summary>
/// Windows <see cref="IEdgeReservation"/> via the application-desktop-toolbar API (<c>SHAppBarMessage</c>) —
/// the same shell facility the taskbar uses. Registering an appbar (<c>ABM_NEW</c>) and committing a
/// position (<c>ABM_SETPOS</c>) shrinks the desktop work area, so maximized windows stop at the reserved
/// strip's edge. The proven sequence (verified by the spike in <c>spikes/reserve-edge/</c>) is
/// NEW → QUERYPOS → re-pin thickness → SETPOS; re-issuing QUERYPOS/SETPOS moves or resizes the same bar
/// without re-registering, so <see cref="Reserve"/> can be called on every collapse/expand/side change.
///
/// <para>This MVP does not yet subscribe to the <c>ABN_*</c> notifications (a stray taskbar move won't be
/// auto-followed until Perch re-asserts on the next screen change); the callback message is registered but
/// left unhandled. Every call is best-effort and never throws.</para>
/// </summary>
public sealed class EdgeReservation : IEdgeReservation
{
    private const uint ABM_NEW      = 0x0;
    private const uint ABM_REMOVE   = 0x1;
    private const uint ABM_QUERYPOS = 0x2;
    private const uint ABM_SETPOS   = 0x3;

    private const uint ABE_LEFT   = 0;
    private const uint ABE_TOP    = 1;
    private const uint ABE_RIGHT  = 2;
    private const uint ABE_BOTTOM = 3;

    // A private window message the shell posts appbar notifications on. Registered with ABM_NEW; not yet
    // handled (see the class remarks). WM_APP + 1.
    private const uint CallbackMessage = 0x8000 + 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int left, top, right, bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct APPBARDATA
    {
        public int    cbSize;
        public IntPtr hWnd;
        public uint   uCallbackMessage;
        public uint   uEdge;
        public RECT   rc;
        public IntPtr lParam;
    }

    [DllImport("shell32.dll")]
    private static extern UIntPtr SHAppBarMessage(uint dwMessage, ref APPBARDATA pData);

    // The handle the appbar is currently registered against; Zero when nothing is reserved.
    private IntPtr _registered;

    public void Reserve(IntPtr handle, ReservedEdge edge, int thicknessPx,
                        int monitorX, int monitorY, int monitorW, int monitorH)
    {
        if (handle == IntPtr.Zero || thicknessPx <= 0) return;
        try
        {
            // Registering is per-handle. If the window handle changed (shouldn't in practice), drop the old
            // registration first so we never leak a reservation against a dead handle.
            if (_registered != IntPtr.Zero && _registered != handle) Release();

            var abd = new APPBARDATA
            {
                cbSize = Marshal.SizeOf<APPBARDATA>(),
                hWnd = handle,
                uCallbackMessage = CallbackMessage,
                uEdge = ToEdge(edge),
            };

            if (_registered == IntPtr.Zero)
            {
                SHAppBarMessage(ABM_NEW, ref abd);
                _registered = handle;
            }

            // Propose the full-edge strip, let the shell nudge it around other appbars, then re-pin our
            // thickness on the edge-perpendicular axis and commit.
            abd.rc = ProposedRect(edge, thicknessPx, monitorX, monitorY, monitorW, monitorH);
            SHAppBarMessage(ABM_QUERYPOS, ref abd);
            RepinThickness(edge, thicknessPx, ref abd.rc);
            SHAppBarMessage(ABM_SETPOS, ref abd);
        }
        catch { /* best-effort: a failed reservation just means Docked mode floats without reserving */ }
    }

    public void Release()
    {
        if (_registered == IntPtr.Zero) return;
        try
        {
            var abd = new APPBARDATA { cbSize = Marshal.SizeOf<APPBARDATA>(), hWnd = _registered };
            SHAppBarMessage(ABM_REMOVE, ref abd);
        }
        catch { /* best-effort */ }
        finally { _registered = IntPtr.Zero; }
    }

    private static uint ToEdge(ReservedEdge edge) => edge switch
    {
        ReservedEdge.Left   => ABE_LEFT,
        ReservedEdge.Top    => ABE_TOP,
        ReservedEdge.Bottom => ABE_BOTTOM,
        _                   => ABE_RIGHT,
    };

    private static RECT ProposedRect(ReservedEdge edge, int t, int mx, int my, int mw, int mh)
    {
        int right = mx + mw, bottom = my + mh;
        return edge switch
        {
            ReservedEdge.Left   => new RECT { left = mx,        top = my, right = mx + t, bottom = bottom },
            ReservedEdge.Right  => new RECT { left = right - t, top = my, right = right,  bottom = bottom },
            ReservedEdge.Top    => new RECT { left = mx, top = my,         right = right, bottom = my + t },
            _                   => new RECT { left = mx, top = bottom - t, right = right, bottom = bottom },
        };
    }

    // ABM_QUERYPOS only guarantees the edge position; re-pin the perpendicular extent to our thickness.
    private static void RepinThickness(ReservedEdge edge, int t, ref RECT rc)
    {
        switch (edge)
        {
            case ReservedEdge.Left:   rc.right  = rc.left + t;   break;
            case ReservedEdge.Right:  rc.left   = rc.right - t;  break;
            case ReservedEdge.Top:    rc.bottom = rc.top + t;    break;
            default:                  rc.top    = rc.bottom - t; break;
        }
    }
}
