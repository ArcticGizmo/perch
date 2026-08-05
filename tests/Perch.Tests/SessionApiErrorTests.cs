using Perch.Data;
using Perch.Platform;
using Xunit;

namespace Perch.Tests;

/// <summary>
/// Exercises the end-to-end API-error status through a real <see cref="SessionMonitor.Scan"/>: a session
/// file that reads idle (the state an API failure leaves behind) whose transcript tail is sitting on a
/// 529 must surface as <see cref="SessionStatus.ApiError"/> — not as a "done" completion — and raise the
/// <see cref="SessionMonitor.ApiError"/> event in place of <see cref="SessionMonitor.NeedsAttention"/>.
/// Uses the injectable probe so the recorded (dead) pid reads as alive, exactly like
/// <see cref="SessionMonitorProbeTests"/>.
/// </summary>
public class SessionApiErrorTests : IDisposable
{
    private const string DeadPid = "2147483646";
    // Must match a transcript fixture under projects/C--fixtures-proj/ so the reader can resolve it.
    private const string SessionId = "sessApiError";

    private readonly string _sessionFile =
        Path.Combine(ClaudePaths.SessionsDir, $"{DeadPid}.json");

    private sealed class AlwaysAlive : IProcessProbe
    {
        public bool IsAlive(int pid) => true;
    }

    public SessionApiErrorTests()
    {
        Directory.CreateDirectory(ClaudePaths.SessionsDir);
        var updatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        // "idle" is what Claude Code leaves the session at after the failed request stops the turn.
        File.WriteAllText(_sessionFile, $$"""
            { "pid": {{DeadPid}}, "sessionId": "{{SessionId}}",
              "status": "idle", "cwd": "C:\\fixtures\\proj", "updatedAt": {{updatedAt}} }
            """);
    }

    [Fact]
    public void Scan_TranscriptEndingOnApiError_SurfacesApiErrorStatusAndDetail()
    {
        using var monitor = new SessionMonitor(new AlwaysAlive());
        var session = Assert.Single(monitor.Scan(), s => s.SessionId == SessionId);

        Assert.Equal(SessionStatus.ApiError, session.Status);
        Assert.True(session.HasApiError);
        Assert.NotNull(session.ApiFailure);
        Assert.Equal(529, session.ApiFailure!.Status);
    }

    [Fact]
    public void Scan_ApiError_FiresApiErrorEventNotDone()
    {
        using var monitor = new SessionMonitor(new AlwaysAlive());
        ClaudeSession? apiErrored = null;
        var doneFired = false;
        monitor.ApiError += s => { if (s.SessionId == SessionId) apiErrored = s; };
        monitor.NeedsAttention += s => { if (s.SessionId == SessionId) doneFired = true; };

        monitor.Scan();

        Assert.NotNull(apiErrored);
        Assert.False(doneFired); // the failure replaces the "done" — it must never masquerade as success

        // Second scan with the transcript unchanged: the one-shot alert does not re-fire.
        apiErrored = null;
        monitor.Scan();
        Assert.Null(apiErrored);
    }

    public void Dispose()
    {
        try { File.Delete(_sessionFile); } catch { /* best-effort cleanup */ }
    }
}
