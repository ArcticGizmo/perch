using System.Diagnostics;
using Perch.Platform;

namespace Perch.Platform.Mac;

/// <summary>
/// macOS <see cref="IWindowActivator"/>: brings a session's hosting terminal/IDE to the foreground. Walks
/// the process tree up from the session's pid (via <c>libproc</c>) to the nearest ancestor that is a
/// regular GUI app — the terminal emulator / IDE hosting the shell — and foregrounds it.
///
/// <para><b>Why <c>open</c> and not <c>NSRunningApplication.activateWithOptions:</c></b>: on modern macOS
/// (Sonoma+) the window server ignores an <c>activate</c> request that originates from a background agent —
/// and Perch is exactly that (an <c>LSUIElement</c> menu-bar app whose overlay is a non-activating window,
/// so Perch is never the frontmost app when a row is clicked). <c>activateWithOptions:</c> returns YES and
/// even flips the target's <c>isActive</c> flag, but focus does not actually move (verified on-device). The
/// stock <c>open -b &lt;bundleId&gt;</c> / <c>open &lt;bundlePath&gt;</c> performs a proper foreground
/// request the system honours from a background process, with no Accessibility/Automation permission. So we
/// still use <c>NSRunningApplication</c> to <em>identify</em> the app (activation policy + bundle id/URL),
/// then shell <c>open</c> to actually raise it.</para>
///
/// Best-effort, and coarser than the Windows implementation: it foregrounds the hosting <em>app</em>, not a
/// specific window/tab (that needs the Accessibility API), so <paramref name="projectHint"/> is not yet
/// used — raising the exact window/tab is a Phase-6 follow-up. Never throws.
/// </summary>
public sealed class WindowActivator : IWindowActivator
{
    private const nint NSApplicationActivationPolicyRegular = 0;

    public void FocusTerminalForProcess(int pid, string? projectHint = null)
    {
        try
        {
            // Walk pid → ppid until we hit the hosting GUI app. The chain crosses a setuid-root `login`
            // (Terminal → login(root) → shell → claude), and libproc's proc_pidinfo refuses to read a
            // root-owned process's info from our non-root process — dead-ending the walk before the
            // terminal. `ps` reads ppids for every process (world-readable sysctl), so read the whole
            // map once and walk it in memory.
            var parents = ReadParentMap();
            int current = pid;
            for (int depth = 0; depth < 12 && current > 1; depth++)
            {
                if (Foreground(current, requireRegular: true)) return;
                current = parents.TryGetValue(current, out var pp) ? pp : 0;
            }
        }
        catch { /* best-effort */ }
    }

    public void FocusProcessMainWindow(int pid)
    {
        try { Foreground(pid, requireRegular: false); }
        catch { /* best-effort */ }
    }

    // Bring the app owning pid to the foreground. When requireRegular, only a Dock-visible ("regular") app
    // qualifies — used while walking ancestors so we skip the shell/claude processes and land on the
    // terminal app. Resolves the app's bundle id/URL via NSRunningApplication, then raises it with `open`
    // (see the class remarks for why activateWithOptions: doesn't work from a background agent).
    private static bool Foreground(int pid, bool requireRegular)
    {
        IntPtr app = ObjC.SendGet(ObjC.Class("NSRunningApplication"),
            ObjC.Sel("runningApplicationWithProcessIdentifier:"), pid);
        if (app == IntPtr.Zero) return false;

        if (requireRegular &&
            ObjC.SendNint(app, ObjC.Sel("activationPolicy")) != NSApplicationActivationPolicyRegular)
            return false;

        // Prefer the bundle identifier (no path escaping); fall back to the bundle URL's filesystem path.
        string? bundleId = ObjC.SendString(app, ObjC.Sel("bundleIdentifier"));
        if (!string.IsNullOrEmpty(bundleId))
            return Open("-b", bundleId!);

        IntPtr url = ObjC.SendGet(app, ObjC.Sel("bundleURL"));
        string? path = ObjC.SendString(url, ObjC.Sel("path"));
        return !string.IsNullOrEmpty(path) && Open(path!);
    }

    // `open -b <id>` / `open <path>` foregrounds an already-running app (exit 0 on success).
    private static bool Open(params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo("/usr/bin/open")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            if (p is null) return false;
            if (!p.WaitForExit(5_000)) { try { p.Kill(true); } catch { } return false; }
            return p.ExitCode == 0;
        }
        catch { return false; }
    }

    // A pid → ppid map of every process, via `ps -Ao pid=,ppid=` (each line "  <pid>  <ppid>"). Unlike
    // libproc this reads across root-owned processes, so the walk can cross the setuid-root `login`.
    private static Dictionary<int, int> ReadParentMap()
    {
        var map = new Dictionary<int, int>();
        try
        {
            var psi = new ProcessStartInfo("/bin/ps")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("-Ao");
            psi.ArgumentList.Add("pid=,ppid=");
            using var p = Process.Start(psi);
            if (p is null) return map;
            string outp = p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(5_000)) { try { p.Kill(true); } catch { } return map; }

            foreach (var line in outp.Split('\n'))
            {
                var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2 && int.TryParse(parts[0], out int pid) && int.TryParse(parts[1], out int ppid))
                    map[pid] = ppid;
            }
        }
        catch { /* best-effort: an empty map just means the walk can't climb */ }
        return map;
    }
}
