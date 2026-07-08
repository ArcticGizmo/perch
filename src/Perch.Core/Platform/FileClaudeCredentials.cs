using Perch.Data;

namespace Perch.Platform;

/// <summary>
/// The portable <see cref="IClaudeCredentials"/>: reads the raw JSON from
/// <c>~/.claude/.credentials.json</c> (<see cref="ClaudePaths.CredentialsFile"/>). This is how Claude Code
/// stores the blob on Windows and Linux, and it is also the macOS fallback if the Keychain read fails
/// (see the macOS implementation). Opened with <see cref="FileShare.ReadWrite"/> because Claude Code
/// rewrites the token in place during a refresh. Never throws — a missing/locked/garbage file reads as null.
/// </summary>
public sealed class FileClaudeCredentials : IClaudeCredentials
{
    public string? ReadCredentialsJson()
    {
        var path = ClaudePaths.CredentialsFile;
        if (!File.Exists(path))
            return null;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(fs);
            return reader.ReadToEnd();
        }
        catch
        {
            return null;
        }
    }
}
