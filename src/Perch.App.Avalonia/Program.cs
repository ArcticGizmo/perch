using Avalonia;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Perch.Avalonia.Rendering;
using Velopack;

namespace Perch.Avalonia;

internal static class Program
{
    /// <summary>True when launched by the plugin's SessionStart hook (--autostarted) rather than by
    /// the user, mirroring the WinForms app. Drives the auto-close-after-last-session behaviour.</summary>
    public static bool AutoStarted { get; private set; }

    /// <summary>Set by Velopack's first-run hook (the first launch after an install). The app reads this
    /// to auto-install the Claude Code plugin once, without the user having to think about it.</summary>
    public static bool IsFirstRun { get; private set; }

    // Per-user-session name: only one tray runs per desktop login. (The port's side-by-side
    // "_Avalonia" mutex is gone now that Avalonia is the one and only Perch.)
    private const string SingleInstanceMutexName = @"Local\Perch_SingleInstance";
    private static Mutex? _instanceMutex;

    // STA for shell/clipboard/COM interop parity with the WinForms app.
    [STAThread]
    public static int Main(string[] args)
    {
        // `perch render <outDir>` dumps views to PNG (headless) for visual verification.
        if (args.Length > 0 && args[0] == "render")
            return HeadlessRenderer.RenderAll(args.Length > 1 ? args[1] : ".");

        // A stale older plugin might still invoke `perch handle <event>` — short-circuit to a no-op
        // so it never launches a second tray. (Matches the WinForms entry point.)
        if (args.Length > 0 && string.Equals(args[0], "handle", StringComparison.OrdinalIgnoreCase))
            return 0;

        AutoStarted = args.Any(a => string.Equals(a, "--autostarted", StringComparison.OrdinalIgnoreCase));

        // Velopack install/update/uninstall lifecycle. The fast callbacks keep the per-user PATH entry
        // in sync so the plugin (and the user) can invoke `perch` from any terminal; the first-run hook
        // flags the launch so the running app installs the Claude Code plugin with a visible tray.
        VelopackApp
            .Build()
            .OnAfterInstallFastCallback(_ => PlatformServices.PathInstaller.Register())
            .OnAfterUpdateFastCallback(_ => PlatformServices.PathInstaller.Register())
            .OnBeforeUninstallFastCallback(_ => PlatformServices.PathInstaller.Unregister())
            .OnFirstRun(_ => IsFirstRun = true)
            .Run();

        _instanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out bool createdNew);
        if (!createdNew)
            return 0; // another Avalonia tray instance already owns the mutex

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

        GC.KeepAlive(_instanceMutex);
        return 0;
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();
}
