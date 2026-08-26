using System.Reflection;
using Perch.Data;
using Xunit;

namespace Perch.Tests;

/// <summary>
/// Guards the settings registry against drift: every user-facing <see cref="AppSettings"/> property must
/// have a descriptor, each descriptor's backing must name a real property, and the live toggle/stepper
/// bindings must actually read and write the property they claim to. If one of these fails, a setting was
/// added or renamed without updating <c>SettingsRegistry</c>.
/// </summary>
public class SettingsRegistryTests
{
    // Properties that are persisted in AppSettings but are not user-facing settings controls, so they
    // deliberately have no registry entry: the global scratch pad (edited from the sticky-note window),
    // the update-bookkeeping stamps, and the legacy keys kept only for one-time migration.
    private static readonly HashSet<string> NotSettings = new()
    {
        nameof(AppSettings.ScratchText),
        // The social region's expand/collapse is UI state toggled by the region's own chevron on the overlay,
        // not a Settings-window control.
        nameof(AppSettings.SocialRegionExpanded),
        // The Todo and Hypertree sections' expand/collapse are UI state toggled by each section's own
        // chevron on the overlay, not Settings-window controls.
        nameof(AppSettings.TodosExpanded),
        nameof(AppSettings.HypertreeExpanded),
        // The secret arcade's unlock flag and the daily Wordle's saved progress are hidden toy state, not
        // user-facing settings controls.
        nameof(AppSettings.ArcadeUnlocked),
        nameof(AppSettings.WordleState),
        // The desktop-basketball lifetime swish tally, painted on the backboard. Hidden toy state (the
        // feature itself is the "basketball" Whimsy toggle).
        nameof(AppSettings.BasketballHoops),
        // Quiet mode's deadline is hidden mode state, toggled from the overlay header's right-click menu
        // (not a Settings-window control). See Perch.Data.QuietMode.
        nameof(AppSettings.QuietUntil),
        // The emoji picker's most-recently-used list is runtime history it maintains itself, not a
        // Settings-window control. See EmojiPickerWindow + AppSettings.RecordRecentEmoji.
        nameof(AppSettings.RecentEmojis),
        // The overlay widths are set from the "Set initial placements…" editor (drag the preview's edge), not
        // a Settings-window control — like the placements themselves.
        nameof(AppSettings.FloatingWidthDip),
        nameof(AppSettings.DockedWidthDip),
        // The git Tree window's diff-view preferences (split vs unified, light mode, hunk staging) are set
        // from that window's own toolbar controls and persisted, not from a Settings-window control.
        nameof(AppSettings.GitReviewSplitView),
        nameof(AppSettings.GitTreeLight),
        nameof(AppSettings.GitTreeHunkStaging),
        // Custom themes are managed by the Appearance page's designer, not a catalogue control.
        nameof(AppSettings.CustomThemes),
        nameof(AppSettings.PendingUpdateVersion),
        nameof(AppSettings.LastSeenVersion),
        // First-run onboarding bookkeeping: whether the Quick Start has been completed, and the last starter
        // tier picked. Not user-facing catalogue controls (the wizard is re-run from the Getting Started
        // section). See docs/onboarding-quickstart-plan.md.
        nameof(AppSettings.FirstRunComplete),
        nameof(AppSettings.OnboardingTierChosen),
        nameof(AppSettings.AutoStartOnFirstSession),
        nameof(AppSettings.ShowGitKraken),
        nameof(AppSettings.ShowSlack),
        // Edited on the dedicated Shortcuts page (per-binding enable + key capture / terminal choice),
        // deliberately not catalogue cards.
        nameof(AppSettings.HotkeyToggleDense),
        nameof(AppSettings.HotkeyCycleSessions),
        nameof(AppSettings.HotkeyOpenSwitcher),
        nameof(AppSettings.HotkeyToggleDocked),
        nameof(AppSettings.ReopenTerminal),
    };

    private static IEnumerable<PropertyInfo> UserFacingProperties() =>
        typeof(AppSettings)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p is { CanRead: true, CanWrite: true } && !NotSettings.Contains(p.Name));

    [Fact]
    public void EverySettingHasADescriptor()
    {
        var covered = SettingsRegistry.All
            .SelectMany(d => d.Backing ?? [])
            .ToHashSet();

        var missing = UserFacingProperties()
            .Select(p => p.Name)
            .Where(name => !covered.Contains(name))
            .OrderBy(n => n)
            .ToList();

        Assert.True(missing.Count == 0,
            "AppSettings properties with no SettingsRegistry entry (add a descriptor, or list it in " +
            $"SettingsRegistryTests.NotSettings): {string.Join(", ", missing)}");
    }

    [Fact]
    public void EveryBackingNamesARealSettableProperty()
    {
        foreach (var d in SettingsRegistry.All)
        foreach (var backing in d.Backing ?? [])
        {
            var prop = typeof(AppSettings).GetProperty(backing, BindingFlags.Public | BindingFlags.Instance);
            Assert.True(prop is { CanWrite: true },
                $"Descriptor '{d.Id}' backs '{backing}', which is not a settable AppSettings property.");
        }
    }

    [Fact]
    public void DescriptorIdsAreUnique()
    {
        var dupes = SettingsRegistry.All
            .GroupBy(d => d.Id)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();

        Assert.True(dupes.Count == 0, $"Duplicate descriptor ids: {string.Join(", ", dupes)}");
    }

    // Docked mode reserves a screen edge through the OS, which only Windows can do, so its setting is
    // withheld everywhere else (see docs/macos-docked-mode-investigation.md). The descriptor still lives in
    // All — the coverage test above needs it, and a settings file written on Windows must still round-trip —
    // so the withholding happens in the Available/Search filters the settings surfaces go through.
    [Fact]
    public void OverlayModeRequiresEdgeReservation()
    {
        var overlayMode = SettingsRegistry.All.Single(d => d.Id == "overlay-mode");
        Assert.Equal(PlatformFeature.EdgeReservation, overlayMode.Requires);
    }

    [Fact]
    public void AvailableHidesSettingsThisPlatformCannotSupport()
    {
        bool NoEdgeReservation(PlatformFeature f) => f != PlatformFeature.EdgeReservation;

        Assert.Equal(SettingsRegistry.All, SettingsRegistry.Available(_ => true));

        var available = SettingsRegistry.Available(NoEdgeReservation).ToList();
        Assert.DoesNotContain(available, d => d.Id == "overlay-mode");
        // Nothing else is collateral damage: only the EdgeReservation-gated entries drop out.
        Assert.Equal(SettingsRegistry.All.Count(d => d.Requires == PlatformFeature.None), available.Count);

        // Search runs through the same gate, so a hidden setting can't be found by typing its name either.
        Assert.DoesNotContain(SettingsRegistry.Search("overlay mode", NoEdgeReservation), d => d.Id == "overlay-mode");
        Assert.Contains(SettingsRegistry.Search("overlay mode", _ => true), d => d.Id == "overlay-mode");
    }

    [Fact]
    public void ToggleBindingsReadAndWriteTheirBackingProperty()
    {
        foreach (var d in SettingsRegistry.All.Where(d => d.Kind == SettingKind.Toggle))
        {
            // A toggle not backed by an AppSettings property (e.g. an env var) binds through raw accessors.
            if (d.Backing is not { Length: > 0 })
            {
                Assert.NotNull(d.GetBoolRaw);
                Assert.NotNull(d.SetBoolRaw);
                continue;
            }

            Assert.NotNull(d.GetBool);
            Assert.NotNull(d.SetBool);
            Assert.Single(d.Backing!);

            var prop = typeof(AppSettings).GetProperty(d.Backing![0])!;
            Assert.Equal(typeof(bool), prop.PropertyType);

            var s = new AppSettings();

            // The property drives the getter…
            prop.SetValue(s, true);
            Assert.True(d.GetBool!(s), $"'{d.Id}' getter does not read {prop.Name}.");
            prop.SetValue(s, false);
            Assert.False(d.GetBool!(s), $"'{d.Id}' getter does not read {prop.Name}.");

            // …and the setter drives the property.
            d.SetBool!(s, true);
            Assert.True((bool)prop.GetValue(s)!, $"'{d.Id}' setter does not write {prop.Name}.");
        }
    }

    [Fact]
    public void StepperBindingsReadAndWriteTheirBackingProperty()
    {
        foreach (var d in SettingsRegistry.All.Where(d => d.Kind == SettingKind.Stepper))
        {
            Assert.NotNull(d.GetInt);
            Assert.NotNull(d.SetInt);
            Assert.Single(d.Backing!);

            var prop = typeof(AppSettings).GetProperty(d.Backing![0])!;
            Assert.Equal(typeof(int), prop.PropertyType);

            var s = new AppSettings();

            prop.SetValue(s, 7);
            Assert.Equal(7, d.GetInt!(s));

            d.SetInt!(s, 3);
            Assert.Equal(3, (int)prop.GetValue(s)!);
        }
    }
}
