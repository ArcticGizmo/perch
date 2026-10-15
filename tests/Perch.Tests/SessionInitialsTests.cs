using Perch.Data;
using Xunit;

namespace Perch.Tests;

public class SessionInitialsTests
{
    [Theory]
    [InlineData("perch", "PE")]
    [InlineData("my-api", "MA")]
    [InlineData("hyper_tree", "HT")]
    [InlineData("PerchApp", "PA")]
    [InlineData("docs site v2", "DS")]
    [InlineData("API", "AP")]          // a run of capitals stays one word
    [InlineData("x", "X")]
    [InlineData("2fa-service", "2S")]
    [InlineData("  spaced  ", "SP")]
    public void For_DerivesTwoLetters(string name, string expected)
        => Assert.Equal(expected, SessionInitials.For(name));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("--- ...")]
    public void For_FallsBackToQuestionMark(string? name)
        => Assert.Equal("?", SessionInitials.For(name));
}
