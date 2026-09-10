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

    /// <summary>A claude-shaped session request on this launch's command line (<c>perch --resume &lt;id&gt;</c>,
    /// <c>perch -c</c>, <c>perch [dir]</c>) when this process became the tray — the app opens the rich session
    /// window for it once the overlay is up. When a tray was already running the request was forwarded to it
    /// over the control pipe instead and this process exited (docs/session-ui-plan.md, Phase 3).</summary>
    public static Perch.Data.Control.SessionOpenIntent? PendingSessionIntent { get; private set; }

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
        // `perch render <outDir> [themeId]` dumps views to PNG (headless) for visual verification, under
        // the given colour theme (default Midnight) — so a preset can be eyeballed / diffed per theme.
        if (args.Length > 0 && args[0] == "render")
            return HeadlessRenderer.RenderAll(args.Length > 1 ? args[1] : ".", args.Length > 2 ? args[2] : null);

        // A stale older plugin might still invoke `perch handle <event>` — short-circuit to a no-op
        // so it never launches a second tray. (Matches the WinForms entry point.)
        if (args.Length > 0 && string.Equals(args[0], "handle", StringComparison.OrdinalIgnoreCase))
            return 0;

        // `perch uninstall` performs the same teardown as Velopack's uninstall callback, for the channels
        // that have no Velopack uninstaller — a portable copy can be cleaned up this way before deleting
        // the folder. Idempotent and best-effort.
        if (args.Length > 0 && string.Equals(args[0], "uninstall", StringComparison.OrdinalIgnoreCase))
            return RunUninstallCleanup();

        // `perch replay <recording>` drives the real app through a recording. Prepare it here — at the
        // very top, before the Velopack/mutex work — because it must repoint CLAUDE_CONFIG_DIR at a
        // sandbox and install the virtual clock + probe before ClaudePaths is ever read.
        bool isReplay = args.Length > 0 && string.Equals(args[0], "replay", StringComparison.OrdinalIgnoreCase);
        if (isReplay)
            AttachParentConsole(); // so a "not a readable recording" error is visible before the GUI boots
        if (isReplay && !Services.Replay.ReplayBootstrap.Prepare(args.Length > 1 ? args[1] : null))
            return 1;

        AutoStarted = args.Any(a => string.Equals(a, "--autostarted", StringComparison.OrdinalIgnoreCase));

        // `perch` as a claude-shaped CLI: `perch --resume [id]`, `perch -c`, `perch [dir]` open a Perch
        // session window. Parsed here (unknown flags are ignored, so a plain tray launch is untouched) and
        // acted on at the single-instance gate below: forwarded to the running tray, or kept for this one.
        // A bare `perch` with no session argument becomes "start a fresh session in the cwd" too, but only
        // for a genuine interactive terminal launch — synthesised after Velopack's Run() (so IsFirstRun is
        // known). See the block below.
        var sessionIntent = isReplay ? null : Perch.Data.Control.SessionOpenIntent.FromArgs(args, Environment.CurrentDirectory);

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
                PlatformServices.LoginItem.Unregister(); // don't leave a Run key pointing at a deleted exe
                Services.HookInstaller.Uninstall();
            });
#endif
        velopack
            .OnFirstRun(_ => IsFirstRun = true)
            .Run();

        // Bare `perch` typed at a terminal is the claude-shaped "start a session here": when nothing on the
        // command line asked for a specific session, open a fresh one in the current directory — but only for
        // a genuine interactive launch. Tray-only launches never do this: the SessionStart hook's
        // `--autostarted`, a first run after install, and the non-interactive starters (the login item, the
        // Start-menu/desktop shortcut, a double-click, Velopack's update restart) all fail the terminal check
        // and so just bring up the tray. `--tray` forces tray-only even from a terminal.
        //
        // The dev profile is excluded: a dev instance is launched with `dotnet run` (a child of the console
        // `dotnet`, so it trips the terminal check every time), and popping a session on every dev launch is
        // just noise — a dev who wants a session passes an explicit arg (`-- <dir>` / `-- -c`), which still works.
        bool trayOnly = AutoStarted || IsFirstRun || Perch.Data.AppProfile.IsDev
            || args.Any(a => string.Equals(a, "--tray", StringComparison.OrdinalIgnoreCase));
        if (sessionIntent is null && !isReplay && !trayOnly && LaunchedFromTerminal())
            sessionIntent = Perch.Data.Control.SessionOpenIntent.StartFresh(Environment.CurrentDirectory);

        // A replay instance gets its own mutex so it runs alongside a live tray instead of no-op'ing
        // against it — you can watch a recording play while your real sessions keep running.
        var mutexName = SingleInstanceMutexName + (isReplay ? "_Replay" : "");
        _instanceMutex = new Mutex(initiallyOwned: true, mutexName, out bool createdNew);
        if (!createdNew)
        {
            // Another tray instance already owns the mutex: a session request is handed to it over the
            // control pipe (this process is just the CLI); anything else is the classic silent no-op.
            return sessionIntent is null ? 0 : ForwardSessionIntent(sessionIntent);
        }
        PendingSessionIntent = sessionIntent;

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

        GC.KeepAlive(_instanceMutex);
        return 0;
    }

    // Drops everything Perch registered outside its own install directory: the per-user PATH entry, the
    // OS login registration, and the managed hook block in ~/.claude/settings.json (plus the stable
    // perch-hook copy). Each step is independent and swallowed — a cleanup that can't finish must never
    // leave the caller's uninstall looking failed.
    private static int RunUninstallCleanup()
    {
        AttachParentConsole();
        try { PlatformServices.PathInstaller.Unregister(); } catch { }
        try { PlatformServices.LoginItem.Unregister(); } catch { }
        try { Services.HookInstaller.Uninstall(); } catch { }
        Console.WriteLine("Perch: removed the PATH entry, login registration and Claude Code hooks.");
        return 0;
    }

    // The CLI half of the control pipe: send the intent to the running tray as one JSON line, print its
    // one-line answer to the launching terminal, and exit with 0 on success. Bounded waits so a wedged tray
    // can't hang the shell; every failure is reported rather than swallowed, since the user is watching.
    private static int ForwardSessionIntent(Perch.Data.Control.SessionOpenIntent intent)
    {
        AttachParentConsole();
        try
        {
            using var pipe = new System.IO.Pipes.NamedPipeClientStream(
                ".", Perch.Data.Control.ControlProtocol.PipeName, System.IO.Pipes.PipeDirection.InOut,
                System.IO.Pipes.PipeOptions.Asynchronous);
            pipe.Connect(3000);
            var payload = System.Text.Encoding.UTF8.GetBytes(intent.ToJson() + "\n");
            pipe.Write(payload, 0, payload.Length);
            pipe.Flush();

            using var reader = new StreamReader(pipe, System.Text.Encoding.UTF8);
            var read = reader.ReadLineAsync();
            if (!read.Wait(10_000) || read.Result is not { } line)
            {
                Console.Error.WriteLine("Perch is running but didn't answer.");
                return 1;
            }
            var reply = Perch.Data.Control.ControlReply.Parse(line);
            Console.WriteLine(reply?.Message ?? line);
            return reply is { Ok: true } ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Perch is running but couldn't be reached: {ex.Message}");
            return 1;
        }
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

        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool FreeConsole();
    }
#endif

    // True when this launch came from an interactive terminal — the signal that a bare `perch` should open a
    // session (like `claude`), rather than just bring up the tray. False for a double-click, a Start-menu /
    // desktop shortcut, the login item, Velopack's update restart, and the SessionStart hook, none of which
    // want a session window. On Windows a GUI-subsystem process has a parent console only when started from a
    // terminal; we probe by attaching and immediately detaching, so the long-lived tray is never tied to the
    // terminal window's lifetime (a lingering attach would let closing the terminal kill the tray — and
    // ForwardSessionIntent re-attaches on its own for the reply). Off Windows, a terminal launch has a tty on
    // stdin while `open`/launchd do not.
    private static bool LaunchedFromTerminal()
    {
#if WINDOWS
        const int ATTACH_PARENT_PROCESS = -1;
        const int ERROR_ACCESS_DENIED = 5;
        if (NativeConsole.AttachConsole(ATTACH_PARENT_PROCESS))
        {
            NativeConsole.FreeConsole();
            return true;
        }
        // Already attached to a console (rare for a WinExe) still means an interactive launch.
        return System.Runtime.InteropServices.Marshal.GetLastWin32Error() == ERROR_ACCESS_DENIED;
#else
        try { return !Console.IsInputRedirected; }
        catch { return false; }
#endif
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            // Perch is a menu-bar/tray app — keep it out of the macOS Dock and app switcher. The plist's
            // LSUIElement handles the pre-launch moment, but Avalonia's macOS backend otherwise forces a
            // Regular activation policy (dock icon) at startup; ShowInDock=false makes it an accessory app,
            // which is the setting that actually sticks. A no-op on Windows/Linux.
            .With(new MacOSPlatformOptions { ShowInDock = false })
            // Force CPU (software) rendering on Windows instead of the default ANGLE/Direct3D GPU path.
            // Perch is a small, rarely-repainted tray overlay, so the GPU stack (ANGLE's av_libGLESv2 +
            // d3d11 + the d3dcompiler) costs far more resident memory than it saves in draw time here.
            // A no-op off Windows: UsePlatformDetect selects the platform backend, and this options object
            // only takes effect when the Win32 backend is the one chosen.
            .With(new Win32PlatformOptions { RenderingMode = [Win32RenderingMode.Software] })
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();
}
