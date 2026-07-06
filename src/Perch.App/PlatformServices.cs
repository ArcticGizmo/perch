using Perch.Platform;
#if WINDOWS
using Impl = Perch.Platform.Windows;
#else
using Impl = Perch.Platform.Mac;
#endif

namespace Perch.Avalonia;

/// <summary>
/// The app's composition root for platform services: constructs the per-OS implementations of
/// Perch.Core's platform interfaces so no UI code references a concrete Win32/AppKit type directly. The
/// implementation set is chosen at compile time by the target framework — the <c>net10.0-windows</c> head
/// binds <see cref="Perch.Platform.Windows"/>, the cross-platform <c>net10.0</c> head binds
/// <see cref="Perch.Platform.Mac"/> — so a Windows-only dependency never even compiles into the mac head.
/// </summary>
internal static class PlatformServices
{
    public static IWindowActivator WindowActivator { get; } = new Impl.WindowActivator();
    public static IPathInstaller PathInstaller { get; } = new Impl.PathInstaller();
    public static IAudioCue AudioCue { get; } = new Impl.AudioCue();
    public static IWindowChrome WindowChrome { get; } = new Impl.WindowChrome();
    public static IImageClipboard ImageClipboard { get; } = new Impl.ImageClipboard();
#if WINDOWS
    public static IAppIconProvider AppIconProvider { get; } = new Impl.WindowsAppIconProvider();
    public static ISystemMetrics SystemMetrics { get; } = new Impl.WindowsSystemMetrics();
#else
    public static IAppIconProvider AppIconProvider { get; } = new Impl.AppIconProvider();
    public static ISystemMetrics SystemMetrics { get; } = new Impl.SystemMetrics();
#endif

    public static ISessionLock CreateSessionLock() => new Impl.SessionLock();
    public static IGlobalHotkey CreateGlobalHotkey() => new Impl.GlobalHotkey();
}
