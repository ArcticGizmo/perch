using System.Diagnostics;
using Perch.Data;
using Perch.Platform;
using Xunit;

namespace Perch.Tests;

/// <summary>
/// Review fixes CP25: a session file (or daemon roster entry) left behind by an unclean exit must not stay
/// "alive" just because some newer process inherited its pid. Liveness compares the live process's start time
/// with the recorded one, one-sidedly: only a process that started well <em>after</em> the record is a stranger.
/// </summary>
public sealed class ProcessIdentityTests : IDisposable
{
    private static readonly DateTime Recorded = new(2026, 9, 30, 9, 0, 0);

    [Theory]
    [InlineData(-3)]        // Claude writes startedAt a beat after the process starts
    [InlineData(0)]
    [InlineData(90)]        // within the tolerance
    [InlineData(-6 * 3600)] // older than the record: it holds the pid, so it wrote the record
    public void Same_process(int processStartOffsetSeconds) =>
        Assert.False(ProcessIdentity.IsRecycled(Recorded.AddSeconds(processStartOffsetSeconds), Recorded));

    [Theory]
    [InlineData(5 * 60)]
    [InlineData(3 * 3600)]
    public void Recycled_pid(int processStartOffsetSeconds) =>
        Assert.True(ProcessIdentity.IsRecycled(Recorded.AddSeconds(processStartOffsetSeconds), Recorded));

    [Fact]
    public void System_probe_checks_the_start_time_of_a_live_process()
    {
        // The test process itself: really alive, with a real start time.
        DateTime started;
        using (var self = Process.GetCurrentProcess()) started = self.StartTime;
        var probe = SystemProcessProbe.Instance;
        int pid = Environment.ProcessId;

        Assert.True(probe.IsAlive(pid));
        Assert.True(probe.IsAlive(pid, null));
        Assert.True(probe.IsAlive(pid, started.AddSeconds(2)));
        Assert.True(probe.IsAlive(pid, started.AddHours(5)));      // record newer than the process: same process
        Assert.False(probe.IsAlive(pid, started.AddHours(-1)));    // process newer than the record: recycled
        Assert.False(probe.IsAlive(int.MaxValue, started));        // no such process
    }

    // Through a real scan: a session file keyed by a live pid, but recorded as starting an hour before that
    // process did, is a leftover and is dropped. The same file with the right start is kept.
    private readonly string _sessionFile = Path.Combine(ClaudePaths.SessionsDir, $"{Environment.ProcessId}.json");

    private void WriteSession(string sessionId, DateTime startedAt)
    {
        Directory.CreateDirectory(ClaudePaths.SessionsDir);
        var ms = new DateTimeOffset(startedAt).ToUnixTimeMilliseconds();
        var updatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        File.WriteAllText(_sessionFile, $$"""
            { "pid": {{Environment.ProcessId}}, "sessionId": "{{sessionId}}", "startedAt": {{ms}},
              "status": "idle", "cwd": "C:\\fixtures\\proj", "updatedAt": {{updatedAt}} }
            """);
    }

    [Fact]
    public void Scan_drops_a_session_file_whose_pid_was_recycled()
    {
        DateTime started;
        using (var self = Process.GetCurrentProcess()) started = self.StartTime;

        WriteSession("cp25-recycled", started.AddHours(-1));
        using (var monitor = new SessionMonitor())
            Assert.DoesNotContain(monitor.Scan(), s => s.SessionId == "cp25-recycled");

        WriteSession("cp25-same", started.AddSeconds(2));
        using (var monitor = new SessionMonitor())
            Assert.Contains(monitor.Scan(), s => s.SessionId == "cp25-same");
    }

    public void Dispose()
    {
        try { File.Delete(_sessionFile); } catch { /* best-effort cleanup */ }
    }
}
