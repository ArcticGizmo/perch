using Perch.Data;
using Xunit;

namespace Perch.Tests;

public class MarkdownFilesReaderTests
{
    private const string Cwd = TestEnvironment.FixtureCwd;

    [Fact]
    public void GetFileSets_SplitsProducedFromReferenced()
    {
        var reader = new MarkdownFilesReader();
        var sets = reader.GetFileSets("sessMarkdown", Cwd);

        // plan.md was written (tu1) then edited (tu5) then read (tu6): produced, de-duplicated to one,
        // and never listed as merely referenced.
        var produced = Assert.Single(sets.Produced);
        Assert.EndsWith(@"docs\plan.md", produced);

        // README.md was only read → referenced. Foo.cs (non-md) is ignored entirely.
        var referenced = Assert.Single(sets.Referenced);
        Assert.EndsWith("README.md", referenced);
    }

    [Fact]
    public void GetFileSets_DropsFailedProduce()
    {
        var reader = new MarkdownFilesReader();
        var sets = reader.GetFileSets("sessMarkdown", Cwd);

        // notes.md was edited (tu4) but the tool_result came back is_error → the write failed, so it must
        // not count as produced (nor referenced).
        Assert.DoesNotContain(sets.Produced, p => p.Contains("notes.md"));
        Assert.DoesNotContain(sets.Referenced, p => p.Contains("notes.md"));
    }

    [Fact]
    public void ProducedAnyMarkdown_TrueWhenSessionWroteMarkdown()
    {
        var reader = new MarkdownFilesReader();
        Assert.True(reader.ProducedAnyMarkdown("sessMarkdown", Cwd));
    }

    [Fact]
    public void ProducedAnyMarkdown_FalseWhenSessionOnlyReadMarkdown()
    {
        var reader = new MarkdownFilesReader();
        // sessA reads Foo.cs and runs Bash but produces no .md — the glyph must stay dark. (It reads no
        // .md either, so this also covers the plain no-markdown case.)
        Assert.False(reader.ProducedAnyMarkdown("sessA", Cwd));
    }

    [Fact]
    public void GetFileSets_EmptyWhenSessionMissing()
    {
        var reader = new MarkdownFilesReader();
        Assert.True(reader.GetFileSets("no-such-session", Cwd).IsEmpty);
        Assert.False(reader.ProducedAnyMarkdown("no-such-session", Cwd));
    }
}
