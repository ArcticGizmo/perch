using Perch.Data.Sprig;
using Xunit;

namespace Perch.Tests;

/// <summary>
/// Covers reading <c>sprig watch --json</c>, the stream behind the overlay's Sprig section, and the outcome of a sprig
/// action. The stream is another tool's contract, possibly from a newer sprig, so the interesting cases are the
/// tolerant ones: unknown types and fields, partial lines, and lines about workspaces we haven't seen.
/// </summary>
public class SprigStatusTests
{
    // Shaped like a live sprig 0.9.10 snapshot: one slot running two of its three apps, one paused, one free.
    private const string Snapshot = """
        {"type":"snapshot","schema":1,"infra":"not-counted","host":{"running":true,"pid":4120,"version":"0.9.10"},"workspaces":[{"key":"shop/1","orchestration":"shop","name":"1","label":"feat-cart","branch":"feat-cart","state":"running","paused":false,"folder":"C:\\src\\shop--1","dirs":["C:\\src\\shop--1"],"madeForAgent":"5f0c-session","apps":[{"name":"shop/api","state":"ready","port":5123,"since":"2026-11-18T10:02:11Z","restarts":0,"logFile":"C:\\logs\\api.log"},{"name":"shop/web","state":"building","doing":"installing packages","since":"2026-11-18T10:02:40Z","restarts":0},{"name":"shop/worker","state":"off"}]},{"key":"shop/2","orchestration":"shop","name":"2","label":"fix-login","branch":"fix-login","state":"paused","paused":true,"dirs":["C:\\src\\shop--2"],"apps":[{"name":"shop/api","state":"off"}]},{"key":"shop/3","orchestration":"shop","name":"3","label":"free","state":"free","paused":false,"dirs":[],"apps":[]}]}
        """;

    private static SprigStatusFold Folded()
    {
        var fold = new SprigStatusFold();
        Assert.True(fold.Apply(Snapshot));
        return fold;
    }

    [Fact]
    public void Snapshot_ReadsHostWorkspacesAndApps()
    {
        var s = Folded().Current;

        Assert.True(s.Seen);
        Assert.True(s.HostRunning);
        Assert.Equal("0.9.10", s.HostVersion);
        Assert.Equal(["shop/1", "shop/2", "shop/3"], s.Workspaces.Select(w => w.Key));

        var one = s.Find("shop/1")!;
        Assert.Equal(SprigWorkspaceState.Running, one.State);
        Assert.Equal("feat-cart", one.Label);
        Assert.Equal("5f0c-session", one.MadeForAgent);
        Assert.Equal([@"C:\src\shop--1"], one.Dirs);
        Assert.Equal(1, one.ReadyCount);
        Assert.Equal(2, one.ActiveCount);

        var api = one.Apps[0];
        Assert.Equal(5123, api.Port);
        Assert.Equal("api", api.ShortName);
        Assert.Equal(new DateTimeOffset(2026, 11, 18, 10, 2, 11, TimeSpan.Zero), api.Since);
        Assert.Equal("installing packages", one.Apps[1].Doing);
        Assert.True(one.Apps[2].IsOff);

        Assert.True(s.Find("shop/2")!.Paused);
        Assert.True(s.Find("shop/3")!.IsFree);
    }

    [Fact]
    public void Shown_RunningWithAppsStillComingUp_ReadsAsStarting()
    {
        var fold = Folded();
        var one = fold.Current.Find("shop/1")!;   // sprig says running; web is still building
        Assert.Equal(SprigWorkspaceState.Running, one.State);
        Assert.Equal(SprigWorkspaceState.Starting, one.Shown);

        fold.Apply("""{"type":"app","workspace":"shop/1","name":"shop/web","state":"ready","port":5124}""");
        Assert.Equal(SprigWorkspaceState.Running, fold.Current.Find("shop/1")!.Shown);   // worker is off by choice

        fold.Apply("""{"type":"app","workspace":"shop/1","name":"shop/web","state":"stopping"}""");
        Assert.Equal(SprigWorkspaceState.Running, fold.Current.Find("shop/1")!.Shown);   // going down isn't starting

        // A crash loop isn't a start: it's trouble (red), as sprig's own readiness counts it.
        fold.Apply("""{"type":"app","workspace":"shop/1","name":"shop/web","state":"restarting","restarts":4}""");
        var looping = fold.Current.Find("shop/1")!;
        Assert.Equal(SprigWorkspaceState.Running, looping.Shown);
        Assert.True(looping.InTrouble);
        Assert.Equal(["shop/web"], looping.Down);
    }

    // A workspace line from a sprig that sends readiness (its starting-state change).
    private const string WithReadiness = """
        {"type":"workspace","key":"shop/1","orchestration":"shop","name":"1","label":"feat-cart","state":"running","paused":false,"dirs":[],"readiness":{"health":"HEALTH","ready":1,"started":3,"total":4,"down":DOWN}}
        """;

    private static SprigWorkspace Readied(string health, string down = "[]")
    {
        var fold = Folded();
        Assert.True(fold.Apply(WithReadiness.Replace("HEALTH", health).Replace("DOWN", down)));
        return fold.Current.Find("shop/1")!;
    }

    [Fact]
    public void Readiness_IsRead_AndWinsOverTheAppList()
    {
        var w = Readied("starting");
        var r = Assert.IsType<SprigReadiness>(w.Readiness);
        Assert.Equal(("starting", 1, 3, 4), (r.Health, r.Ready, r.Started, r.Total));
        Assert.Empty(r.Down);
        Assert.Equal(SprigWorkspaceState.Starting, w.Shown);
        Assert.Equal((1, 3), w.ReadyOf);   // sprig's counts (jobs and apps stopped on purpose left out)

        // "up" from sprig wins even though the app lines we hold still have web building.
        var up = Readied("up");
        Assert.Equal(SprigWorkspaceState.Running, up.Shown);
        Assert.True(up.AllUp);
        Assert.False(up.InTrouble);
    }

    [Fact]
    public void Readiness_Trouble_IsRedWithWhatsDown()
    {
        var w = Readied("trouble", """["shop/api","shop/web"]""");
        Assert.True(w.InTrouble);
        Assert.Equal(["shop/api", "shop/web"], w.Down);
        Assert.Equal(SprigWorkspaceState.Running, w.Shown);
    }

    [Fact]
    public void Readiness_OnlyMattersWhileRunning()
    {
        var fold = Folded();
        fold.Apply(WithReadiness.Replace("\"running\"", "\"paused\"").Replace("HEALTH", "trouble").Replace("DOWN", "[]"));
        var w = fold.Current.Find("shop/1")!;
        Assert.Equal(SprigWorkspaceState.Paused, w.Shown);
        Assert.False(w.InTrouble);
    }

    // A snapshot as sprig 0.9.11 sends it (captured live, trimmed): readiness on every workspace, "none" while
    // nothing runs; and a stopped workspace whose apps all exited, which reports trouble but is shown stopped.
    private const string Sprig0911 = """
        {"type":"snapshot","schema":1,"infra":"not-counted","host":{"running":true,"pid":9,"version":"0.9.11"},"workspaces":[{"key":"Safety-Virtual/1","orchestration":"Safety-Virtual","name":"1","label":"NO-TICKET-injuries-v2-core","branch":"NO-TICKET-injuries-v2-core","state":"paused","paused":true,"folder":"C:\\git\\Safety-Virtual--1","dirs":["C:\\git\\Safety-Virtual--1"],"readiness":{"health":"none","ready":0,"started":0,"total":8,"down":[]},"apps":[{"name":"safety/client","state":"off"}]},{"key":"Safety-Virtual/3","orchestration":"Safety-Virtual","name":"3","label":"migration","state":"stopped","paused":false,"dirs":[],"readiness":{"health":"trouble","ready":0,"started":0,"total":8,"down":["safety/client"]},"apps":[{"name":"safety/client","state":"exited","exit":1}]},{"key":"Safety-Virtual/2","orchestration":"Safety-Virtual","name":"2","label":"onboarding","state":"running","paused":false,"dirs":[],"readiness":{"health":"starting","ready":0,"started":0,"total":8,"down":[]},"apps":[{"name":"safety/migrate","state":"running"}]}]}
        """;

    [Fact]
    public void Readiness_Sprig0911Snapshot_ReadsAsSprigShowsIt()
    {
        var fold = new SprigStatusFold();
        Assert.True(fold.Apply(Sprig0911));

        var paused = fold.Current.Find("Safety-Virtual/1")!;
        Assert.Equal(SprigReadiness.None, paused.Readiness!.Health);
        Assert.Equal(8, paused.Readiness.Total);
        Assert.Equal(SprigWorkspaceState.Paused, paused.Shown);

        var stopped = fold.Current.Find("Safety-Virtual/3")!;   // trouble, but nothing is going: shown stopped
        Assert.Equal(SprigWorkspaceState.Stopped, stopped.Shown);
        Assert.False(stopped.InTrouble);

        var jobs = fold.Current.Find("Safety-Virtual/2")!;      // only a job running so far
        Assert.Equal(SprigWorkspaceState.Starting, jobs.Shown);
        Assert.Equal((0, 0), jobs.ReadyOf);
    }

    [Fact]
    public void Readiness_Absent_FallsBackToTheApps()
    {
        var w = Folded().Current.Find("shop/1")!;   // the 0.9.10-shaped snapshot has no readiness
        Assert.Null(w.Readiness);
        Assert.Equal((1, 2), w.ReadyOf);
        Assert.False(w.AllUp);
    }

    [Fact]
    public void Notices_Ready_UsesSprigsReadinessWhenItHasIt()
    {
        var fold = Folded();
        var before = fold.Current;
        fold.Apply(WithReadiness.Replace("HEALTH", "up").Replace("DOWN", "[]"));
        var awaiting = new HashSet<string> { "shop/1" };

        var n = Assert.Single(SprigNotices.Diff(before, fold.Current, awaiting));
        Assert.Equal("feat-cart is ready", n.Title);

        Assert.Equal(SprigWorkspaceState.Paused, fold.Current.Find("shop/2")!.Shown);
    }

    [Fact]
    public void AppLine_ReplacesThatAppOnly()
    {
        var fold = Folded();

        Assert.True(fold.Apply("""{"type":"app","workspace":"shop/1","name":"shop/web","state":"ready","port":5124,"restarts":0}"""));

        var one = fold.Current.Find("shop/1")!;
        Assert.Equal(2, one.ReadyCount);
        Assert.Equal(5124, one.Apps[1].Port);
        Assert.Null(one.Apps[1].Doing);   // an app line carries every field, so what it leaves out is gone
        Assert.Equal("ready", one.Apps[0].State);
    }

    [Fact]
    public void AppLine_ForAnAppNotListedYet_IsAdded()
    {
        var fold = Folded();

        Assert.True(fold.Apply("""{"type":"app","workspace":"shop/2","name":"shop/jobs","state":"running"}"""));

        Assert.Equal(["shop/api", "shop/jobs"], fold.Current.Find("shop/2")!.Apps.Select(a => a.Name));
    }

    [Fact]
    public void WorkspaceLine_KeepsTheAppsWeHold()
    {
        var fold = Folded();

        Assert.True(fold.Apply("""{"type":"workspace","key":"shop/1","orchestration":"shop","name":"1","label":"feat-cart","branch":"feat-cart","state":"stopped","paused":true,"dirs":["C:\\src\\shop--1"]}"""));

        var one = fold.Current.Find("shop/1")!;
        Assert.Equal(SprigWorkspaceState.Stopped, one.State);
        Assert.True(one.Paused);
        Assert.Equal(3, one.Apps.Count);
    }

    [Fact]
    public void WorkspaceLine_ForANewWorkspace_IsAddedWithNoApps()
    {
        var fold = Folded();

        Assert.True(fold.Apply("""{"type":"workspace","key":"other/1","orchestration":"other","name":"1","label":"main-ish","state":"starting","paused":false,"dirs":[]}"""));

        var added = fold.Current.Find("other/1")!;
        Assert.Equal(SprigWorkspaceState.Starting, added.State);
        Assert.Empty(added.Apps);
    }

    [Fact]
    public void Removals_DropTheWorkspaceOrApp()
    {
        var fold = Folded();

        Assert.True(fold.Apply("""{"type":"app-removed","workspace":"shop/1","name":"shop/worker"}"""));
        Assert.True(fold.Apply("""{"type":"workspace-removed","key":"shop/3"}"""));

        Assert.Equal(2, fold.Current.Find("shop/1")!.Apps.Count);
        Assert.Null(fold.Current.Find("shop/3"));
        Assert.False(fold.Apply("""{"type":"workspace-removed","key":"shop/3"}"""));   // already gone
    }

    [Fact]
    public void HostLine_MarksTheHostGone_AndANewSnapshotBringsItBack()
    {
        var fold = Folded();

        Assert.True(fold.Apply("""{"type":"host","running":false}"""));
        Assert.False(fold.Current.HostRunning);
        Assert.Null(fold.Current.HostVersion);
        Assert.False(fold.Apply("""{"type":"host","running":false}"""));

        Assert.True(fold.Apply(Snapshot));
        Assert.True(fold.Current.HostRunning);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json")]
    [InlineData("""{"type":"snapshot","workspaces":[""")]          // cut off mid-line
    [InlineData("[1,2,3]")]
    [InlineData("""{"type":"usage-spike","cpu":99}""")]           // a type a newer sprig added
    [InlineData("""{"type":"app","workspace":"nope/9","name":"x","state":"ready"}""")]   // a workspace we never saw
    [InlineData("""{"type":"workspace"}""")]                       // no key
    public void UnusableLines_AreSkipped_AndChangeNothing(string line)
    {
        var fold = Folded();
        var before = fold.Current;

        Assert.False(fold.Apply(line));
        Assert.Same(before, fold.Current);
    }

    [Fact]
    public void UnknownFieldsAndStates_AreTolerated()
    {
        var fold = new SprigStatusFold();

        Assert.True(fold.Apply("""{"type":"snapshot","schema":2,"newThing":{"a":1},"host":{"running":false},"workspaces":[{"key":"x/1","state":"hibernating","label":"b","apps":[{"name":"x/a","state":"warming","port":"not a number"}]}]}"""));

        var w = fold.Current.Find("x/1")!;
        Assert.Equal(SprigWorkspaceState.Unknown, w.State);
        Assert.Equal("hibernating", w.StateText);
        Assert.Equal("x", w.Orchestration);   // inferred from the key when the field is missing
        Assert.Null(w.Apps[0].Port);
        Assert.False(fold.Current.HostRunning);
    }

    [Fact]
    public void Reset_ForgetsEverything()
    {
        var fold = Folded();
        fold.Reset();
        Assert.Same(SprigStatus.Empty, fold.Current);
    }

    // ── sessions ─────────────────────────────────────────────────────────────────────────────────────────────

    // Rooted for whichever OS runs the suite, since the matcher normalizes through Path.GetFullPath.
    private static readonly string Root = OperatingSystem.IsWindows() ? @"C:\src" : "/src";
    private static string P(params string[] parts) => Path.Combine([Root, .. parts]);

    private static SprigStatus Linked()
    {
        static SprigWorkspace Ws(string slot, SprigWorkspaceState state, string? agent, params string[] dirs)
            => new($"shop/{slot}", "shop", slot, $"b{slot}", $"b{slot}", state, "", false, null, null, dirs, agent, []);

        return new SprigStatus(true, true, null,
        [
            Ws("main", SprigWorkspaceState.Running, null, P("shop")),
            Ws("1", SprigWorkspaceState.Running, "agent-1", P("shop--1")),
            Ws("10", SprigWorkspaceState.Stopped, null, P("shop--10")),
            Ws("2", SprigWorkspaceState.Free, null, P("shop--2")),
            Ws("3", SprigWorkspaceState.Paused, null, P("shop--3", "api"), P("shop--3", "web")),
        ]);
    }

    [Fact]
    public void Link_MadeForAgent_WinsOverTheFolder()
    {
        var w = SprigSessionLink.Find(Linked(), "AGENT-1", P("shop--3", "api"), null);
        Assert.Equal("shop/1", w?.Key);
    }

    [Fact]
    public void Link_FolderInsideADir_OnASeparatorBoundary()
    {
        Assert.Equal("shop/1", SprigSessionLink.Find(Linked(), "s", P("shop--1", "src", "Api"), null)?.Key);
        Assert.Equal("shop/10", SprigSessionLink.Find(Linked(), "s", P("shop--10"), null)?.Key);   // not shop--1
        Assert.Equal("shop/main", SprigSessionLink.Find(Linked(), "s", P("shop", "docs"), null)?.Key);
        Assert.Equal("shop/3", SprigSessionLink.Find(Linked(), "s", P("shop--3", "web", "src"), null)?.Key);
    }

    [Fact]
    public void Link_TheWorktreeTheSessionMovedInto_WinsOverItsFolder()
    {
        var w = SprigSessionLink.Find(Linked(), "s", P("shop"), P("shop--10"));
        Assert.Equal("shop/10", w?.Key);
    }

    [Fact]
    public void Link_FreeSlotsAndElsewhere_DontMatch()
    {
        Assert.Null(SprigSessionLink.Find(Linked(), "s", P("shop--2", "api"), null));
        Assert.Null(SprigSessionLink.Find(Linked(), "s", P("elsewhere"), null));
        Assert.Null(SprigSessionLink.Find(Linked(), "s", null, null));
        Assert.Null(SprigSessionLink.Find(SprigStatus.Empty, "agent-1", P("shop--1"), null));
    }

    [Fact]
    public void Link_TrailingSeparatorsAndSlashes_AreIgnored()
    {
        var withSlash = P("shop--1") + Path.DirectorySeparatorChar;
        Assert.Equal("shop/1", SprigSessionLink.Find(Linked(), "s", withSlash, null)?.Key);
        Assert.Equal("shop/1", SprigSessionLink.Find(Linked(), "s", P("shop--1").Replace('\\', '/'), null)?.Key);
    }

    // ── notices ──────────────────────────────────────────────────────────────────────────────────────────────

    private static SprigStatus After(SprigStatusFold fold, params string[] lines)
    {
        foreach (var l in lines) fold.Apply(l);
        return fold.Current;
    }

    [Fact]
    public void Notices_NothingOnTheFirstSnapshot()
    {
        var awaiting = new HashSet<string> { "shop/1" };
        Assert.Empty(SprigNotices.Diff(SprigStatus.Empty, Folded().Current, awaiting));
    }

    [Fact]
    public void Notices_AnAppThatExits_IsOneProblem()
    {
        var fold = Folded();
        var before = fold.Current;
        var after = After(fold, """{"type":"app","workspace":"shop/1","name":"shop/api","state":"exited","exit":134,"restarts":0}""");

        var n = Assert.Single(SprigNotices.Diff(before, after, new HashSet<string>()));
        Assert.Equal("api exited · feat-cart", n.Title);
        Assert.Equal("exit code 134", n.Body);
        Assert.True(n.Problem);
        Assert.Equal("shop/1", n.WorkspaceKey);

        Assert.Empty(SprigNotices.Diff(after, after, new HashSet<string>()));   // only the change counts
    }

    [Fact]
    public void Notices_SeveralAppsAtOnce_AreOneNotice()
    {
        var fold = Folded();
        var before = fold.Current;
        var after = After(fold,
            """{"type":"app","workspace":"shop/1","name":"shop/api","state":"failed","error":"port 5123 in use"}""",
            """{"type":"app","workspace":"shop/1","name":"shop/web","state":"exited","exit":1}""");

        var n = Assert.Single(SprigNotices.Diff(before, after, new HashSet<string>()));
        Assert.Equal("2 apps stopped working · feat-cart", n.Title);
        Assert.Contains("api (port 5123 in use)", n.Body);
    }

    [Fact]
    public void Notices_StoppingOrTheHostGoing_IsNotAFailure()
    {
        var fold = Folded();
        var before = fold.Current;
        var after = After(fold,
            """{"type":"app","workspace":"shop/1","name":"shop/api","state":"stopped"}""",
            """{"type":"host","running":false}""",
            """{"type":"app","workspace":"shop/1","name":"shop/web","state":"off"}""");

        Assert.Empty(SprigNotices.Diff(before, after, new HashSet<string>()));
    }

    [Fact]
    public void Notices_AWorkspaceThatFails_SaysSoOnce()
    {
        var fold = Folded();
        var before = fold.Current;
        var after = After(fold, """{"type":"workspace","key":"shop/2","orchestration":"shop","name":"2","label":"fix-login","state":"failed","error":"docker didn't answer","dirs":[]}""");
        var awaiting = new HashSet<string> { "shop/2" };

        var n = Assert.Single(SprigNotices.Diff(before, after, awaiting));
        Assert.Equal("fix-login failed to start", n.Title);
        Assert.Equal("docker didn't answer", n.Body);
        Assert.Empty(awaiting);
    }

    [Fact]
    public void Notices_Ready_OnlyForWorkspacesStartedFromPerch()
    {
        var fold = Folded();
        var before = fold.Current;
        // shop/1 had api ready and web building; web becomes ready (worker is off, so 2 of 2 active).
        var after = After(fold, """{"type":"app","workspace":"shop/1","name":"shop/web","state":"ready","port":5124}""");

        Assert.Empty(SprigNotices.Diff(before, after, new HashSet<string>()));

        var awaiting = new HashSet<string> { "shop/1" };
        var n = Assert.Single(SprigNotices.Diff(before, after, awaiting));
        Assert.Equal("feat-cart is ready", n.Title);
        Assert.Equal("api :5123 · web :5124", n.Body);
        Assert.False(n.Problem);
        Assert.Empty(awaiting);
    }

    [Fact]
    public void Notices_StillStoppedRightAfterAStart_KeepsWaiting()
    {
        // Perch adds the key when it asks sprig to start; the next status can still read stopped/paused.
        var fold = Folded();
        var before = fold.Current;
        var awaiting = new HashSet<string> { "shop/2" };   // shop/2 is paused in the snapshot

        Assert.Empty(SprigNotices.Diff(before, before, awaiting));
        Assert.Contains("shop/2", awaiting);

        // Once it had got going and is stopped again, there's nothing to wait for.
        var going = After(fold, """{"type":"workspace","key":"shop/2","orchestration":"shop","name":"2","label":"fix-login","state":"starting","dirs":[]}""");
        SprigNotices.Diff(before, going, awaiting);
        var stopped = After(fold, """{"type":"workspace","key":"shop/2","orchestration":"shop","name":"2","label":"fix-login","state":"stopped","dirs":[]}""");
        SprigNotices.Diff(going, stopped, awaiting);
        Assert.Empty(awaiting);
    }

    [Fact]
    public void Notices_ARemovedWorkspace_LeavesTheWaitingSet()
    {
        var fold = Folded();
        var awaiting = new HashSet<string> { "shop/3" };
        var before = fold.Current;
        var after = After(fold, """{"type":"workspace-removed","key":"shop/3"}""");

        Assert.Empty(SprigNotices.Diff(before, after, awaiting));
        Assert.Empty(awaiting);
    }

    // ── actions ──────────────────────────────────────────────────────────────────────────────────────────────

    private static SprigWorkspace Workspace() => Folded().Current.Find("shop/1")!;

    [Fact]
    public void ActionArgs_NameTheWorkspace_AndNeverWaitForStarts()
    {
        Assert.Equal(["start", "shop", "1", "--json", "--no-wait"], SprigCli.ActionArgs(SprigAction.Start, Workspace(), null));
        Assert.Equal(["restart", "shop", "1", "shop/api", "--json", "--no-wait"], SprigCli.ActionArgs(SprigAction.Restart, Workspace(), "shop/api"));
        Assert.Equal(["stop", "shop", "1", "shop/api", "--json"], SprigCli.ActionArgs(SprigAction.Stop, Workspace(), "shop/api"));
        Assert.Equal(["pause", "shop", "1", "--json"], SprigCli.ActionArgs(SprigAction.Pause, Workspace(), null));
        Assert.Equal(["resume", "shop", "1", "--json", "--no-wait"], SprigCli.ActionArgs(SprigAction.Resume, Workspace(), null));
    }

    [Fact]
    public void ActionArgs_PauseAndResume_IgnoreAnApp()
    {
        Assert.Equal(["pause", "shop", "1", "--json"], SprigCli.ActionArgs(SprigAction.Pause, Workspace(), "shop/api"));
    }

    [Fact]
    public void Parse_ExitZero_IsOk()
    {
        var r = SprigCli.Parse(0, """{"ok":true,"apps":["shop/api"]}""", "");
        Assert.True(r.Ok);
    }

    [Fact]
    public void Parse_Failure_ReadsTheLastJsonObject()
    {
        var stdout = """
            {"event":"progress","text":"making worktrees"}
            {"ok":false,"code":"untrusted","error":"sprig.yaml isn't approved","untrusted":["C:\\src\\sprig.yaml"]}
            """;
        var r = SprigCli.Parse(1, stdout, "");

        Assert.False(r.Ok);
        Assert.Equal("untrusted", r.Code);
        Assert.Equal("sprig.yaml isn't approved", r.Error);
        Assert.True(r.NeedsSprig);
    }

    [Fact]
    public void Parse_FailureWithoutJson_FallsBackToStderr()
    {
        var r = SprigCli.Parse(1, "", "error: shop has no workspace named '9'\n");

        Assert.False(r.Ok);
        Assert.Equal("error", r.Code);
        Assert.Equal("shop has no workspace named '9'", r.Error);
        Assert.False(r.NeedsSprig);
    }

    [Fact]
    public void Parse_DockerUnavailable_IsItsOwnCode()
    {
        var r = SprigCli.Parse(1, """{"ok":false,"code":"docker-unavailable","error":"Docker isn't running"}""", "");
        Assert.Equal("docker-unavailable", r.Code);
        Assert.False(r.NeedsSprig);
    }
}
