using Perch.Data.Control;
using Xunit;

namespace Perch.Tests;

/// <summary>
/// The <c>{sessionId}.perch-lock</c> ownership sidecar (docs/session-ui-plan.md, collision defence (b)).
/// Writes into the fixture config dir's <c>sessions/</c> under unique ids and cleans up after itself.
/// </summary>
public class SessionLockTests
{
    private static string FreshId() => "lock-test-" + Guid.NewGuid().ToString("N");

    [Fact]
    public void Acquire_WritesOurLock_AndReleaseRemovesIt()
    {
        var id = FreshId();
        try
        {
            Assert.True(SessionLock.Acquire(id, @"C:\proj"));
            Assert.True(File.Exists(SessionLock.PathFor(id)));

            var info = SessionLock.Read(id);
            Assert.NotNull(info);
            Assert.Equal(id, info!.SessionId);
            Assert.Equal(Environment.ProcessId, info.Pid);
            Assert.Equal(@"C:\proj", info.Cwd);
            Assert.True(info.IsOurs);
            Assert.True(info.IsLive);
            Assert.Null(SessionLock.HeldByOther(id));   // our own lock never blocks us

            // Re-acquiring our own lock is fine (a follow-up init with the same id).
            Assert.True(SessionLock.Acquire(id, @"C:\proj"));

            SessionLock.Release(id);
            Assert.False(File.Exists(SessionLock.PathFor(id)));
            Assert.Null(SessionLock.Read(id));
        }
        finally
        {
            try { File.Delete(SessionLock.PathFor(id)); } catch { }
        }
    }

    [Fact]
    public void StaleLock_IsTreatedAsAbsent_AndSwept()
    {
        var id = FreshId();
        try
        {
            // A PID no process can have: dead by construction.
            Directory.CreateDirectory(Path.GetDirectoryName(SessionLock.PathFor(id))!);
            File.WriteAllText(SessionLock.PathFor(id),
                """{"sessionId":"X","pid":"2147483646","cwd":"C:\\old","profile":"Perch","since":"2026-01-01T00:00:00Z"}""");

            var info = SessionLock.Read(id);
            Assert.NotNull(info);
            Assert.False(info!.IsLive);
            Assert.Null(SessionLock.HeldByOther(id));

            // A stale lock never blocks a new owner; Acquire overwrites it.
            Assert.True(SessionLock.Acquire(id, @"C:\new"));
            Assert.True(SessionLock.Read(id)!.IsOurs);
            SessionLock.Release(id);

            File.WriteAllText(SessionLock.PathFor(id), """{"sessionId":"X","pid":"2147483646"}""");
            Assert.True(SessionLock.SweepStale() >= 1);
            Assert.False(File.Exists(SessionLock.PathFor(id)));
        }
        finally
        {
            try { File.Delete(SessionLock.PathFor(id)); } catch { }
        }
    }

    [Fact]
    public void Read_ToleratesGarbage()
    {
        var id = FreshId();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SessionLock.PathFor(id))!);
            File.WriteAllText(SessionLock.PathFor(id), "not json");
            Assert.Null(SessionLock.Read(id));
            Assert.Null(SessionLock.HeldByOther(id));
            SessionLock.Release(id);   // unreadable → treated as ours/stale → deleted
            Assert.False(File.Exists(SessionLock.PathFor(id)));
        }
        finally
        {
            try { File.Delete(SessionLock.PathFor(id)); } catch { }
        }
    }

    [Fact]
    public void Acquire_RejectsEmptyId() => Assert.False(SessionLock.Acquire("", "C:\\x"));
}
