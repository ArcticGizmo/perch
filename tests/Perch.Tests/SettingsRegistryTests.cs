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
        // Custom themes are managed by the Appearance page's designer, not a catalogue control.
        nameof(AppSettings.CustomThemes),
        nameof(AppSettings.PendingUpdateVersion),
        nameof(AppSettings.LastSeenVersion),
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
