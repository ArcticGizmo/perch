using Perch.Data;
using Xunit;

namespace Perch.Tests;

/// <summary>
/// Covers the Quiet-mode resolution layer: the active-window test, the duration → deadline policy, and the
/// masking of every <see cref="SettingDescriptor.Playful"/> feature (and only those) while quiet is active.
/// </summary>
public class QuietModeTests
{
    // The AppSettings properties Quiet mode is expected to silence. Guards against a feature being tagged
    // (or un-tagged) playful by accident — if this list and the registry disagree, one of them is wrong.
    private static readonly string[] ExpectedPlayful =
    {
        nameof(AppSettings.SocialEnabled),
        nameof(AppSettings.NotifyOnFriendPost),
        nameof(AppSettings.ShowLargeReactions),
        nameof(AppSettings.PerchReacts),
        nameof(AppSettings.NotifyOnAchievement),
        nameof(AppSettings.AchievementToasts),
        nameof(AppSettings.UpsideDownQuickLinks),
    };

    [Fact]
    public void IsActive_TrueOnlyForAFutureDeadline()
    {
        var now = new DateTime(2026, 1, 15, 20, 0, 0);
        Assert.False(QuietMode.IsActive(null, now));
        Assert.False(QuietMode.IsActive(now.AddMinutes(-1), now));
        Assert.False(QuietMode.IsActive(now, now));                 // boundary: exactly now is off
        Assert.True(QuietMode.IsActive(now.AddMinutes(1), now));
    }

    [Fact]
    public void DeadlineFor_MapsEachPreset()
    {
        var now = new DateTime(2026, 1, 15, 20, 0, 0);
        Assert.Null(QuietMode.DeadlineFor(QuietDuration.Off, now));
        Assert.Equal(now.AddMinutes(1), QuietMode.DeadlineFor(QuietDuration.Minute1, now));
        Assert.Equal(now.AddMinutes(30), QuietMode.DeadlineFor(QuietDuration.Minutes30, now));
        Assert.Equal(now.AddHours(1), QuietMode.DeadlineFor(QuietDuration.Hour1, now));
        Assert.Equal(now.AddHours(2), QuietMode.DeadlineFor(QuietDuration.Hours2, now));
    }

    [Theory]
    // Evening: the coming morning is tomorrow's 7am.
    [InlineData(2026, 1, 15, 20, 0, 2026, 1, 16, 7)]
    // Small hours before 7am: the coming morning is today's 7am.
    [InlineData(2026, 1, 15, 3, 0, 2026, 1, 15, 7)]
    // Exactly 7am counts as passed, so it rolls to tomorrow.
    [InlineData(2026, 1, 15, 7, 0, 2026, 1, 16, 7)]
    public void DeadlineFor_UntilMorning_IsTheNextSevenAm(
        int y, int mo, int d, int h, int mi, int ey, int emo, int ed, int eh)
    {
        var now = new DateTime(y, mo, d, h, mi, 0);
        Assert.Equal(new DateTime(ey, emo, ed, eh, 0, 0), QuietMode.DeadlineFor(QuietDuration.UntilMorning, now));
    }

    [Fact]
    public void Resolve_ReturnsTheSameInstanceWhenInactive()
    {
        var s = new AppSettings();
        Assert.Same(s, QuietMode.Resolve(s, quietActive: false));
    }

    [Fact]
    public void Resolve_MasksEveryPlayfulToggleAndLeavesTheRestAlone()
    {
        var raw = new AppSettings
        {
            // Playful — should all end up false.
            SocialEnabled = true,
            NotifyOnFriendPost = true,
            ShowLargeReactions = true,
            PerchReacts = true,
            NotifyOnAchievement = true,
            AchievementToasts = true,
            UpsideDownQuickLinks = true,
            // Not playful — should be untouched.
            ShowUsage = true,
            NotifyOnDone = true,
            ShowTodos = true,
            QuietUntil = new DateTime(2026, 1, 15, 21, 0, 0),
        };

        var eff = QuietMode.Resolve(raw, quietActive: true);

        // A copy, not the original.
        Assert.NotSame(raw, eff);
        Assert.True(raw.SocialEnabled, "Resolve must not mutate the raw settings.");

        // Every playful toggle is off on the effective copy.
        Assert.False(eff.SocialEnabled);
        Assert.False(eff.NotifyOnFriendPost);
        Assert.False(eff.ShowLargeReactions);
        Assert.False(eff.PerchReacts);
        Assert.False(eff.NotifyOnAchievement);
        Assert.False(eff.AchievementToasts);
        Assert.False(eff.UpsideDownQuickLinks);

        // Non-playful settings survive, and the deadline is preserved so callers can still read the remaining time.
        Assert.True(eff.ShowUsage);
        Assert.True(eff.NotifyOnDone);
        Assert.True(eff.ShowTodos);
        Assert.Equal(raw.QuietUntil, eff.QuietUntil);
    }

    [Fact]
    public void RegistryPlayfulSetMatchesExpectation()
    {
        var playful = SettingsRegistry.All
            .Where(d => d.Playful)
            .SelectMany(d => d.Backing ?? [])
            .OrderBy(n => n)
            .ToArray();

        Assert.Equal(ExpectedPlayful.OrderBy(n => n).ToArray(), playful);
    }
}
