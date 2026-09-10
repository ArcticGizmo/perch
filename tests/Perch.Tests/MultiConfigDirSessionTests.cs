using Perch.Data;
using Perch.Data.Control;
using Perch.Platform;
using Xunit;

namespace Perch.Tests;

/// <summary>
/// Session discovery and the control paths across <em>several</em> config directories — the
/// regression being guarded: a session's <c>{pid}.json</c> lands under whichever config dir launched
/// it, and Perch listed only the one it resolved at start-up, taking the mode sidecar, the notify
/// marker, the lock and the terminate identity check with it. The write cases matter most, since a
/// sidecar written to the wrong config dir <em>succeeds</em> and does nothing.
/// </summary>
public class MultiConfigDirSessionTests : IDisposable
{
    // Pids no real process owns; an injected probe keeps them alive for the scan.
    private const string HubPid = "2147483645";
    private const string EnvPid = "2147483644";

    private readonly string _root;
    private readonly ClaudeConfigDir _hub;
    private readonly ClaudeConfigDir _env;
    private readonly string _hubSessionId = "hub-" + Guid.NewGuid().ToString("N");
    private readonly string _envSessionId = "env-" + Guid.NewGuid().ToString("N");

    private sealed class AlwaysAlive : IProcessProbe
    {
        public bool IsAlive(int pid) => true;
    }

    public MultiConfigDirSessionTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "perch-multi-" + Guid.NewGuid().ToString("N"));
        var hubRoot = Path.Combine(_root, ".claude");
        var envRoot = Path.Combine(_root, ".claude-envs", "envs", "inflight");
        Directory.CreateDirectory(Path.Combine(hubRoot, "sessions"));
        Directory.CreateDirectory(Path.Combine(envRoot, "sessions"));

        _hub = new ClaudeConfigDir(hubRoot, hubRoot, isHub: true);
        _env = new ClaudeConfigDir(envRoot, envRoot, "inflight", "InFlight",
            declaredOrg: "Redux InFlight");

        WriteSession(_hub, HubPid, _hubSessionId, @"C:\fixtures\proj");
        WriteSession(_env, EnvPid, _envSessionId, @"C:\fixtures\envproj");

        ClaudeConfigSet.SetForTesting([_hub, _env]);
    }

    public void Dispose()
    {
        ClaudeConfigSet.SetForTesting(null);
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    private static void WriteSession(
        ClaudeConfigDir dir, string pid, string sessionId, string cwd, long? startedAt = null)
    {
        Directory.CreateDirectory(dir.SessionsDir);
        var updatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var started = startedAt ?? updatedAt - 1000;
        File.WriteAllText(Path.Combine(dir.SessionsDir, $"{pid}.json"), $$"""
            { "pid": {{pid}}, "sessionId": "{{sessionId}}", "status": "idle",
              "cwd": "{{cwd.Replace("\\", "\\\\")}}", "startedAt": {{started}}, "updatedAt": {{updatedAt}} }
            """);
    }

    private static IReadOnlyList<ClaudeSession> Scan()
    {
        using var monitor = new SessionMonitor(new AlwaysAlive());
        return monitor.Scan();
    }

    [Fact]
    public void Scan_ListsSessionsFromEveryConfigDir()
    {
        var sessions = Scan();

        Assert.Contains(sessions, s => s.SessionId == _hubSessionId);
        Assert.Contains(sessions, s => s.SessionId == _envSessionId);
    }

    [Fact]
    public void Scan_TagsEachSessionWithTheConfigDirItWasFoundIn()
    {
        var sessions = Scan();

        var hubSession = Assert.Single(sessions, s => s.SessionId == _hubSessionId);
        var envSession = Assert.Single(sessions, s => s.SessionId == _envSessionId);

        Assert.Equal(_hub, hubSession.ConfigDir);
        Assert.Equal(_env, envSession.ConfigDir);
        Assert.Equal("InFlight", envSession.ConfigLabel);
        Assert.Equal("Redux InFlight", envSession.ConfigOrg);
        Assert.Equal(_env.SessionsDir, envSession.SessionsDir);
    }

    // ── which config dir is running a session ─────────────────────────────
    //
    // Where sessions/ is shared by link the sidecar's own directory attributes nothing, so the hook
    // records the config dir it ran under - visible only from inside the session - into
    // {sessionId}.configdir.

    [Fact]
    public void Scan_ReadsTheReportedConfigDirFromItsSidecar()
    {
        File.WriteAllText(Path.Combine(_env.SessionsDir, $"{_envSessionId}.configdir"), _env.Root);

        var session = Assert.Single(Scan(), s => s.SessionId == _envSessionId);

        Assert.Equal(_env.Root, session.ReportedConfigDir);
        Assert.Equal(_env, session.EnvDir);
    }

    [Fact]
    public void Scan_AttributesABareClaudeSessionToTheStockConfigDir()
    {
        // The reason for recording the dir rather than the slug: a bare `claude` sets no slug but does
        // have a config dir, and the organization is readable from the .claude.json there. Recording
        // only the slug threw that away and left the session unlabelled.
        File.WriteAllText(Path.Combine(_hub.SessionsDir, $"{_hubSessionId}.configdir"), _hub.Root);

        var session = Assert.Single(Scan(), s => s.SessionId == _hubSessionId);

        Assert.Equal(_hub, session.EnvDir);
    }

    [Fact]
    public void Scan_LeavesTheConfigDirUnreportedWithNoSidecar()
    {
        // Only sessions that started before the hook wrote the marker. Unknown stays unknown here;
        // whether the path can stand in for it is AttributedEnvDir's business, not this property's.
        var session = Assert.Single(Scan(), s => s.SessionId == _hubSessionId);

        Assert.Null(session.ReportedConfigDir);
        Assert.Null(session.EnvDir);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(@"C:\somewhere\that\is\not\a\config\dir")]
    public void Scan_IgnoresAMarkerThatNamesNoKnownConfigDir(string written)
    {
        // The body is compared against the known set and never opened, so an unrecognised one is
        // simply unknown rather than something to trust.
        File.WriteAllText(Path.Combine(_env.SessionsDir, $"{_envSessionId}.configdir"), written);

        var session = Assert.Single(Scan(), s => s.SessionId == _envSessionId);

        Assert.Null(session.EnvDir);
    }

    [Fact]
    public void EnvDisplay_NamesTheDirectoryWhenTwoOfThemReportTheSameOrganization()
    {
        // Reachable the moment the stock config is signed in to an org an environment also uses, which
        // is exactly what happened on the owner's machine. The organization alone would then read the
        // same on both rows.
        var twin = new ClaudeConfigDir(
            Path.Combine(_root, ".claude-envs", "envs", "twin"),
            Path.Combine(_root, ".claude-envs", "envs", "twin"),
            "twin", "Twin", declaredOrg: "Redux InFlight");
        ClaudeConfigSet.SetForTesting([_hub, _env, twin]);
        File.WriteAllText(Path.Combine(_env.SessionsDir, $"{_envSessionId}.configdir"), _env.Root);

        var session = Assert.Single(Scan(), s => s.SessionId == _envSessionId);

        Assert.Equal("InFlight · Redux InFlight", session.EnvDisplay);
    }

    [Fact]
    public void EnvDisplay_IsJustTheOrganizationWhenItIsUnambiguous()
    {
        File.WriteAllText(Path.Combine(_env.SessionsDir, $"{_envSessionId}.configdir"), _env.Root);

        var session = Assert.Single(Scan(), s => s.SessionId == _envSessionId);

        Assert.Equal("Redux InFlight", session.EnvDisplay);
    }

    [Fact]
    public void ForRoot_DoesNotFallBackToThePrimary()
    {
        // The whole point of the lookup: an unknown root is unknown. Falling back would attribute a
        // session to whichever dir happens to be first in the set.
        Assert.Equal(_env, ClaudeConfigSet.ForRoot(_env.Root));
        Assert.Equal(_hub, ClaudeConfigSet.ForRoot(_hub.Root));
        Assert.Null(ClaudeConfigSet.ForRoot(Path.Combine(_root, ".claude-envs", "envs", "removed")));
        Assert.Null(ClaudeConfigSet.ForRoot(null));
        Assert.Null(ClaudeConfigSet.ForRoot("  "));
    }



    [Fact]
    public void AttributedEnvDir_FallsBackToThePathWhenTheDirectoryIsNotShared()
    {
        // Every setup that shares nothing - a plain CLAUDE_CONFIG_DIR per environment - attributes
        // perfectly by path, and a session predating the hook reports nothing. Showing nothing there
        // would have been a regression on behaviour that already worked.
        var session = Assert.Single(Scan(), s => s.SessionId == _envSessionId);

        Assert.Null(session.ReportedConfigDir);
        Assert.Equal(_env, session.AttributedEnvDir);
        Assert.Equal("InFlight", session.EnvLabel);
    }

    [Fact]
    public void AttributedEnvDir_IsNullWhenTheSessionsDirectoryIsSharedAndNothingWasReported()
    {
        // Two config dirs, one physical sessions directory: the path attributes nothing, so with no
        // slug there is nothing honest to show.
        var alias = new ClaudeConfigDir(
            Path.Combine(_root, ".claude-envs", "envs", "alias"), _env.RealRoot + "-alias", "alias");
        Directory.CreateDirectory(alias.Root);
        if (!TryLinkDirectory(alias.SessionsDir, _env.SessionsDir))
            return;   // no link support on this host
        ClaudeConfigSet.SetForTesting([_hub, _env, alias]);

        var session = Assert.Single(Scan(), s => s.SessionId == _envSessionId);

        Assert.Null(session.ReportedConfigDir);
        Assert.Null(session.AttributedEnvDir);
        Assert.Null(session.EnvLabel);
    }

    [Fact]
    public void AttributedEnvDir_PrefersWhatTheSessionReported()
    {
        File.WriteAllText(Path.Combine(_env.SessionsDir, $"{_envSessionId}.configdir"), _env.Root);

        var session = Assert.Single(Scan(), s => s.SessionId == _envSessionId);

        Assert.Equal(_env, session.AttributedEnvDir);
    }

    [Fact]
    public void Scan_DoesNotDuplicateSessionsWhenConfigDirsResolveToTheSamePlace()
    {
        // Two records for one physical directory, as a junctioned config dir gives.
        var alias = new ClaudeConfigDir(
            Path.Combine(_root, ".claude-envs", "envs", "alias"), _env.RealRoot, "alias");
        ClaudeConfigSet.SetForTesting([_hub, _env, alias]);

        Assert.Single(Scan(), s => s.SessionId == _envSessionId);
    }

    [Fact]
    public void Scan_ListsSessionsOnceWhenConfigDirsShareOneSessionsDirectoryByLink()
    {
        // A scheme may share sessions/ by link the way it shares projects/ - the upstream claude-envs
        // manifest does exactly that. Each config dir then has its own path to one physical directory,
        // so enumerating per config dir would list every session once per dir.
        var linked = Path.Combine(_root, ".claude-envs", "envs", "linked");
        Directory.CreateDirectory(linked);
        if (!TryLinkDirectory(Path.Combine(linked, "sessions"), _hub.SessionsDir))
            return;   // no link support on this host; the injected-resolver case still covers the logic

        var shared = new ClaudeConfigDir(linked, linked, "linked");
        ClaudeConfigSet.SetForTesting([_hub, _env, shared]);

        Assert.Single(Scan(), s => s.SessionId == _hubSessionId);
    }

    // A directory link the platform allows without elevation: a junction on Windows (which needs
    // neither Developer Mode nor admin), a symlink elsewhere. False when neither works.
    private static bool TryLinkDirectory(string link, string target)
    {
        try
        {
            if (!OperatingSystem.IsWindows())
            {
                Directory.CreateSymbolicLink(link, target);
                return Directory.ResolveLinkTarget(link, returnFinalTarget: true) is not null;
            }

            var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("/c");
            psi.ArgumentList.Add("mklink");
            psi.ArgumentList.Add("/J");
            psi.ArgumentList.Add(link);
            psi.ArgumentList.Add(target);
            using var process = System.Diagnostics.Process.Start(psi);
            if (process is null) return false;
            process.WaitForExit(10_000);
            return process.ExitCode == 0
                && Directory.ResolveLinkTarget(link, returnFinalTarget: true) is not null;
        }
        catch
        {
            return false;
        }
    }

    [Fact]
    public void Scan_SkipsAConfigDirWithNoSessionsDirectory()
    {
        // An environment never signed into has no sessions/ yet. Must not throw or hide the others.
        var bare = Path.Combine(_root, ".claude-envs", "envs", "pdg");
        Directory.CreateDirectory(bare);
        ClaudeConfigSet.SetForTesting([_hub, _env, new ClaudeConfigDir(bare, bare, "pdg")]);

        var sessions = Scan();
        Assert.Contains(sessions, s => s.SessionId == _hubSessionId);
        Assert.Contains(sessions, s => s.SessionId == _envSessionId);
    }

    [Fact]
    public void Scan_ReadsTheModeSidecarFromTheOwningConfigDir()
    {
        File.WriteAllText(Path.Combine(_env.SessionsDir, $"{_envSessionId}.mode"), "acceptEdits");
        // A decoy under the hub, so this proves the read is scoped rather than finding *a* file.
        File.WriteAllText(Path.Combine(_hub.SessionsDir, $"{_envSessionId}.mode"), "plan");

        var envSession = Assert.Single(Scan(), s => s.SessionId == _envSessionId);
        Assert.Equal(PermissionMode.AcceptEdits, envSession.Mode);
    }

    [Fact]
    public void Scan_ReadsTheNotifyMarkerFromTheOwningConfigDir()
    {
        File.WriteAllText(Path.Combine(_env.SessionsDir, $"{_envSessionId}.notify"), _envSessionId);

        var sessions = Scan();
        Assert.True(Assert.Single(sessions, s => s.SessionId == _envSessionId).ExternalNotify);
        Assert.False(Assert.Single(sessions, s => s.SessionId == _hubSessionId).ExternalNotify);
    }

    [Fact]
    public void ToggleExternalNotify_WritesIntoTheOwningConfigDir()
    {
        using var monitor = new SessionMonitor(new AlwaysAlive());
        monitor.Scan();   // attribution comes from the scan

        Assert.True(monitor.ToggleExternalNotify(_envSessionId));

        var expected = Path.Combine(_env.SessionsDir, $"{_envSessionId}.notify");
        Assert.True(File.Exists(expected));
        Assert.False(File.Exists(Path.Combine(_hub.SessionsDir, $"{_envSessionId}.notify")));

        Assert.False(monitor.ToggleExternalNotify(_envSessionId));
        Assert.False(File.Exists(expected));
    }

    [Fact]
    public void SessionLock_IsWrittenAndReadInTheOwningConfigDir()
    {
        Assert.True(SessionLock.Acquire(_envSessionId, @"C:\fixtures\envproj", _env.SessionsDir));

        Assert.True(File.Exists(Path.Combine(_env.SessionsDir, _envSessionId + SessionLock.Extension)));
        Assert.False(File.Exists(Path.Combine(_hub.SessionsDir, _envSessionId + SessionLock.Extension)));

        Assert.NotNull(SessionLock.Read(_envSessionId, _env.SessionsDir));
        // The wrong dir finds nothing - why the monitor passes the session's own when deciding
        // whether it is Perch-controlled.
        Assert.Null(SessionLock.Read(_envSessionId, _hub.SessionsDir));

        SessionLock.Release(_envSessionId, _env.SessionsDir);
        Assert.False(File.Exists(Path.Combine(_env.SessionsDir, _envSessionId + SessionLock.Extension)));
    }

    [Fact]
    public void SweepStale_ClearsDeadLocksFromEveryConfigDir()
    {
        // A lock owned by a pid that cannot be alive, in each config dir.
        foreach (var dir in new[] { _hub, _env })
            File.WriteAllText(Path.Combine(dir.SessionsDir, "stale-" + dir.Label + SessionLock.Extension),
                """{ "sessionId": "stale", "pid": "2147483643", "cwd": "", "profile": "", "since": "" }""");

        SessionLock.SweepStale();

        foreach (var dir in new[] { _hub, _env })
            Assert.Empty(Directory.GetFiles(dir.SessionsDir, "*" + SessionLock.Extension));
    }

    [Fact]
    public void Terminate_LooksForTheIdentityFileInTheGivenConfigDir()
    {
        // Needs a genuinely live process: Terminate returns AlreadyGone for an unknown pid before it
        // ever reaches the identity check, so a fake pid cannot exercise this.
        using var child = StartSleeper();
        try
        {
            var pid = child.Id.ToString();
            WriteSession(_env, pid, "term-" + Guid.NewGuid().ToString("N"), @"C:\fixtures\envproj",
                startedAt: new DateTimeOffset(child.StartTime).ToUnixTimeMilliseconds());

            // Wrong config dir: no identity file, so the kill is refused rather than risked.
            Assert.Equal(TerminateResult.NotTheSession,
                SessionTerminator.Terminate(pid, _hub.SessionsDir));
            Assert.False(child.HasExited);

            // Owning config dir: identity checks out and it is killed.
            Assert.Equal(TerminateResult.Terminated,
                SessionTerminator.Terminate(pid, _env.SessionsDir));
            Assert.True(child.WaitForExit(10_000));
        }
        finally
        {
            try { if (!child.HasExited) child.Kill(entireProcessTree: true); } catch { }
        }
    }

    private static System.Diagnostics.Process StartSleeper()
    {
        var psi = OperatingSystem.IsWindows()
            ? new System.Diagnostics.ProcessStartInfo("cmd.exe", "/c timeout /t 30 /nobreak")
            : new System.Diagnostics.ProcessStartInfo("/bin/sleep", "30");
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        psi.RedirectStandardOutput = true;
        var process = System.Diagnostics.Process.Start(psi);
        Assert.NotNull(process);
        return process!;
    }

    [Fact]
    public void HistoryTrigger_IsConsumedFromEveryConfigDir()
    {
        File.WriteAllText(Path.Combine(_env.SessionsDir, $"{_envSessionId}.history"), "");

        using var monitor = new SessionMonitor(new AlwaysAlive());
        var opened = new List<string>();
        monitor.OpenHistoryRequested += id => opened.Add(id);
        monitor.Scan();

        Assert.Contains(_envSessionId, opened);
        // One-shot: deleted, so it cannot re-fire.
        Assert.False(File.Exists(Path.Combine(_env.SessionsDir, $"{_envSessionId}.history")));
    }
}
