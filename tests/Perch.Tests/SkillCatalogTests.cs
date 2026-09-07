using Perch.Data.Control;
using Xunit;

namespace Perch.Tests;

/// <summary>
/// Skill discovery behind the session UI's command palette (docs/session-slash-commands-plan.md): user and
/// project skills come straight from disk, plugin skills are gated on the session's advertised command list,
/// built-ins are excluded, and descriptions are lifted from SKILL.md front-matter. Backed by the fixture
/// <c>skills</c> / <c>plugins</c> trees under <c>fixtures/claude</c>.
/// </summary>
public class SkillCatalogTests
{
    private static SkillInfo? Find(IReadOnlyList<SkillInfo> skills, string command) =>
        skills.FirstOrDefault(s => s.Command == command);

    [Fact]
    public void ForSession_ListsUserSkills_WithDescriptions()
    {
        var skills = SkillCatalog.ForSession(TestEnvironment.FixtureCwd, ["acme:widget"]);

        var demo = Find(skills, "demo-skill");
        Assert.NotNull(demo);
        Assert.False(demo!.IsPlugin);
        Assert.Equal("A demo user skill used by the SkillCatalog tests.", demo.Description);
    }

    [Fact]
    public void ForSession_ParsesBlockScalarDescription_FirstLineOnly()
    {
        var skills = SkillCatalog.ForSession(TestEnvironment.FixtureCwd, []);

        var block = Find(skills, "block-skill");
        Assert.NotNull(block);
        Assert.Equal("First line of a block-scalar description.", block!.Description);
    }

    [Fact]
    public void ForSession_ExcludesSkillsThatCollideWithBuiltIns()
    {
        var skills = SkillCatalog.ForSession(TestEnvironment.FixtureCwd, []);

        Assert.Null(Find(skills, "context"));   // there's a fixture skill named "context"; /context is a built-in
    }

    [Fact]
    public void ForSession_IncludesAdvertisedPluginSkills_WithDescriptions()
    {
        var skills = SkillCatalog.ForSession(TestEnvironment.FixtureCwd, ["acme:widget"]);

        var widget = Find(skills, "acme:widget");
        Assert.NotNull(widget);
        Assert.True(widget!.IsPlugin);
        Assert.Equal("An advertised plugin skill (acme:widget).", widget.Description);
    }

    [Fact]
    public void ForSession_ExcludesInstalledButUnadvertisedPluginSkills()
    {
        // "acme:gadget" exists on disk but isn't in the advertised list → the session can't run it.
        var skills = SkillCatalog.ForSession(TestEnvironment.FixtureCwd, ["acme:widget"]);

        Assert.Null(Find(skills, "acme:gadget"));
    }

    [Fact]
    public void ForSession_IgnoresBuiltInAndInternalAdvertisedCommands()
    {
        var skills = SkillCatalog.ForSession(TestEnvironment.FixtureCwd, ["compact", "model", "__internal", "workflow-launch-exec"]);

        Assert.DoesNotContain(skills, s => s.Command is "compact" or "model" or "__internal" or "workflow-launch-exec");
    }

    [Fact]
    public void ForSession_ToleratesLeadingSlashesOnAdvertisedNames()
    {
        var skills = SkillCatalog.ForSession(TestEnvironment.FixtureCwd, ["/acme:widget"]);

        Assert.NotNull(Find(skills, "acme:widget"));
    }

    [Fact]
    public void ToPaletteItems_TiersEverythingAsSkill()
    {
        var skills = SkillCatalog.ForSession(TestEnvironment.FixtureCwd, ["acme:widget"]);
        var items = SlashCommandCatalog.ToPaletteItems(skills);

        Assert.NotEmpty(items);
        Assert.All(items, i => Assert.Equal(SlashCommandTier.Skill, i.Tier));
        Assert.All(items, i => Assert.Equal("", i.ArgHint));
    }

    [Fact]
    public void Search_AppendsSkillsAfterBuiltIns_OnBlankQuery()
    {
        var items = SlashCommandCatalog.ToPaletteItems(
            SkillCatalog.ForSession(TestEnvironment.FixtureCwd, ["acme:widget"]));

        var results = SlashCommandCatalog.Search("", items);

        int firstSkill = results.ToList().FindIndex(r => r.Tier == SlashCommandTier.Skill);
        int lastBuiltIn = results.ToList().FindLastIndex(r => r.Tier != SlashCommandTier.Skill);
        Assert.True(firstSkill > lastBuiltIn, "skills should follow every built-in");
        Assert.Contains(results, r => r.Name == "demo-skill");
        Assert.Contains(results, r => r.Name == "acme:widget");
    }

    [Fact]
    public void Search_FuzzyMatchesSkillsByName()
    {
        var items = SlashCommandCatalog.ToPaletteItems(
            SkillCatalog.ForSession(TestEnvironment.FixtureCwd, ["acme:widget"]));

        var results = SlashCommandCatalog.Search("widget", items);

        Assert.Contains(results, r => r.Name == "acme:widget");
    }
}
