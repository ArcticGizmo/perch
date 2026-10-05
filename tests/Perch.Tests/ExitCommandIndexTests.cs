using Perch.Data;
using Xunit;

namespace Perch.Tests;

/// <summary>The <c>/exit</c> index over <c>history.jsonl</c> (docs/session-recovery-plan.md, R1): which sessions the
/// user deliberately exited, read incrementally from an append-only file.</summary>
public sealed class ExitCommandIndexTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"perch-history-{Guid.NewGuid():N}.jsonl");

    public void Dispose()
    {
        try { File.Delete(_path); } catch { }
    }

    private static string Entry(string display, long ms, string? sessionId) =>
        sessionId is null
            ? $$"""{"display":"{{display}}","pastedContents":{},"timestamp":{{ms}},"project":"C:\\fixtures\\proj"}"""
            : $$"""{"display":"{{display}}","pastedContents":{},"timestamp":{{ms}},"project":"C:\\fixtures\\proj","sessionId":"{{sessionId}}"}""";

    private static DateTime At(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms).LocalDateTime;

    private void Append(params string[] lines) => File.AppendAllText(_path, string.Concat(lines.Select(l => l + "\n")));

    [Fact]
    public void IndexesExitAndQuit_PerSession_NewestWins()
    {
        Append(
            Entry("hi", 1_000, "s1"),
            Entry("/exit", 2_000, "s1"),
            Entry("/exit", 9_000, "s1"),
            Entry("/quit", 3_000, "s2"),
            Entry(" /EXIT ", 4_000, "s3"));
        var index = new ExitCommandIndex(_path);
        index.Refresh();

        Assert.Equal(At(9_000), index.LastExit("s1"));
        Assert.Equal(At(3_000), index.LastExit("s2"));
        Assert.Equal(At(4_000), index.LastExit("s3"));
    }

    [Fact]
    public void IgnoresPromptsThatMerelyMentionExit_AndEntriesWithoutASession()
    {
        Append(
            Entry("don't /exit yet", 1_000, "s1"),
            Entry("/exit", 2_000, null),            // older Claude Code: no sessionId
            "not json at all /exit",
            Entry("/exit-plan", 3_000, "s2"));
        var index = new ExitCommandIndex(_path);
        index.Refresh();

        Assert.Null(index.LastExit("s1"));
        Assert.Null(index.LastExit("s2"));
    }

    [Fact]
    public void Refresh_ReadsOnlyWhatWasAppended()
    {
        Append(Entry("/exit", 1_000, "s1"));
        var index = new ExitCommandIndex(_path);
        index.Refresh();
        Assert.Equal(At(1_000), index.LastExit("s1"));

        Append(Entry("/exit", 5_000, "s2"));
        index.Refresh();
        Assert.Equal(At(1_000), index.LastExit("s1"));
        Assert.Equal(At(5_000), index.LastExit("s2"));
    }

    [Fact]
    public void ALineStillBeingWritten_IsPickedUpOnceComplete()
    {
        var line = Entry("/exit", 7_000, "s1");
        File.WriteAllText(_path, line[..20]);   // half a line, no newline
        var index = new ExitCommandIndex(_path);
        index.Refresh();
        Assert.Null(index.LastExit("s1"));

        File.AppendAllText(_path, line[20..] + "\n");
        index.Refresh();
        Assert.Equal(At(7_000), index.LastExit("s1"));
    }

    [Fact]
    public void AReplacedFile_IsReadAgainFromTheStart()
    {
        Append(Entry("/exit", 1_000, "s1"), Entry("hi", 2_000, "s1"));
        var index = new ExitCommandIndex(_path);
        index.Refresh();
        Assert.NotNull(index.LastExit("s1"));

        File.WriteAllText(_path, Entry("/exit", 3_000, "s2") + "\n");   // shorter: a new file
        index.Refresh();
        Assert.Null(index.LastExit("s1"));
        Assert.Equal(At(3_000), index.LastExit("s2"));
    }

    [Fact]
    public void MissingFile_IsHarmless()
    {
        var index = new ExitCommandIndex(_path + ".absent");
        index.Refresh();
        Assert.Null(index.LastExit("s1"));
    }
}
