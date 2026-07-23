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

    private static RangeReport Range(StatsReport totals, int activeDays = 0, int? streak = null,
        TimeSpan longest = default, IReadOnlyList<DayPoint>? trend = null)
        => new("All time", "Active per day", totals, trend ?? [], activeDays, streak, null, TimeSpan.Zero, longest, null);

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
        // TokenTotals is (Input, Output, CacheWrite, CacheRead) — 15M output.
        var output = Fam(Eval(Report(tokens: new TokenTotals(0, 15_000_000, 0, 0))), "output");
        Assert.True(output.Earned);
        Assert.Equal(2, output.Level);            // past 1M and 10M, not 50M
        Assert.Equal(5, output.MaxLevel);
        Assert.Equal("Prolific", output.Name);    // the level-2 rung
        Assert.Equal("Output", output.Category);
    }

    [Fact]
    public void ScalingFamily_ProgressIsBandTowardNextLevel()
    {
        // 15M output sits 1/8 of the way from the 10M rung to the 50M rung.
        var output = Fam(Eval(Report(tokens: new TokenTotals(0, 15_000_000, 0, 0))), "output");
        Assert.NotNull(output.Progress);
        Assert.Equal((15_000_000.0 - 10_000_000) / (50_000_000 - 10_000_000), output.Progress!.Value, 4);
    }

    [Fact]
    public void MaxedFamily_HasNoProgressBar()
    {
        // "Cached" folds cache reads + writes; 200B blows past the 100B top rung.
        var cached = Fam(Eval(Report(tokens: new TokenTotals(0, 0, 0, 200_000_000_000))), "cached");
        Assert.Equal(cached.MaxLevel, cached.Level);
        Assert.Null(cached.Progress);
        Assert.Equal("Cache Baron", cached.Name);
    }

    [Fact]
    public void LevelsCarryNamespacedIdsAndEarnedFlags()
    {
        var output = Fam(Eval(Report(tokens: new TokenTotals(0, 15_000_000, 0, 0))), "output");
        Assert.Equal("output.ghostwriter", output.Levels[0].Id);
        Assert.True(output.Levels[0].Earned);
        Assert.True(output.Levels[1].Earned);
        Assert.False(output.Levels[2].Earned);
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

    [Fact]
    public void ToolBadges_EarnAtTheirThreshold()
    {
        var tools = new List<ToolStat>
        {
            new("WebFetch", 100), new("WebSearch", 500), new("TodoWrite", 500), new("ExitPlanMode", 25),
        };
        var fams = Eval(Report(tools: tools));
        Assert.True(Fam(fams, "web-crawler").Earned);
        Assert.True(Fam(fams, "search-party").Earned);
        Assert.True(Fam(fams, "list-maker").Earned);
        Assert.True(Fam(fams, "plan-b").Earned);
    }

    [Fact]
    public void ToolBadge_ShowsProgressWhileShort()
    {
        var crawler = Fam(Eval(Report(tools: [new("WebFetch", 50)])), "web-crawler");
        Assert.False(crawler.Earned);
        Assert.Equal(0.5, crawler.Progress!.Value, 3);
    }

    [Fact]
    public void SecretBadge_LockedShowsHintAndIsFlaggedSecret()
    {
        var witching = Fam(Eval(Report()), "the-witching-hour");
        Assert.True(witching.Secret);
        Assert.False(witching.Earned);
        Assert.Equal("Something stirs when the clocks reset.", witching.Description);   // the cryptic hint
        Assert.Null(witching.Progress);                                                 // no bar → no leak
    }

    [Fact]
    public void SecretBadge_UnlockRevealsTheRealCriteria()
    {
        var witching = Fam(Eval(Report(sessions: 1, peakHour: 0)), "the-witching-hour");
        Assert.True(witching.Earned);
        Assert.Equal("Logged work in the midnight hour", witching.Description);
    }

    [Fact]
    public void Elite_EarnsAt1337Prompts()
    {
        Assert.False(Fam(Eval(Report(prompts: 1336)), "elite").Earned);
        Assert.True(Fam(Eval(Report(prompts: 1337)), "elite").Earned);
    }

    [Fact]
    public void TheAnswer_And_FlatCircle_ComeFromRange()
    {
        var report = Report(sessions: 1);
        var fams = Eval(report, Range(report, activeDays: 42, longest: TimeSpan.FromHours(12)));
        Assert.True(Fam(fams, "the-answer").Earned);
        Assert.True(Fam(fams, "flat-circle").Earned);
    }

    [Fact]
    public void Nocturnal_NeedsMoreNightThanDay()
    {
        Assert.True(Fam(Eval(Report(sessions: 1, peakHour: 0)), "nocturnal").Earned);    // 9000s at midnight
        Assert.False(Fam(Eval(Report(sessions: 1, peakHour: 12)), "nocturnal").Earned);  // busiest at noon
    }

    [Fact]
    public void GroundhogDay_NeedsAFullMondayToSundayWeek()
    {
        var report = Report(sessions: 1);
        var monday = new DateOnly(2024, 1, 1);   // a Monday
        var fullWeek = Enumerable.Range(0, 7)
            .Select(i => new DayPoint(monday.AddDays(i), 1, TimeSpan.FromHours(1), 0)).ToList();
        Assert.True(Fam(Eval(report, Range(report, trend: fullWeek)), "groundhog-day").Earned);

        var sixDays = fullWeek.Take(6).ToList();
        Assert.False(Fam(Eval(report, Range(report, trend: sixDays)), "groundhog-day").Earned);
    }

    [Fact]
    public void Completionist_IsSecretAndUnearnedUntilEverythingElseIs()
    {
        var completionist = Fam(Eval(Report()), "completionist");
        Assert.True(completionist.Secret);
        Assert.False(completionist.Earned);
        Assert.Equal("There is nothing left to prove.", completionist.Description);
    }
}
