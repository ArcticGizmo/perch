using Perch.Platform;
using Win = Perch.Platform.Windows;

namespace Perch.Avalonia;

/// <summary>
/// The Avalonia app's composition root for platform services — the counterpart of the WinForms app's
/// PlatformServices. Constructs the Windows implementations of Perch.Core's platform interfaces so no
/// UI code references the concrete Win32 types directly. Phase 7 will choose the implementation per OS.
/// </summary>
internal static class PlatformServices
{
    public static IWindowActivator WindowActivator { get; } = new Win.WindowActivator();
    public static IPathInstaller PathInstaller { get; } = new Win.PathInstaller();
    public static IAudioCue AudioCue { get; } = new Win.AudioCue();
    public static ISystemMetrics SystemMetrics { get; } = new Win.WindowsSystemMetrics();
    public static IAppIconProvider AppIconProvider { get; } = new Win.WindowsAppIconProvider();
    public static IWindowChrome WindowChrome { get; } = new Win.WindowChrome();

    public static ISessionLock CreateSessionLock() => new Win.SessionLock();
    public static IGlobalHotkey CreateGlobalHotkey() => new Win.GlobalHotkey();
}
