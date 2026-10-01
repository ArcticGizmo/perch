using System.Text;

namespace Perch.Data;

/// <summary>
/// Perch's small append-only diagnostic logs, one file per topic under <c>&lt;settings dir&gt;/logs/</c> (e.g.
/// <c>%APPDATA%\Perch (Dev)\logs\launch.log</c>). Each is capped at ~1 MB by starting over. Local-only and
/// best-effort: a failed write is ignored, and callers must never put prompt text or secrets in a message.
/// </summary>
public static class DiagnosticLog
{
    private const long MaxBytes = 1024 * 1024;
    private static readonly Lock Gate = new();

    /// <summary>The logs folder.</summary>
    public static string Dir =>
        Path.Combine(Path.GetDirectoryName(AppSettings.SettingsFilePath) ?? Path.GetTempPath(), "logs");

    /// <summary>Appends a timestamped line to <c>logs/&lt;fileName&gt;</c>.</summary>
    public static void Append(string fileName, string message) =>
        AppendRaw(fileName, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {message}", MaxBytes);

    /// <summary>Appends <paramref name="line"/> as-is to <c>logs/&lt;fileName&gt;</c>, starting the file over once
    /// it passes <paramref name="maxBytes"/>. <paramref name="fileName"/> is a bare name: anything with a
    /// directory part is refused, so no caller can steer a log out of the logs folder.</summary>
    public static void AppendRaw(string fileName, string line, long maxBytes)
    {
        try
        {
            // Either slash is refused on every OS (a '\' is a plain name character on macOS), so one rule holds everywhere.
            if (fileName.Length == 0 || fileName.IndexOfAny(['/', '\\']) >= 0 || fileName != Path.GetFileName(fileName)) return;
            var path = Path.Combine(Dir, fileName);
            lock (Gate)
            {
                Directory.CreateDirectory(Dir);
                if (File.Exists(path) && new FileInfo(path).Length > maxBytes) File.Delete(path);
                File.AppendAllText(path, line + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch { /* diagnostics must never break the caller */ }
    }
}
