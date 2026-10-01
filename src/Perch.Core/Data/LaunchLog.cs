namespace Perch.Data;

/// <summary>
/// A small append-only diagnostic log for terminal launches (reopen / hand back / <c>claude auth</c>): which
/// transcript and config dir Perch resolved, and the exact command line it handed the terminal. Written to
/// <c>&lt;settings dir&gt;/logs/launch.log</c> (e.g. <c>%APPDATA%\Perch (Dev)\logs</c>), capped at ~1 MB by starting over.
/// Local-only and best-effort: it holds paths and session ids, never prompt text, and a failed write is ignored.
/// </summary>
public static class LaunchLog
{
    private const string FileName = "launch.log";

    /// <summary>The log file's path.</summary>
    public static string FilePath => Path.Combine(DiagnosticLog.Dir, FileName);

    public static void Write(string message) => DiagnosticLog.Append(FileName, message);

    /// <summary>A value in brackets so leading/trailing whitespace is visible; <c>(null)</c> for null.</summary>
    public static string Show(string? value) => value is null ? "(null)" : $"[{value}]";
}
