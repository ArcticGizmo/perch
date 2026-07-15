using Perch.Data;
using Xunit;

namespace Perch.Tests;

public class StatusMonitorTests
{
    // A healthy summary: indicator "none", no incidents → all-clear, nothing to surface.
    [Fact]
    public void Parse_AllOperational_HasNoIssue()
    {
        const string json = """
        {
          "page": { "url": "https://status.claude.com" },
          "status": { "indicator": "none", "description": "All Systems Operational" },
          "components": [ { "name": "claude.ai", "status": "operational" } ],
          "incidents": [],
          "scheduled_maintenances": []
        }
        """;

        var info = StatusMonitor.Parse(json);

        Assert.NotNull(info);
        Assert.Equal(StatusLevel.None, info!.Level);
        Assert.False(info.HasIssue);
        Assert.Empty(info.Incidents);
        Assert.Equal("https://status.claude.com", info.PageUrl);
    }

    // A live outage: indicator "major" + one unresolved incident with updates → surfaced, newest update
    // body picked, shortlink carried through for the menu.
    [Fact]
    public void Parse_MajorOutage_SurfacesIncident()
    {
        const string json = """
        {
          "page": { "url": "https://status.claude.com" },
          "status": { "indicator": "major", "description": "Partial System Outage" },
          "incidents": [
            {
              "name": "Elevated errors on the API",
              "impact": "major",
              "status": "investigating",
              "shortlink": "https://stspg.io/abc",
              "incident_updates": [
                { "status": "investigating", "body": "We are investigating." },
                { "status": "investigating", "body": "Older note." }
              ]
            }
          ],
          "scheduled_maintenances": []
        }
        """;

        var info = StatusMonitor.Parse(json);

        Assert.NotNull(info);
        Assert.Equal(StatusLevel.Major, info!.Level);
        Assert.True(info.HasIssue);
        var inc = Assert.Single(info.Incidents);
        Assert.Equal("Elevated errors on the API", inc.Name);
        Assert.Equal("major", inc.Impact);
        Assert.Equal("We are investigating.", inc.LatestUpdate); // newest-first
        Assert.Equal("https://stspg.io/abc", inc.Url);
    }

    // In-progress maintenance is surfaced; scheduled-but-not-started maintenance is not.
    [Fact]
    public void Parse_OnlyInProgressMaintenanceIsSurfaced()
    {
        const string json = """
        {
          "page": { "url": "https://status.claude.com" },
          "status": { "indicator": "maintenance", "description": "Scheduled Maintenance In Progress" },
          "incidents": [],
          "scheduled_maintenances": [
            { "name": "Database upgrade", "impact": "maintenance", "status": "in_progress", "incident_updates": [] },
            { "name": "Future window", "impact": "maintenance", "status": "scheduled", "incident_updates": [] }
          ]
        }
        """;

        var info = StatusMonitor.Parse(json);

        Assert.NotNull(info);
        Assert.Equal(StatusLevel.Maintenance, info!.Level);
        Assert.True(info.HasIssue);
        var m = Assert.Single(info.Incidents);
        Assert.Equal("Database upgrade", m.Name);
    }

    // A missing shortlink falls back to the status page URL so the menu link always works.
    [Fact]
    public void Parse_IncidentWithoutShortlink_FallsBackToPageUrl()
    {
        const string json = """
        {
          "page": { "url": "https://status.claude.com" },
          "status": { "indicator": "minor", "description": "Minor Service Outage" },
          "incidents": [ { "name": "Slow responses", "impact": "minor", "status": "monitoring" } ]
        }
        """;

        var info = StatusMonitor.Parse(json);

        Assert.NotNull(info);
        var inc = Assert.Single(info!.Incidents);
        Assert.Equal("https://status.claude.com", inc.Url);
        Assert.Null(inc.LatestUpdate);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("[]")] // valid JSON, wrong shape (array, not object)
    public void Parse_Malformed_ReturnsNull(string json)
    {
        Assert.Null(StatusMonitor.Parse(json));
    }
}
