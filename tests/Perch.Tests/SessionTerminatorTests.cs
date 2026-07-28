using System.Diagnostics;
using System.Text.Json.Nodes;
using Perch.Data;
using Xunit;

namespace Perch.Tests;

/// <summary>
/// Covers <see cref="SessionTerminator"/> — in particular its PID-identity guard, which is the whole
/// reason the terminator isn't a bare <c>Process.Kill</c>. Session files are keyed by PID and outlive an
/// unclean exit, so a stale <c>{pid}.json</c> whose PID has since been recycled must never get an
/// unrelated process killed on the user's behalf. These tests spawn a real throwaway process and assert
/// it <em>survives</em> the cases that should be refused.
/// </summary>
public class SessionTerminatorTests
{
    [Theory]
    [InlineData("")]
    [InlineData("not-a-pid")]
    [InlineData("0")]
    [InlineData("-1")]
    public void Terminate_WithUnusablePid_IsRefused(string pid) =>
        Assert.Equal(TerminateResult.NotTheSession, SessionTerminator.Terminate(pid));

    // No session file at all means identity can't be established, so the kill is refused rather than
    // risked — the guard's default. The spawned process must still be running afterwards.
    [Fact]
    public void Terminate_WithNoSessionFile_IsRefusedAndLeavesProcessAlive()
    {
        using var victim = StartLongRunningProcess();
        try
        {
            Assert.Equal(TerminateResult.NotTheSession,
                SessionTerminator.Terminate(victim.Id.ToString()));
            Assert.False(victim.HasExited);
        }
        finally
        {
            KillQuietly(victim);
        }
    }

    // The PID-reuse case the guard exists for: a session file left behind by a long-dead session whose PID
    // has been handed to something else. The recorded startedAt is nowhere near the live process's start
    // time, so the terminator refuses and the process lives.
    [Fact]
    public void Terminate_WithStaleStartedAt_IsRefusedAndLeavesProcessAlive()
    {
        using var victim = StartLongRunningProcess();
        var sessionFile = WriteSessionFile(victim.Id, DateTime.Now.AddHours(-9));
        try
        {
            Assert.Equal(TerminateResult.NotTheSession,
                SessionTerminator.Terminate(victim.Id.ToString()));
            Assert.False(victim.HasExited);
        }
        finally
        {
            KillQuietly(victim);
            Delete(sessionFile);
        }
    }

    // The happy path: a session file whose startedAt matches the live process, so the kill goes ahead and
    // the process really dies.
    [Fact]
    public void Terminate_WithMatchingSessionFile_KillsTheProcess()
    {
        using var victim = StartLongRunningProcess();
        var sessionFile = WriteSessionFile(victim.Id, victim.StartTime);
        try
        {
            Assert.Equal(TerminateResult.Terminated,
                SessionTerminator.Terminate(victim.Id.ToString()));
            Assert.True(victim.WaitForExit(10_000), "the terminated process should have exited");
        }
        finally
        {
            KillQuietly(victim);
            Delete(sessionFile);
        }
    }

    // A session that exited between the scan and the click reports AlreadyGone, not a failure — there is
    // nothing wrong, the row is simply stale and the next scan drops it.
    [Fact]
    public void Terminate_WithDeadPid_IsAlreadyGone()
    {
        var victim = StartLongRunningProcess();
        int pid = victim.Id;
        var sessionFile = WriteSessionFile(pid, victim.StartTime);
        try
        {
            victim.Kill(entireProcessTree: true);
            Assert.True(victim.WaitForExit(10_000));
            victim.Dispose();

            Assert.Equal(TerminateResult.AlreadyGone, SessionTerminator.Terminate(pid.ToString()));
        }
        finally
        {
            Delete(sessionFile);
        }
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    // A process that stays up long enough to be probed but needs no stdin (so it can't block on a console)
    // and dies on its own if a test aborts before cleaning up.
    private static Process StartLongRunningProcess()
    {
        var psi = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("cmd.exe", "/c ping -n 60 127.0.0.1")
            : new ProcessStartInfo("/bin/sleep", "60");
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        psi.RedirectStandardOutput = true;

        var process = Process.Start(psi);
        Assert.NotNull(process);
        return process!;
    }

    // Writes the minimal {pid}.json the terminator's guard reads — only startedAt matters to it.
    private static string WriteSessionFile(int pid, DateTime startedAt)
    {
        Directory.CreateDirectory(ClaudePaths.SessionsDir);
        var path = Path.Combine(ClaudePaths.SessionsDir, $"{pid}.json");
        var payload = new JsonObject
        {
            ["pid"] = pid,
            ["sessionId"] = "terminator-test-" + Guid.NewGuid().ToString("N"),
            ["startedAt"] = new DateTimeOffset(startedAt).ToUnixTimeMilliseconds(),
            ["status"] = "idle",
        };
        File.WriteAllText(path, payload.ToJsonString());
        return path;
    }

    private static void KillQuietly(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
    }

    private static void Delete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
