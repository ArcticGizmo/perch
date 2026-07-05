using Velopack;

namespace Perch.App;

internal static class Program
{
    // Set by Velopack's first-run hook (the first launch after an install). The tray context reads
    // this to auto-install the Claude Code plugin once, without the user having to think about it.
    public static bool IsFirstRun { get; private set; }

    // True when launched with --autostarted, i.e. by the plugin's SessionStart hook rather than by
    // the user. The tray context only arms the "auto-close after last session" behaviour in this
    // case, so a manually-opened window never closes itself out from under the user.
    public static bool AutoStarted { get; private set; }

    // Per-user-session name: only one tray runs per desktop login, which is what we want.
    private const string SingleInstanceMutexName = @"Local\Perch_SingleInstance";

    // Held for the whole process lifetime so the single-instance mutex is never finalized (and thus
    // released) while the tray is running. A static field keeps it rooted for the GC.
    private static Mutex? _instanceMutex;

    // Must be STA: the WinForms clipboard (and other OLE-backed features) throw on an MTA thread,
    // and top-level statements don't emit [STAThread] on the generated entry point.
    [STAThread]
    private static void Main(string[] args)
    {
        // The plugin's hooks now drive everything from PowerShell (writing the session sidecar files
        // the tray observes), so the exe no longer has a CLI mode. A stale older plugin might still
        // invoke `perch.exe handle <event>`, though — short-circuit it to a harmless no-op so
        // it never falls through and launches a second tray.
        if (args.Length > 0 && string.Equals(args[0], "handle", StringComparison.OrdinalIgnoreCase))
        {
            Environment.Exit(0);
            return;
        }

        AutoStarted = args.Any(a => string.Equals(a, "--autostarted", StringComparison.OrdinalIgnoreCase));

        VelopackApp
            .Build()
            .OnAfterInstallFastCallback(_ => PlatformServices.PathInstaller.Register())
            .OnAfterUpdateFastCallback(_ => PlatformServices.PathInstaller.Register())
            .OnBeforeUninstallFastCallback(_ => PlatformServices.PathInstaller.Unregister())
            // First launch after an install: flag it so the tray auto-installs the Claude Code
            // plugin. Done here (not in a fast callback) because installing it is a slow network
            // op that needs the running app — and a tray icon — to show progress.
            .OnFirstRun(_ => IsFirstRun = true)
            .Run();

        // Single-instance guard: launching perch again (Start Menu, PATH, etc.) while a tray
        // is already running just exits, instead of stacking confusing duplicate overlays.
        _instanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out bool createdNew);
        if (!createdNew)
            return; // another tray instance already owns the mutex

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.Run(new OverlayApplicationContext());

        GC.KeepAlive(_instanceMutex);
    }
}
