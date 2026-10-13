using Perch.Data;
using Xunit;

namespace Perch.Tests;

public class FilePathDetectTests
{
    private static string[] Words(string text) => FilePathDetect.Find(text).Select(c => c.Text).ToArray();

    [Theory]
    [InlineData("see src/Perch.App/App.axaml.cs:42 for that", "src/Perch.App/App.axaml.cs:42")]
    [InlineData(@"edited docs\plan.md.", @"docs\plan.md")]
    [InlineData("Updated README.md, then left.", "README.md")]
    [InlineData("(tests/LocalPathTests.cs#L86)", "tests/LocalPathTests.cs#L86")]
    [InlineData(@"at C:\work\repo\a.cs:3:", @"C:\work\repo\a.cs:3")]
    public void Finds_the_path_shaped_word(string text, string expected) =>
        Assert.Equal([expected], Words(text));

    [Theory]
    [InlineData("version 0.5.27 shipped")]
    [InlineData("e.g. this, i.e. that")]
    [InlineData("open https://example.com/a.md now")]
    [InlineData("a mailto:x@example.com link")]
    [InlineData("Note: nothing here")]
    [InlineData("")]
    public void Skips_words_that_are_not_paths(string text) => Assert.Empty(Words(text));

    [Fact]
    public void Reports_the_char_range_of_each_candidate()
    {
        const string text = "a.md and b/c.cs:9";
        var found = FilePathDetect.Find(text);
        Assert.Equal(2, found.Count);
        Assert.Equal("a.md", text.Substring(found[0].Start, found[0].Length));
        Assert.Equal("b/c.cs:9", text.Substring(found[1].Start, found[1].Length));
    }

    [Fact]
    public void A_unc_word_is_still_a_candidate_but_the_resolver_never_probes_it()
    {
        // Detection is shape only; the network gate is FileRefResolver's (review fixes CP9).
        var c = Assert.Single(FilePathDetect.Find(@"see \\perch-cp9.invalid\s\a.md"));
        var probed = new List<string>();
        Assert.Null(FileRefResolver.ResolveRef(@"C:\work\repo", c.Text, p => { probed.Add(p); return true; }));
        Assert.Empty(probed);
    }

    [Fact]
    public void Caps_the_number_of_candidates()
    {
        var text = string.Join(' ', Enumerable.Range(0, 100).Select(i => $"f{i}.md"));
        Assert.Equal(32, FilePathDetect.Find(text).Count);
    }
}
