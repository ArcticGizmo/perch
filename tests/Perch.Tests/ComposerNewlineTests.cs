using Perch.Data.Control;
using Xunit;

namespace Perch.Tests;

/// <summary>The composers' terminal-style "\ then Enter" newline: a backslash right before the caret means Enter
/// inserts a newline (over the backslash) instead of sending the prompt.</summary>
public class ComposerNewlineTests
{
    [Theory]
    [InlineData(@"first line\", 11)]    // trailing
    [InlineData(@"ab\cd", 3)]           // mid-text, caret right after it
    public void BackslashBeforeCaret_Continues(string input, int caret) =>
        Assert.True(ComposerNewline.IsContinuation(input, caret, caret, caret));

    [Theory]
    [InlineData("send me", 7)]          // no backslash
    [InlineData(@"\path", 5)]           // backslash not directly before the caret
    [InlineData(@"x\", 0)]              // caret at the start
    [InlineData("", 0)]
    public void OtherwiseEnterSends(string input, int caret) =>
        Assert.False(ComposerNewline.IsContinuation(input, caret, caret, caret));

    [Fact]
    public void WithSelection_EnterSends() => Assert.False(ComposerNewline.IsContinuation(@"abc\", 4, 1, 4));

    [Fact]
    public void NullOrOutOfRange_DoesNotThrow()
    {
        Assert.False(ComposerNewline.IsContinuation(null, 0, 0, 0));
        Assert.False(ComposerNewline.IsContinuation(@"a\", 9, 9, 9));
    }
}
