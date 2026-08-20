namespace Perch.Platform;

/// <summary>Which screen edge an <see cref="IEdgeReservation"/> reserves space along.</summary>
public enum ReservedEdge { Left, Top, Right, Bottom }

/// <summary>
/// Reserves a strip of screen space along a monitor edge so <em>maximized</em> windows stop at its
/// boundary instead of covering it — the mechanism behind Perch's "Docked" overlay mode. On Windows this
/// is an application desktop toolbar (<c>SHAppBarMessage</c>), the same shell facility the taskbar uses:
/// committing a reservation shrinks the desktop work area, and the OS then keeps maximized windows clear
/// of the strip. macOS/Linux heads have no direct equivalent yet, so they get a no-op implementation and
/// Docked mode simply floats without reserving there.
///
/// <para>Every method is best-effort and never throws; a zero handle is ignored. A single instance tracks
/// one reservation for one window — call <see cref="Reserve"/> again to move/resize it (edge, thickness or
/// monitor), and <see cref="Release"/> to give the space back. Fullscreen apps are unaffected by the
/// reservation (appbars never force themselves over a fullscreen window), which is the desired behaviour.</para>
/// </summary>
public interface IEdgeReservation
{
    /// <summary>Reserves (or updates) a strip <paramref name="thicknessPx"/> physical pixels deep along
    /// <paramref name="edge"/> of the monitor whose physical bounds are given. Ties the reservation to
    /// <paramref name="handle"/> (the overlay's native window handle); registering on the first call.
    /// Best-effort; a zero handle is ignored.</summary>
    void Reserve(IntPtr handle, ReservedEdge edge, int thicknessPx,
                 int monitorX, int monitorY, int monitorW, int monitorH);

    /// <summary>Releases the reservation, restoring the desktop work area. Safe to call when nothing is
    /// reserved. (Windows also auto-releases when the window handle is destroyed, so a crash self-heals.)</summary>
    void Release();
}
