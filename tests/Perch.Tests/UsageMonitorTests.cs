using System.Text.Json.Nodes;
using Perch.Data;
using Xunit;

namespace Perch.Tests;

/// <summary>
/// Covers <see cref="UsageMonitor.Parse"/> — the pure half of the usage poll. The payloads here are
/// trimmed from a real /api/oauth/usage response, so they keep the shapes that actually bite: the
/// scoped weekly bucket living only in <c>limits[]</c> (the top-level <c>seven_day_opus</c> etc. are
/// null even when one exists) and its <c>resets_at</c> coming back null.
/// </summary>
public class UsageMonitorTests
{
    private const string FullPayload = """
    {
      "five_hour": {"utilization": 14.0, "resets_at": "2026-07-25T05:49:59.643379+00:00"},
      "seven_day": {"utilization": 100.0, "resets_at": "2026-07-25T01:59:59.643405+00:00"},
      "seven_day_opus": null,
      "seven_day_sonnet": null,
      "extra_usage": {
        "is_enabled": true, "monthly_limit": 10000, "used_credits": 1250,
        "currency": "AUD", "decimal_places": 2, "spend_limit_reached": false
      },
      "limits": [
        {"kind": "session", "group": "session", "percent": 14, "resets_at": "2026-07-25T05:49:59.643379+00:00", "scope": null},
        {"kind": "weekly_all", "group": "weekly", "percent": 100, "resets_at": "2026-07-25T01:59:59.643405+00:00", "scope": null},
        {"kind": "weekly_scoped", "group": "weekly", "percent": 0, "resets_at": null,
         "scope": {"model": {"id": null, "display_name": "Fable"}, "surface": null}}
      ]
    }
    """;

    private static UsageInfo Parse(string json) => UsageMonitor.Parse(JsonNode.Parse(json)!.AsObject());

    [Fact]
    public void ReadsTheTwoAccountWideWindows()
    {
        var usage = Parse(FullPayload);

        Assert.True(usage.Ok);
        Assert.Equal(14.0, usage.FiveHourPercent);
        Assert.Equal(100.0, usage.SevenDayPercent);
        Assert.Equal(
            new DateTimeOffset(2026, 7, 25, 1, 59, 59, 643, TimeSpan.Zero).LocalDateTime,
            usage.SevenDayResetsAt!.Value,
            TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void ReadsTheScopedWeeklyWindowFromTheLimitsArray()
    {
        var usage = Parse(FullPayload);

        var scoped = Assert.Single(usage.Scoped);
        Assert.Equal("Fable", scoped.Label);
        Assert.Equal(0, scoped.Percent);
    }

    [Fact]
    public void ScopedWindowBorrowsTheWeeklyResetWhenItsOwnIsNull()
    {
        var usage = Parse(FullPayload);

        // The payload's scoped entry has resets_at: null — without the fallback the bar would lose its
        // expected-rate marker, so this is the behaviour that keeps it in step with the Weekly bar.
        Assert.Equal(usage.SevenDayResetsAt, Assert.Single(usage.Scoped).ResetsAt);
    }

    [Fact]
    public void ScopedWindowKeepsItsOwnResetWhenTheEndpointSuppliesOne()
    {
        var usage = Parse("""
        {
          "seven_day": {"utilization": 50.0, "resets_at": "2026-07-25T01:59:59+00:00"},
          "limits": [
            {"kind": "weekly_scoped", "percent": 12, "resets_at": "2026-07-28T09:00:00+00:00",
             "scope": {"model": {"display_name": "Fable"}}}
          ]
        }
        """);

        var scoped = Assert.Single(usage.Scoped);
        Assert.Equal(
            new DateTimeOffset(2026, 7, 28, 9, 0, 0, TimeSpan.Zero).LocalDateTime,
            scoped.ResetsAt!.Value,
            TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void IgnoresNonScopedLimitEntries()
    {
        // session / weekly_all are already covered by the top-level windows; picking them up here
        // would draw duplicate bars.
        Assert.Empty(Parse("""
        {
          "five_hour": {"utilization": 1.0},
          "limits": [
            {"kind": "session", "percent": 14, "scope": null},
            {"kind": "weekly_all", "percent": 100, "scope": null}
          ]
        }
        """).Scoped);
    }

    [Fact]
    public void SkipsScopedEntriesWithNoModelName()
    {
        // Nothing to caption a bar with, so it must not produce a nameless row.
        Assert.Empty(Parse("""
        {"limits": [{"kind": "weekly_scoped", "percent": 5, "scope": {"model": {"display_name": null}}}]}
        """).Scoped);
    }

    [Fact]
    public void ReadsTheMonthlyExtraUsageSpendWindow()
    {
        var e = Parse(FullPayload).ExtraUsage;

        Assert.NotNull(e);
        Assert.True(e!.Enabled);
        // Raw payload is minor units (1250 / 10000 at decimal_places 2) → $12.50 spent of a $100.00 cap.
        Assert.Equal(12.5m, e.Used);
        Assert.Equal(100m, e.Limit);
        Assert.Equal("AUD", e.Currency);
        Assert.False(e.LimitReached);
        // 12.5 of 100 → 12.5%; the whole limit drops its ".00" but the fractional spend keeps precision.
        Assert.Equal(12.5, e.Percent);
        Assert.Equal("$12.5/$100 AUD", e.Compact);
    }

    [Theory]
    // A $14,000.00 cap arrives as 1_400_000 minor units; shifting by decimal_places is what was missing.
    [InlineData("""{"extra_usage": {"is_enabled": true, "monthly_limit": 14000000, "used_credits": 500000, "currency": "USD", "decimal_places": 2}}""",
        5000, 140000, "$5k/$140k USD")]
    // Zero-decimal currencies (JPY) carry no minor units, so no shift — and the yen symbol resolves.
    [InlineData("""{"extra_usage": {"is_enabled": true, "monthly_limit": 14000, "used_credits": 3000, "currency": "JPY", "decimal_places": 0}}""",
        3000, 14000, "¥3k/¥14k JPY")]
    public void ExtraUsageAmountsAreShiftedByDecimalPlaces(
        string json, int expectUsed, int expectLimit, string expectCompact)
    {
        var e = Parse(json).ExtraUsage!;

        Assert.Equal((decimal)expectUsed, e.Used);
        Assert.Equal((decimal)expectLimit, e.Limit);
        Assert.Equal(expectCompact, e.Compact);
    }

    [Fact]
    public void ExtraUsageIsNullWhenTheBlockIsAbsent()
    {
        Assert.Null(Parse("""{"five_hour": {"utilization": 1.0}}""").ExtraUsage);
    }

    [Theory]
    [InlineData(0, 100, "$0/$100 AUD")]            // whole amounts drop the decimals
    [InlineData(12.5, 100, "$12.5/$100 AUD")]      // sub-1k fractional keeps precision
    [InlineData(12500, 140000, "$12.5k/$140k AUD")] // five/six figures scale to k
    [InlineData(1400000, 2000000, "$1.4M/$2M AUD")] // and to M past a million
    public void CompactScalesLargeAmountsToKAndM(double used, double limit, string expected)
    {
        var e = new ExtraUsageInfo(true, (decimal)used, (decimal)limit, "AUD", 2, false);
        Assert.Equal(expected, e.Compact);
    }

    [Fact]
    public void ToleratesAPayloadWithNoLimitsArray()
    {
        var usage = Parse("""{"five_hour": {"utilization": 3.0}, "seven_day": {"utilization": 7.0}}""");

        Assert.Empty(usage.Scoped);
        Assert.Equal(3.0, usage.FiveHourPercent);
    }
}
