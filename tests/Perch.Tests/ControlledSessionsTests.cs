using Perch.Data;
using Perch.Data.Control;
using Perch.Platform;
using Xunit;

namespace Perch.Tests;

/// <summary>
/// The in-process <see cref="ControlledSessions"/> registry that tracks the sessions this Perch instance
/// drives over stream-json, plus the live status it now carries and the <see cref="SessionMonitor"/> status
/// override that reads it. The CLI leaves a controlled session's on-disk <c>status</c> pinned at <c>"idle"</c>,
/// so without the override the overlay reads every controlled session as inactive.
/// </summary>
public class ControlledSessionsTests
{
    // A pid no real process owns, so the session file's own liveness wouldn't matter (an injected probe keeps
    // it alive for the scan).
    private const string Pid = "2147483646";

    private static string FreshId() => "controlled-test-" + Guid.NewGuid().ToString("N");

    private sealed class AlwaysAlive : IProcessProbe
    {
        public bool IsAlive(int pid) => true;
    }

    private static void WriteSessionFile(string sessionId)
    {
        Directory.CreateDirectory(ClaudePaths.SessionsDir);
        var updatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        File.WriteAllText(Path.Combine(ClaudePaths.SessionsDir, $"{Pid}.json"), $$"""
            { "pid": {{Pid}}, "sessionId": "{{sessionId}}",
              "status": "idle", "cwd": "C:\\fixtures\\proj", "updatedAt": {{updatedAt}} }
            """);
    }

    private static void Cleanup(string sessionId)
    {
        ControlledSessions.Unregister(sessionId);
        try { File.Delete(Path.Combine(ClaudePaths.SessionsDir, $"{Pid}.json")); } catch { }
    }

    [Fact]
    public void Changed_FiresOnRealTransitions_NotOnNoOps()
    {
        var id = FreshId();
        int fires = 0;
        void OnChanged() => fires++;
        ControlledSessions.Changed += OnChanged;
        try
        {
            ControlledSessions.Register(id);                             // membership change → fires
            Assert.Equal(1, fires);

            ControlledSessions.SetActivity(id, ControlledActivity.Idle); // already idle → no-op, no fire
            Assert.Equal(1, fires);

            ControlledSessions.SetActivity(id, ControlledActivity.Busy); // real change → fires
            Assert.Equal(2, fires);

            ControlledSessions.SetActivity(id, ControlledActivity.Busy); // same again → no fire
            Assert.Equal(2, fires);

            ControlledSessions.Unregister(id);                           // membership change → fires
            Assert.Equal(3, fires);

            ControlledSessions.Unregister(id);                           // already gone → no fire
            Assert.Equal(3, fires);
        }
        finally
        {
            ControlledSessions.Changed -= OnChanged;
            ControlledSessions.Unregister(id);
        }
    }

    [Fact]
    public void Register_Set_Unregister_TracksActivity()
    {
        var id = FreshId();
        try
        {
            Assert.False(ControlledSessions.Owns(id));
            Assert.Null(ControlledSessions.Activity(id));

            ControlledSessions.Register(id);
            Assert.True(ControlledSessions.Owns(id));
            Assert.Equal(ControlledActivity.Idle, ControlledSessions.Activity(id));   // seeded idle

            ControlledSessions.SetActivity(id, ControlledActivity.Busy);
            Assert.Equal(ControlledActivity.Busy, ControlledSessions.Activity(id));

            ControlledSessions.Unregister(id);
            Assert.False(ControlledSessions.Owns(id));
            Assert.Null(ControlledSessions.Activity(id));

            // A status update after Unregister must not resurrect the session.
            ControlledSessions.SetActivity(id, ControlledActivity.Busy);
            Assert.Null(ControlledSessions.Activity(id));
        }
        finally { ControlledSessions.Unregister(id); }
    }

    // Parameters are strings, not ControlledActivity, because a public xUnit method can't expose the internal
    // enum (CS0051); mapped to it in the body.
    [Theory]
    [InlineData("busy", SessionStatus.Running)]
    [InlineData("waiting", SessionStatus.AwaitingInput)]
    [InlineData("idle", SessionStatus.Idle)]
    public void Scan_OwnedSession_UsesRegistryStatus(string activity, SessionStatus expected)
    {
        var id = FreshId();
        try
        {
            // The CLI's on-disk status is stuck at "idle"; we own the session and report its real status.
            WriteSessionFile(id);
            ControlledSessions.Register(id);
            ControlledSessions.SetActivity(id, activity switch
            {
                "busy" => ControlledActivity.Busy,
                "waiting" => ControlledActivity.Waiting,
                _ => ControlledActivity.Idle,
            });

            using var monitor = new SessionMonitor(new AlwaysAlive());
            var session = Assert.Single(monitor.Scan(), s => s.SessionId == id);

            Assert.True(session.IsPerchControlled);
            Assert.Equal(expected, session.Status);
        }
        finally { Cleanup(id); }
    }

    [Fact]
    public void Scan_UnownedSession_KeepsCliStatus()
    {
        var id = FreshId();
        try
        {
            // Not in the registry (no live Perch lock either): the CLI's "idle" stands and the session is not
            // marked Perch-controlled.
            WriteSessionFile(id);

            using var monitor = new SessionMonitor(new AlwaysAlive());
            var session = Assert.Single(monitor.Scan(), s => s.SessionId == id);

            Assert.False(session.IsPerchControlled);
            Assert.Equal(SessionStatus.Idle, session.Status);
        }
        finally { Cleanup(id); }
    }
}
