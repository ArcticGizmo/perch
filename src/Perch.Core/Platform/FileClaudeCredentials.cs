using System.Text;
using Perch.Data;

namespace Perch.Platform;

/// <summary>
/// The portable <see cref="IClaudeCredentials"/>: reads the raw JSON from
/// <c>~/.claude/.credentials.json</c> (<see cref="ClaudePaths.CredentialsFile"/>), or from the file named at
/// construction (another config dir's). This is how Claude Code stores the blob on Windows and Linux, and it is also
/// the macOS fallback if the Keychain read fails (see the macOS implementation). Opened with
/// <see cref="FileShare.ReadWrite"/> because Claude Code rewrites the token in place during a refresh. Never throws —
/// a missing/locked/garbage file reads as null.
/// </summary>
public sealed class FileClaudeCredentials : IClaudeCredentials
{
    private readonly string? _path;

    public FileClaudeCredentials(string? path = null) => _path = path;

    // Resolved per call, so the default follows the primary config dir if it changes while running.
    private string FilePath => _path ?? ClaudePaths.CredentialsFile;

    public string? ReadCredentialsJson()
    {
        var path = FilePath;
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

    public bool WriteCredentialsJson(string json) => WriteFile(FilePath, json);

    /// <summary>
    /// Replaces <paramref name="path"/> with <paramref name="json"/> through a temp file in the same folder, so a
    /// reader (Claude Code, or Perch's own usage poll) sees the old blob or the new one, never half of one. The
    /// rename can lose to a reader holding the file without delete-sharing for a moment, so it retries briefly.
    /// </summary>
    public static bool WriteFile(string path, string json)
    {
        string? tmp = null;
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return false;
            var staged = Path.Combine(dir, $".credentials.{Guid.NewGuid():N}.tmp");
            tmp = staged;
            File.WriteAllText(staged, json, new UTF8Encoding(false));
            // Claude Code keeps the file owner-only; a fresh temp file would otherwise take the umask's wider mode.
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(staged, UnixFileMode.UserRead | UnixFileMode.UserWrite);

            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    File.Move(staged, path, overwrite: true);
                    tmp = null;
                    return true;
                }
                catch (IOException) when (attempt < 9)
                {
                    Thread.Sleep(100);
                }
                catch (UnauthorizedAccessException) when (attempt < 9)
                {
                    Thread.Sleep(100);
                }
            }
        }
        catch
        {
            return false;
        }
        finally
        {
            if (tmp is not null)
                try { File.Delete(tmp); } catch { }
        }
    }
}
