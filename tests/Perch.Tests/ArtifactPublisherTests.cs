using Perch.Data;
using Xunit;

namespace Perch.Tests;

public class ArtifactPublisherTests
{
    // The shape `claude --bg` prints (the separator is a middle dot).
    [Theory]
    [InlineData("Starting background service…\nbackgrounded · 715e4caa\n  claude agents   list sessions", "715e4caa")]
    [InlineData("backgrounded · d81d0ee7 (idle — send a prompt to start)", "d81d0ee7")]
    [InlineData("error: not logged in", null)]
    [InlineData("", null)]
    public void Background_id_comes_from_the_backgrounded_line(string output, string? id) =>
        Assert.Equal(id, ArtifactPublisher.ParseBackgroundId(output));

    private const string Agents = """
        [
          { "pid": 71200, "cwd": "C:\\work", "kind": "interactive", "sessionId": "a30ccded-624a-4e78-aba8-44b5502581b2", "status": "busy" },
          { "id": "715e4caa", "cwd": "C:\\work", "kind": "background", "sessionId": "715e4caa-15c4-4dba-9e2a-748afb73bcf8", "state": "done" }
        ]
        """;

    [Fact]
    public void Agent_listing_maps_the_short_id_to_its_session_and_state()
    {
        var a = ArtifactPublisher.ParseAgent(Agents, "715e4caa");
        Assert.NotNull(a);
        Assert.Equal("715e4caa-15c4-4dba-9e2a-748afb73bcf8", a!.Value.SessionId);
        Assert.Equal("done", a.Value.State);
        Assert.Equal(@"C:\work", a.Value.Cwd);
    }

    [Theory]
    [InlineData("deadbeef")]
    public void Agent_listing_without_the_id_is_null(string id) =>
        Assert.Null(ArtifactPublisher.ParseAgent(Agents, id));

    [Theory]
    [InlineData("not json at all")]
    [InlineData("[{\"id\": \"715e4caa\"")]   // truncated
    public void Agent_listing_tolerates_garbage(string json) =>
        Assert.Null(ArtifactPublisher.ParseAgent(json, "715e4caa"));

    // Trimmed from a real background-session transcript: the Artifact tool's user/tool_result record.
    private static string ResultLine(string url, string path, bool updated = false) =>
        "{\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":[{\"tool_use_id\":\"toolu_1\",\"type\":\"tool_result\"," +
        "\"content\":\"Published at " + url + "\"}]},\"toolUseResult\":{\"url\":\"" + url + "\",\"path\":" +
        System.Text.Json.JsonSerializer.Serialize(path) + ",\"artifact_id\":\"84ec5d1f\",\"title\":\"Perch artifact test\"," +
        "\"updated\":" + (updated ? "true" : "false") + ",\"audience\":\"owner\"},\"timestamp\":\"2026-10-14T01:00:00Z\"}";

    // The documented opt-out (code.claude.com/docs/en/agent-view, "How file edits are isolated").
    [Fact]
    public void Publishing_sessions_opt_out_of_worktree_isolation()
    {
        using var doc = System.Text.Json.JsonDocument.Parse(ArtifactPublisher.NoWorktreeSettingsJson);
        Assert.Equal("none", doc.RootElement.GetProperty("worktree").GetProperty("bgIsolation").GetString());
    }

    [Fact]
    public void Artifact_result_record_parses()
    {
        var a = ArtifactPublisher.ParseRecord(ResultLine("https://claude.ai/artifact/HR1Ujn", @"C:\work\artefacts\t.html", updated: true));
        Assert.NotNull(a);
        Assert.Equal("https://claude.ai/artifact/HR1Ujn", a!.Url);
        Assert.Equal(@"C:\work\artefacts\t.html", a.Path);
        Assert.Equal("Perch artifact test", a.Title);
        Assert.True(a.Updated);
    }

    [Theory]
    [InlineData("{\"toolUseResult\":{\"url\":\"https://evil.example/claude.ai/artifact/x\"}}")]   // not claude.ai
    [InlineData("{\"toolUseResult\":{\"matches\":[\"Artifact\"],\"query\":\"select:Artifact\"}}")]
    [InlineData("{\"toolUseResult\":{\"url\":\"https://claude.ai/artifact/x\"")]                // partial trailing line
    [InlineData("")]
    public void Other_records_are_not_artifacts(string line) =>
        Assert.Null(ArtifactPublisher.ParseRecord(line));

    [Fact]
    public void Transcript_scan_returns_the_last_publish_of_that_file()
    {
        var dir = Directory.CreateTempSubdirectory("perch-artifact-").FullName;
        try
        {
            var page = Path.Combine(dir, "page.html");
            var other = Path.Combine(dir, "other.html");
            var transcript = Path.Combine(dir, "t.jsonl");
            File.WriteAllLines(transcript,
            [
                "{\"type\":\"user\",\"message\":{\"content\":\"publish it\"}}",
                ResultLine("https://claude.ai/artifact/first", page),
                ResultLine("https://claude.ai/artifact/someone-else", other),
                ResultLine("https://claude.ai/artifact/second", page),
                "{\"toolUseResult\":{\"url\":\"https://claude.ai/arti",   // still being written
            ]);
            Assert.Equal("https://claude.ai/artifact/second", ArtifactPublisher.FindPublished(transcript, page)?.Url);
            Assert.Null(ArtifactPublisher.FindPublished(Path.Combine(dir, "missing.jsonl"), page));

            // A session moved into a worktree published its own copy: still this publish's result.
            var copy = Path.Combine(dir, ".claude", "worktrees", "w", "page.html");
            File.WriteAllLines(transcript, [ResultLine("https://claude.ai/artifact/from-worktree", copy)]);
            Assert.Equal("https://claude.ai/artifact/from-worktree", ArtifactPublisher.FindPublished(transcript, page)?.Url);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}

public class PerchSessionPluginTests
{
    [Fact]
    public void Plugin_is_written_with_its_manifest_and_skill()
    {
        var dir = Path.Combine(Directory.CreateTempSubdirectory("perch-plugin-").FullName, "claude-plugin");
        try
        {
            Assert.Equal(dir, PerchSessionPlugin.Ensure(dir));
            Assert.Equal(PerchSessionPlugin.Manifest, File.ReadAllText(Path.Combine(dir, ".claude-plugin", "plugin.json")));
            var skill = File.ReadAllText(Path.Combine(dir, "skills", "publish-artifact", "SKILL.md"));
            Assert.StartsWith("---\nname: publish-artifact\ndescription: ", skill.ReplaceLineEndings("\n"));
            Assert.Contains("artifact publish", skill);
            Assert.Contains("$" + PerchSessionPlugin.ExeEnvVar, skill);

            // Unchanged content isn't rewritten.
            var stamp = File.GetLastWriteTimeUtc(Path.Combine(dir, ".claude-plugin", "plugin.json"));
            Assert.Equal(dir, PerchSessionPlugin.Ensure(dir));
            Assert.Equal(stamp, File.GetLastWriteTimeUtc(Path.Combine(dir, ".claude-plugin", "plugin.json")));
        }
        finally { Directory.Delete(Path.GetDirectoryName(dir)!, recursive: true); }
    }

    // It rides the claude command line as one quoted argument.
    [Fact]
    public void System_prompt_note_is_command_line_safe_and_names_the_skill()
    {
        Assert.True(ClaudeCli.IsSafePrompt(PerchSessionPlugin.SystemPromptNote));
        Assert.Contains("perch:publish-artifact", PerchSessionPlugin.SystemPromptNote);
    }
}
