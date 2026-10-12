using Perch.Data;
using Xunit;

namespace Perch.Tests;

public class TranscriptSearchTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "perch-search-" + Guid.NewGuid().ToString("N"));

    public TranscriptSearchTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    // A small transcript in the CLI's on-disk shape: a typed prompt, a reply with thinking + a tool call, the tool's
    // output, a sub-agent's line, a slash-command echo, a malformed line, and a prompt with a Windows path and quotes.
    private static readonly string[] Sample =
    [
        """{"type":"user","uuid":"u1","timestamp":"2026-10-01T09:00:00Z","message":{"role":"user","content":"Why does the Flux capacitor leak memory?"}}""",
        """{"type":"assistant","uuid":"a1","timestamp":"2026-10-01T09:00:05Z","message":{"role":"assistant","content":[{"type":"thinking","thinking":"the flux buffer is never released"},{"type":"tool_use","id":"t1","name":"Grep","input":{"pattern":"FluxBuffer","path":"src"}}]}}""",
        """{"type":"user","uuid":"r1","timestamp":"2026-10-01T09:00:06Z","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"t1","content":"src/Flux.cs:12: var b = new FluxBuffer();"}]}}""",
        """{"type":"assistant","uuid":"a2","timestamp":"2026-10-01T09:00:09Z","message":{"role":"assistant","content":[{"type":"text","text":"The capacitor keeps\n\na reference to every frame."}]}}""",
        """{"type":"assistant","uuid":"s1","isSidechain":true,"message":{"role":"assistant","content":[{"type":"text","text":"sub-agent mentions the capacitor too"}]}}""",
        """{"type":"user","uuid":"c1","message":{"role":"user","content":"<command-name>/capacitor</command-name>"}}""",
        """{"type":"assistant","uuid":"broken","message":{"content":[{"type":"text","text":"capacitor""",
        """{"type":"user","uuid":"u2","timestamp":"2026-10-01T10:00:00Z","message":{"role":"user","content":"Open C:\\src\\perch and say \"hello there\""}}""",
    ];

    private string Write(params string[] lines)
    {
        var path = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".jsonl");
        File.WriteAllLines(path, lines);
        return path;
    }

    [Fact]
    public void FindsPromptsAndReplies_CaseInsensitive_InFileOrder()
    {
        var m = TranscriptSearch.SearchFile(Write(Sample), "CAPACITOR", includeTools: false, maxHits: 10);
        Assert.NotNull(m);
        Assert.Equal(2, m!.Count);
        Assert.Equal(["u1", "a2"], m.Hits.Select(h => h.Uuid));
        Assert.Equal([TranscriptHitKind.Prompt, TranscriptHitKind.Reply], m.Hits.Select(h => h.Kind));
        Assert.NotNull(m.Hits[0].Timestamp);
    }

    [Fact]
    public void SkipsSubAgentLines_SlashCommandEchoes_AndMalformedLines()
    {
        var m = TranscriptSearch.SearchFile(Write(Sample), "capacitor", includeTools: false, maxHits: 10)!;
        Assert.DoesNotContain(m.Hits, h => h.Uuid is "s1" or "c1" or "broken");
    }

    [Fact]
    public void ToolsAndThinking_OnlyWhenAsked()
    {
        var path = Write(Sample);
        Assert.Null(TranscriptSearch.SearchFile(path, "FluxBuffer", includeTools: false, maxHits: 10));

        var m = TranscriptSearch.SearchFile(path, "FluxBuffer", includeTools: true, maxHits: 10)!;
        Assert.Equal(["a1", "r1"], m.Hits.Select(h => h.Uuid));
        Assert.All(m.Hits, h => Assert.Equal(TranscriptHitKind.Tool, h.Kind));

        var thinking = TranscriptSearch.SearchFile(path, "never released", includeTools: true, maxHits: 10)!;
        Assert.Equal(TranscriptHitKind.Thinking, Assert.Single(thinking.Hits).Kind);
    }

    [Fact]
    public void ToolCall_MatchesItsInputValues_NotItsJson()
    {
        var m = TranscriptSearch.SearchFile(Write(Sample), "\"pattern\"", includeTools: true, maxHits: 10);
        Assert.Null(m);   // a JSON key is not text the viewer shows
    }

    [Fact]
    public void BackslashesAndQuotes_SurviveTheJsonEscaping()
    {
        var path = Write(Sample);
        Assert.Equal("u2", TranscriptSearch.SearchFile(path, @"C:\src\perch", false, 10)!.Hits.Single().Uuid);
        Assert.Equal("u2", TranscriptSearch.SearchFile(path, "say \"hello", false, 10)!.Hits.Single().Uuid);
    }

    [Fact]
    public void TooShortQuery_FindsNothing()
    {
        Assert.Null(TranscriptSearch.SearchFile(Write(Sample), " a ", false, 10));
    }

    [Fact]
    public void MaxHits_KeepsTheFirst_ButCountsOn()
    {
        var lines = Enumerable.Range(0, 5).Select(i =>
            $$$"""{"type":"user","uuid":"p{{{i}}}","message":{"role":"user","content":"needle {{{i}}}"}}""").ToArray();
        var m = TranscriptSearch.SearchFile(Write(lines), "needle", false, maxHits: 2)!;
        Assert.Equal(5, m.Count);
        Assert.False(m.Capped);
        Assert.Equal(["p0", "p1"], m.Hits.Select(h => h.Uuid));
    }

    [Fact]
    public void Count_StopsAtTheCap()
    {
        var lines = Enumerable.Range(0, TranscriptSearch.CountCap + 20).Select(i =>
            $$$"""{"type":"user","uuid":"p{{{i}}}","message":{"role":"user","content":"needle"}}""").ToArray();
        var m = TranscriptSearch.SearchFile(Write(lines), "needle", false, maxHits: 3)!;
        Assert.Equal(TranscriptSearch.CountCap, m.Count);
        Assert.True(m.Capped);
    }

    [Fact]
    public void MissingFile_IsNoMatch_NotAThrow()
    {
        Assert.Null(TranscriptSearch.SearchFile(Path.Combine(_dir, "nope.jsonl"), "anything", false, 10));
    }

    [Fact]
    public void SearchMany_ReportsEachMatchingFileByIndex()
    {
        var a = Write(Sample);
        var b = Write("""{"type":"user","uuid":"x","message":{"role":"user","content":"nothing here"}}""");
        var c = Write("""{"type":"user","uuid":"y","message":{"role":"user","content":"a capacitor again"}}""");
        var found = new System.Collections.Concurrent.ConcurrentDictionary<int, TranscriptMatches>();
        int scanned = 0;
        TranscriptSearch.SearchMany([a, b, c], "capacitor", false, 3,
            (i, m) => found[i] = m, () => Interlocked.Increment(ref scanned), CancellationToken.None);
        Assert.Equal([0, 2], found.Keys.Order());
        Assert.Equal(3, scanned);
    }

    [Fact]
    public void SearchMany_Cancelled_Throws()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() =>
            TranscriptSearch.SearchMany([Write(Sample)], "capacitor", false, 3, (_, _) => { }, null, cts.Token));
    }

    [Fact]
    public void Snippet_CollapsesWhitespace_AndMarksTheMatch()
    {
        var s = TranscriptSearch.Snippet("The capacitor keeps\n\na reference", 4, 9);
        Assert.Equal("The capacitor keeps a reference", s.Text);
        Assert.Equal("capacitor", s.Text.Substring(s.MatchStart, s.MatchLength));
    }

    [Fact]
    public void Snippet_ElidesBothEnds_KeepingTheMatchIntact()
    {
        var text = new string('x', 200) + " MATCH " + new string('y', 200);
        var s = TranscriptSearch.Snippet(text, 201, 5, radius: 10);
        Assert.StartsWith("…", s.Text);
        Assert.EndsWith("…", s.Text);
        Assert.Equal("MATCH", s.Text.Substring(s.MatchStart, s.MatchLength));
    }

    [Fact]
    public void RawNeedle_EscapesLikeTheJsonOnDisk()
    {
        Assert.Equal(@"C:\\src", TranscriptSearch.RawNeedle(@"C:\src"));
        Assert.Equal("say \\\"hi\\\"", TranscriptSearch.RawNeedle("say \"hi\""));
    }
}
