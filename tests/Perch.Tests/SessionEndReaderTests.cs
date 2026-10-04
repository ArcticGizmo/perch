using Perch.Data;
using Xunit;

namespace Perch.Tests;

/// <summary>How a finished session ended, from its transcript tail (docs/session-recovery-plan.md, R1). The fixtures
/// mirror what Claude Code 2.1.x wrote in the spike: a graceful exit ends in the <c>cost-state</c> flush; a kill
/// leaves the last turn's records with nothing after them.</summary>
public class SessionEndReaderTests
{
    private static string Fixture(string name) =>
        Path.Combine(AppContext.BaseDirectory, "fixtures", "session-end", name);

    private static DateTime Local(string iso) => DateTimeOffset.Parse(iso).LocalDateTime;

    [Fact]
    public void GracefulExit_IsClosed_EndingAtTheExitFlush()
    {
        var path = Fixture("graceful.jsonl");
        var end = SessionEndReader.Read(path, lastExitCommand: null);

        Assert.Equal(SessionEndKind.Closed, end.Kind);
        // The exit flush is the file's last write, so that's the end (cost-state's own start+duration isn't usable).
        Assert.Equal(File.GetLastWriteTime(path), end.EndedAt);
    }

    [Fact]
    public void GracefulExit_WithExitTypedAfterTheLastActivity_IsExited()
    {
        var path = Fixture("graceful.jsonl");
        var end = SessionEndReader.Read(path, Local("2026-10-01T05:00:10Z"));

        Assert.Equal(SessionEndKind.Exited, end.Kind);
        Assert.Equal(File.GetLastWriteTime(path), end.EndedAt);
    }

    [Fact]
    public void KilledMidTurn_IsAbrupt_EndingAtTheLastActivity()
    {
        var end = SessionEndReader.Read(Fixture("killed-mid-turn.jsonl"), lastExitCommand: null);

        Assert.Equal(SessionEndKind.Abrupt, end.Kind);
        Assert.Equal(Local("2026-10-01T05:10:07Z"), end.EndedAt);
    }

    [Fact]
    public void ExitFlushMidFile_ThenMoreTurns_IsAbrupt()
    {
        // It exited cleanly once, was resumed, then killed: only the tail decides.
        var end = SessionEndReader.Read(Fixture("resumed-then-killed.jsonl"), lastExitCommand: null);

        Assert.Equal(SessionEndKind.Abrupt, end.Kind);
        Assert.Equal(Local("2026-10-01T06:00:05Z"), end.EndedAt);
    }

    [Fact]
    public void ExitTypedBeforeALaterResume_DoesNotCount()
    {
        // An /exit from the first run (05:00:10) predates the resumed turns (06:00), so it isn't this ending.
        var end = SessionEndReader.Read(Fixture("resumed-then-killed.jsonl"), Local("2026-10-01T05:00:10Z"));

        Assert.Equal(SessionEndKind.Abrupt, end.Kind);
    }

    [Fact]
    public void TornLastLine_IsAbrupt_EvenAfterAnExitFlush()
    {
        var end = SessionEndReader.Read(Fixture("torn-tail.jsonl"), lastExitCommand: null);

        Assert.Equal(SessionEndKind.Abrupt, end.Kind);
        Assert.Equal(Local("2026-10-01T07:00:00Z"), end.EndedAt);
    }

    [Fact]
    public void ATranscriptWithMessages_HasConversation()
    {
        Assert.True(SessionEndReader.Read(Fixture("graceful.jsonl"), null).HasConversation);
        Assert.True(SessionEndReader.Read(Fixture("torn-tail.jsonl"), null).HasConversation);
    }

    [Fact]
    public void BookkeepingOnly_HasNoConversation()
    {
        // Started and closed without a prompt: the exit flush and bookkeeping, but no user or assistant message.
        var end = SessionEndReader.Read(Fixture("no-messages.jsonl"), null);

        Assert.Equal(SessionEndKind.Closed, end.Kind);
        Assert.False(end.HasConversation);
    }

    [Fact]
    public void MissingOrEmptyTranscript_IsUnknown()
    {
        Assert.Equal(SessionEnd.Unknown, SessionEndReader.Read(Fixture("does-not-exist.jsonl"), null));

        var empty = Path.Combine(Path.GetTempPath(), $"perch-end-{Guid.NewGuid():N}.jsonl");
        File.WriteAllText(empty, "");
        try { Assert.Equal(SessionEnd.Unknown, SessionEndReader.Read(empty, null)); }
        finally { File.Delete(empty); }
    }

    [Fact]
    public void HugeLastRecord_GrowsTheWindow_ToFindIt()
    {
        // A last record bigger than the first 64KB window (a large tool result just before a kill) must still be seen.
        var path = Path.Combine(Path.GetTempPath(), $"perch-end-{Guid.NewGuid():N}.jsonl");
        var big = new string('x', 200 * 1024);
        File.WriteAllLines(path,
        [
            """{"type":"user","message":{"role":"user","content":"hi"},"timestamp":"2026-10-01T08:00:00.000Z"}""",
            """{"type":"cost-state","totalDuration":1,"startTime":1}""",
            $$"""{"type":"user","message":{"role":"user","content":"{{big}}"},"timestamp":"2026-10-01T08:30:00.000Z"}""",
        ]);
        try
        {
            var end = SessionEndReader.Read(path, null);
            Assert.Equal(SessionEndKind.Abrupt, end.Kind);
            Assert.Equal(Local("2026-10-01T08:30:00Z"), end.EndedAt);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void UntimestampedBookkeepingAfterTheFlush_StillCountsAsClean()
    {
        // last-prompt / mode records carry no timestamp; they mustn't turn a clean exit into an abrupt one.
        var tail = SessionEndReader.Scan(
        [
            """{"type":"user","timestamp":"2026-10-01T09:00:00.000Z"}""",
            """{"type":"cost-state","totalDuration":1,"startTime":1}""",
            """{"type":"last-prompt","lastPrompt":"hi"}""",
            "",
        ]);

        Assert.True(tail.CleanExit);
        Assert.Equal(Local("2026-10-01T09:00:00Z"), tail.LastActivity);
    }
}
