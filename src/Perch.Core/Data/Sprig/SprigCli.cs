using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Perch.Data.Sprig;

/// <summary>What a sprig command said: <see cref="Ok"/>, or the error <see cref="Code"/> sprig's <c>--json</c> gives
/// (<c>untrusted</c>, <c>inputs-needed</c>, <c>docker-unavailable</c> …) and its message.</summary>
internal sealed record SprigActionResult(bool Ok, string? Code = null, string? Error = null)
{
    /// <summary>Sprig couldn't be found or started.</summary>
    public const string Unavailable = "unavailable";

    /// <summary>The command didn't return in time and was stopped.</summary>
    public const string TimedOut = "timed-out";

    /// <summary>Sprig needs a person in its own UI to go on: approving a definition, giving inputs, taking over a
    /// checkout, or fixing a slot.</summary>
    public bool NeedsSprig => Code is "untrusted" or "inputs-needed" or "held" or "slot-broken" or "problems";
}

/// <summary>What Perch can ask sprig to do to a workspace, or one of its apps.</summary>
internal enum SprigAction { Start, Restart, Stop, Pause, Resume }

internal static class SprigActions
{
    /// <summary>What a workspace is doing while <paramref name="action"/> runs, so the spinner can show from the click
    /// rather than from sprig's first report.</summary>
    public static SprigActivity ActivityOf(SprigAction action)
        => action is SprigAction.Stop or SprigAction.Pause ? SprigActivity.Stopping : SprigActivity.Starting;
}

/// <summary>
/// Finds sprig and runs its CLI for the things Perch asks it to <em>do</em>. Everything Perch reads comes from the
/// <c>sprig watch --json</c> stream (see <see cref="SprigStatusFold"/>); only actions launch a command.
/// </summary>
/// <remarks>
/// <para>The CLI rather than sprig's host pipe, because the pipe is internal to sprig and free to change, while
/// <c>--json</c> is its stable contract. Commands that can start apps pass <c>--no-wait</c>: progress shows up on
/// the stream, so there's nothing to wait for here.</para>
/// <para>Every child gets redirected, UTF-8 stdio and a closed stdin, so sprig sees no terminal and never prompts.
/// Blocking; call off the UI thread.</para>
/// </remarks>
internal static class SprigCli
{
    // Velopack installs sprig under its packId; `current` holds sprig.exe beside sprig-gui and sprig-host.
    private static string InstalledPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Sprig", "current", "sprig.exe");

    /// <summary>
    /// The sprig executable, or null when it isn't installed. The install location first, then PATH. On Windows
    /// it asks for <c>sprig.exe</c> by name, because sprig's <c>bin</c> folder on PATH holds <c>sprig.cmd</c> and
    /// <c>sprig.ps1</c> shims (for <c>sprig cd</c>), which aren't the CLI.
    /// </summary>
    public static string? Locate()
    {
        if (OperatingSystem.IsWindows() && File.Exists(InstalledPath)) return InstalledPath;
        return ExecutableResolver.Find(OperatingSystem.IsWindows() ? "sprig.exe" : "sprig");
    }

    /// <summary>How to start the long-lived <c>sprig watch --json</c>. Stdin stays redirected <em>and open</em>:
    /// sprig ends the watch when it closes, which is how it learns Perch has gone.</summary>
    public static ProcessStartInfo WatchStartInfo(string exe)
    {
        var psi = BaseStartInfo(exe);
        psi.ArgumentList.Add("watch");
        psi.ArgumentList.Add("--json");
        return psi;
    }

    /// <summary>The command line for <paramref name="action"/> on a workspace, or on one app of it.</summary>
    internal static IReadOnlyList<string> ActionArgs(SprigAction action, SprigWorkspace workspace, string? app)
    {
        var verb = action switch
        {
            SprigAction.Start => "start",
            SprigAction.Restart => "restart",
            SprigAction.Stop => "stop",
            SprigAction.Pause => "pause",
            _ => "resume",
        };
        var args = new List<string> { verb, workspace.Orchestration, workspace.Name };
        if (app is not null && action is SprigAction.Start or SprigAction.Restart or SprigAction.Stop) args.Add(app);
        args.Add("--json");
        if (action is SprigAction.Start or SprigAction.Restart or SprigAction.Resume) args.Add("--no-wait");
        return args;
    }

    /// <summary>Runs <paramref name="action"/>. A start can first have to make the workspace ready (worktrees,
    /// files, its infra), which can take minutes the first time an image is pulled, so the limit is generous.</summary>
    public static SprigActionResult Run(SprigAction action, SprigWorkspace workspace, string? app = null)
        => Run(ActionArgs(action, workspace, app), TimeSpan.FromMinutes(10));

    /// <summary>Brings sprig's window up on <paramref name="workspace"/> (or sprig as it is, when null), launching
    /// the app if it isn't running.</summary>
    public static SprigActionResult Open(SprigWorkspace? workspace)
    {
        var args = new List<string> { "open" };
        if (workspace is not null) { args.Add(workspace.Orchestration); args.Add(workspace.Name); }
        args.Add("--json");
        return Run(args, TimeSpan.FromSeconds(30));
    }

    private static SprigActionResult Run(IReadOnlyList<string> args, TimeSpan limit)
    {
        if (Locate() is not { } exe) return new(false, SprigActionResult.Unavailable, "sprig isn't installed");
        try
        {
            var psi = BaseStartInfo(exe);
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            if (p is null) return new(false, SprigActionResult.Unavailable, "sprig didn't start");
            p.StandardInput.Close();
            // Both pipes are read before waiting: a full pipe nobody drains would block sprig and turn a long
            // message into a hang.
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(limit))
            {
                // Only the CLI itself. A start may have launched sprig's host, which is this process's child by
                // parent id; killing the tree would take the host and every app it runs down with it.
                try { p.Kill(); } catch { /* already gone */ }
                return new(false, SprigActionResult.TimedOut, "sprig didn't finish in time");
            }
            // Bounded, so a process that kept the pipes open after sprig exited can't hold this thread; the exit
            // code still decides.
            bool drained = Task.WaitAll([stdout, stderr], TimeSpan.FromSeconds(5));
            return Parse(p.ExitCode, drained ? stdout.Result : "", drained ? stderr.Result : "");
        }
        catch (Exception e)
        {
            return new(false, SprigActionResult.Unavailable, e.Message);
        }
    }

    /// <summary>Reads a command's outcome. The exit code decides; the last JSON object on stdout carries the
    /// <c>code</c> and <c>error</c> (progress may come first as other lines).</summary>
    internal static SprigActionResult Parse(int exitCode, string stdout, string stderr)
    {
        string? code = null, error = null;
        foreach (var line in stdout.Split('\n').Select(l => l.Trim()).Where(l => l.StartsWith('{')).Reverse())
        {
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (!root.TryGetProperty("ok", out _)) continue;
                code = root.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
                error = root.TryGetProperty("error", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
                break;
            }
            catch (JsonException) { }
        }
        if (exitCode == 0) return new(true);
        if (error is null && stderr.Trim() is { Length: > 0 } text)
            error = text.StartsWith("error: ", StringComparison.Ordinal) ? text["error: ".Length..] : text;
        return new(false, code ?? "error", error ?? $"sprig exited with code {exitCode}");
    }

    private static ProcessStartInfo BaseStartInfo(string exe) => new(exe)
    {
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardInput = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        StandardOutputEncoding = Encoding.UTF8,
        StandardErrorEncoding = Encoding.UTF8,
        // Sprig reads "the workspace this folder is in" from the working directory; every command names its
        // workspace, so run from sprig's own folder rather than whatever Perch's happens to be.
        WorkingDirectory = Path.GetDirectoryName(exe) ?? "",
    };
}
