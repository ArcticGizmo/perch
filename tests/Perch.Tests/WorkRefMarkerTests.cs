using Perch.Data;
using Perch.Platform;
using Xunit;

namespace Perch.Tests;

/// <summary>
/// The hook half of work items (docs/work-items-plan.md W2): a <c>claude</c> started with <c>PERCH_WORK</c> set gets a
/// <c>{sessionId}.work</c> marker from perch-hook, and the scan surfaces it as <see cref="ClaudeSession.WorkRef"/> for
/// the app to resolve. A session without the marker has none.
/// </summary>
public sealed class WorkRefMarkerTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("perch-workref-").FullName;

    private sealed class AlwaysAlive : IProcessProbe
    {
        public bool IsAlive(int pid) => true;
    }

    public void Dispose()
    {
        ClaudeConfigSet.ResetForTesting();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void Scan_ReadsTheWorkMarker()
    {
        var dir = new ClaudeConfigDir(_root);
        Directory.CreateDirectory(dir.SessionsDir);
        Seed(dir, "4101", "with-work");
        Seed(dir, "4102", "without-work");
        File.WriteAllText(Path.Combine(dir.SessionsDir, "with-work.work"), "  Payments refactor \n");
        ClaudeConfigSet.SetForTesting(new[] { dir });

        using var monitor = new SessionMonitor(new AlwaysAlive());
        var sessions = monitor.Scan();

        Assert.Equal("Payments refactor", Assert.Single(sessions, s => s.SessionId == "with-work").WorkRef);
        Assert.Null(Assert.Single(sessions, s => s.SessionId == "without-work").WorkRef);
    }

    private static void Seed(ClaudeConfigDir dir, string pid, string sessionId)
    {
        var updatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        File.WriteAllText(Path.Combine(dir.SessionsDir, $"{pid}.json"), $$"""
            { "pid": {{pid}}, "sessionId": "{{sessionId}}", "status": "idle",
              "cwd": {{System.Text.Json.JsonSerializer.Serialize(@"C:\p\x")}}, "updatedAt": {{updatedAt}} }
            """);
    }
}
