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
