using Perch.Data;
using Xunit;

namespace Perch.Tests;

public class PreserveCaseTextTests
{
    [Theory]
    // ALL-UPPERCASE match → uppercase the replacement.
    [InlineData("COLOR", "colour", "COLOUR")]
    // all-lowercase match → lowercase the replacement.
    [InlineData("color", "Colour", "colour")]
    // Capitalized match (leading upper) → capitalize the replacement's first letter, keep the rest.
    [InlineData("Color", "colour", "Colour")]
    [InlineData("Color", "coLOUR", "CoLOUR")]
    // Mixed with a lowercase lead → leave the replacement as authored.
    [InlineData("cOlOr", "colour", "colour")]
    public void Apply_CopiesCasingShape(string matched, string replacement, string expected) =>
        Assert.Equal(expected, PreserveCaseText.Apply(matched, replacement));

    [Fact]
    public void Apply_EmptyOrLetterlessMatch_LeavesReplacementUntouched()
    {
        Assert.Equal("colour", PreserveCaseText.Apply("", "colour"));
        Assert.Equal("colour", PreserveCaseText.Apply("123", "colour"));   // no letters to take a shape from
        Assert.Equal("", PreserveCaseText.Apply("COLOR", ""));             // nothing to recase
    }

    [Fact]
    public void Apply_SingleLetterMatch_TreatedByCase()
    {
        Assert.Equal("XYZ", PreserveCaseText.Apply("A", "xyz"));   // single upper → all upper
        Assert.Equal("xyz", PreserveCaseText.Apply("a", "xyz"));   // single lower → all lower
    }
}
