using Perch.Platform;
using Win = Perch.Platform.Windows;

namespace Perch.App;

/// <summary>
/// The WinForms app's composition root for platform services: constructs the Windows implementations
/// of Perch.Core's platform interfaces and exposes them to call sites that can't take constructor
/// injection (static helpers, forms created ad-hoc). The Avalonia app will do the same wiring in its
/// own root — the point of the seam is that no UI code references the concrete Win32 types directly,
/// only these interfaces.
/// </summary>
internal static class PlatformServices
{
    public static IWindowActivator WindowActivator { get; } = new Win.WindowActivator();
    public static IPathInstaller PathInstaller { get; } = new Win.PathInstaller();
    public static IAudioCue AudioCue { get; } = new Win.AudioCue();

    /// <summary>Creates a fresh session-lock monitor. The caller owns it (it's disposable) — the tray
    /// context holds one for the app's lifetime.</summary>
    public static ISessionLock CreateSessionLock() => new Win.SessionLock();
}
