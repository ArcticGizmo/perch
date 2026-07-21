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

        // `perch export <sessionId> <out.perchreplay> [--no-redact]` captures a session on disk into a
        // portable replay recording. Runs ahead of the Velopack/mutex work so it never spins up a tray.
        if (args.Length > 0 && string.Equals(args[0], "export", StringComparison.OrdinalIgnoreCase))
        {
            // The Windows head is a WinExe (GUI subsystem) with no console of its own, so Console output
            // is discarded to an interactive terminal. Attach to the launching terminal's console so the
            // session list + confirmation are actually visible.
            AttachParentConsole();
            return Services.ReplayExportCli.Run(args);
        }

        // A stale older plugin might still invoke `perch handle <event>` — short-circuit to a no-op
        // so it never launches a second tray. (Matches the WinForms entry point.)
        if (args.Length > 0 && string.Equals(args[0], "handle", StringComparison.OrdinalIgnoreCase))
            return 0;

        // `perch replay <recording>` drives the real app through a recording. Prepare it here — at the
        // very top, before the Velopack/mutex work — because it must repoint CLAUDE_CONFIG_DIR at a
        // sandbox and install the virtual clock + probe before ClaudePaths is ever read.
        bool isReplay = args.Length > 0 && string.Equals(args[0], "replay", StringComparison.OrdinalIgnoreCase);
        if (isReplay)
            AttachParentConsole(); // so a "not a readable recording" error is visible before the GUI boots
        if (isReplay && !Services.Replay.ReplayBootstrap.Prepare(args.Length > 1 ? args[1] : null))
            return 1;

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

        // A replay instance gets its own mutex so it runs alongside a live tray instead of no-op'ing
        // against it — you can watch a recording play while your real sessions keep running.
        var mutexName = SingleInstanceMutexName + (isReplay ? "_Replay" : "");
        _instanceMutex = new Mutex(initiallyOwned: true, mutexName, out bool createdNew);
        if (!createdNew)
            return 0; // another Avalonia tray instance already owns the mutex

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

        GC.KeepAlive(_instanceMutex);
        return 0;
    }

    // Attaches this WinExe to the launching terminal's console (Windows only) and reopens the standard
    // streams onto it, so `perch <cli-subcommand>` output is actually visible. A GUI-subsystem process
    // isn't wired to an interactive console for stdio (only when its output is redirected to a pipe/file),
    // which is why the CLI subcommands looked silent. A no-op off Windows or when there's no parent
    // console (e.g. double-clicked), where output simply goes nowhere as before.
    private static void AttachParentConsole()
    {
#if WINDOWS
        const int ATTACH_PARENT_PROCESS = -1;
        if (!NativeConsole.AttachConsole(ATTACH_PARENT_PROCESS))
            return;
        try
        {
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
            Console.SetError(new StreamWriter(Console.OpenStandardError()) { AutoFlush = true });
        }
        catch { /* best-effort console reopen */ }
#endif
    }

#if WINDOWS
    private static class NativeConsole
    {
        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool AttachConsole(int dwProcessId);
    }
#endif

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            // Perch is a menu-bar/tray app — keep it out of the macOS Dock and app switcher. The plist's
            // LSUIElement handles the pre-launch moment, but Avalonia's macOS backend otherwise forces a
            // Regular activation policy (dock icon) at startup; ShowInDock=false makes it an accessory app,
            // which is the setting that actually sticks. A no-op on Windows/Linux.
            .With(new MacOSPlatformOptions { ShowInDock = false })
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();
}
