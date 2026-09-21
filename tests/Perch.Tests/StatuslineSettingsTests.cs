using System;
using System.IO;
using System.Text.Json.Nodes;
using Perch.Data;
using Xunit;

namespace Perch.Tests;

/// <summary>The <c>statusLine</c> read/write helpers on <see cref="ClaudeUserSettings"/>: they must set
/// and clear the block while preserving every other key in <c>settings.json</c>. Works against a
/// throwaway file via the path-taking overloads, so nothing touches the shared fixture config dir.</summary>
public sealed class StatuslineSettingsTests : IDisposable
{
    private readonly string _dir;
    private readonly string _settings;

    public StatuslineSettingsTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "perch-sl-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _settings = Path.Combine(_dir, "settings.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private JsonObject Read() => (JsonObject)JsonNode.Parse(File.ReadAllText(_settings))!;

    [Fact]
    public void ReadStatusLineCommand_is_null_when_file_absent() =>
        Assert.Null(ClaudeUserSettings.ReadStatusLineCommand(_settings));

    [Fact]
    public void SetStatusLine_writes_the_block_and_reads_back()
    {
        Assert.True(ClaudeUserSettings.SetStatusLine(_settings, "\"C:\\Program Files\\perch\\perch.exe\" statusline", 2));
        Assert.Equal("\"C:\\Program Files\\perch\\perch.exe\" statusline",
            ClaudeUserSettings.ReadStatusLineCommand(_settings));

        var block = (JsonObject)Read()["statusLine"]!;
        Assert.Equal("command", block["type"]!.GetValue<string>());
        Assert.Equal(2, block["padding"]!.GetValue<int>());
    }

    [Fact]
    public void SetStatusLine_preserves_other_keys()
    {
        File.WriteAllText(_settings, """{ "model": "opus", "env": { "FOO": "1" } }""");
        ClaudeUserSettings.SetStatusLine(_settings, "cmd");

        var root = Read();
        Assert.Equal("opus", root["model"]!.GetValue<string>());
        Assert.Equal("1", ((JsonObject)root["env"]!)["FOO"]!.GetValue<string>());
        Assert.Equal("cmd", ClaudeUserSettings.ReadStatusLineCommand(_settings));
    }

    [Fact]
    public void SetStatusLine_overwrites_an_existing_block()
    {
        ClaudeUserSettings.SetStatusLine(_settings, "old");
        ClaudeUserSettings.SetStatusLine(_settings, "new");
        Assert.Equal("new", ClaudeUserSettings.ReadStatusLineCommand(_settings));
    }

    [Fact]
    public void ClearStatusLine_removes_the_block_but_keeps_the_rest()
    {
        File.WriteAllText(_settings, """{ "model": "opus" }""");
        ClaudeUserSettings.SetStatusLine(_settings, "cmd");
        Assert.True(ClaudeUserSettings.ClearStatusLine(_settings));

        var root = Read();
        Assert.False(root.ContainsKey("statusLine"));
        Assert.Equal("opus", root["model"]!.GetValue<string>());
        Assert.Null(ClaudeUserSettings.ReadStatusLineCommand(_settings));
    }
}
