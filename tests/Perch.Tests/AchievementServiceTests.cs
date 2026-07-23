using Perch.Data;
using Xunit;

namespace Perch.Tests;

public class AchievementServiceTests
{
    private static string TempPath() => Path.Combine(Path.GetTempPath(), $"perch-ach-{Guid.NewGuid():N}.json");

    // Minimal all-time report earning a controllable set of levels (sessions + input-token families).
    private static StatsReport Report(int sessions = 0, long tokens = 0) =>
        new(DateOnly.FromDateTime(DateTime.Now), sessions, TimeSpan.Zero, 0, 0, 0, 0,
            new TokenTotals(tokens, 0, 0, 0), TokenTotals.Zero, 0m, true, [], [], [], [], new int[24]);

    [Fact]
    public void Store_RoundTripsUnlockedIds_AndTracksExistence()
    {
        var path = TempPath();
        try
        {
            var fresh = AchievementStore.LoadFrom(path);
            Assert.False(fresh.Existed);
            Assert.True(fresh.Add("input.wordsmith"));
            Assert.False(fresh.Add("input.wordsmith"));
            fresh.Save();

            var reloaded = AchievementStore.LoadFrom(path);
            Assert.True(reloaded.Existed);
            Assert.True(reloaded.Contains("input.wordsmith"));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void FirstRun_ReturnsEveryEarnedLevel_AndPersists()
    {
        var path = TempPath();
        try
        {
            var svc = new AchievementService(AchievementStore.LoadFrom(path));   // no file → first run
            var announced = svc.Sync(Report(sessions: 1, tokens: 1_000_000), null, includeCost: true);

            // The first run returns everything earned (the App collapses a big batch into one summary toast).
            Assert.Contains(announced, u => u.Name == "First Flight");
            Assert.Contains(announced, u => u.Name == "Wordsmith");

            var reloaded = AchievementStore.LoadFrom(path);
            Assert.True(reloaded.Contains("sessions.firstflight"));
            Assert.True(reloaded.Contains("input.wordsmith"));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void OnceCommitted_ASubsequentRunIsQuiet()
    {
        var path = TempPath();
        try
        {
            var report = Report(sessions: 1, tokens: 1_000_000);
            new AchievementService(AchievementStore.LoadFrom(path)).Sync(report, null, includeCost: true);

            // A fresh service over the now-populated store re-scans the same history and finds nothing new.
            var second = new AchievementService(AchievementStore.LoadFrom(path)).Sync(report, null, includeCost: true);
            Assert.Empty(second);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ExistingStore_AnnouncesOnlyNewLevels()
    {
        var path = TempPath();
        try
        {
            var seed = AchievementStore.LoadFrom(path);
            seed.Add("sessions.firstflight");
            seed.Save();

            var svc = new AchievementService(AchievementStore.LoadFrom(path));
            var announced = svc.Sync(Report(sessions: 1, tokens: 1_000_000), null, includeCost: true);

            Assert.Contains(announced, u => u.Name == "Wordsmith");
            Assert.DoesNotContain(announced, u => u.Name == "First Flight");   // already unlocked
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void CrossingSeveralLevelsAtOnce_AnnouncesEachNewOne()
    {
        var path = TempPath();
        try
        {
            var svc = new AchievementService(AchievementStore.LoadFrom(path));
            svc.Sync(Report(sessions: 1), null, includeCost: true);   // silent seed: First Flight only

            var announced = svc.Sync(Report(sessions: 100), null, includeCost: true);
            Assert.Contains(announced, u => u.Name == "Frequent Flyer");   // 10
            Assert.Contains(announced, u => u.Name == "Century");          // 100
            Assert.DoesNotContain(announced, u => u.Name == "First Flight");
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void SameReportTwice_AnnouncesOnce()
    {
        var path = TempPath();
        try
        {
            AchievementStore.LoadFrom(path).Save();   // make the store pre-exist (empty) so syncs announce
            var svc = new AchievementService(AchievementStore.LoadFrom(path));

            var report = Report(sessions: 1);
            Assert.Contains(svc.Sync(report, null, includeCost: true), u => u.Name == "First Flight");
            Assert.Empty(svc.Sync(report, null, includeCost: true));
        }
        finally { File.Delete(path); }
    }
}
