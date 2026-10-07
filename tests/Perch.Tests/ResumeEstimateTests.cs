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

    // Claude Code caches with the 1-hour TTL, so a session idle for half an hour still resumes warm — the
    // estimate used to assume the 5-minute TTL and price every such resume as a full cold re-write.
    [Theory]
    [InlineData(2, "Warm")]
    [InlineData(30, "Warm")]      // well inside the hour-long cache
    [InlineData(55, "Cooling")]   // the last minutes before the hour: a maybe
    [InlineData(180, "Cold")]     // beyond an hour
    public void Warmth_TracksIdleTime(int idleMinutes, string expected)
    {
        var est = ResumeEstimate.Compute(50_000, Window(), TimeSpan.FromMinutes(idleMinutes));
        Assert.Equal(expected, est.Warmth.ToString());
    }

    [Fact]
    public void Cost_ColdReWritesTheContextIntoTheHourCache()
    {
        // 100k tokens on Opus 4.8 ($5/Mtok input): cold = a 1-hour cache write, 100k*5*2/1e6 = $1.00;
        // warm = a cache read, 100k*5*0.10/1e6 = $0.05.
        var est = ResumeEstimate.Compute(100_000, Window(200_000, "claude-opus-4-8"), TimeSpan.FromHours(3));
        Assert.Equal(1.00m, est.ColdCostUsd);
        Assert.Equal(0.05m, est.WarmCostUsd);
        Assert.Equal(est.ColdCostUsd, est.LikelyCostUsd);   // cold session → the cold figure is the likely one
    }

    [Fact]
    public void Cost_UsesTheModelsOwnPrices()
    {
        // Opus 5.5 is $4/Mtok input with reads at $0.20 (0.05×): 300k idle 20 min reads back for $0.06.
        var est = ResumeEstimate.Compute(300_000, Window(1_000_000, "claude-opus-5-5"), TimeSpan.FromMinutes(20));
        Assert.Equal(0.06m, est.WarmCostUsd);
        Assert.Equal(2.40m, est.ColdCostUsd);
        Assert.Equal(est.WarmCostUsd, est.LikelyCostUsd);
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
    public void FiveHourPercent_WeighsTheContextByItsLikelyCachePrice()
    {
        long ctx = ResumeEstimate.AssumedFiveHourInputTokens / 10;   // a tenth of the assumed window, unweighted
        // Warm: read back at 0.1× → 1%. Cold: re-written at 2× → 20%.
        var warm = ResumeEstimate.Compute(ctx, Window(), TimeSpan.FromMinutes(1));
        var cold = ResumeEstimate.Compute(ctx, Window(), TimeSpan.FromHours(3));
        Assert.Equal(1.0, warm.FiveHourPercent, precision: 3);
        Assert.Equal(20.0, cold.FiveHourPercent, precision: 3);
    }

    [Fact]
    public void NoUsage_HasNoData()
    {
        var est = ResumeEstimate.Compute(0, Window(), TimeSpan.FromMinutes(1));
        Assert.False(est.HasData);
    }
}
