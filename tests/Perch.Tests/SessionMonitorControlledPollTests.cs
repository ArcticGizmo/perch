using Perch.Data;
using Perch.Data.Control;
using Perch.Platform;
using Xunit;

namespace Perch.Tests;

/// <summary>
/// Covers <see cref="SessionMonitor.HasWorkingControlledSession"/> — the signal
/// <c>SessionMonitorHost</c> uses to run its short-cadence poll while a Perch-controlled session is working.
/// A controlled (<c>claude -p</c>) session never heartbeats its <c>sessions/{id}.json</c> status, so nothing
/// incidental triggers a rescan while it runs a turn; without the poll the overlay only re-reads the
/// session's (background) sub-agents on the 30s reconcile, long enough to miss the whole sub-agent. See the
/// investigation in <c>docs</c>/memory "controlled-session-subagents-gap".
/// </summary>
public class SessionMonitorControlledPollTests : IDisposable
{
    private const string DeadPid = "2147483644";
    private const string SessionId = "sessControlledPoll";
    private readonly string _sessionFile = Path.Combine(ClaudePaths.SessionsDir, $"{DeadPid}.json");

    private sealed class AlwaysAlive : IProcessProbe { public bool IsAlive(int pid) => true; }

    public SessionMonitorControlledPollTests() => Directory.CreateDirectory(ClaudePaths.SessionsDir);

    // A controlled session's file status is written by the CLI, which in -p mode leaves it at "idle" however
    // hard the session works — the monitor's real status comes from the ControlledSessions registry instead.
    private void WriteSessionFile(string status) =>
        File.WriteAllText(_sessionFile, $$"""
            { "pid": {{DeadPid}}, "sessionId": "{{SessionId}}",
              "status": "{{status}}", "cwd": "C:\\fixtures\\proj",
              "updatedAt": {{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}} }
            """);

    [Fact]
    public void ControlledSessionWorking_SetsSignal()
    {
        WriteSessionFile("idle");                    // the -p CLI never heartbeats "busy"
        ControlledSessions.Register(SessionId);
        ControlledSessions.SetActivity(SessionId, ControlledActivity.Busy);
        try
        {
            using var monitor = new SessionMonitor(new AlwaysAlive());
            var session = Assert.Single(monitor.Scan(), s => s.SessionId == SessionId);
            Assert.True(session.IsPerchControlled);
            Assert.Equal(SessionStatus.Running, session.Status);   // status comes from the registry, not the file
            Assert.True(monitor.HasWorkingControlledSession);
        }
        finally { ControlledSessions.Unregister(SessionId); }
    }

    [Fact]
    public void ControlledSessionIdle_DoesNotSetSignal()
    {
        WriteSessionFile("idle");
        ControlledSessions.Register(SessionId);      // registered => Idle by default
        try
        {
            using var monitor = new SessionMonitor(new AlwaysAlive());
            var session = Assert.Single(monitor.Scan(), s => s.SessionId == SessionId);
            Assert.True(session.IsPerchControlled);
            Assert.Equal(SessionStatus.Idle, session.Status);
            Assert.False(monitor.HasWorkingControlledSession);
        }
        finally { ControlledSessions.Unregister(SessionId); }
    }

    [Fact]
    public void UncontrolledBusySession_DoesNotSetSignal()
    {
        // A busy session that Perch does NOT drive must not arm the poll — its file heartbeats already
        // trigger scans, and it has no controlled background sub-agent to sample.
        WriteSessionFile("busy");
        using var monitor = new SessionMonitor(new AlwaysAlive());
        var session = Assert.Single(monitor.Scan(), s => s.SessionId == SessionId);
        Assert.False(session.IsPerchControlled);
        Assert.Equal(SessionStatus.Running, session.Status);
        Assert.False(monitor.HasWorkingControlledSession);
    }

    public void Dispose()
    {
        ControlledSessions.Unregister(SessionId);
        try { File.Delete(_sessionFile); } catch { }
    }
}
