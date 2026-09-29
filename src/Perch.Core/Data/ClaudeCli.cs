using System.Diagnostics;

namespace Perch.Data;

/// <summary>
/// Small helpers for constructing Claude Code CLI invocations, shared so the terminal launcher
/// (<see cref="Perch.Platform.ISessionLauncher"/>) and the "copy resume command" clipboard path can never
/// drift apart.
/// </summary>
public static class ClaudeCli
{
    /// <summary>The command that resumes an existing session by id: <c>claude --resume &lt;sessionId&gt;</c>.</summary>
    public static string ResumeCommand(string sessionId) => $"claude --resume {sessionId}";

    /// <summary>A general <c>claude &lt;args&gt;</c> invocation (e.g. <c>args = "auth login"</c>).</summary>
    public static string Command(string args) => $"claude {args}";

    /// <summary>The absolute path of the <c>claude</c> CLI on PATH, or <c>null</c> when it isn't installed. Never the
    /// current directory — see <see cref="ExecutableResolver"/>.</summary>
    public static string? Find() => ExecutableResolver.Find("claude");

    /// <summary>A start info running <c>claude &lt;args&gt;</c> (<paramref name="args"/> must already be safe tokens)
    /// without ever resolving <c>claude</c> against a working directory. Windows: a native <c>claude.exe</c> is exec'd
    /// directly; an npm <c>.cmd</c> shim goes through <c>%SystemRoot%\System32\cmd.exe</c> by absolute path, with
    /// <c>NoDefaultCurrentDirectoryInExePath</c> set so the shim's own bare <c>node</c> lookup can't hit the cwd
    /// either. Elsewhere: a login shell, so the user's full PATH applies (a GUI app's PATH is minimal on macOS) — POSIX
    /// shells don't search the cwd. Throws <see cref="FileNotFoundException"/> on Windows when claude isn't on PATH.</summary>
    public static ProcessStartInfo CreateStartInfo(string args)
    {
        if (!OperatingSystem.IsWindows())
            return new ProcessStartInfo { FileName = "/bin/sh", Arguments = $"-lc \"claude {args}\"" };
        var claude = Find() ?? throw new FileNotFoundException("Couldn't find the `claude` CLI on PATH.");
        return CreateWindowsStartInfo(claude, args, ExecutableResolver.SystemTool("cmd.exe"));
    }

    /// <summary>The Windows half of <see cref="CreateStartInfo"/>, over an already-resolved path (split out for tests).</summary>
    internal static ProcessStartInfo CreateWindowsStartInfo(string claudePath, string args, string cmdPath)
    {
        if (Path.GetExtension(claudePath).Equals(".exe", StringComparison.OrdinalIgnoreCase))
            return new ProcessStartInfo { FileName = claudePath, Arguments = args };

        // /s strips exactly the outer quote pair, so the quoted path survives intact whatever it contains.
        var psi = new ProcessStartInfo { FileName = cmdPath, Arguments = $"/d /s /c \"\"{claudePath}\" {args}\"" };
        psi.Environment["NoDefaultCurrentDirectoryInExePath"] = "1";
        return psi;
    }
}
