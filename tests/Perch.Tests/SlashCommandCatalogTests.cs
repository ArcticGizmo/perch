using Perch.Data.Control;
using Xunit;

namespace Perch.Tests;

/// <summary>
/// The curated built-in slash-command catalogue behind the session UI's command palette
/// (docs/session-slash-commands-plan.md): its integrity, fuzzy search, and the composer's command detection.
/// </summary>
public class SlashCommandCatalogTests
{
    [Fact]
    public void BuiltIns_AreWellFormed()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in SlashCommandCatalog.BuiltIns)
        {
            Assert.False(string.IsNullOrWhiteSpace(c.Name));
            Assert.Equal(c.Name.ToLowerInvariant(), c.Name);           // names are the lower-case wire form
            Assert.DoesNotContain('/', c.Name);                        // stored without the leading slash
            Assert.False(string.IsNullOrWhiteSpace(c.Description));
            Assert.False(SlashCommandCatalog.IsInternal(c.Name));      // no plumbing commands leak in
            Assert.True(seen.Add(c.Name), $"duplicate command '{c.Name}'");
        }
    }

    [Fact]
    public void IsBuiltIn_And_IsInternal()
    {
        Assert.True(SlashCommandCatalog.IsBuiltIn("context"));
        Assert.True(SlashCommandCatalog.IsBuiltIn("CONTEXT"));         // case-insensitive
        Assert.False(SlashCommandCatalog.IsBuiltIn("grill-me"));       // a skill, not a built-in

        Assert.True(SlashCommandCatalog.IsInternal("__remote-workflow"));
        Assert.True(SlashCommandCatalog.IsInternal("workflow-launch-exec"));
        Assert.False(SlashCommandCatalog.IsInternal("context"));
    }

    [Fact]
    public void Search_BlankQuery_ReturnsAllAlphabetically()
    {
        var all = SlashCommandCatalog.Search("");
        // Every command, sorted by name (a bare "/" is a browsable list, not a capped shortlist).
        Assert.Equal(SlashCommandCatalog.BuiltIns.Count, all.Count);
        var names = all.Select(c => c.Name).ToList();
        Assert.Equal(names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase), names);
    }

    [Theory]
    [InlineData("compact", "compact")]
    [InlineData("cost", "cost")]
    [InlineData("model", "model")]
    [InlineData("ctx", "context")]     // fuzzy subsequence
    public void Search_RanksExpectedFirst(string query, string expectedFirst)
    {
        var results = SlashCommandCatalog.Search(query);
        Assert.NotEmpty(results);
        Assert.Equal(expectedFirst, results[0].Name);
    }

    [Fact]
    public void Search_NoMatch_IsEmpty()
    {
        Assert.Empty(SlashCommandCatalog.Search("zzzznotacommand"));
    }

    [Theory]
    [InlineData("/context", true)]
    [InlineData("  /context", true)]   // leading whitespace tolerated
    [InlineData("/", false)]           // a lone slash is not a command
    [InlineData("/ hello", false)]     // slash then space
    [InlineData("hello", false)]
    [InlineData("", false)]
    public void LooksLikeCommand(string text, bool expected)
    {
        Assert.Equal(expected, SlashCommandCatalog.LooksLikeCommand(text));
    }

    [Theory]
    [InlineData("/compact keep the tests", "compact")]
    [InlineData("/CONTEXT", "context")]
    [InlineData("/rename My session", "rename")]
    [InlineData("not a command", null)]
    public void CommandName(string text, string? expected)
    {
        Assert.Equal(expected, SlashCommandCatalog.CommandName(text));
    }
}
