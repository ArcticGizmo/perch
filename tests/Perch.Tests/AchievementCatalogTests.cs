using Perch.Data;
using Xunit;

namespace Perch.Tests;

public class AchievementCatalogTests
{
    // Builds a minimal all-time StatsReport; only the fields an assertion cares about need real values.
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

    private static bool Earned(IReadOnlyList<Achievement> badges, string id) =>
        badges.Single(b => b.Id == id).Earned;

    private static IReadOnlyList<Achievement> Eval(StatsReport r, RangeReport? range = null, bool cost = true)
        => AchievementCatalog.Evaluate(r, range, cost);

    [Fact]
    public void EmptyHistory_UnlocksNothing()
    {
        var badges = Eval(Report());
        Assert.All(badges, b => Assert.False(b.Earned));
    }

    [Fact]
    public void FirstSession_UnlocksFirstFlight()
    {
        Assert.True(Earned(Eval(Report(sessions: 1)), "first-flight"));
    }

    [Fact]
    public void MillionTokens_UnlocksWordsmith_ButNotTokenTitan()
    {
        var badges = Eval(Report(tokens: new TokenTotals(1_000_000, 0, 0, 0)));
        Assert.True(Earned(badges, "wordsmith"));
        Assert.False(Earned(badges, "token-titan"));
    }

    [Fact]
    public void LateNightPeak_UnlocksNightOwl_NotEarlyBird()
    {
        var badges = Eval(Report(sessions: 1, peakHour: 2));
        Assert.True(Earned(badges, "night-owl"));
        Assert.False(Earned(badges, "early-bird"));
    }

    [Fact]
    public void MorningPeak_UnlocksEarlyBird()
    {
        Assert.True(Earned(Eval(Report(sessions: 1, peakHour: 7)), "early-bird"));
    }

    [Fact]
    public void EveryHourActive_UnlocksAroundTheClock()
    {
        Assert.True(Earned(Eval(Report(sessions: 1, allHours: true)), "around-the-clock"));
    }

    [Fact]
    public void GrepHeavy_UnlocksGrepGoblin()
    {
        var tools = new List<ToolStat> { new("Grep", 500), new("Read", 3) };
        Assert.True(Earned(Eval(Report(tools: tools)), "grep-goblin"));
    }

    [Fact]
    public void OneToolDominates_UnlocksOneTrickPony()
    {
        var tools = new List<ToolStat> { new("Bash", 190), new("Read", 10) };
        Assert.True(Earned(Eval(Report(toolCalls: 200, tools: tools)), "one-trick-pony"));
    }

    [Fact]
    public void ThreeModelFamilies_UnlocksModelCitizen()
    {
        var models = new List<ModelStat>
        {
            new("claude-opus-4-8", TokenTotals.Zero, null),
            new("claude-sonnet-4-5", TokenTotals.Zero, null),
            new("claude-haiku-4-5-20251001", TokenTotals.Zero, null),
        };
        Assert.True(Earned(Eval(Report(models: models)), "model-citizen"));
    }

    [Fact]
    public void Streak_ComesFromRange()
    {
        var report = Report(sessions: 3);
        Assert.True(Earned(Eval(report, Range(report, streak: 7)), "on-fire"));
        Assert.False(Earned(Eval(report, range: null), "on-fire"));   // no range → streak reads as 0
    }

    [Fact]
    public void LongestSession_ComesFromRange()
    {
        var report = Report(sessions: 1);
        Assert.True(Earned(Eval(report, Range(report, longest: TimeSpan.FromHours(4))), "ultramarathoner"));
    }

    [Fact]
    public void CacheMoney_NeedsCacheReadsOverFreshInput()
    {
        Assert.True(Earned(Eval(Report(tokens: new TokenTotals(100, 0, 0, 500))), "cache-money"));
        Assert.False(Earned(Eval(Report(tokens: new TokenTotals(500, 0, 0, 100))), "cache-money"));
    }

    [Fact]
    public void HidingCost_DropsSpendBadgesEntirely()
    {
        var report = Report(cost: 5000m);
        Assert.Contains(Eval(report, cost: true), b => b.Id == "whale");
        Assert.DoesNotContain(Eval(report, cost: false), b => b.Id == "whale");
    }

    [Fact]
    public void QuotaBadge_ReportsProgressTowardTarget()
    {
        var century = Eval(Report(sessions: 50)).Single(b => b.Id == "century");   // target 100
        Assert.False(century.Earned);
        Assert.NotNull(century.Progress);
        Assert.Equal(0.5, century.Progress!.Value, 3);
    }

    [Fact]
    public void QuotaBadge_ProgressCapsAtOne_WhenEarned()
    {
        var century = Eval(Report(sessions: 250)).Single(b => b.Id == "century");
        Assert.True(century.Earned);
        Assert.Equal(1.0, century.Progress);
    }

    [Fact]
    public void ConditionalBadge_HasNoProgressBar()
    {
        var nightOwl = Eval(Report(sessions: 1, peakHour: 2)).Single(b => b.Id == "night-owl");
        Assert.Null(nightOwl.Progress);
    }

    [Fact]
    public void OneShotBadge_HasNoProgressBar()
    {
        // A target of 1 is 0%-or-100%, so it carries no bar even while locked.
        var firstFlight = Eval(Report(sessions: 1)).Single(b => b.Id == "first-flight");
        Assert.True(firstFlight.Earned);
        Assert.Null(firstFlight.Progress);
    }
}
