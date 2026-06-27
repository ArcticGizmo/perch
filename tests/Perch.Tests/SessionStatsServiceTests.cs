using Perch.Data;
using Xunit;

namespace Perch.Tests;

public class SessionStatsServiceTests
{
    private const string Cwd = TestEnvironment.FixtureCwd;

    private static SessionStatsService.SessionDayData ParseSingleDay(string sessionId)
    {
        var path = TranscriptLocator.Resolve(sessionId, Cwd);
        Assert.NotNull(path);
        var byDay = SessionStatsService.ParseSession(path!, null, DateTime.MaxValue);
        // Every fixture session is authored to fall on a single local day (a tight midday-UTC window),
        // so there is exactly one bucket regardless of the test machine's timezone.
        return Assert.Single(byDay.Values);
    }

    [Fact]
    public void ParseSession_CountsPromptsToolsAndTokens()
    {
        var data = ParseSingleDay("sessA");

        Assert.Equal(1, data.Prompts);                 // one authored user prompt (the /model line is isMeta)
        Assert.Equal(2, data.ToolCalls);               // Read + Bash
        Assert.Equal(0, data.SubAgents);               // no Task tool in sessA
        Assert.Equal(1, data.ToolCounts["Read"]);
        Assert.Equal(1, data.ToolCounts["Bash"]);

        // Input 100+5+5+50000, Output 50+5+5+10, CacheWrite 10, CacheRead 200+150000.
        Assert.Equal(new TokenTotals(50110, 70, 10, 150200), data.Tokens);

        Assert.Equal("proj", data.Project);
        Assert.Equal("main", data.Branch);
    }

    [Fact]
    public void ParseSession_AttributesTokensPerModel()
    {
        var data = ParseSingleDay("sessA");

        Assert.Equal(380, data.Models["claude-opus-4-8"].Total);    // 110+60+10+200
        Assert.Equal(200010, data.Models["claude-sonnet-4-6"].Total); // 50000+10+0+150000
    }

    [Fact]
    public void ParseSession_CountsTaskToolAsSubAgent()
    {
        var data = ParseSingleDay("sessB");
        Assert.Equal(2, data.SubAgents);   // tk1 + tk2 (both "Task")
        Assert.Equal(2, data.ToolCalls);
    }

    [Fact]
    public void ActiveSpan_SumsCappedGapsPlusTail()
    {
        SessionStatsService.IdleThreshold = TimeSpan.FromMinutes(5);

        var baseTime = new DateTime(2025, 3, 10, 12, 0, 0, DateTimeKind.Local);
        // Two 1-minute gaps (< threshold, uncapped) + 30s tail = 150s.
        var times = new List<DateTime> { baseTime, baseTime.AddMinutes(1), baseTime.AddMinutes(2) };
        Assert.Equal(TimeSpan.FromSeconds(150), SessionStatsService.ActiveSpan(times));

        // A 10-minute gap is capped at the 5-minute idle threshold, + 30s tail = 330s.
        var withGap = new List<DateTime> { baseTime, baseTime.AddMinutes(10) };
        Assert.Equal(TimeSpan.FromSeconds(330), SessionStatsService.ActiveSpan(withGap));
    }

    [Fact]
    public void ActiveSpan_ForSessA_Is510Seconds()
    {
        SessionStatsService.IdleThreshold = TimeSpan.FromMinutes(5);
        var data = ParseSingleDay("sessA");
        var times = new List<DateTime>(data.Times);
        times.Sort();
        Assert.Equal(TimeSpan.FromSeconds(510), SessionStatsService.ActiveSpan(times));
    }

    [Theory]
    [InlineData("claude-opus-4-8", 1_000_000, 0, 0, 0, 5.0)]        // input @ $5/M
    [InlineData("claude-opus-4-8", 0, 1_000_000, 0, 0, 25.0)]       // output @ $25/M
    [InlineData("claude-opus-4-8", 0, 0, 1_000_000, 0, 6.25)]       // cache write @ 1.25x input
    [InlineData("claude-opus-4-8", 0, 0, 0, 1_000_000, 0.5)]        // cache read @ 0.1x input
    [InlineData("claude-haiku-4-5-20251001", 1_000_000, 0, 0, 0, 1.0)]  // prefix match on "claude-haiku-4"
    public void CostOf_PricesKnownModels(string model, long input, long output, long cw, long cr, double expected)
    {
        var cost = SessionStatsService.CostOf(model, new TokenTotals(input, output, cw, cr));
        Assert.NotNull(cost);
        Assert.Equal((decimal)expected, cost!.Value);
    }

    [Fact]
    public void CostOf_NullForUnknownModel() =>
        Assert.Null(SessionStatsService.CostOf("some-future-model", new TokenTotals(1, 1, 1, 1)));

    [Fact]
    public void ReportAllTime_AggregatesAcrossEveryFixtureSession()
    {
        SessionStatsService.IdleThreshold = TimeSpan.FromMinutes(5);
        var report = SessionStatsService.ReportAllTime(DateOnly.FromDateTime(DateTime.Now));
        var totals = report.Totals;

        // Composition should see exactly the transcripts the locator enumerates.
        int transcriptCount = TranscriptLocator.EnumerateTranscripts().Count();
        Assert.Equal(transcriptCount, totals.SessionCount);
        Assert.True(totals.SessionCount >= 6, $"expected >= 6 fixture sessions, saw {totals.SessionCount}");

        Assert.True(totals.Prompts > 0);
        Assert.True(totals.ToolCalls > 0);
        Assert.True(totals.Tokens.Total > 0);
        Assert.True(totals.EstimatedCost > 0m);

        var models = totals.Models.Select(m => m.Model).ToList();
        Assert.Contains("claude-opus-4-8", models);
        Assert.Contains("claude-sonnet-4-6", models);
        Assert.Contains("claude-haiku-4-5-20251001", models);

        var tools = totals.Tools.Select(t => t.Tool).ToList();
        Assert.Contains("Read", tools);
        Assert.Contains("Bash", tools);
        Assert.Contains("Task", tools);
    }
}
