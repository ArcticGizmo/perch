using Perch.Data;
using Perch.Data.Control;
using Perch.Platform;
using Xunit;

namespace Perch.Tests;

/// <summary>
/// A Perch-controlled session's "done" through a real <see cref="SessionMonitor.Scan"/>. Its status comes from the
/// <see cref="ControlledSessions"/> registry (the <c>-p</c> CLI never heartbeats its session file), so the overlay's
/// busy→idle completion depends on the session window publishing Busy for every turn — including one the CLI starts
/// on its own, such as a background task handing its result back after the previous turn's <c>result</c>. Before
/// <see cref="SessionConversation"/> reopened such a turn it published Idle throughout: the row read "idle" while the
/// turn ran and no "done" followed it.
/// </summary>
public class SessionMonitorControlledDoneTests : IDisposable
{
    private const string DeadPid = "2147483643";
    // No transcript fixture: nothing on disk can read as an interrupt, a bare command or an outstanding agent.
    private const string SessionId = "sessControlledDone";
    private readonly string _sessionFile = Path.Combine(ClaudePaths.SessionsDir, $"{DeadPid}.json");

    private sealed class AlwaysAlive : IProcessProbe { public bool IsAlive(int pid) => true; }

    private sealed class SteppedClock(DateTime start) : IClockProvider
    {
        public DateTime Now { get; set; } = start;
        public DateTime UtcNow => Now.ToUniversalTime();
    }

    public SessionMonitorControlledDoneTests()
    {
        Directory.CreateDirectory(ClaudePaths.SessionsDir);
        File.WriteAllText(_sessionFile, $$"""
            { "pid": {{DeadPid}}, "sessionId": "{{SessionId}}",
              "status": "idle", "cwd": "C:\\fixtures\\proj",
              "updatedAt": {{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}} }
            """);
    }

    [Fact]
    public void ControlledTurnEnding_RaisesDoneAfterSettle()
    {
        var clock = new SteppedClock(DateTime.Now);
        Clock.SetProvider(clock);
        ControlledSessions.Register(SessionId);
        try
        {
            using var monitor = new SessionMonitor(new AlwaysAlive());
            int done = 0;
            monitor.NeedsAttention += s => { if (s.SessionId == SessionId) done++; };

            ControlledSessions.SetActivity(SessionId, ControlledActivity.Busy);
            Assert.Equal(SessionStatus.Running, Status(monitor));

            ControlledSessions.SetActivity(SessionId, ControlledActivity.Idle);
            Assert.Equal(SessionStatus.Idle, Status(monitor));   // the busy→idle edge opens the settle window
            Assert.Equal(0, done);

            clock.Now = clock.Now.AddSeconds(2);
            Assert.Equal(SessionStatus.NeedsAttention, Status(monitor));
            Assert.Equal(1, done);

            clock.Now = clock.Now.AddSeconds(2);
            Assert.Equal(SessionStatus.NeedsAttention, Status(monitor));   // the badge stays until acknowledged
            Assert.Equal(1, done);                                         // and the alert fires once
        }
        finally
        {
            ControlledSessions.Unregister(SessionId);
            Clock.Reset();
        }
    }

    [Fact]
    public void ControlledSessionNeverBusy_RaisesNoDone()
    {
        // The pre-fix shape of a CLI-started turn: the registry never leaves Idle, so there's no edge to report.
        var clock = new SteppedClock(DateTime.Now);
        Clock.SetProvider(clock);
        ControlledSessions.Register(SessionId);
        try
        {
            using var monitor = new SessionMonitor(new AlwaysAlive());
            int done = 0;
            monitor.NeedsAttention += s => { if (s.SessionId == SessionId) done++; };

            Assert.Equal(SessionStatus.Idle, Status(monitor));
            clock.Now = clock.Now.AddSeconds(2);
            Assert.Equal(SessionStatus.Idle, Status(monitor));
            Assert.Equal(0, done);
        }
        finally
        {
            ControlledSessions.Unregister(SessionId);
            Clock.Reset();
        }
    }

    [Fact]
    public void MarkUnread_RearmsDoneBadgeWithoutAlert_UntilAcknowledged()
    {
        // "Mark as unread" on an idle row: the badge comes back (no toast — it isn't a completion) and stays
        // until read again, exactly like a real "done".
        var clock = new SteppedClock(DateTime.Now);
        Clock.SetProvider(clock);
        try
        {
            using var monitor = new SessionMonitor(new AlwaysAlive());
            int done = 0;
            monitor.NeedsAttention += s => { if (s.SessionId == SessionId) done++; };

            Assert.Equal(SessionStatus.Idle, Status(monitor));
            monitor.MarkUnread(DeadPid);
            Assert.Equal(SessionStatus.NeedsAttention, Status(monitor));
            clock.Now = clock.Now.AddMinutes(5);
            Assert.Equal(SessionStatus.NeedsAttention, Status(monitor));
            Assert.Equal(0, done);

            monitor.Acknowledge(DeadPid);
            Assert.Equal(SessionStatus.Idle, Status(monitor));
        }
        finally
        {
            Clock.Reset();
        }
    }

    [Fact]
    public void MarkUnread_IgnoresRunningSession()
    {
        ControlledSessions.Register(SessionId);
        try
        {
            using var monitor = new SessionMonitor(new AlwaysAlive());
            ControlledSessions.SetActivity(SessionId, ControlledActivity.Busy);
            Assert.Equal(SessionStatus.Running, Status(monitor));
            monitor.MarkUnread(DeadPid);
            Assert.Equal(SessionStatus.Running, Status(monitor));

            // Idling straight after must still go through the normal settle, not pick up a stale manual badge.
            ControlledSessions.SetActivity(SessionId, ControlledActivity.Idle);
            Assert.Equal(SessionStatus.Idle, Status(monitor));
        }
        finally
        {
            ControlledSessions.Unregister(SessionId);
        }
    }

    private static SessionStatus Status(SessionMonitor monitor) =>
        Assert.Single(monitor.Scan(), s => s.SessionId == SessionId).Status;

    public void Dispose()
    {
        ControlledSessions.Unregister(SessionId);
        Clock.Reset();
        try { File.Delete(_sessionFile); } catch { }
    }
}
