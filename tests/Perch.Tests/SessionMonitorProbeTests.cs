using Perch.Data;
using Perch.Platform;
using Xunit;

namespace Perch.Tests;

/// <summary>
/// Exercises the <see cref="IProcessProbe"/> seam through a real <see cref="SessionMonitor.Scan"/>.
/// Before the seam existed the full scan couldn't run against fixtures at all — it dropped every
/// session whose pid wasn't a live OS process (see the note on <see cref="SessionStatusTests"/>). With
/// an injectable probe a recorded (dead) pid can be reported alive, which is exactly what lets replay
/// materialise sessions that no longer have a running process.
/// </summary>
public class SessionMonitorProbeTests : IDisposable
{
    // A pid no real process owns, so the default OS probe genuinely reports it dead.
    private const string DeadPid = "2147483647";
    private const string SessionId = "replay-probe-test";

    private readonly string _sessionFile =
        Path.Combine(ClaudePaths.SessionsDir, $"{DeadPid}.json");

    private sealed class AlwaysAlive : IProcessProbe
    {
        public bool IsAlive(int pid) => true;
    }

    public SessionMonitorProbeTests()
    {
        Directory.CreateDirectory(ClaudePaths.SessionsDir);
        var updatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        File.WriteAllText(_sessionFile, $$"""
            { "pid": {{DeadPid}}, "sessionId": "{{SessionId}}",
              "status": "idle", "cwd": "C:\\fixtures\\proj", "updatedAt": {{updatedAt}} }
            """);
    }

    [Fact]
    public void Scan_WithInjectedProbe_KeepsSessionWhosePidIsDead()
    {
        using var monitor = new SessionMonitor(new AlwaysAlive());
        var sessions = monitor.Scan();
        var session = Assert.Single(sessions, s => s.SessionId == SessionId);
        Assert.Equal(DeadPid, session.Pid);
    }

    [Fact]
    public void Scan_WithDefaultProbe_DropsSessionWhosePidIsDead()
    {
        // Regression guard on the default: the real OS probe still discards a stale session file.
        using var monitor = new SessionMonitor();
        var sessions = monitor.Scan();
        Assert.DoesNotContain(sessions, s => s.SessionId == SessionId);
    }

    public void Dispose()
    {
        try { File.Delete(_sessionFile); } catch { /* best-effort cleanup */ }
    }
}
