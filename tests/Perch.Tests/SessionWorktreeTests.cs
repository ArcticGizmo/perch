using System.Text.Json.Nodes;
using Perch.Data;
using Xunit;

namespace Perch.Tests;

/// <summary>
/// Guards the PR/Jira glyphs following the worktree a session is really working in. Claude Code's cwd stays on the
/// folder the session started in even after it runs <c>git worktree add ..\repo--feature</c> and edits there by
/// absolute path, so the worktree comes from a pin, else the session's newest write, matched against the repo's own
/// worktree list.
/// </summary>
public sealed class SessionWorktreeTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "perch-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_tmp, recursive: true); } catch { }
    }

    private string Root => Path.Combine(_tmp, "Safety");
    private string Sibling => Path.Combine(_tmp, "Safety--merge-queue-docs");
    private string Nested => Path.Combine(Root, ".claude", "worktrees", "pr-12");

    private GitWorktreeSet Set(params string[] linked) =>
        new(Root, Bare: false, MainBranch: "main", [.. linked.Select(p => new GitWorktree(p, "feature"))]);

    [Fact]
    public void ANewestWriteInASiblingWorktreeMovesTheLookupThere()
    {
        var pick = SessionWorktree.Pick(Root, null, [Path.Combine(Sibling, "CLAUDE.md")], Set(Sibling));
        Assert.Equal(new SessionWorktreeChoice(Sibling, Pinned: false), pick);
    }

    [Fact]
    public void ANewestWriteBackInTheSessionsOwnCheckoutKeepsTheCwd()
    {
        string[] writes = [Path.Combine(Root, "src", "a.cs"), Path.Combine(Sibling, "CLAUDE.md")];
        Assert.Null(SessionWorktree.Pick(Root, null, writes, Set(Sibling)));
    }

    [Fact]
    public void WritesOutsideTheRepoAreSkipped()
    {
        // Memory files and another repo are written after the worktree edit; neither belongs to this repo.
        string[] writes =
        [
            Path.Combine(_tmp, "home", ".claude", "projects", "x", "memory", "note.md"),
            Path.Combine(_tmp, "OtherRepo", "README.md"),
            Path.Combine(Sibling, "docs", "plan.md"),
        ];
        Assert.Equal(Sibling, SessionWorktree.Pick(Root, null, writes, Set(Sibling))?.Dir);
    }

    [Fact]
    public void ANestedWorktreeOwnsItsPathsOverTheCheckoutAroundIt()
    {
        var pick = SessionWorktree.Pick(Root, null, [Path.Combine(Nested, "x.cs")], Set(Nested, Sibling));
        Assert.Equal(Nested, pick?.Dir);
    }

    [Fact]
    public void APrefixThatIsNotAFolderBoundaryDoesNotMatch()
    {
        // "Safety--merge-queue-docs" starts with "Safety" but isn't inside it.
        var pick = SessionWorktree.Pick(Root, null, [Path.Combine(Sibling, "a.md")], Set(Sibling));
        Assert.Equal(Sibling, pick?.Dir);
        Assert.Null(SessionWorktree.Pick(Root, null, [Sibling + "x" + Path.DirectorySeparatorChar + "a.md"], Set(Sibling)));
    }

    [Fact]
    public void APinBeatsTheEditsAndCanPinTheMainCheckout()
    {
        string[] writes = [Path.Combine(Sibling, "a.md")];
        Assert.Equal(new SessionWorktreeChoice(Root, Pinned: true), SessionWorktree.Pick(Root, Root, writes, Set(Sibling)));
        Assert.Equal(new SessionWorktreeChoice(Sibling, Pinned: true), SessionWorktree.Pick(Root, Sibling, [], Set(Sibling)));
    }

    [Fact]
    public void APinToARemovedWorktreeFallsBackToTheEdits()
    {
        var gone = Path.Combine(_tmp, "Safety--gone");
        var pick = SessionWorktree.Pick(Root, gone, [Path.Combine(Sibling, "a.md")], Set(Sibling));
        Assert.Equal(new SessionWorktreeChoice(Sibling, Pinned: false), pick);
    }

    [Fact]
    public void ARepoWithNoLinkedWorktreesHasNothingToChoose() =>
        Assert.Null(SessionWorktree.Pick(Root, Root, [Path.Combine(Root, "a.md")], Set()));

    [Fact]
    public void TheTranscriptFoldKeepsSuccessfulWritesNewestFirst()
    {
        var a = Path.Combine(Root, "a.cs");
        var b = Path.Combine(Sibling, "CLAUDE.md");
        var failed = Path.Combine(_tmp, "elsewhere", "denied.md");
        var notebook = Path.Combine(Sibling, "n.ipynb");
        var transcript = Path.Combine(_tmp, "t.jsonl");
        Directory.CreateDirectory(_tmp);
        File.WriteAllLines(transcript,
        [
            ToolUse("tu1", "Write", "file_path", a),
            ToolUse("tu2", "Read", "file_path", Path.Combine(Root, "README.md")),     // a read isn't a write
            ToolUse("tu3", "Edit", "file_path", "relative/path.cs"),                  // not fully qualified
            ToolUse("tu4", "Edit", "file_path", b),
            ToolUse("tu5", "Edit", "file_path", failed),
            ErroredResult("tu5"),
            ToolUse("tu6", "NotebookEdit", "notebook_path", notebook),
            "{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"tool_use\"",   // partial trailing line
        ]);

        Assert.Equal([notebook, b, a], new SessionWorktree().WritesAt(transcript));
    }

    [Fact]
    public void WorktreesForListsTheMainCheckoutThenTheLinkedOnes()
    {
        var git = Path.Combine(Root, ".git");
        Directory.CreateDirectory(git);
        File.WriteAllText(Path.Combine(git, "HEAD"), "ref: refs/heads/main\n");
        File.WriteAllText(Path.Combine(git, "config"), "[core]\n\tbare = false\n");
        Assert.Null(SessionWorktree.WorktreesFor(Root));          // nothing to choose between yet

        var meta = Path.Combine(git, "worktrees", "Safety--merge-queue-docs");
        Directory.CreateDirectory(meta);
        Directory.CreateDirectory(Sibling);
        File.WriteAllText(Path.Combine(meta, "gitdir"), Path.Combine(Sibling, ".git") + "\n");
        File.WriteAllText(Path.Combine(meta, "HEAD"), "ref: refs/heads/NO-TICKET-merge-queue-docs\n");
        File.WriteAllText(Path.Combine(meta, "commondir"), "../..\n");
        File.WriteAllText(Path.Combine(Sibling, ".git"), $"gitdir: {meta}\n");

        Assert.Equal(
            [new GitWorktree(Root, "main"), new GitWorktree(Sibling, "NO-TICKET-merge-queue-docs")],
            SessionWorktree.WorktreesFor(Path.Combine(Root, "backend")));
    }

    private static string ToolUse(string id, string name, string key, string path) =>
        new JsonObject
        {
            ["type"] = "assistant",
            ["message"] = new JsonObject
            {
                ["content"] = new JsonArray(new JsonObject
                {
                    ["type"] = "tool_use", ["id"] = id, ["name"] = name,
                    ["input"] = new JsonObject { [key] = path },
                }),
            },
        }.ToJsonString();

    private static string ErroredResult(string id) =>
        new JsonObject
        {
            ["type"] = "user",
            ["message"] = new JsonObject
            {
                ["content"] = new JsonArray(new JsonObject
                {
                    ["type"] = "tool_result", ["tool_use_id"] = id, ["is_error"] = true, ["content"] = "denied",
                }),
            },
        }.ToJsonString();
}
