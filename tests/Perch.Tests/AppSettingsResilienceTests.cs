using System.Linq;
using System.Text.Json;
using Perch.Data;
using Xunit;

namespace Perch.Tests;

/// <summary>
/// Guards the settings file against the "one bad value wipes everything" failure: the persisted enums
/// (<see cref="StartMode"/>, <see cref="OverlayPresentationMode"/>, <see cref="OverlaySection"/>, …) all live in
/// one <see cref="AppSettings"/> file, so with the stock string-enum converter a single value the current build
/// doesn't recognise — e.g. a settings.json written by a newer or feature-branch build — makes the WHOLE file
/// fail to deserialize. <see cref="AppSettings.Load"/> would then fall back to fresh defaults, silently losing
/// every setting AND (because a fresh AppSettings has FirstRunComplete=false) re-running the first-run Quick
/// Start. The <see cref="TolerantStringEnumConverter{T}"/> makes an unknown value degrade to the default member
/// instead of throwing, so the rest of the file survives. Exercises the serializer directly (never
/// AppSettings.Load, which touches the real per-user settings.json) — same discipline as AppSettingsStartModeTests.
/// </summary>
public class AppSettingsResilienceTests
{
    private static AppSettings Read(string json) => JsonSerializer.Deserialize<AppSettings>(json)!;

    [Fact]
    public void UnknownEnumString_FallsBackToDefault_WithoutThrowing()
    {
        var s = Read("""{ "StartMode": "SomeFutureMode", "OverlayMode": "Docked" }""");
        Assert.Equal(StartMode.Off, s.StartMode);                        // unknown -> default, no throw
        Assert.Equal(OverlayPresentationMode.Docked, s.OverlayMode);     // a known sibling still round-trips
    }

    [Fact]
    public void UnknownSectionInOrder_DoesNotThrow_AndNormalisesAway()
    {
        // The exact shape that used to blow up: a section-order list carrying a name this build doesn't have.
        var s = Read("""{ "SectionOrder": ["Todo", "NotARealSection", "Friends"] }""");
        Assert.NotNull(s.SectionOrder);   // parsed rather than throwing the whole file away

        var normalized = OverlaySectionOrder.Normalize(s.SectionOrder);
        Assert.Contains(OverlaySection.Todo, normalized);
        Assert.Contains(OverlaySection.Friends, normalized);
        Assert.Equal(normalized.Distinct().Count(), normalized.Count);   // known-only, deduped
    }

    [Fact]
    public void OneBadEnum_DoesNotResetTheRestOfTheFile_NorReRunOnboarding()
    {
        // A realistic file with a stray enum plus real settings: everything else must survive, and crucially
        // FirstRunComplete must be preserved so a returning user is never re-shown the Quick Start.
        var s = Read("""
            {
              "StartMode": "SomeFutureMode",
              "FirstRunComplete": true,
              "MaxFriendsShown": 7,
              "ActiveThemeId": "nord-dark"
            }
            """);
        Assert.True(s.FirstRunComplete);
        Assert.Equal(7, s.MaxFriendsShown);
        Assert.Equal("nord-dark", s.ActiveThemeId);
        Assert.Equal(StartMode.Off, s.StartMode);
    }

    [Fact]
    public void NumericEnum_KnownValueKept_UnknownFallsBack()
    {
        Assert.Equal(StartMode.OnSessionStart, Read("""{ "StartMode": 1 }""").StartMode);
        Assert.Equal(StartMode.Off, Read("""{ "StartMode": 999 }""").StartMode);
    }

    [Fact]
    public void KnownEnums_StillSerialiseByName()
    {
        // The tolerant converter must keep writing the member name (perch-hook's string parser depends on it,
        // and files round-trip). Covers the docked/floating mode alongside the existing StartMode coverage.
        var json = JsonSerializer.Serialize(new AppSettings { OverlayMode = OverlayPresentationMode.Docked });
        Assert.Contains("\"OverlayMode\":\"Docked\"", json);
    }
}
