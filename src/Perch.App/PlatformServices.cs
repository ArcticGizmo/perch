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
    public static ISessionLauncher SessionLauncher { get; } = new Impl.SessionLauncher();
    public static IPathInstaller PathInstaller { get; } = new Impl.PathInstaller();
    public static IAudioCue AudioCue { get; } = new Impl.AudioCue();
    public static IWindowChrome WindowChrome { get; } = new Impl.WindowChrome();
    public static IImageClipboard ImageClipboard { get; } = new Impl.ImageClipboard();
#if WINDOWS
    public static IAppIconProvider AppIconProvider { get; } = new Impl.WindowsAppIconProvider();
    public static ISystemMetrics SystemMetrics { get; } = new Impl.WindowsSystemMetrics();
    // Windows/Linux store the OAuth blob in ~/.claude/.credentials.json — the portable file reader.
    public static IClaudeCredentials ClaudeCredentials { get; } = new FileClaudeCredentials();
#else
    public static IAppIconProvider AppIconProvider { get; } = new Impl.AppIconProvider();
    public static ISystemMetrics SystemMetrics { get; } = new Impl.SystemMetrics();
    // macOS keeps the OAuth blob in the login Keychain (with a file fallback for a Linux head).
    public static IClaudeCredentials ClaudeCredentials { get; } = new Impl.KeychainClaudeCredentials();
#endif

    public static ISessionLock CreateSessionLock() => new Impl.SessionLock();
    public static IGlobalHotkey CreateGlobalHotkey() => new Impl.GlobalHotkey();

    // The now-playing controller lives in the app head (not a platform project) because it needs the
    // Windows-10 WinRT projection the head already targets — same arrangement as the Action-Center toast
    // notifier. Dual-guarded like the notifier: compiled only on the Windows head, and gated at runtime so
    // a Windows binary running elsewhere falls back to the no-op.
    public static IMediaController CreateMediaController() =>
#if WINDOWS
        OperatingSystem.IsWindows() ? new Media.WindowsMediaController() : new Media.NullMediaController();
#else
        new Media.NullMediaController();
#endif
}
