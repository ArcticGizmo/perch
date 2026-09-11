using Perch.Data;
using Perch.Data.Control;
using Perch.Platform;
using Xunit;

namespace Perch.Tests;

/// <summary>
/// Covers M2 of config-dir discovery: scanning and attributing sessions across the whole config-dir set.
/// The suite runs under a pinned <c>CLAUDE_CONFIG_DIR</c> (so discovery is hermetically collapsed), so
/// these drive the multi-dir case directly with <see cref="ClaudeConfigSet.SetForTesting"/> over two
/// throwaway config dirs and an always-alive process probe (a fixture pid owns no real process). Verifies
/// that sessions from every dir are listed and tagged with their owning <see cref="ClaudeConfigDir"/>,
/// that per-session writes land in the owning dir, that the lock sweep spans every dir, and that a
/// transcript in a non-primary tree is found.
/// </summary>
public sealed class MultiConfigDirSessionTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("perch-multidir-").FullName;

    private sealed class AlwaysAlive : IProcessProbe
    {
        public bool IsAlive(int pid) => true;
    }

    public void Dispose()
    {
        ClaudeConfigSet.ResetForTesting();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    // Creates a config dir with a sessions/ folder under the sandbox and returns it.
    private ClaudeConfigDir MakeDir(string name)
    {
        var root = Path.Combine(_root, name);
        Directory.CreateDirectory(Path.Combine(root, "sessions"));
        return new ClaudeConfigDir(root);
    }

    private static void SeedSession(ClaudeConfigDir dir, string pid, string sessionId, string cwd)
    {
        var updatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        File.WriteAllText(Path.Combine(dir.SessionsDir, $"{pid}.json"), $$"""
            { "pid": {{pid}}, "sessionId": "{{sessionId}}",
              "status": "idle", "cwd": {{System.Text.Json.JsonSerializer.Serialize(cwd)}},
              "updatedAt": {{updatedAt}} }
            """);
    }

    [Fact]
    public void Scan_ListsSessionsFromEveryDir_TaggedWithOwner()
    {
        var a = MakeDir("a");
        var b = MakeDir("b");
        SeedSession(a, "111111", "sa", @"C:\proj\a");
        SeedSession(b, "222222", "sb", @"C:\proj\b");
        ClaudeConfigSet.SetForTesting(new[] { a, b });

        using var monitor = new SessionMonitor(new AlwaysAlive());
        var sessions = monitor.Scan();

        var sa = Assert.Single(sessions, s => s.SessionId == "sa");
        var sb = Assert.Single(sessions, s => s.SessionId == "sb");
        Assert.Equal(a, sa.ConfigDir);
        Assert.Equal(b, sb.ConfigDir);
        Assert.Equal(b.SessionsDir, sb.SessionsDir);
        Assert.Equal(b.SessionsDir, monitor.SessionsDirFor("sb"));
        Assert.Equal(a.SessionsDir, monitor.SessionsDirFor("sa"));
    }

    [Fact]
    public void ToggleExternalNotify_ForNonPrimarySession_LandsInOwningDir_AndReadsBack()
    {
        var a = MakeDir("a");
        var b = MakeDir("b");
        SeedSession(b, "222222", "sb", @"C:\proj\b");
        ClaudeConfigSet.SetForTesting(new[] { a, b });

        using var monitor = new SessionMonitor(new AlwaysAlive());
        monitor.Scan(); // populates the owner map

        Assert.True(monitor.ToggleExternalNotify("sb"));

        Assert.True(File.Exists(Path.Combine(b.SessionsDir, "sb.notify")));
        Assert.False(File.Exists(Path.Combine(a.SessionsDir, "sb.notify")));

        var sessions = monitor.Scan();
        var sb = Assert.Single(sessions, s => s.SessionId == "sb");
        Assert.True(sb.ExternalNotify);
    }

    [Fact]
    public void SessionLock_ForNonPrimaryDir_WritesAndReadsBackInThatDir()
    {
        var a = MakeDir("a");
        var b = MakeDir("b");
        ClaudeConfigSet.SetForTesting(new[] { a, b });

        Assert.True(SessionLock.Acquire("sb", @"C:\proj\b", b.SessionsDir));

        Assert.True(File.Exists(Path.Combine(b.SessionsDir, "sb.perch-lock")));
        Assert.NotNull(SessionLock.Read("sb", b.SessionsDir));
        // The primary dir must not see the non-primary lock.
        Assert.Null(SessionLock.Read("sb", a.SessionsDir));

        SessionLock.Release("sb", b.SessionsDir);
        Assert.False(File.Exists(Path.Combine(b.SessionsDir, "sb.perch-lock")));
    }

    [Fact]
    public void SweepStale_CleansLocksInEveryDir()
    {
        var a = MakeDir("a");
        var b = MakeDir("b");
        ClaudeConfigSet.SetForTesting(new[] { a, b });

        // A lock owned by a dead pid in each dir (2147483647 owns no process).
        foreach (var dir in new[] { a, b })
            File.WriteAllText(Path.Combine(dir.SessionsDir, "dead.perch-lock"),
                """{ "sessionId": "dead", "pid": "2147483647", "cwd": "", "profile": "x", "since": "2020-01-01T00:00:00Z" }""");

        var removed = SessionLock.SweepStale();

        Assert.Equal(2, removed);
        Assert.False(File.Exists(Path.Combine(a.SessionsDir, "dead.perch-lock")));
        Assert.False(File.Exists(Path.Combine(b.SessionsDir, "dead.perch-lock")));
    }

    [Fact]
    public void TranscriptLocator_FindsTranscriptInNonPrimaryProjectsTree()
    {
        var a = MakeDir("a");
        var b = MakeDir("b");
        var cwd = @"C:\proj\b";
        var enc = TranscriptLocator.EncodeProjectDir(cwd);
        var projDir = Path.Combine(b.ProjectsDir, enc);
        Directory.CreateDirectory(projDir);
        var transcript = Path.Combine(projDir, "sb.jsonl");
        File.WriteAllText(transcript, "{}\n");

        ClaudeConfigSet.SetForTesting(new[] { a, b });

        Assert.Equal(transcript, TranscriptLocator.Resolve("sb", cwd));
        // And the by-scan fallback (no cwd) finds it too.
        Assert.Equal(transcript, TranscriptLocator.Resolve("sb", ""));
    }
}
