using Perch.Data;
using Xunit;

namespace Perch.Tests;

/// <summary>
/// The overlay's permission-mode badge reads the mode Claude Code records in the transcript (a <c>permission-mode</c>
/// record, or <c>permissionMode</c> on a user prompt). A transcript with neither — an older Claude Code build — has no
/// mode, and the session shows no badge.
/// </summary>
public sealed class PermissionModeTests : IDisposable
{
    private readonly string _dir = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), "perch-mode-" + Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string Write(params string[] lines)
    {
        var path = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".jsonl");
        File.WriteAllText(path, string.Concat(lines.Select(l => l + "\n")));
        return path;
    }

    private const string Prompt =
        """{"type":"user","message":{"role":"user","content":"hi"},"permissionMode":"default"}""";
    private const string Assistant =
        """{"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"ok"}]}}""";

    [Fact]
    public void NoRecordedMode_ReadsAsNull_AndNoBadge()
    {
        var path = Write("""{"type":"user","message":{"role":"user","content":"hi"}}""", Assistant);

        var mode = new TranscriptReader().PermissionModeAt(path);

        Assert.Null(mode);
        Assert.Equal(PermissionMode.Normal, SessionMonitor.ParsePermissionMode(mode));
    }

    [Fact]
    public void LatestRecordWins_AcrossPromptsAndModeRecords()
    {
        var path = Write(
            Prompt,
            Assistant,
            """{"type":"permission-mode","permissionMode":"acceptEdits","sessionId":"s"}""");

        Assert.Equal(PermissionMode.AcceptEdits,
            SessionMonitor.ParsePermissionMode(new TranscriptReader().PermissionModeAt(path)));
    }

    [Fact]
    public void SidechainRecords_DoNotOverrideTheSessionMode()
    {
        var path = Write(
            """{"type":"permission-mode","permissionMode":"plan","sessionId":"s"}""",
            """{"type":"user","isSidechain":true,"message":{"role":"user","content":"sub"},"permissionMode":"bypassPermissions"}""");

        Assert.Equal("plan", new TranscriptReader().PermissionModeAt(path));
    }

    [Fact]
    public void FollowsAModeChange_AsTheTranscriptGrows()
    {
        var path = Write("""{"type":"permission-mode","permissionMode":"auto","sessionId":"s"}""");
        var reader = new TranscriptReader();
        Assert.Equal("auto", reader.PermissionModeAt(path));

        File.AppendAllText(path, """{"type":"permission-mode","permissionMode":"plan","sessionId":"s"}""" + "\n");

        Assert.Equal("plan", reader.PermissionModeAt(path));
    }

    [Theory]
    [InlineData("default", PermissionMode.Normal)]
    [InlineData("acceptEdits", PermissionMode.AcceptEdits)]
    [InlineData("plan", PermissionMode.Plan)]
    [InlineData("auto", PermissionMode.Auto)]
    [InlineData("bypassPermissions", PermissionMode.Bypass)]
    [InlineData(null, PermissionMode.Normal)]
    public void ParsesClaudeCodeModeNames(string? text, PermissionMode expected) =>
        Assert.Equal(expected, SessionMonitor.ParsePermissionMode(text));
}
