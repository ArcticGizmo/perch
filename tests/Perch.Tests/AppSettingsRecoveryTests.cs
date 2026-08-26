using Perch.Data;
using Xunit;

namespace Perch.Tests;

/// <summary>
/// Guards the settings-file recovery path (see AppSettings.SalvageMerge): a file that no longer
/// deserializes as a whole must be salvaged property-by-property — a broken settings file is usually only
/// off by one value and still carries plenty of good ones — and must never re-run the first-run Quick
/// Start on someone who's clearly used Perch before.
/// </summary>
public class AppSettingsRecoveryTests
{
    [Fact]
    public void OneBrokenPropertySalvagesEverythingElse()
    {
        // QuickLinks as a number breaks whole-object deserialization; the neighbours must survive.
        var s = AppSettings.SalvageMerge(
            """{"ShowUsage": false, "QuickLinks": 5, "JiraSubdomain": "acme", "StatsActiveIdleMinutes": 9}""");

        Assert.False(s.ShowUsage);
        Assert.Equal("acme", s.JiraSubdomain);
        Assert.Equal(9, s.StatsActiveIdleMinutes);
        Assert.Null(s.QuickLinks);                 // the broken bit falls back (re-seeded by migration)
        Assert.True(s.FirstRunComplete);           // a file existing is proof this isn't a first run
    }

    [Fact]
    public void ReadableFirstRunCompleteWinsOverTheSeed()
    {
        // A fresh install that quit mid-onboarding (explicit false) keeps its wizard on the next launch,
        // even when another property is broken.
        var s = AppSettings.SalvageMerge("""{"FirstRunComplete": false, "QuickLinks": 5}""");
        Assert.False(s.FirstRunComplete);
    }

    [Fact]
    public void UnknownPropertiesAreIgnored()
    {
        var s = AppSettings.SalvageMerge("""{"SomeFutureSetting": true, "ShowNotes": true}""");
        Assert.True(s.ShowNotes);
        Assert.True(s.FirstRunComplete);
    }

    [Fact]
    public void GarbageFallsBackToDefaultsButNeverToTheWizard()
    {
        // A torn write isn't even JSON: nothing to salvage, but the file existed — no Quick Start.
        var s = AppSettings.SalvageMerge("{\"ShowUsage\": fal");
        Assert.True(s.ShowUsage);                  // default
        Assert.True(s.FirstRunComplete);
    }

    [Fact]
    public void BadEnumValueDegradesToDefaultViaTheTolerantConverter()
    {
        // Enum values already degrade through TolerantStringEnumConverter, so they don't even need the
        // salvage path — but if they arrive alongside a genuinely broken property, both must be survivable.
        var s = AppSettings.SalvageMerge(
            """{"OverlayMode": "HoloDeck", "ShowTaskProgress": false, "QuickLinks": 5}""");
        Assert.Equal(OverlayPresentationMode.Floating, s.OverlayMode);
        Assert.False(s.ShowTaskProgress);
    }

    [Fact]
    public void SalvageOfAHealthyFileMatchesStrictDeserialization()
    {
        // Optimism must not distort: run a normal round-trip through the merge and compare a spread of values.
        var original = new AppSettings
        {
            ShowUsage = false,
            JiraSubdomain = "acme",
            StatsActiveIdleMinutes = 12,
            OverlayMode = OverlayPresentationMode.Docked,
            BasketballEnabled = true,
            FirstRunComplete = true,
        };
        var s = AppSettings.SalvageMerge(System.Text.Json.JsonSerializer.Serialize(original));

        Assert.False(s.ShowUsage);
        Assert.Equal("acme", s.JiraSubdomain);
        Assert.Equal(12, s.StatsActiveIdleMinutes);
        Assert.Equal(OverlayPresentationMode.Docked, s.OverlayMode);
        Assert.True(s.BasketballEnabled);
        Assert.True(s.FirstRunComplete);
    }
}
