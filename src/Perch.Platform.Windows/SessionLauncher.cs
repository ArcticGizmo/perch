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

    public bool RunClaudeCommand(string cwd, string claudeArgs, TerminalApp terminal, string? configDir = null,
        string? prompt = null)
    {
        // The prompt is the one free-text piece of the line; anything a shell could read into is refused up front.
        if (prompt is not null && !ClaudeCli.IsSafePrompt(prompt)) return false;
        // claude is resolved to an absolute path up front: the terminal opens *in* `cwd` — a repo Perch didn't
        // write — and cmd (like ShellExecute) looks in the current directory before PATH, so a bare `claude` would
        // prefer a claude.cmd committed there (review fixes CP7). Not on PATH → false, and the app falls back to
        // copying the command for the user to run themselves.
        if (ClaudeCli.Find() is not { } claude) return false;
        // configDir pins the account (CLAUDE_CONFIG_DIR): without it a session from another config dir resumes
        // under the primary, where its transcript doesn't exist. An unusable dir → false (copy fallback), never
        // a silent launch under the wrong account.
        if (ClaudeCli.WindowsCmdLine(claude, claudeArgs, configDir, prompt) is not { } cmdLine) return false;

        // Try the preferred terminal first.
        if (TryStart(StartInfo(terminal, cwd, claude, claudeArgs, cmdLine, configDir, prompt))) return true;

        // If an explicit choice failed (wt alias disabled, pwsh missing, …), fall back to a plain console so
        // the command still runs. CommandPrompt is already that fallback, so don't try it twice.
        if (terminal != TerminalApp.CommandPrompt
            && TryStart(StartInfo(TerminalApp.CommandPrompt, cwd, claude, claudeArgs, cmdLine, configDir, prompt)))
            return true;

        return false;
    }

    // -d sets Windows Terminal's tab start directory (wt ignores the parent's cwd); everything else takes
    // WorkingDirectory. Every host is an absolute path too: ShellExecute searches WorkingDirectory — the repo —
    // for a bare name. wt.exe is an execution alias on PATH, so it goes through the resolver. The -d value survives
    // a trailing backslash and a `;` (ClaudeCli.WindowsTerminalStartDir; review fixes CP13).
    private static ProcessStartInfo StartInfo(
        TerminalApp terminal, string cwd, string claude, string claudeArgs, string cmdLine, string? configDir, string? prompt)
    {
        string cmd = ExecutableResolver.SystemTool("cmd.exe");
        return terminal switch
        {
            TerminalApp.PowerShell =>
                new ProcessStartInfo(
                    Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"),
                    $"-NoExit -Command \"{ClaudeCli.WindowsPowerShellScript(claude, claudeArgs, configDir, prompt)}\"")
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

    public bool OpenTerminal(string cwd, TerminalApp terminal, string? configDir = null)
    {
        if (string.IsNullOrWhiteSpace(cwd) || !Directory.Exists(cwd)) return false;
        if (string.IsNullOrEmpty(configDir)) configDir = null;
        if (configDir is not null && ClaudeCli.WindowsShellCmdLine(configDir) is null) return false;
        if (TryStart(ShellStartInfo(terminal, cwd, configDir))) return true;
        return terminal != TerminalApp.CommandPrompt && TryStart(ShellStartInfo(TerminalApp.CommandPrompt, cwd, configDir));
    }

    // A bare shell in cwd — the hosts are absolute paths for the same reason as StartInfo's. A config dir rides the
    // command line, as in StartInfo, so Windows Terminal runs cmd rather than its default profile: a wt tab takes the
    // running WT's environment, and there's no profile-agnostic way to set a variable in it.
    private static ProcessStartInfo ShellStartInfo(TerminalApp terminal, string cwd, string? configDir)
    {
        string cmd = ExecutableResolver.SystemTool("cmd.exe");
        string? cmdArgs = configDir is null ? null : $"/k {ClaudeCli.WindowsShellCmdLine(configDir)}";
        return terminal switch
        {
            TerminalApp.PowerShell =>
                new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"),
                        configDir is null ? "" : $"-NoExit -Command \"{ClaudeCli.WindowsPowerShellShellScript(configDir)}\"")
                    { UseShellExecute = true, WorkingDirectory = cwd },
            TerminalApp.CommandPrompt =>
                new ProcessStartInfo(cmd, cmdArgs ?? "") { UseShellExecute = true, WorkingDirectory = cwd },
            _ => // WindowsTerminal, and Auto → Windows Terminal's default profile (falling back to Command Prompt)
                new ProcessStartInfo(ExecutableResolver.Resolve("wt.exe"),
                        $"-d {ClaudeCli.WindowsTerminalStartDir(cwd)}" + (cmdArgs is null ? "" : $" {QuoteIfSpaced(cmd)} {cmdArgs}"))
                    { UseShellExecute = true },
        };
    }

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
