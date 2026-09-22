using System;
using System.IO;
using System.Text.Json.Nodes;
using Perch.Data;
using Xunit;

namespace Perch.Tests;

/// <summary>The per-directory <c>env.CLAUDE_CODE_EXPERIMENTAL_AGENT_TEAMS</c> read/write helpers on
/// <see cref="ClaudeUserSettings"/>: enabling writes "1", disabling removes the key (and the env object when
/// it empties), and every other key is preserved. Uses the path-taking overloads against throwaway files so
/// two directories can be flipped independently without touching the shared fixture config dir.</summary>
public sealed class AgentTeamsSettingsTests : IDisposable
{
    private readonly string _dir;
    private readonly string _a;
    private readonly string _b;

    public AgentTeamsSettingsTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "perch-at-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _a = Path.Combine(_dir, "a", "settings.json");
        _b = Path.Combine(_dir, "b", "settings.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void Absent_file_reads_as_off() =>
        Assert.False(ClaudeUserSettings.IsAgentTeamsEnabled(_a));

    [Fact]
    public void Enable_writes_the_flag_and_reads_back()
    {
        Assert.True(ClaudeUserSettings.SetAgentTeamsEnabled(_a, true));
        Assert.True(ClaudeUserSettings.IsAgentTeamsEnabled(_a));

        var env = (JsonObject)((JsonObject)JsonNode.Parse(File.ReadAllText(_a))!)["env"]!;
        Assert.Equal("1", env["CLAUDE_CODE_EXPERIMENTAL_AGENT_TEAMS"]!.ToString());
    }

    [Fact]
    public void Disable_removes_the_key_and_empty_env()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_a)!);
        File.WriteAllText(_a, """{ "model": "opus" }""");
        ClaudeUserSettings.SetAgentTeamsEnabled(_a, true);
        ClaudeUserSettings.SetAgentTeamsEnabled(_a, false);

        var root = (JsonObject)JsonNode.Parse(File.ReadAllText(_a))!;
        Assert.False(ClaudeUserSettings.IsAgentTeamsEnabled(_a));
        Assert.False(root.ContainsKey("env"));       // the env object was dropped when it emptied
        Assert.Equal("opus", root["model"]!.GetValue<string>());
    }

    [Fact]
    public void Disable_preserves_other_env_vars()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_a)!);
        File.WriteAllText(_a, """{ "env": { "FOO": "1", "CLAUDE_CODE_EXPERIMENTAL_AGENT_TEAMS": "1" } }""");
        ClaudeUserSettings.SetAgentTeamsEnabled(_a, false);

        var env = (JsonObject)((JsonObject)JsonNode.Parse(File.ReadAllText(_a))!)["env"]!;
        Assert.Equal("1", env["FOO"]!.ToString());
        Assert.False(env.ContainsKey("CLAUDE_CODE_EXPERIMENTAL_AGENT_TEAMS"));
    }

    [Fact]
    public void Two_directories_are_independent()
    {
        ClaudeUserSettings.SetAgentTeamsEnabled(_a, true);
        ClaudeUserSettings.SetAgentTeamsEnabled(_b, false);

        Assert.True(ClaudeUserSettings.IsAgentTeamsEnabled(_a));
        Assert.False(ClaudeUserSettings.IsAgentTeamsEnabled(_b));
    }
}
