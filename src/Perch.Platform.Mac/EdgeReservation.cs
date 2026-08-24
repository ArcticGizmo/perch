using Perch.Platform;

namespace Perch.Platform.Mac;

/// <summary>
/// macOS <see cref="IEdgeReservation"/> — permanently unsupported, so Docked mode isn't offered on this head
/// at all (<see cref="IsSupported"/> is false and the app hides the setting, the hotkey and the placement
/// editor's docked segment).
///
/// <para>This isn't a stub waiting to be filled in. macOS computes <c>NSScreen.visibleFrame</c> from the menu
/// bar and the Dock only; nothing a third-party app can do contributes to it, and there is no private API
/// that changes that either. The only way to approximate it is to become a window manager — take the
/// Accessibility permission and resize other apps' windows out of the way after the fact, the way uBar does —
/// which is a different feature with a different cost. The full investigation, including what it would take,
/// is in <c>docs/macos-docked-mode-investigation.md</c>.</para>
/// </summary>
public sealed class EdgeReservation : IEdgeReservation
{
    /// <summary>Always false — see the class remarks. Docked mode is withheld on macOS rather than shipped
    /// as a column that windows quietly slide underneath.</summary>
    public bool IsSupported => false;

    public void Reserve(IntPtr handle, ReservedEdge edge, int thicknessPx,
                        int monitorX, int monitorY, int monitorW, int monitorH) { }

    public void Release() { }
}
