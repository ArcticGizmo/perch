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
    public bool Reopen(string cwd, string sessionId, TerminalApp terminal)
    {
        string inner = ClaudeCli.ResumeCommand(sessionId);

        // Try the preferred terminal first.
        if (TryStart(StartInfo(terminal, cwd, inner))) return true;

        // If an explicit choice failed (wt alias disabled, pwsh missing, …), fall back to a plain console so
        // reopening still works. CommandPrompt is already that fallback, so don't try it twice.
        if (terminal != TerminalApp.CommandPrompt && TryStart(StartInfo(TerminalApp.CommandPrompt, cwd, inner)))
            return true;

        return false;
    }

    // -d sets Windows Terminal's tab start directory (wt ignores the parent's cwd); everything else takes
    // WorkingDirectory. UseShellExecute resolves the wt.exe execution alias and opens a new window.
    private static ProcessStartInfo StartInfo(TerminalApp terminal, string cwd, string inner) => terminal switch
    {
        TerminalApp.WindowsTerminal =>
            new ProcessStartInfo("wt.exe", $"-d \"{cwd}\" cmd /k {inner}") { UseShellExecute = true },
        TerminalApp.PowerShell =>
            new ProcessStartInfo("powershell.exe", $"-NoExit -Command \"{inner}\"")
                { UseShellExecute = true, WorkingDirectory = cwd },
        TerminalApp.CommandPrompt =>
            new ProcessStartInfo("cmd.exe", $"/k {inner}") { UseShellExecute = true, WorkingDirectory = cwd },
        _ => // Auto → Windows Terminal (the Reopen fallback then covers Command Prompt)
            new ProcessStartInfo("wt.exe", $"-d \"{cwd}\" cmd /k {inner}") { UseShellExecute = true },
    };

    private static bool TryStart(ProcessStartInfo psi)
    {
        try { return Process.Start(psi) is not null; }
        catch { return false; } // terminal missing / alias disabled — let the caller fall back
    }
}
