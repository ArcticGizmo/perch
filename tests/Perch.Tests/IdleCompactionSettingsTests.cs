using System;
using System.IO;
using System.Text.Json.Nodes;
using Perch.Data;
using Xunit;

namespace Perch.Tests;

/// <summary>The per-directory <c>idleCompaction</c> read/write helpers on <see cref="ClaudeUserSettings"/>:
/// Claude Code defaults it on and only honours an explicit false, so disabling writes false, enabling removes
/// the key, and every other key is preserved.</summary>
public sealed class IdleCompactionSettingsTests : IDisposable
{
    private readonly string _dir;
    private readonly string _a;
    private readonly string _b;

    public IdleCompactionSettingsTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "perch-ic-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _a = Path.Combine(_dir, "a", "settings.json");
        _b = Path.Combine(_dir, "b", "settings.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void Absent_file_reads_as_on() =>
        Assert.True(ClaudeUserSettings.IsIdleCompactionEnabled(_a));

    [Fact]
    public void Enabling_an_absent_file_does_not_create_it()
    {
        Assert.True(ClaudeUserSettings.SetIdleCompactionEnabled(_a, true));
        Assert.False(File.Exists(_a));
    }

    [Fact]
    public void Disable_writes_false_and_reads_back()
    {
        Assert.True(ClaudeUserSettings.SetIdleCompactionEnabled(_a, false));
        Assert.False(ClaudeUserSettings.IsIdleCompactionEnabled(_a));

        var root = (JsonObject)JsonNode.Parse(File.ReadAllText(_a))!;
        Assert.False(root["idleCompaction"]!.GetValue<bool>());
    }

    [Fact]
    public void Enable_removes_the_key_and_preserves_others()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_a)!);
        File.WriteAllText(_a, """{ "model": "opus", "idleCompaction": false }""");
        ClaudeUserSettings.SetIdleCompactionEnabled(_a, true);

        var root = (JsonObject)JsonNode.Parse(File.ReadAllText(_a))!;
        Assert.True(ClaudeUserSettings.IsIdleCompactionEnabled(_a));
        Assert.False(root.ContainsKey("idleCompaction"));
        Assert.Equal("opus", root["model"]!.GetValue<string>());
    }

    [Fact]
    public void Explicit_true_reads_as_on()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_a)!);
        File.WriteAllText(_a, """{ "idleCompaction": true }""");
        Assert.True(ClaudeUserSettings.IsIdleCompactionEnabled(_a));
    }

    [Fact]
    public void Two_directories_are_independent()
    {
        ClaudeUserSettings.SetIdleCompactionEnabled(_a, false);
        ClaudeUserSettings.SetIdleCompactionEnabled(_b, true);

        Assert.False(ClaudeUserSettings.IsIdleCompactionEnabled(_a));
        Assert.True(ClaudeUserSettings.IsIdleCompactionEnabled(_b));
    }
}
