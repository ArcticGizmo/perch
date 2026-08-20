using Perch.Platform;

namespace Perch.Platform.Mac;

/// <summary>
/// macOS <see cref="IEdgeReservation"/> — a no-op stub. macOS has no clean public equivalent of the Windows
/// AppBar (reserving <c>NSScreen.visibleFrame</c> needs private/awkward APIs), so Docked mode on the mac
/// head simply floats the column at the edge without reserving space. Kept as a seam so the port plan can
/// fill it in later. See <c>docs/reserve-edge-plan.md</c> and <c>docs/macos-port-plan.md</c>.
/// </summary>
public sealed class EdgeReservation : IEdgeReservation
{
    public void Reserve(IntPtr handle, ReservedEdge edge, int thicknessPx,
                        int monitorX, int monitorY, int monitorW, int monitorH) { }

    public void Release() { }
}
