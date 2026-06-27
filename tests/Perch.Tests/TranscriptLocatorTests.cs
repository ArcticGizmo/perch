using Perch.Data;
using Xunit;

namespace Perch.Tests;

public class TranscriptLocatorTests
{
    [Theory]
    [InlineData(@"C:\fixtures\proj", "C--fixtures-proj")]
    [InlineData(@"C:\a\b.c", "C--a-b-c")]
    [InlineData("/home/me/proj", "-home-me-proj")]
    public void EncodeProjectDir_ReplacesNonAlphanumericWithDash(string cwd, string expected) =>
        Assert.Equal(expected, TranscriptLocator.EncodeProjectDir(cwd));

    [Fact]
    public void Resolve_DirectPath_WhenCwdEncodesToTheProjectDir()
    {
        var path = TranscriptLocator.Resolve("sessA", TestEnvironment.FixtureCwd);
        Assert.NotNull(path);
        Assert.EndsWith(Path.Combine("C--fixtures-proj", "sessA.jsonl"), path);
    }

    [Fact]
    public void Resolve_ScanFallback_WhenCwdDoesNotEncodeToTheDir()
    {
        // sessScan lives under C--scan-target, but we query with a cwd that encodes elsewhere, so the
        // direct path misses and the project-dir scan must find it by session id.
        var path = TranscriptLocator.Resolve("sessScan", @"C:\not\matching\dir");
        Assert.NotNull(path);
        Assert.EndsWith(Path.Combine("C--scan-target", "sessScan.jsonl"), path);
    }

    [Fact]
    public void Resolve_ReturnsNull_ForUnknownSession() =>
        Assert.Null(TranscriptLocator.Resolve("no-such-session", TestEnvironment.FixtureCwd));

    [Fact]
    public void EnumerateTranscripts_FindsTopLevelProjectFiles_NotNestedSubagentDirs()
    {
        var files = TranscriptLocator.EnumerateTranscripts().Select(Path.GetFileName).ToList();
        Assert.Contains("sessA.jsonl", files);
        Assert.Contains("sessScan.jsonl", files);
        // The 2.1 sub-agent transcript is nested two levels down and must NOT be enumerated as a session.
        Assert.DoesNotContain("agent-bg1.jsonl", files);
    }
}
