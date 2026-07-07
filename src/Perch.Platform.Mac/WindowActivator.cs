using System.Diagnostics;
using Perch.Platform;

namespace Perch.Platform.Mac;

/// <summary>
/// macOS <see cref="IWindowActivator"/>: brings a session's hosting terminal/IDE to the foreground. Walks
/// the process tree up from the session's pid to the nearest ancestor that is a regular GUI app — the
/// terminal emulator / IDE hosting the shell — and raises it.
///
/// <para><b>Window/tab precision.</b> For Terminal.app we raise the <em>exact</em> window and tab running
/// the session by matching the session's controlling <c>tty</c> (e.g. <c>/dev/ttys001</c>) against Terminal's
/// per-tab <c>tty</c> via AppleScript — so clicking a row with several Terminal windows open lands on the
/// right one (parity with the Windows head, which resolves the specific host window). For any other terminal
/// we fall back to app-level foregrounding (still correct app, last-used window) — window precision there is
/// a per-terminal follow-up (iTerm2 exposes <c>tty</c> per session too; VS Code / others don't script tabs).</para>
///
/// <para><b>Why <c>open</c>/AppleScript and not <c>NSRunningApplication.activateWithOptions:</c></b>: on
/// modern macOS (Sonoma+) the window server ignores an <c>activate</c> request from a background agent — and
/// Perch is exactly that (an <c>LSUIElement</c> menu-bar app whose overlay is non-activating, so Perch is
/// never frontmost when a row is clicked). <c>activateWithOptions:</c> returns YES and flips the target's
/// <c>isActive</c> flag, but focus does not move (verified on-device). <c>open</c> and AppleScript
/// <c>activate</c> perform a proper foreground request the system honours from a background process. So we
/// use <c>NSRunningApplication</c> only to <em>identify</em> the host app (activation policy + bundle id).</para>
///
/// <para>The pid→ppid walk reads <c>ps</c>, not libproc's <c>proc_pidinfo</c>: a terminal's chain crosses a
/// setuid-<b>root</b> <c>login</c> (Terminal → login(root) → shell → claude), and <c>proc_pidinfo</c> can't
/// read a root-owned process's info from our non-root process, which would dead-end the walk. Never throws.</para>
/// </summary>
public sealed class WindowActivator : IWindowActivator
{
    private const nint NSApplicationActivationPolicyRegular = 0;
    private const string TerminalBundleId = "com.apple.Terminal";

    public void FocusTerminalForProcess(int pid, string? projectHint = null)
    {
        try
        {
            var parents = ReadParentMap();
            int current = pid;
            for (int depth = 0; depth < 12 && current > 1; depth++)
            {
                IntPtr app = RunningApp(current);
                if (app != IntPtr.Zero &&
                    ObjC.SendNint(app, ObjC.Sel("activationPolicy")) == NSApplicationActivationPolicyRegular)
                {
                    FocusHostApp(app, pid);
                    return;
                }
                current = parents.TryGetValue(current, out var pp) ? pp : 0;
            }
        }
        catch { /* best-effort */ }
    }

    public void FocusProcessMainWindow(int pid)
    {
        try
        {
            IntPtr app = RunningApp(pid);
            if (app != IntPtr.Zero) ForegroundApp(app);
        }
        catch { /* best-effort */ }
    }

    // Raise the host app, window-precise when we know how. sessionPid is the session's own process (claude),
    // whose controlling tty identifies the exact terminal window/tab; the app is its hosting GUI ancestor.
    private static void FocusHostApp(IntPtr app, int sessionPid)
    {
        string? bundleId = ObjC.SendString(app, ObjC.Sel("bundleIdentifier"));

        // Terminal.app: raise the exact window+tab whose tty is the session's controlling tty.
        if (bundleId == TerminalBundleId && TtyOf(sessionPid) is { } tty && FocusTerminalAppTab(tty))
            return;

        ForegroundApp(app, bundleId);
    }

    // App-level foreground: `open -b <id>` (no path escaping) or `open <bundlePath>`. On modern macOS this
    // is honoured from a background process where NSRunningApplication.activate is not (see class remarks).
    private static void ForegroundApp(IntPtr app, string? bundleId = null)
    {
        bundleId ??= ObjC.SendString(app, ObjC.Sel("bundleIdentifier"));
        if (!string.IsNullOrEmpty(bundleId)) { Open("-b", bundleId!); return; }

        IntPtr url = ObjC.SendGet(app, ObjC.Sel("bundleURL"));
        if (ObjC.SendString(url, ObjC.Sel("path")) is { Length: > 0 } path) Open(path);
    }

    private static IntPtr RunningApp(int pid) => ObjC.SendGet(ObjC.Class("NSRunningApplication"),
        ObjC.Sel("runningApplicationWithProcessIdentifier:"), pid);

    // Select the Terminal.app window+tab whose tty matches and bring Terminal forward, via AppleScript
    // (the only interface exposing a tab's tty). Returns false if no tab matches or Automation permission
    // is denied — the caller then falls back to app-level foregrounding. The first run may raise a one-time
    // "Perch wants to control Terminal.app" prompt; if the user declines, we degrade to app-level.
    private static bool FocusTerminalAppTab(string tty)
    {
        string script =
            "tell application \"Terminal\"\n" +
            "  repeat with w in windows\n" +
            "    repeat with t in tabs of w\n" +
            "      if tty of t is \"" + tty + "\" then\n" +
            "        set selected of t to true\n" +
            "        set frontmost of w to true\n" +
            "        activate\n" +
            "        return \"ok\"\n" +
            "      end if\n" +
            "    end repeat\n" +
            "  end repeat\n" +
            "end tell\n" +
            "return \"notfound\"";
        return RunOsascript(script) is { } outp && outp.Trim() == "ok";
    }

    // The controlling terminal of pid as an AppleScript-comparable device path ("/dev/ttys001"), or null
    // when the process has no tty (a backgrounded/SDK session) — `ps -o tty=` prints e.g. "ttys001" or "??".
    private static string? TtyOf(int pid)
    {
        string? t = RunOutput("/bin/ps", "-o", "tty=", "-p", pid.ToString())?.Trim();
        return string.IsNullOrEmpty(t) || t == "?" || t == "??" ? null : "/dev/" + t;
    }

    private static string? RunOsascript(string script) => RunOutput("/usr/bin/osascript", "-e", script);

    // `open -b <id>` / `open <path>` foregrounds an already-running app (exit 0 on success).
    private static bool Open(params string[] args) => RunOutput("/usr/bin/open", args) is not null;

    // A pid → ppid map of every process, via `ps -Ao pid=,ppid=`. Unlike libproc this reads across
    // root-owned processes, so the walk can cross the setuid-root `login`.
    private static Dictionary<int, int> ReadParentMap()
    {
        var map = new Dictionary<int, int>();
        string? outp = RunOutput("/bin/ps", "-Ao", "pid=,ppid=");
        if (outp is null) return map;
        foreach (var line in outp.Split('\n'))
        {
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && int.TryParse(parts[0], out int pid) && int.TryParse(parts[1], out int ppid))
                map[pid] = ppid;
        }
        return map;
    }

    // Run a tool and return stdout on exit 0, else null (a timeout, non-zero exit, or launch failure).
    private static string? RunOutput(string exe, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            if (p is null) return null;
            string outp = p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(5_000)) { try { p.Kill(true); } catch { } return null; }
            return p.ExitCode == 0 ? outp : null;
        }
        catch { return null; }
    }
}
