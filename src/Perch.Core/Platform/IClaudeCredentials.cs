namespace Perch.Platform;

/// <summary>
/// Reads Claude Code's stored OAuth credentials as the raw JSON blob
/// (<c>{ "claudeAiOauth": { "accessToken": … } }</c>) that the usage poll authenticates with. This is a
/// platform seam only because Claude Code stores the blob <em>differently per OS</em>: on Windows/Linux it
/// is the file <c>~/.claude/.credentials.json</c>, but on macOS it lives in the login <b>Keychain</b> (a
/// generic-password item named <c>Claude Code-credentials</c>) and the file does not exist — which is why
/// the file-only read surfaced "Couldn't read Claude credentials" on a Mac.
///
/// The implementation only knows <em>where</em> the blob lives; parsing the OAuth shape stays in
/// <see cref="Perch.Data"/>'s usage monitor. Best-effort: returns null when unavailable (not signed in,
/// unreadable, or access denied) so the caller shows the dimmed "sign in" state rather than throwing.
/// </summary>
public interface IClaudeCredentials
{
    /// <summary>The raw credentials JSON, or null when unavailable.</summary>
    string? ReadCredentialsJson();
}
