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
    public static void Append(string fileName, string message)
    {
        try
        {
            var path = Path.Combine(Dir, fileName);
            lock (Gate)
            {
                Directory.CreateDirectory(Dir);
                if (File.Exists(path) && new FileInfo(path).Length > MaxBytes) File.Delete(path);
                File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {message}{Environment.NewLine}", Encoding.UTF8);
            }
        }
        catch { /* diagnostics must never break the caller */ }
    }
}
