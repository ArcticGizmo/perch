using System.Text.Json;
using Perch.Data;
using Xunit;

namespace Perch.Tests;

/// <summary>
/// Guards the onboarding tier model: every managed id is a real boolean toggle, the presets are strict
/// supersets, applying a tier (or a custom set) writes exactly the managed universe and nothing else, and the
/// first-run seed migration only fires for pre-onboarding settings files. See
/// docs/onboarding-quickstart-plan.md (milestone M0).
/// </summary>
public class OnboardingTiersTests
{
    // ── Catalog integrity ────────────────────────────────────────────────────

    [Fact]
    public void EveryManagedIdIsARealBooleanToggle()
    {
        foreach (var id in OnboardingTiers.Managed)
        {
            var d = SettingsRegistry.ById(id);
            Assert.True(d != null, $"Managed onboarding id '{id}' is not in SettingsRegistry.");
            Assert.Equal(SettingKind.Toggle, d!.Kind);
            Assert.NotNull(d.SetBool);
            Assert.NotNull(d.GetBool);
        }
    }

    [Fact]
    public void ManagedIdsAreUnique()
    {
        var dupes = OnboardingTiers.Managed
            .GroupBy(id => id)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();

        Assert.True(dupes.Count == 0, $"Duplicate onboarding ids: {string.Join(", ", dupes)}");
    }

    [Fact]
    public void ManagedUniverseIsExactlyTheGroups()
    {
        var fromGroups = OnboardingTiers.Groups.SelectMany(g => g.Items).Select(i => i.Id).ToList();
        Assert.Equal(fromGroups, OnboardingTiers.Managed);
    }

    // The wizard deliberately does not manage these — a regression that pulled them in would let a re-run
    // clobber configuration it shouldn't, or offer a mode a platform can't honour.
    [Theory]
    [InlineData("overlay-mode")]      // chosen on its own placement step
    [InlineData("agent-teams")]       // writes Claude Code's settings.json, not an AppSettings property
    [InlineData("quick-links")]       // a list, not a toggle
    [InlineData("theme")]             // a dropdown
    [InlineData("detect-error-streaks")] // stuck-detection sub-heuristic, left at default
    [InlineData("git-review-split")]  // git-tree display sub-preference
    public void ExcludedIdsAreNotManaged(string id)
        => Assert.DoesNotContain(id, OnboardingTiers.Managed);

    // ── Preset shape ─────────────────────────────────────────────────────────

    [Fact]
    public void PresetsAreStrictSupersets()
    {
        var basic = OnboardingTiers.Preset(OnboardingTier.Basic).ToHashSet();
        var inter = OnboardingTiers.Preset(OnboardingTier.Intermediate).ToHashSet();
        var kitchen = OnboardingTiers.Preset(OnboardingTier.KitchenSink).ToHashSet();

        Assert.True(basic.IsProperSubsetOf(inter), "Basic must be a strict subset of Intermediate.");
        Assert.True(inter.IsProperSubsetOf(kitchen), "Intermediate must be a strict subset of Kitchen sink.");
    }

    [Fact]
    public void KitchenSinkTurnsOnTheWholeManagedUniverse()
    {
        Assert.True(OnboardingTiers.Preset(OnboardingTier.KitchenSink).SetEquals(OnboardingTiers.Managed));
        Assert.Equal(OnboardingTiers.Managed.Count, OnboardingTiers.Count(OnboardingTier.KitchenSink));
    }

    [Fact]
    public void CountsAscendAcrossTiers()
    {
        Assert.True(OnboardingTiers.Count(OnboardingTier.Basic) < OnboardingTiers.Count(OnboardingTier.Intermediate));
        Assert.True(OnboardingTiers.Count(OnboardingTier.Intermediate) < OnboardingTiers.Count(OnboardingTier.KitchenSink));
    }

    [Fact]
    public void BasicIsCalm_NoPlayfulFeaturesOn()
    {
        var basic = OnboardingTiers.Preset(OnboardingTier.Basic);
        foreach (var id in basic)
            Assert.False(SettingsRegistry.ById(id)!.Playful, $"Basic should not enable playful '{id}'.");
    }

    // ── Apply ────────────────────────────────────────────────────────────────

    [Fact]
    public void ApplyTierSetsExactlyThePreset()
    {
        var s = new AppSettings();
        OnboardingTiers.Apply(s, OnboardingTier.Intermediate);

        var expected = OnboardingTiers.Preset(OnboardingTier.Intermediate);
        foreach (var id in OnboardingTiers.Managed)
        {
            var on = SettingsRegistry.ById(id)!.GetBool!(s);
            Assert.Equal(expected.Contains(id), on);
        }
    }

    [Fact]
    public void ApplyLowerTierAfterHigherTurnsExtrasBackOff()
    {
        var s = new AppSettings();
        OnboardingTiers.Apply(s, OnboardingTier.KitchenSink);
        OnboardingTiers.Apply(s, OnboardingTier.Basic);

        var basic = OnboardingTiers.Preset(OnboardingTier.Basic);
        foreach (var id in OnboardingTiers.Managed)
            Assert.Equal(basic.Contains(id), SettingsRegistry.ById(id)!.GetBool!(s));
    }

    [Fact]
    public void ApplyIsIdempotent()
    {
        var a = new AppSettings();
        var b = new AppSettings();
        OnboardingTiers.Apply(a, OnboardingTier.Intermediate);
        OnboardingTiers.Apply(b, OnboardingTier.Intermediate);
        OnboardingTiers.Apply(b, OnboardingTier.Intermediate); // twice

        foreach (var id in OnboardingTiers.Managed)
            Assert.Equal(SettingsRegistry.ById(id)!.GetBool!(a), SettingsRegistry.ById(id)!.GetBool!(b));
    }

    [Fact]
    public void ApplyCustomSetTurnsOnExactlyThatSet()
    {
        // Basic plus one Kitchen-sink-only feature — nothing else from Kitchen sink should leak in.
        var custom = OnboardingTiers.Preset(OnboardingTier.Basic).ToHashSet();
        custom.Add("social");

        var s = new AppSettings();
        OnboardingTiers.Apply(s, custom);

        Assert.True(SettingsRegistry.ById("social")!.GetBool!(s));
        Assert.False(SettingsRegistry.ById("media-controller")!.GetBool!(s)); // another kitchen-only id, still off
        foreach (var id in OnboardingTiers.Managed)
            Assert.Equal(custom.Contains(id), SettingsRegistry.ById(id)!.GetBool!(s));
    }

    [Fact]
    public void ApplyDoesNotTouchUnmanagedConfiguration()
    {
        var s = new AppSettings
        {
            JiraSubdomain = "acme",
            NtfyHost = "https://ntfy.example",
            WaitingTimerRedMinutes = 42,
            PullRequestIntervalMinutes = 17,
            MaxFriendsShown = 9,
            OverlayMode = OverlayPresentationMode.Docked,
            ActiveThemeId = "nord-dark",
        };

        OnboardingTiers.Apply(s, OnboardingTier.KitchenSink);

        Assert.Equal("acme", s.JiraSubdomain);
        Assert.Equal("https://ntfy.example", s.NtfyHost);
        Assert.Equal(42, s.WaitingTimerRedMinutes);
        Assert.Equal(17, s.PullRequestIntervalMinutes);
        Assert.Equal(9, s.MaxFriendsShown);
        Assert.Equal(OverlayPresentationMode.Docked, s.OverlayMode); // placement is its own step, not managed
        Assert.Equal("nord-dark", s.ActiveThemeId);
    }

    // ── MatchedTier ──────────────────────────────────────────────────────────

    [Fact]
    public void MatchedTierRecognisesEachPreset()
    {
        foreach (var tier in OnboardingTiers.AllTiers)
            Assert.Equal(tier, OnboardingTiers.MatchedTier(OnboardingTiers.Preset(tier)));
    }

    [Fact]
    public void MatchedTierIsNullForACustomMix()
    {
        var custom = OnboardingTiers.Preset(OnboardingTier.Basic).ToHashSet();
        custom.Add("social");
        Assert.Null(OnboardingTiers.MatchedTier(custom));
    }

    // ── First-run seed migration ─────────────────────────────────────────────

    [Fact]
    public void MigrateFirstRun_SeedsTrueForPreOnboardingFile()
    {
        // An older settings file: has other keys, but no FirstRunComplete.
        var json = """{ "ShowUsage": true, "StartMode": "Off" }""";
        var s = JsonSerializer.Deserialize<AppSettings>(json)!;
        Assert.False(s.FirstRunComplete);

        s.MigrateFirstRun(json);
        Assert.True(s.FirstRunComplete);
    }

    [Fact]
    public void MigrateFirstRun_LeavesExplicitFalseAlone()
    {
        // A file written by this version where onboarding was started but not finished.
        var json = """{ "FirstRunComplete": false, "ShowUsage": true }""";
        var s = JsonSerializer.Deserialize<AppSettings>(json)!;

        s.MigrateFirstRun(json);
        Assert.False(s.FirstRunComplete); // must still be able to show the wizard
    }

    [Fact]
    public void MigrateFirstRun_LeavesExplicitTrueAlone()
    {
        var json = """{ "FirstRunComplete": true }""";
        var s = JsonSerializer.Deserialize<AppSettings>(json)!;

        s.MigrateFirstRun(json);
        Assert.True(s.FirstRunComplete);
    }

    [Fact]
    public void MigrateFirstRun_ToleratesGarbage()
    {
        var s = new AppSettings();
        s.MigrateFirstRun("not json at all");
        Assert.False(s.FirstRunComplete); // unparseable → default, wizard still runs on a fresh install
    }

    [Fact]
    public void OnboardingTierChosenRoundTripsByName()
    {
        var s = new AppSettings { OnboardingTierChosen = OnboardingTier.KitchenSink };
        var json = JsonSerializer.Serialize(s);
        Assert.Contains("\"KitchenSink\"", json); // persisted by name, not ordinal
        var back = JsonSerializer.Deserialize<AppSettings>(json)!;
        Assert.Equal(OnboardingTier.KitchenSink, back.OnboardingTierChosen);
    }
}
