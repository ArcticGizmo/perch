using Perch.Data;
using Xunit;

namespace Perch.Tests;

public class AchievementCatalogTests
{
    private static StatsReport Report(
        int sessions = 0, int activeHours = 0, int prompts = 0, int toolCalls = 0,
        int subAgents = 0, int teammates = 0, TokenTotals tokens = default, decimal cost = 0,
        int? peakHour = null, bool allHours = false,
        IReadOnlyList<ProjectStat>? projects = null, IReadOnlyList<ProjectStat>? branches = null,
        IReadOnlyList<ToolStat>? tools = null, IReadOnlyList<ModelStat>? models = null)
    {
        var hourly = new int[24];
        if (allHours) Array.Fill(hourly, 100);
        if (peakHour is { } h) hourly[h] = 9000;
        return new StatsReport(
            DateOnly.FromDateTime(DateTime.Now), sessions, TimeSpan.FromHours(activeHours),
            prompts, toolCalls, subAgents, teammates, tokens, TokenTotals.Zero, cost, true,
            projects ?? [], tools ?? [], models ?? [], branches ?? [], hourly);
    }

    private static RangeReport Range(StatsReport totals, int activeDays = 0, int? streak = null, TimeSpan longest = default)
        => new("All time", "Active per day", totals, [], activeDays, streak, null, TimeSpan.Zero, longest, null);

    private static IReadOnlyList<Achievement> Eval(StatsReport r, RangeReport? range = null, bool cost = true)
        => AchievementCatalog.Evaluate(r, range, cost);

    private static Achievement Fam(IReadOnlyList<Achievement> fams, string id) => fams.Single(f => f.Id == id);

    [Fact]
    public void EmptyHistory_LeavesEveryFamilyAtLevelZero()
    {
        var fams = Eval(Report());
        Assert.All(fams, f => Assert.False(f.Earned));
        Assert.All(fams, f => Assert.Equal(0, f.Level));
    }

    [Fact]
    public void ScalingFamily_LevelsUpInPlace()
    {
        var tokens = Fam(Eval(Report(tokens: new TokenTotals(15_000_000, 0, 0, 0))), "tokens");
        Assert.True(tokens.Earned);
        Assert.Equal(2, tokens.Level);            // past 1M and 10M, not 100M
        Assert.Equal(5, tokens.MaxLevel);
        Assert.Equal("Prolific", tokens.Name);    // the level-2 rung
        Assert.Equal("Tokens", tokens.Category);
    }

    [Fact]
    public void ScalingFamily_ProgressIsBandTowardNextLevel()
    {
        // 15M sits 1/18 of the way from the 10M rung to the 100M rung.
        var tokens = Fam(Eval(Report(tokens: new TokenTotals(15_000_000, 0, 0, 0))), "tokens");
        Assert.NotNull(tokens.Progress);
        Assert.Equal((15_000_000.0 - 10_000_000) / (100_000_000 - 10_000_000), tokens.Progress!.Value, 4);
    }

    [Fact]
    public void MaxedFamily_HasNoProgressBar()
    {
        var tokens = Fam(Eval(Report(tokens: new TokenTotals(20_000_000_000, 0, 0, 0))), "tokens");
        Assert.Equal(tokens.MaxLevel, tokens.Level);
        Assert.Null(tokens.Progress);
        Assert.Equal("Tokenlord", tokens.Name);
    }

    [Fact]
    public void LevelsCarryNamespacedIdsAndEarnedFlags()
    {
        var tokens = Fam(Eval(Report(tokens: new TokenTotals(15_000_000, 0, 0, 0))), "tokens");
        Assert.Equal("tokens.wordsmith", tokens.Levels[0].Id);
        Assert.True(tokens.Levels[0].Earned);
        Assert.True(tokens.Levels[1].Earned);
        Assert.False(tokens.Levels[2].Earned);
    }

    [Fact]
    public void OneOffQuota_HasProgressButNoCategory()
    {
        var tools = new List<ToolStat> { new("Grep", 250) };
        var grep = Fam(Eval(Report(tools: tools)), "grep-goblin");
        Assert.False(grep.Earned);
        Assert.Equal("", grep.Category);
        Assert.Equal(0.5, grep.Progress!.Value, 3);
    }

    [Fact]
    public void ConditionalBadge_HasNoProgressAndNoCategory()
    {
        var nightOwl = Fam(Eval(Report(sessions: 1, peakHour: 2)), "night-owl");
        Assert.True(nightOwl.Earned);
        Assert.Null(nightOwl.Progress);
        Assert.Equal("", nightOwl.Category);
    }

    [Fact]
    public void StreakAndLongest_ComeFromRange()
    {
        var report = Report(sessions: 3);
        var fams = Eval(report, Range(report, streak: 7, longest: TimeSpan.FromHours(4)));
        Assert.Equal(2, Fam(fams, "streak").Level);      // past 3-day and 7-day
        Assert.Equal(2, Fam(fams, "longest").Level);     // past 2h and 4h
    }

    [Fact]
    public void HidingCost_DropsTheSpendFamily()
    {
        var report = Report(cost: 5000m);
        Assert.Contains(Eval(report, cost: true), f => f.Id == "spend");
        Assert.DoesNotContain(Eval(report, cost: false), f => f.Id == "spend");
    }
}
