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
    public bool Reopen(string cwd, string sessionId, TerminalApp terminal) =>
        RunClaudeCommand(cwd, $"--resume {sessionId}", terminal);

    public bool RunClaudeCommand(string cwd, string claudeArgs, TerminalApp terminal)
    {
        // claude is resolved to an absolute path up front: the terminal opens *in* `cwd` — a repo Perch didn't
        // write — and cmd (like ShellExecute) looks in the current directory before PATH, so a bare `claude` would
        // prefer a claude.cmd committed there (review fixes CP7). Not on PATH → false, and the app falls back to
        // copying the command for the user to run themselves.
        if (ClaudeCli.Find() is not { } claude) return false;

        // Try the preferred terminal first.
        if (TryStart(StartInfo(terminal, cwd, claude, claudeArgs))) return true;

        // If an explicit choice failed (wt alias disabled, pwsh missing, …), fall back to a plain console so
        // the command still runs. CommandPrompt is already that fallback, so don't try it twice.
        if (terminal != TerminalApp.CommandPrompt && TryStart(StartInfo(TerminalApp.CommandPrompt, cwd, claude, claudeArgs)))
            return true;

        return false;
    }

    // -d sets Windows Terminal's tab start directory (wt ignores the parent's cwd); everything else takes
    // WorkingDirectory. Every host is an absolute path too: ShellExecute searches WorkingDirectory — the repo —
    // for a bare name. wt.exe is an execution alias on PATH, so it goes through the resolver.
    private static ProcessStartInfo StartInfo(TerminalApp terminal, string cwd, string claude, string claudeArgs)
    {
        // cmd keeps a quoted first token only when it's the sole quoted pair — true here (the args are safe tokens).
        string cmdLine = $"{QuoteIfSpaced(claude)} {claudeArgs}";
        string cmd = ExecutableResolver.SystemTool("cmd.exe");
        return terminal switch
        {
            TerminalApp.PowerShell =>
                new ProcessStartInfo(
                    Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"),
                    $"-NoExit -Command \"& '{claude.Replace("'", "''")}' {claudeArgs}\"")
                    { UseShellExecute = true, WorkingDirectory = cwd },
            TerminalApp.CommandPrompt =>
                new ProcessStartInfo(cmd, $"/k {cmdLine}") { UseShellExecute = true, WorkingDirectory = cwd },
            _ => // WindowsTerminal, and Auto → Windows Terminal (the Reopen fallback then covers Command Prompt)
                new ProcessStartInfo(ExecutableResolver.Resolve("wt.exe"), $"-d \"{cwd}\" {QuoteIfSpaced(cmd)} /k {cmdLine}")
                    { UseShellExecute = true },
        };
    }

    private static string QuoteIfSpaced(string path) => path.Contains(' ') ? $"\"{path}\"" : path;

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
        try { return Process.Start(psi) is not null; }
        catch { return false; } // terminal missing / alias disabled — let the caller fall back
    }
}
