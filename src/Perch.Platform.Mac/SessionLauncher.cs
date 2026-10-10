using Perch.Data;
using Perch.Platform;

namespace Perch.Platform.Mac;

/// <summary>
/// macOS <see cref="ISessionLauncher"/>. Not yet implemented (see docs/macos-port-plan.md): reopening a
/// session should open Terminal.app (or the user's preferred terminal) running
/// <c>claude --resume &lt;id&gt;</c> in the session's cwd. Returns false for now, so the app falls back to
/// copying the resume command to the clipboard.
/// </summary>
public sealed class SessionLauncher : ISessionLauncher
{
    public bool Reopen(string cwd, string sessionId, TerminalApp terminal, string? configDir = null) => false;

    // Not yet implemented (see docs/macos-port-plan.md): should open Terminal.app running `claude <args>` in
    // cwd. Returns false for now, so the app tells the user it couldn't launch one.
    public bool RunClaudeCommand(string cwd, string claudeArgs, TerminalApp terminal, string? configDir = null) => false;

    // Opens Terminal.app in cwd (`open -a Terminal <dir>` starts a shell there). The TerminalApp choices are
    // Windows hosts, so every value maps to Terminal.app.
    public bool OpenTerminal(string cwd, TerminalApp terminal)
    {
        if (string.IsNullOrWhiteSpace(cwd) || !Directory.Exists(cwd)) return false;
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("/usr/bin/open") { UseShellExecute = false };
            psi.ArgumentList.Add("-a");
            psi.ArgumentList.Add("Terminal");
            psi.ArgumentList.Add(cwd);
            return System.Diagnostics.Process.Start(psi) is not null;
        }
        catch { return false; }
    }

    // Not yet implemented (see docs/macos-port-plan.md): should activate Claude Desktop via its bundle id
    // (`open -b <bundleId>`). Returns false for now, so the app tells the user it couldn't open it.
    public bool OpenClaudeDesktop() => false;
}
