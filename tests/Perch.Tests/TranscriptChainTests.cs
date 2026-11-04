using Perch.Data;
using Perch.Data.Control;
using Xunit;

namespace Perch.Tests;

/// <summary>
/// The active-branch filter (docs/session-rewind-plan.md, R1). The tree shape mirrors a live capture against
/// claude 2.1.318: after a truncated resume the next prompt parents onto the kept entry, leaving the dropped turn
/// in the file on a dead branch.
/// </summary>
public class TranscriptChainTests
{
    private static string User(string uuid, string? parent, string text, bool sidechain = false) =>
        $$$"""{"type":"user","uuid":"{{{uuid}}}","parentUuid":{{{Json(parent)}}},"isSidechain":{{{(sidechain ? "true" : "false")}}},"message":{"role":"user","content":"{{{text}}}"}}""";

    private static string Assistant(string uuid, string parent, string text) =>
        $$$"""{"type":"assistant","uuid":"{{{uuid}}}","parentUuid":"{{{parent}}}","isSidechain":false,"message":{"role":"assistant","content":[{"type":"text","text":"{{{text}}}"}],"nested":{"uuid":"not-a-record"}""" + "}}";

    private static string Json(string? s) => s is null ? "null" : $"\"{s}\"";

    private const string Title = """{"type":"custom-title","customTitle":"t"}""";

    // p1 → a1 → p2 → a2 (dropped)  ·  a1 → p3 → a3 (kept)
    private static readonly string[] Rewound =
    [
        Title,
        User("p1", null, "first"),
        Assistant("a1", "p1", "one"),
        User("p2", "a1", "second"),
        Assistant("a2", "p2", "two"),
        User("p3", "a1", "third"),
        Assistant("a3", "p3", "three"),
    ];

    [Fact]
    public void ReadLink_ReadsTopLevelFieldsOnly()
    {
        var link = TranscriptChain.ReadLink(Assistant("a1", "p1", "x"));
        Assert.Equal("a1", link.Uuid);   // not the nested "not-a-record"
        Assert.Equal("p1", link.Parent);
        Assert.False(link.Sidechain);

        Assert.Null(TranscriptChain.ReadLink(User("p1", null, "x")).Parent);
        Assert.Equal(default, TranscriptChain.ReadLink(""));
        Assert.Equal("u", TranscriptChain.ReadLink("""{"uuid":"u","parentUuid":"p","message":{"content":"trunc""").Uuid);
    }

    [Fact]
    public void ActiveLines_DropsTheAbandonedBranch()
    {
        var kept = TranscriptChain.ActiveLines(Rewound);
        Assert.Equal([Title, Rewound[1], Rewound[2], Rewound[5], Rewound[6]], kept);
    }

    [Fact]
    public void ActiveLines_NamedLeaf_PicksABranchNotYetWrittenPast()
    {
        // Rewound to before p3 and not sent since: the kept entry a1 is mid-file.
        var kept = TranscriptChain.ActiveLines(Rewound, leafUuid: "a1");
        Assert.Equal([Title, Rewound[1], Rewound[2]], kept);

        // An unknown leaf falls back to the file's own.
        Assert.Equal(TranscriptChain.ActiveLines(Rewound), TranscriptChain.ActiveLines(Rewound, "nope"));
    }

    [Fact]
    public void ActiveLines_LinearFile_KeepsEverything()
    {
        string[] linear = [User("p1", null, "a"), Assistant("a1", "p1", "b"), User("p2", "a1", "c"), Assistant("a2", "p2", "d")];
        Assert.Equal(linear, TranscriptChain.ActiveLines(linear));
    }

    [Fact]
    public void ActiveLines_HopsACompactionBoundary()
    {
        string boundary = """{"type":"system","subtype":"compact_boundary","uuid":"cb","parentUuid":null,"logicalParentUuid":"a2","isSidechain":false}""";
        string[] lines =
        [
            User("p1", null, "a"), Assistant("a1", "p1", "b"),
            User("p2", "a1", "dropped"), Assistant("a2x", "p2", "dropped"),   // a dead branch before the compaction
            User("p2b", "a1", "kept"), Assistant("a2", "p2b", "kept"),
            boundary, User("p3", "cb", "after"),
        ];
        var kept = TranscriptChain.ActiveLines(lines);
        Assert.DoesNotContain(lines[2], kept);
        Assert.DoesNotContain(lines[3], kept);
        Assert.Equal(6, kept.Count);
    }

    [Fact]
    public void ActiveLines_BrokenOrClippedChain_KeepsEverythingBeforeTheGap()
    {
        // The head of the file was clipped: p2's parent isn't here. Nothing before the gap is dropped.
        string[] lines = [Assistant("a0", "gone", "x"), User("p2", "a0x", "y"), Assistant("a2", "p2", "z")];
        Assert.Equal(lines, TranscriptChain.ActiveLines(lines));
    }

    [Fact]
    public void ActiveLines_KeepsSidechainAndUnchainedRecords()
    {
        string side = User("s1", "a2", "sub-agent", sidechain: true);
        string[] lines = [.. Rewound[..5], side, .. Rewound[5..]];
        var kept = TranscriptChain.ActiveLines(lines);
        Assert.Contains(side, kept);
        Assert.Contains(Title, kept);
        Assert.DoesNotContain(Rewound[3], kept);
    }

    [Fact]
    public void ActiveLines_StreamingOverload_MatchesTheListOne()
    {
        Assert.Equal(TranscriptChain.ActiveLines(Rewound), TranscriptChain.ActiveLines(() => Rewound, null).ToList());
    }

    [Fact]
    public void RewindPoints_ReadsPromptsOnTheActiveChain()
    {
        var prompts = RewindPoints.Read(TranscriptChain.ActiveLines(Rewound));
        Assert.Equal(["p1", "p3"], prompts.Select(p => p.Uuid));
        Assert.Null(prompts[0].ParentUuid);
        Assert.Equal("a1", prompts[1].ParentUuid);
        Assert.Equal("third", prompts[1].Text);
    }

    [Fact]
    public void RewindPoints_Match_PairsByTextNewestFirst()
    {
        PromptRef[] transcript =
        [
            new("u1", null, "fix the bug"),
            new("u2", "x1", "from my phone"),   // sent over Remote Control: never in Perch's thread
            new("u3", "x2", "now add a test"),
        ];
        string[] thread = ["fix the bug", "/compact", "now add a test\r\n"];
        var matched = RewindPoints.Match(thread, transcript);
        Assert.Equal("u1", matched[0]?.Uuid);
        Assert.Null(matched[1]);
        Assert.Equal("u3", matched[2]?.Uuid);
    }

    [Fact]
    public void RewindPoints_Match_UnplaceablePromptStaysNull()
    {
        PromptRef[] transcript = [new("u1", null, "a")];
        var matched = RewindPoints.Match(["queued mid-turn", "a"], transcript);
        Assert.Null(matched[0]);
        Assert.Equal("u1", matched[1]?.Uuid);
    }
}
