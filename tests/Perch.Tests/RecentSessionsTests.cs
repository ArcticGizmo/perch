using Perch.Data;
using Xunit;

namespace Perch.Tests;

/// <summary>The Recent list (docs/session-recovery-plan.md, Q1/Q2/Q4), over a throwaway config dir built from the
/// session-end fixtures.</summary>
public sealed class RecentSessionsTests : IDisposable
{
    // Local times, with the fixtures' timestamps (2026-10-01) inside the 3-day window.
    private static readonly DateTime Now = DateTimeOffset.Parse("2026-10-02T12:00:00Z").LocalDateTime;
    private static readonly DateTime GracefulEnd = DateTimeOffset.Parse("2026-10-02T10:00:00Z").LocalDateTime;
    private static readonly DateTime KilledEnd = DateTimeOffset.Parse("2026-10-01T05:10:07Z").LocalDateTime;

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"perch-recent-{Guid.NewGuid():N}");
    private readonly string _projectDir;
    private static readonly IReadOnlySet<string> None = new HashSet<string>();
    private static readonly IReadOnlyDictionary<string, DateTime> NoDismissals = new Dictionary<string, DateTime>();

    public RecentSessionsTests()
    {
        _projectDir = Path.Combine(_root, "projects", "C--fixtures-proj");
        Directory.CreateDirectory(_projectDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    // Copies a session-end fixture in as {id}.jsonl, last written at `lastWrite`, and returns its history entry.
    private HistoryEntry Transcript(string fixture, string id, DateTime lastWrite, bool active = false)
    {
        var path = Path.Combine(_projectDir, id + ".jsonl");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "fixtures", "session-end", fixture), path);
        File.SetLastWriteTime(path, lastWrite);
        return new HistoryEntry(id, "proj", TestEnvironment.FixtureCwd, path, lastWrite, active);
    }

    [Fact]
    public void FlaggedFirst_ThenNewestFirst()
    {
        var graceful = Transcript("graceful.jsonl", "g", GracefulEnd);
        var killed = Transcript("killed-mid-turn.jsonl", "k", KilledEnd.AddSeconds(1));
        var older = Transcript("graceful.jsonl", "g-old", GracefulEnd.AddHours(-3));

        var rows = new RecentSessions().Build([older, graceful, killed], None, NoDismissals, [], Now);

        Assert.Equal(["k", "g", "g-old"], rows.Select(r => r.Entry.SessionId));
        Assert.Equal(SessionEndKind.Abrupt, rows[0].End.Kind);
        Assert.True(rows[0].IsFlagged);
        Assert.Equal(KilledEnd, rows[0].EndedAt);
        Assert.Equal(SessionEndKind.Closed, rows[1].End.Kind);
        Assert.False(rows[1].IsFlagged);
    }

    [Fact]
    public void LeavesOutRunning_HeldByPerch_AndOldSessions()
    {
        var running = Transcript("graceful.jsonl", "live", GracefulEnd, active: true);
        var held = Transcript("graceful.jsonl", "mine", GracefulEnd);
        var old = Transcript("graceful.jsonl", "old", Now.AddDays(-4));
        var kept = Transcript("graceful.jsonl", "kept", GracefulEnd);

        var rows = new RecentSessions().Build(
            [running, held, old, kept, HistoryEntry.Placeholder], new HashSet<string> { "mine" }, NoDismissals, [], Now);

        Assert.Equal(["kept"], rows.Select(r => r.Entry.SessionId));
    }

    [Fact]
    public void ADismissedEnding_IsHidden_UntilTheSessionEndsAgain()
    {
        var graceful = Transcript("graceful.jsonl", "g", GracefulEnd);
        var recent = new RecentSessions();

        Assert.Empty(recent.Build([graceful], None, new Dictionary<string, DateTime> { ["g"] = GracefulEnd }, [], Now));
        // Dismissed an earlier ending; it was resumed and has ended again since.
        Assert.Single(recent.Build([graceful], None, new Dictionary<string, DateTime> { ["g"] = GracefulEnd.AddHours(-1) }, [], Now));
    }

    [Fact]
    public void EndingJustBeforeAShutdown_IsFlaggedAndSortedFirst()
    {
        var graceful = Transcript("graceful.jsonl", "g", GracefulEnd);
        var later = Transcript("graceful.jsonl", "later", GracefulEnd.AddMinutes(30));

        var rows = new RecentSessions().Build([later, graceful], None, NoDismissals, [GracefulEnd.AddMinutes(5)], Now);

        Assert.Equal(["g", "later"], rows.Select(r => r.Entry.SessionId));
        Assert.True(rows[0].JustBeforeShutdown);
        Assert.True(rows[0].IsFlagged);
        Assert.False(rows[1].JustBeforeShutdown);
    }

    [Fact]
    public void AnExitInTheConfigDirsPromptHistory_MakesItExited()
    {
        var graceful = Transcript("graceful.jsonl", "g", GracefulEnd);
        var exitAt = DateTimeOffset.Parse("2026-10-01T05:00:10Z").ToUnixTimeMilliseconds();   // after its last activity
        File.WriteAllText(Path.Combine(_root, "history.jsonl"),
            $$"""{"display":"/exit","pastedContents":{},"timestamp":{{exitAt}},"project":"C:\\fixtures\\proj","sessionId":"g"}""" + "\n");

        var row = Assert.Single(new RecentSessions().Build([graceful], None, NoDismissals, [], Now));

        Assert.Equal(SessionEndKind.Exited, row.End.Kind);
    }
}
