using System;
using Perch.Data;
using Perch.Data.Control;
using Xunit;

namespace Perch.Tests;

/// <summary>The pre-resume cost estimate behind the launcher's recent-session rows (SessionWindow) and the
/// investigation that prompted it: what resuming a session re-sends, and whether its cache is likely warm.</summary>
public class ResumeEstimateTests
{
    private static ContextWindowInfo Window(int tokens = 200_000, string? model = "claude-opus-4-8") =>
        new(tokens, model, ContextWindowSource.ModelId);

    [Fact]
    public void ContextPercent_IsShareOfWindow()
    {
        var est = ResumeEstimate.Compute(50_000, Window(200_000), TimeSpan.FromMinutes(1));
        Assert.Equal(25.0, est.ContextPercent, precision: 3);
        Assert.True(est.HasData);
    }

    [Theory]
    [InlineData(2, "Warm")]      // inside the 5-min TTL
    [InlineData(30, "Cooling")]  // past 5 min, within the hour
    [InlineData(180, "Cold")]    // beyond an hour
    public void Warmth_TracksIdleTime(int idleMinutes, string expected)
    {
        var est = ResumeEstimate.Compute(50_000, Window(), TimeSpan.FromMinutes(idleMinutes));
        Assert.Equal(expected, est.Warmth.ToString());
    }

    [Fact]
    public void Cost_ColdReBillsHigherThanWarmReads()
    {
        // 100k tokens on Opus ($5/Mtok input): cold ~ 100k*5*1.25/1e6 = $0.625; warm ~ *0.10 = $0.05.
        var est = ResumeEstimate.Compute(100_000, Window(200_000, "claude-opus-4-8"), TimeSpan.FromHours(3));
        Assert.Equal(0.625m, est.ColdCostUsd);
        Assert.Equal(0.05m, est.WarmCostUsd);
        Assert.Equal(est.ColdCostUsd, est.LikelyCostUsd);   // cold session → the cold figure is the likely one
    }

    [Fact]
    public void Cost_NullForUnknownModel()
    {
        var est = ResumeEstimate.Compute(100_000, Window(model: "some-future-model"), TimeSpan.FromMinutes(1));
        Assert.Null(est.ColdCostUsd);
        Assert.Null(est.WarmCostUsd);
        Assert.Null(est.LikelyCostUsd);
    }

    [Fact]
    public void FiveHourPercent_IsAgainstTheAssumedBudget()
    {
        long ctx = ResumeEstimate.AssumedFiveHourInputTokens / 10;   // exactly a tenth of the assumed window
        var est = ResumeEstimate.Compute(ctx, Window(), TimeSpan.FromMinutes(1));
        Assert.Equal(10.0, est.FiveHourPercent, precision: 3);
    }

    [Fact]
    public void NoUsage_HasNoData()
    {
        var est = ResumeEstimate.Compute(0, Window(), TimeSpan.FromMinutes(1));
        Assert.False(est.HasData);
    }
}
