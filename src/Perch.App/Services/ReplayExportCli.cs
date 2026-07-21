using Perch.Data.Replay;

namespace Perch.Avalonia.Services;

/// <summary>
/// The <c>perch export</c> subcommand: captures a session already on disk into a portable
/// <c>.perchreplay</c> recording for later replay. A scripted counterpart to the (debug) picker window,
/// so a repro can be captured straight from a terminal. Redaction is ON by default (shareable exports
/// must be clean per the org PII policy); pass <c>--no-redact</c> to keep a raw local recording.
/// </summary>
internal static class ReplayExportCli
{
    /// <summary>Runs the export and returns a process exit code. <paramref name="args"/> is the full
    /// argv, i.e. <c>["export", &lt;sessionId&gt;, &lt;out&gt;, ...]</c>.</summary>
    public static int Run(string[] args)
    {
        var positional = args.Skip(1).Where(a => !a.StartsWith("--")).ToArray();
        if (positional.Length < 2)
        {
            Console.Error.WriteLine(
                "usage: perch export <sessionId> <out.perchreplay> [--no-redact]");
            Console.Error.WriteLine();
            Console.Error.WriteLine("Recent sessions:");
            foreach (var s in RecordingExporter.DiscoverSessions().Take(15))
                Console.Error.WriteLine(
                    $"  {s.SessionId}  {s.LastActivityUtc:yyyy-MM-dd HH:mm}  " +
                    $"{s.SizeBytes / 1024,6} KB  {s.Cwd}");
            return 1;
        }

        var sessionId = positional[0];
        var outPath = positional[1];
        var redact = !args.Any(a => string.Equals(a, "--no-redact", StringComparison.OrdinalIgnoreCase));

        var session = RecordingExporter.DiscoverSessions()
            .FirstOrDefault(s => s.SessionId == sessionId);
        if (session == null)
        {
            Console.Error.WriteLine($"No session '{sessionId}' found under the Claude config dir.");
            return 1;
        }

        try
        {
            var manifest = RecordingExporter.Export([session], outPath, redact);
            var timeline = manifest.Timelines.FirstOrDefault();
            Console.WriteLine(
                $"Exported {sessionId} -> {outPath} " +
                $"({(redact ? "redacted" : "RAW — keep local")}, " +
                $"{(timeline?.DurationMs ?? 0) / 1000}s of timeline).");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Export failed: {ex.Message}");
            return 1;
        }
    }
}
