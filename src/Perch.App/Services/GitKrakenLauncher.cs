using System.Diagnostics;
using Avalonia.Threading;
using Perch.Platform;

namespace Perch.Avalonia.Services;

/// <summary>
/// Opens a repository in GitKraken from the overlay's right-click menu. GitKraken ships a CLI that the user
/// adds to PATH via Preferences -> CLI; on Windows it's a <c>gitkraken.cmd</c> shim that re-launches the
/// Electron app in Node mode as a short-lived CLI, hands the repo to the running GUI, and exits. That shape
/// forces two things this launcher handles: the shim must run <em>without</em> flashing a console window,
/// and because the CLI process is windowless it does not by itself bring the GUI forward — so once the CLI
/// has finished handing off we focus the running GitKraken window through the platform activator.
/// </summary>
internal sealed class GitKrakenLauncher
{
    /// <summary>The GitKraken CLI resolved off PATH once and cached, or <c>null</c> when it isn't installed
    /// / not on PATH. Gates whether the "Open in GitKraken" menu item appears at all.</summary>
    public static readonly Lazy<string?> CliPath = new(Resolve);

    private readonly IWindowActivator _activator;

    public GitKrakenLauncher(IWindowActivator activator) => _activator = activator;

    /// <summary>Opens <paramref name="cwd"/> in GitKraken and then focuses its window. Best-effort and
    /// entirely off the UI thread (the CLI hand-off can take a moment, and a cold GitKraken needs time to
    /// raise a window); the focus itself marshals back to the UI thread.</summary>
    public void Open(string cwd)
    {
        var cli = CliPath.Value;
        if (cli is null || string.IsNullOrEmpty(cwd)) return;

        Task.Run(() =>
        {
            try
            {
                RunCli(cli, cwd);
                FocusGitKraken();
            }
            catch { /* best-effort - GitKraken may have left PATH, or the launch failed */ }
        });
    }

    // Runs `gitkraken -p <cwd>` with no visible window, then waits (bounded) for the CLI to finish handing the repo
    // to the GUI. The CLI is a gitkraken.cmd shim, which only runs through cmd; the old `cmd /c` left the repo path
    // unquoted, so a benign `C:\work\R&D` broke the launch (and a crafted name could run a command). CmdShim quotes
    // every argument (review fixes CP12).
    private static void RunCli(string cli, string cwd)
    {
        var psi = Perch.Data.CmdShim.StartInfo(cli, ["-p", cwd]);
        if (psi is null) return;   // the path holds a character cmd would still act on (such as %): don't launch

        using var p = Process.Start(psi);
        p?.WaitForExit(15_000);
    }

    // The CLI process is short-lived and windowless; the GUI is a separate, long-lived "gitkraken" process
    // (Electron spawns several - GPU/renderer/utility children share the name but own no top-level window,
    // so MainWindowHandle filters to the real browser window). A cold GitKraken may still be raising its
    // window when the CLI returns, so poll briefly before giving up.
    private void FocusGitKraken()
    {
        for (int attempt = 0; attempt < 8; attempt++)
        {
            if (TryFindGitKrakenWindow() is { } pid)
            {
                Dispatcher.UIThread.Post(() => _activator.FocusProcessMainWindow(pid));
                return;
            }
            Thread.Sleep(400);
        }
    }

    private static int? TryFindGitKrakenWindow()
    {
        foreach (var p in Process.GetProcessesByName("gitkraken"))
        {
            try
            {
                if (p.MainWindowHandle != IntPtr.Zero) return p.Id;
            }
            catch { /* process exited between enumeration and query */ }
            finally { p.Dispose(); }
        }
        return null;
    }

    // Same CLI name across platforms; on Windows it's typically the .cmd shim (PATHEXT also accepts an .exe).
    private static string? Resolve() => Perch.Data.ExecutableResolver.Find("gitkraken");
}
