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
    public bool Reopen(string cwd, string sessionId, TerminalApp terminal) => false;
}
