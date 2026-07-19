using Perch.Data;
using Xunit;

namespace Perch.Tests;

public class AchievementServiceTests
{
    private static string TempPath() => Path.Combine(Path.GetTempPath(), $"perch-ach-{Guid.NewGuid():N}.json");

    // Minimal all-time report earning a controllable set of badges (first-flight, century, wordsmith).
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
            Assert.True(fresh.Add("night-owl"));
            Assert.False(fresh.Add("night-owl"));   // already present
            fresh.Save();

            var reloaded = AchievementStore.LoadFrom(path);
            Assert.True(reloaded.Existed);
            Assert.True(reloaded.Contains("night-owl"));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void FirstRun_SeedsSilently_ButPersists()
    {
        var path = TempPath();
        try
        {
            var svc = new AchievementService(AchievementStore.LoadFrom(path));   // no file → first run
            var announced = svc.Sync(Report(sessions: 1, tokens: 1_000_000), null, includeCost: true);

            Assert.Empty(announced);   // nothing toasted on the seeding run

            var reloaded = AchievementStore.LoadFrom(path);
            Assert.True(reloaded.Contains("first-flight"));
            Assert.True(reloaded.Contains("wordsmith"));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ExistingStore_AnnouncesOnlyNewUnlocks()
    {
        var path = TempPath();
        try
        {
            var seed = AchievementStore.LoadFrom(path);
            seed.Add("first-flight");
            seed.Save();   // store now exists on disk with first-flight already celebrated

            var svc = new AchievementService(AchievementStore.LoadFrom(path));
            var announced = svc.Sync(Report(sessions: 1, tokens: 1_000_000), null, includeCost: true);

            Assert.Contains(announced, a => a.Id == "wordsmith");
            Assert.DoesNotContain(announced, a => a.Id == "first-flight");   // already unlocked
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void SecondSyncInProcess_AnnouncesNewlyCrossedThresholds()
    {
        var path = TempPath();
        try
        {
            var svc = new AchievementService(AchievementStore.LoadFrom(path));
            svc.Sync(Report(sessions: 1), null, includeCost: true);   // silent seed (first-flight)

            var announced = svc.Sync(Report(sessions: 100, tokens: 1_000_000), null, includeCost: true);
            Assert.Contains(announced, a => a.Id == "century");
            Assert.Contains(announced, a => a.Id == "wordsmith");
            Assert.DoesNotContain(announced, a => a.Id == "first-flight");
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
            Assert.Contains(svc.Sync(report, null, includeCost: true), a => a.Id == "first-flight");
            Assert.Empty(svc.Sync(report, null, includeCost: true));   // nothing new the second time
        }
        finally { File.Delete(path); }
    }
}
