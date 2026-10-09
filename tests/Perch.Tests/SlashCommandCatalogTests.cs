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

    // The reloads run headless as local commands (verified over stream-json, claude 2.1.295), so they ride as
    // text; /exit never goes to the CLI — Perch ends the session itself.
    [Theory]
    [InlineData("reload-skills", nameof(SlashCommandTier.PlainText))]
    [InlineData("reload-plugins", nameof(SlashCommandTier.PlainText))]
    [InlineData("exit", nameof(SlashCommandTier.Native))]
    public void SessionLifecycleCommands_AreCatalogued(string name, string tier)
    {
        var cmd = Assert.Single(SlashCommandCatalog.BuiltIns, c => c.Name == name);
        Assert.Equal(tier, cmd.Tier.ToString());
        Assert.False(cmd.TakesArgs);
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

    // ActiveToken: the slash token the caret is in (caret = '|' in the input, removed before the call).
    [Theory]
    [InlineData("/|", 0, 1, "", true)]
    [InlineData("/con|", 0, 4, "con", true)]
    [InlineData("  /mo|", 2, 5, "mo", true)]
    [InlineData("/co|ntext", 0, 8, "co", true)]          // token runs past the caret
    [InlineData("please run /gri|", 11, 15, "gri", false)]
    [InlineData("line one\n/gr|", 9, 12, "gr", false)]  // after a newline is still mid-prompt
    [InlineData("hi /| there", 3, 4, "", false)]
    public void ActiveToken_Found(string marked, int start, int end, string query, bool leading)
    {
        int caret = marked.IndexOf('|');
        var tok = SlashCommandCatalog.ActiveToken(marked.Remove(caret, 1), caret);
        Assert.Equal((start, end, query, leading), tok);
    }

    [Theory]
    [InlineData("|")]
    [InlineData("hello|")]
    [InlineData("/compact keep|")]       // onto the arguments
    [InlineData("src/foo|")]             // slash not at a word start
    [InlineData("/usr/bin|")]            // a path
    [InlineData("|/context")]            // caret before the slash
    public void ActiveToken_None(string marked)
    {
        int caret = marked.IndexOf('|');
        Assert.Null(SlashCommandCatalog.ActiveToken(marked.Remove(caret, 1), caret));
    }

    [Fact]
    public void SearchSkills_OnlySkills()
    {
        var skills = new[]
        {
            new SlashCommandInfo("grill-me", "", "Interview", SlashCommandTier.Skill),
            new SlashCommandInfo("bump-version", "", "Bump", SlashCommandTier.Skill),
        };
        Assert.Equal(["bump-version", "grill-me"], SlashCommandCatalog.SearchSkills("", skills).Select(s => s.Name));
        Assert.Equal("grill-me", SlashCommandCatalog.SearchSkills("gri", skills)[0].Name);
        Assert.Empty(SlashCommandCatalog.SearchSkills("context", skills));   // built-ins never appear
    }
}
