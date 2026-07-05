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

    // Per-user-session name: only one tray runs per desktop login. The Windows "Local\" session
    // namespace prefix isn't valid off Windows, so use a plain name there. A dev instance gets its own
    // name (see AppProfile) so it can run alongside an installed Perch instead of no-op'ing against its
    // mutex. (The port's side-by-side "_Avalonia" mutex is gone now that Avalonia is the one and only Perch.)
    private static readonly string SingleInstanceMutexName =
        (OperatingSystem.IsWindows() ? @"Local\Perch_SingleInstance" : "Perch_SingleInstance")
        + (Perch.Data.AppProfile.IsDev ? "_Dev" : "");
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
        var velopack = VelopackApp.Build();
#if WINDOWS
        // The install/update/uninstall fast callbacks are Velopack's Windows-only installer surface; they
        // keep the per-user PATH entry in sync so `perch` resolves in any terminal. macOS packaging
        // (Phase 5) wires the equivalent PATH symlink through the .app/.pkg install instead.
        // Uninstall also tears down the self-managed hooks: strip our block from ~/.claude/settings.json
        // and delete the stable perch-hook bin (macOS relies on perch-hook's own self-heal until it has
        // an uninstaller of its own).
        velopack
            .OnAfterInstallFastCallback(_ => PlatformServices.PathInstaller.Register())
            .OnAfterUpdateFastCallback(_ => PlatformServices.PathInstaller.Register())
            .OnBeforeUninstallFastCallback(_ =>
            {
                PlatformServices.PathInstaller.Unregister();
                Services.HookInstaller.Uninstall();
            });
#endif
        velopack
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
