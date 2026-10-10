using System.Diagnostics;
using Perch.Data;
using Perch.Platform;

namespace Perch.Platform.Windows;

/// <summary>
/// Windows <see cref="ISessionLauncher"/>: reopens a past session in a fresh terminal running
/// <c>claude --resume &lt;id&gt;</c> in its working directory. Honours the user's <see cref="TerminalApp"/>
/// choice (Windows Terminal, PowerShell, or Command Prompt), with <see cref="TerminalApp.Auto"/> preferring
/// Windows Terminal and falling back to Command Prompt. Any explicit choice that can't be launched also
/// falls back to a plain console, so reopening still works; a total failure returns false so the app can
/// degrade to copying the command. The shells keep their window open (<c>cmd /k</c> / <c>-NoExit</c>) and
/// route through a shell that can resolve the <c>claude</c> shim (a .cmd on PATH).
/// </summary>
public sealed class SessionLauncher : ISessionLauncher
{
    // The id lands unquoted on a cmd / PowerShell / wt command line, so anything but a plain session id is refused
    // (false → the app offers to copy the command instead; review fixes CP13).
    public bool Reopen(string cwd, string sessionId, TerminalApp terminal, string? configDir = null) =>
        ClaudeCli.IsSessionId(sessionId) && RunClaudeCommand(cwd, $"--resume {sessionId}", terminal, configDir);

    public bool RunClaudeCommand(string cwd, string claudeArgs, TerminalApp terminal, string? configDir = null)
    {
        // claude is resolved to an absolute path up front: the terminal opens *in* `cwd` — a repo Perch didn't
        // write — and cmd (like ShellExecute) looks in the current directory before PATH, so a bare `claude` would
        // prefer a claude.cmd committed there (review fixes CP7). Not on PATH → false, and the app falls back to
        // copying the command for the user to run themselves.
        if (ClaudeCli.Find() is not { } claude) return false;
        // configDir pins the account (CLAUDE_CONFIG_DIR): without it a session from another config dir resumes
        // under the primary, where its transcript doesn't exist. An unusable dir → false (copy fallback), never
        // a silent launch under the wrong account.
        if (ClaudeCli.WindowsCmdLine(claude, claudeArgs, configDir) is not { } cmdLine) return false;

        // Try the preferred terminal first.
        if (TryStart(StartInfo(terminal, cwd, claude, claudeArgs, cmdLine, configDir))) return true;

        // If an explicit choice failed (wt alias disabled, pwsh missing, …), fall back to a plain console so
        // the command still runs. CommandPrompt is already that fallback, so don't try it twice.
        if (terminal != TerminalApp.CommandPrompt
            && TryStart(StartInfo(TerminalApp.CommandPrompt, cwd, claude, claudeArgs, cmdLine, configDir)))
            return true;

        return false;
    }

    // -d sets Windows Terminal's tab start directory (wt ignores the parent's cwd); everything else takes
    // WorkingDirectory. Every host is an absolute path too: ShellExecute searches WorkingDirectory — the repo —
    // for a bare name. wt.exe is an execution alias on PATH, so it goes through the resolver. The -d value survives
    // a trailing backslash and a `;` (ClaudeCli.WindowsTerminalStartDir; review fixes CP13).
    private static ProcessStartInfo StartInfo(
        TerminalApp terminal, string cwd, string claude, string claudeArgs, string cmdLine, string? configDir)
    {
        string cmd = ExecutableResolver.SystemTool("cmd.exe");
        return terminal switch
        {
            TerminalApp.PowerShell =>
                new ProcessStartInfo(
                    Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"),
                    $"-NoExit -Command \"{ClaudeCli.WindowsPowerShellScript(claude, claudeArgs, configDir)}\"")
                    { UseShellExecute = true, WorkingDirectory = cwd },
            TerminalApp.CommandPrompt =>
                new ProcessStartInfo(cmd, $"/k {cmdLine}") { UseShellExecute = true, WorkingDirectory = cwd },
            _ => // WindowsTerminal, and Auto → Windows Terminal (the Reopen fallback then covers Command Prompt)
                new ProcessStartInfo(ExecutableResolver.Resolve("wt.exe"),
                        $"-d {ClaudeCli.WindowsTerminalStartDir(cwd)} {QuoteIfSpaced(cmd)} /k {cmdLine}")
                    { UseShellExecute = true },
        };
    }

    private static string QuoteIfSpaced(string path) => path.Contains(' ') ? $"\"{path}\"" : path;

    public bool OpenTerminal(string cwd, TerminalApp terminal)
    {
        if (string.IsNullOrWhiteSpace(cwd) || !Directory.Exists(cwd)) return false;
        if (TryStart(ShellStartInfo(terminal, cwd))) return true;
        return terminal != TerminalApp.CommandPrompt && TryStart(ShellStartInfo(TerminalApp.CommandPrompt, cwd));
    }

    // A bare shell in cwd — the hosts are absolute paths for the same reason as StartInfo's.
    private static ProcessStartInfo ShellStartInfo(TerminalApp terminal, string cwd) => terminal switch
    {
        TerminalApp.PowerShell =>
            new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"))
                { UseShellExecute = true, WorkingDirectory = cwd },
        TerminalApp.CommandPrompt =>
            new ProcessStartInfo(ExecutableResolver.SystemTool("cmd.exe")) { UseShellExecute = true, WorkingDirectory = cwd },
        _ => // WindowsTerminal, and Auto → Windows Terminal's default profile (falling back to Command Prompt)
            new ProcessStartInfo(ExecutableResolver.Resolve("wt.exe"), $"-d {ClaudeCli.WindowsTerminalStartDir(cwd)}")
                { UseShellExecute = true },
    };

    // Claude Desktop ships as an MSIX-packaged (Store) app, so its exe lives under the ACL-protected,
    // version-stamped C:\Program Files\WindowsApps\... — you can't launch it by path. The supported way is
    // to activate it by AppUserModelID, which is stable across version bumps (the family name's publisher
    // hash and the "Claude" app id don't change on update). "explorer.exe shell:AppsFolder\<AUMID>" is the
    // canonical shell activation; if the app is already running it just brings the window forward, which is
    // exactly what we want for one that's been closed to the tray. Re-derive the AUMID if it ever changes
    // with:  Get-StartApps | ? Name -like 'Claude*'   (AppID column).
    private const string ClaudeDesktopAumid = "Claude_pzs8sxrjxfjjc!Claude";

    public bool OpenClaudeDesktop() =>
        TryStart(new ProcessStartInfo(ExecutableResolver.WindowsTool("explorer.exe"), $"shell:AppsFolder\\{ClaudeDesktopAumid}")
            { UseShellExecute = true });

    private static bool TryStart(ProcessStartInfo psi)
    {
        // Logged (LaunchLog): the exact command handed to the shell, bracketed so quoting/whitespace is visible.
        LaunchLog.Write($"terminal launch: file={LaunchLog.Show(psi.FileName)} args={LaunchLog.Show(psi.Arguments)} " +
                        $"workdir={LaunchLog.Show(psi.WorkingDirectory)}");
        try { return Process.Start(psi) is not null; }
        catch (Exception ex)
        {
            LaunchLog.Write($"terminal launch failed: {ex.Message}");
            return false; // terminal missing / alias disabled — let the caller fall back
        }
    }
}
