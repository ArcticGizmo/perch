using Perch.Data;
using Perch.Platform;
using Xunit;

namespace Perch.Tests;

/// <summary>
/// Covers M3 of config-dir discovery: attributing sessions correctly under a <b>shared</b>
/// <c>sessions/</c> — several config dirs junctioned onto one physical folder, so the folder alone can't
/// say which dir ran a session. The self-reported <c>{sessionId}.configdir</c> marker (written by the
/// hook) rescues that case. Junctions need elevation and won't exist in CI, so these inject a fake link
/// resolver (<see cref="ClaudeConfigSet.SetLinkResolverForTesting"/>) that maps two sessions dirs onto one
/// "real" path — the same technique the discovery tests use for dedup. The shared-sessions detection is
/// falsifiable: the last test shows that without sharing an unmarked session attributes to its owning dir,
/// so the detection is what makes the shared case resolve to nothing instead of guessing.
/// </summary>
public sealed class SharedSessionsAttributionTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("perch-shared-").FullName;

    private sealed class AlwaysAlive : IProcessProbe
    {
        public bool IsAlive(int pid) => true;
    }

    public void Dispose()
    {
        ClaudeConfigSet.ResetForTesting();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private ClaudeConfigDir MakeDir(string name)
    {
        var r = Path.Combine(_root, name);
        Directory.CreateDirectory(Path.Combine(r, "sessions"));
        return new ClaudeConfigDir(r);
    }

    // Maps both A/sessions and B/sessions onto one shared "real" path, so the set treats sessions/ as
    // junctioned across the two dirs.
    private static Func<string, string> ShareSessions(ClaudeConfigDir a, ClaudeConfigDir b, string shared) =>
        p => (ClaudeConfigDir.PathComparer.Equals(p, a.SessionsDir) ||
              ClaudeConfigDir.PathComparer.Equals(p, b.SessionsDir)) ? shared : p;

    [Fact]
    public void AttributedConfigDir_UsesReportedDir_UnderSharedSessions()
    {
        var a = MakeDir("a");
        var b = MakeDir("b");
        var shared = Path.Combine(_root, "shared", "sessions");
        ClaudeConfigSet.SetLinkResolverForTesting(ShareSessions(a, b, shared));
        ClaudeConfigSet.SetForTesting(new[] { a, b });

        // Both files sit in the one shared folder (ConfigDir == a for both), but each reports its own dir.
        var sa = new ClaudeSession("1", "sa", SessionStatus.Idle, @"C:\p\a", "a", DateTime.Now)
            { ConfigDir = a, ReportedConfigDir = a.Root };
        var sb = new ClaudeSession("2", "sb", SessionStatus.Idle, @"C:\p\b", "b", DateTime.Now)
            { ConfigDir = a, ReportedConfigDir = b.Root };
        var unmarked = new ClaudeSession("3", "sc", SessionStatus.Idle, @"C:\p\c", "c", DateTime.Now)
            { ConfigDir = a };

        Assert.Equal(a, sa.AttributedConfigDir);
        Assert.Equal(b, sb.AttributedConfigDir);          // reported marker rescues the shared case
        Assert.Null(unmarked.AttributedConfigDir);        // shared + no marker => don't guess
    }

    [Fact]
    public void Scan_ReadsMarker_AndAttributesTwoSessionsInOneSharedFolderCorrectly()
    {
        var a = MakeDir("a");
        var b = MakeDir("b");
        var shared = Path.Combine(_root, "shared", "sessions");
        ClaudeConfigSet.SetLinkResolverForTesting(ShareSessions(a, b, shared));

        // Physically, the deduped owner is A, so both sessions' files live in A/sessions with their markers.
        Seed(a, "111", "sa", @"C:\p\a", reportedDir: a.Root);
        Seed(a, "222", "sb", @"C:\p\b", reportedDir: b.Root);
        Seed(a, "333", "sc", @"C:\p\c", reportedDir: null); // no marker

        ClaudeConfigSet.SetForTesting(new[] { a, b });

        using var monitor = new SessionMonitor(new AlwaysAlive());
        var sessions = monitor.Scan();

        Assert.Equal(a, Assert.Single(sessions, s => s.SessionId == "sa").AttributedConfigDir);
        Assert.Equal(b, Assert.Single(sessions, s => s.SessionId == "sb").AttributedConfigDir);
        Assert.Null(Assert.Single(sessions, s => s.SessionId == "sc").AttributedConfigDir);
    }

    [Fact]
    public void WithoutSharing_UnmarkedSession_AttributesToOwningDir()
    {
        // Falsifiable counterpart: with sessions/ NOT shared, the unmarked session attributes to the dir
        // its sidecars sat in. So it's the shared-sessions detection alone that makes the shared, unmarked
        // case resolve to null above — stub that detection off and the assertion there would break.
        var a = MakeDir("a");
        var b = MakeDir("b");
        ClaudeConfigSet.SetForTesting(new[] { a, b });

        var unmarked = new ClaudeSession("3", "sc", SessionStatus.Idle, @"C:\p\c", "c", DateTime.Now)
            { ConfigDir = a };

        Assert.False(ClaudeConfigSet.Instance.SharesSessionsDir(a));
        Assert.Equal(a, unmarked.AttributedConfigDir);
    }

    private static void Seed(ClaudeConfigDir dir, string pid, string sessionId, string cwd, string? reportedDir)
    {
        var updatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        File.WriteAllText(Path.Combine(dir.SessionsDir, $"{pid}.json"), $$"""
            { "pid": {{pid}}, "sessionId": "{{sessionId}}",
              "status": "idle", "cwd": {{System.Text.Json.JsonSerializer.Serialize(cwd)}},
              "updatedAt": {{updatedAt}} }
            """);
        if (reportedDir is not null)
            File.WriteAllText(Path.Combine(dir.SessionsDir, $"{sessionId}.configdir"), reportedDir);
    }
}
