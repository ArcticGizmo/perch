using Perch.Data;
using Xunit;
using static Perch.Tests.ShutdownClockTests;

namespace Perch.Tests;

/// <summary>Perch's own ledger (docs/session-recovery-plan.md, D10): run stamps, recent shutdowns, held sessions.</summary>
public sealed class SessionLedgerTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 10, 2, 9, 0, 0, DateTimeKind.Local);
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"perch-ledger-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        try { File.Delete(_path); } catch { }
    }

    [Fact]
    public void SaveAndLoad_RoundTrips()
    {
        var ledger = new SessionLedger();
        ledger.BeginRun(Now, null);
        ledger.Heartbeat(Now.AddMinutes(1));
        ledger.MarkShutdown(Now.AddMinutes(2));
        ledger.Track(new LedgerSession("s1", @"C:\fixtures\proj", @"C:\cfg", "Feature work"));
        ledger.Save(_path);

        var back = SessionLedger.Load(_path);
        Assert.Equal(Now, back.Run.StartedAt);
        Assert.Equal(Now.AddMinutes(2), back.Run.ShutdownAt);
        Assert.Equal(Now.AddMinutes(2), back.Run.LastAlive);
        Assert.Equal(new LedgerSession("s1", @"C:\fixtures\proj", @"C:\cfg", "Feature work"), Assert.Single(back.Sessions));
    }

    [Fact]
    public void MissingOrCorruptFile_LoadsEmpty()
    {
        Assert.Empty(SessionLedger.Load(_path).Sessions);

        File.WriteAllText(_path, "{ not json");
        var ledger = SessionLedger.Load(_path);
        Assert.Empty(ledger.Sessions);
        Assert.Equal(default, ledger.Run.StartedAt);
    }

    [Fact]
    public void BeginRun_RecordsTheShutdownThatEndedThePreviousRun_AndResetsTheStamps()
    {
        var ledger = new SessionLedger();
        ledger.BeginRun(Now.AddHours(-10), null);
        ledger.Track(new LedgerSession("s1", "cwd", null, null));
        ledger.MarkShutdown(Now.AddHours(-1));

        var found = ledger.BeginRun(Now, null);

        Assert.Equal(Now.AddHours(-1), found);
        Assert.Equal([Now.AddHours(-1)], ledger.Shutdowns);
        Assert.Equal(Now, ledger.Run.StartedAt);
        Assert.Null(ledger.Run.ShutdownAt);
        Assert.Null(ledger.Run.CleanExitAt);
        Assert.Single(ledger.Sessions);   // held sessions carry over: they come back dormant
    }

    [Fact]
    public void BeginRun_DoesNotRecordTheSameShutdownTwice_AndPrunesOldOnes()
    {
        var shutdown = Now.AddHours(-1);
        var ledger = new SessionLedger { Shutdowns = [Now.AddDays(-4), shutdown.AddSeconds(-10)] };
        ledger.Run = new LedgerRun { StartedAt = Now.AddHours(-5), LastAlive = Now.AddHours(-2) };

        ledger.BeginRun(Now, new FakePower(true, shutdown));

        Assert.Equal([shutdown.AddSeconds(-10)], ledger.Shutdowns);
    }

    [Fact]
    public void CleanExit_ThenNoPowerHistory_IsNoShutdown()
    {
        var ledger = new SessionLedger();
        ledger.BeginRun(Now.AddHours(-5), null);
        ledger.MarkCleanExit(Now.AddHours(-4));

        Assert.Null(ledger.BeginRun(Now, null));
        Assert.Empty(ledger.Shutdowns);
    }

    [Fact]
    public void TrackForgetAndRebind()
    {
        var ledger = new SessionLedger();
        ledger.Track(new LedgerSession("a", "cwd", null, "first"));
        ledger.Track(new LedgerSession("b", "cwd", null, null));
        ledger.Track(new LedgerSession("a", "cwd", null, "renamed"));   // an update, not a second entry
        Assert.Equal(["a", "b"], ledger.Sessions.Select(s => s.SessionId));
        Assert.Equal("renamed", ledger.Sessions[0].Title);

        ledger.Rebind("a", "a2");   // /clear: same conversation slot, new id
        Assert.Equal(["a2", "b"], ledger.Sessions.Select(s => s.SessionId));
        Assert.Equal("renamed", ledger.Sessions[0].Title);

        ledger.Rebind("a2", "b");   // collides: the moved entry keeps its place, the stale one goes
        Assert.Equal(["b"], ledger.Sessions.Select(s => s.SessionId));
        Assert.Equal("renamed", ledger.Sessions[0].Title);

        ledger.Forget("b");
        Assert.Empty(ledger.HeldIds());
    }
}
