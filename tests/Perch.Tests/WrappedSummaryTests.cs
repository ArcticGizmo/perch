using Perch.Data;
using Xunit;

namespace Perch.Tests;

public class WrappedSummaryTests
{
    // Builds a minimal StatsReport; only the fields a given assertion cares about need realistic values.
    private static StatsReport Report(
        int sessions = 1, int activeMinutes = 10, int prompts = 5, int toolCalls = 5,
        int subAgents = 0, long tokens = 0, int? peakHour = null, string? topModel = null)
    {
        var hourly = new int[24];
        if (peakHour is { } h) hourly[h] = 9000;
        var models = topModel != null
            ? new List<ModelStat> { new(topModel, new TokenTotals(tokens, 0, 0, 0), 1m) }
            : new List<ModelStat>();
        return new StatsReport(
            DateOnly.FromDateTime(DateTime.Now), sessions, TimeSpan.FromMinutes(activeMinutes),
            prompts, toolCalls, subAgents, 0, new TokenTotals(tokens, 0, 0, 0), TokenTotals.Zero, 0m, true,
            new List<ProjectStat>(), new List<ToolStat>(), models, new List<ProjectStat>(), hourly);
    }

    private static WrappedSummary Build(StatsReport r, RangeReport? range = null) =>
        WrappedSummary.Build(r, range, "Test", "", showCost: false);

    [Fact]
    public void Persona_SubAgentHeavy_IsAgentWrangler()
    {
        // Delegation is the loudest signal — it wins even over a strong peak-hour pull.
        var s = Build(Report(subAgents: 10, peakHour: 23));
        Assert.Equal(WrappedPersona.AgentWrangler, s.Persona);
    }

    [Fact]
    public void Persona_ManyToolsPerPrompt_IsToolWhisperer()
    {
        var s = Build(Report(prompts: 3, toolCalls: 40, peakHour: 14));
        Assert.Equal(WrappedPersona.ToolWhisperer, s.Persona);
    }

    [Fact]
    public void Persona_LateNightPeak_IsNightOwl()
    {
        var s = Build(Report(peakHour: 23));
        Assert.Equal(WrappedPersona.NightOwl, s.Persona);
    }

    [Fact]
    public void Persona_MorningPeak_IsEarlyBird()
    {
        var s = Build(Report(peakHour: 7));
        Assert.Equal(WrappedPersona.EarlyBird, s.Persona);
    }

    [Fact]
    public void Persona_NoStandoutSignal_IsBuilder()
    {
        // No peak hour (all-zero hourly), light usage → the friendly default.
        var s = Build(Report(prompts: 2, toolCalls: 2));
        Assert.Equal(WrappedPersona.Builder, s.Persona);
    }

    [Fact]
    public void Equivalences_TranslateTokensAndTimeIntoFunUnits()
    {
        // 12M tokens ≈ 9M words ≈ 100 novels; 600 active minutes = 10h ≈ 5 movies.
        var s = Build(Report(activeMinutes: 600, tokens: 12_000_000));
        Assert.Contains(s.Equivalences, e => e.Text.Contains("novel"));
        Assert.Contains(s.Equivalences, e => e.Text.Contains("movie"));
    }

    [Fact]
    public void Highlights_AreCappedAtThree()
    {
        var report = Report(subAgents: 4, peakHour: 23);
        var range = new RangeReport("All time", "Active per day", report,
            new List<DayPoint>(), ActiveDays: 30, StreakDays: 12,
            BusiestDay: new DateOnly(2026, 3, 14), BusiestDayActive: TimeSpan.FromHours(6),
            LongestSession: TimeSpan.FromHours(4), FirstActiveDay: new DateOnly(2025, 9, 1));
        var s = Build(report, range);
        Assert.True(s.Highlights.Count <= 3);
    }

    [Fact]
    public void TopModel_DropsClaudePrefixAndDatedSuffix()
    {
        var s = Build(Report(tokens: 100, topModel: "claude-opus-4-8-20251101"));
        Assert.Equal("opus-4-8", s.TopModel);
    }
}
