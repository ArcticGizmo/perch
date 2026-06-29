using Perch.Data;
using Xunit;

namespace Perch.Tests;

public class TeamReaderTests
{
    private const string Cwd = TestEnvironment.FixtureCwd;

    private static string TeamSessionPath()
    {
        var path = TranscriptLocator.Resolve("sessTeam", Cwd);
        Assert.NotNull(path);
        return path!;
    }

    [Fact]
    public void GetTeammates_ReturnsRosterWithColours_ExcludingPlainSubAgents()
    {
        var roster = TeamReader.GetTeammates(TeamSessionPath());

        // Two teammates only (the two plain Explore sub-agents in the same dir are not teammates),
        // ordered by name: "devils-advocate" before "ux-explorer".
        Assert.Equal(2, roster.Count);
        Assert.Equal("devils-advocate", roster[0].Name);
        Assert.Equal("yellow", roster[0].Color);
        Assert.Equal("ux-explorer", roster[1].Name);
        Assert.Equal("blue", roster[1].Color);
    }

    [Fact]
    public void GetTeammates_EmptyForSessionWithoutATeam()
    {
        // sessBg has a subagents/ dir but only an ordinary (non-teammate) sub-agent.
        var path = TranscriptLocator.Resolve("sessBg", Cwd);
        Assert.NotNull(path);
        Assert.Empty(TeamReader.GetTeammates(path!));

        // sessA has no subagents/ dir at all.
        var plain = TranscriptLocator.Resolve("sessA", Cwd);
        Assert.NotNull(plain);
        Assert.Empty(TeamReader.GetTeammates(plain!));
    }

    [Fact]
    public void ParseContributions_CountsTeammatesAndRollsTokensByDay()
    {
        var byDay = TeamReader.ParseContributions(TeamSessionPath(), null, DateTime.MaxValue);

        // Both teammates' records fall on a single local day (a tight midday-UTC window).
        var day = Assert.Single(byDay.Values);

        Assert.Equal(2, day.Teammates);
        // aux-explorer (100,20,30,0) + devils-advocate (80,10,0,0).
        Assert.Equal(new TokenTotals(180, 30, 30, 0), day.Tokens);
    }

    [Fact]
    public void ParseContributions_EmptyForSessionWithoutATeam()
    {
        var path = TranscriptLocator.Resolve("sessBg", Cwd);
        Assert.NotNull(path);
        Assert.Empty(TeamReader.ParseContributions(path!, null, DateTime.MaxValue));
    }

    [Fact]
    public void ReportAllTime_SurfacesTeammateCountAndTokensSeparately()
    {
        var report = SessionStatsService.ReportAllTime(DateOnly.FromDateTime(DateTime.Now)).Totals;

        // sessTeam is the only fixture that ran a team: 2 teammates, tokens kept out of the main total.
        Assert.Equal(2, report.Teammates);
        Assert.Equal(new TokenTotals(180, 30, 30, 0), report.TeammateTokens);
    }
}
