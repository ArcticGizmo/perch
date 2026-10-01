using Perch.Data;
using Xunit;

namespace Perch.Tests;

/// <summary>
/// CP20 (docs/review-fixes-plan.md): a session with no transcript yet used to make every reader re-scan every
/// project folder on every scan. The fallback scan's miss is now remembered — but the direct path is always
/// checked, and a new project folder (its root's mtime moves) forgets the miss at once. Driven over a temp
/// projects tree through the internal seam, so the shared fixture tree is never touched.
/// </summary>
public sealed class TranscriptLocatorMissCacheTests : IDisposable
{
    private static readonly TimeSpan Long = TimeSpan.FromHours(1);

    private readonly string _projects = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), "perch-locator-" + Guid.NewGuid().ToString("N"))).FullName;
    private readonly string _sid = Guid.NewGuid().ToString();

    public void Dispose()
    {
        try { Directory.Delete(_projects, recursive: true); } catch { }
    }

    private string? Resolve(string cwd, TimeSpan ttl) => TranscriptLocator.Resolve([_projects], _sid, cwd, ttl);

    [Fact]
    public void A_miss_is_remembered_until_its_ttl_lapses()
    {
        var other = Directory.CreateDirectory(Path.Combine(_projects, "C--elsewhere")).FullName;
        Assert.Null(Resolve(@"C:\no\match", Long));

        // The transcript appears in an existing, non-direct folder: the cached miss stands…
        File.WriteAllText(Path.Combine(other, _sid + ".jsonl"), "{}\n");
        Assert.Null(Resolve(@"C:\no\match", Long));
        // …until the TTL lapses (zero here), when the fallback scan runs again and finds it.
        Assert.Equal(Path.Combine(other, _sid + ".jsonl"), Resolve(@"C:\no\match", TimeSpan.Zero));
    }

    [Fact]
    public void The_direct_path_is_always_checked_despite_a_cached_miss()
    {
        const string cwd = @"C:\work\proj";
        Assert.Null(Resolve(cwd, Long));

        var direct = Directory.CreateDirectory(Path.Combine(_projects, TranscriptLocator.EncodeProjectDir(cwd))).FullName;
        File.WriteAllText(Path.Combine(direct, _sid + ".jsonl"), "{}\n");
        Assert.Equal(Path.Combine(direct, _sid + ".jsonl"), Resolve(cwd, Long));
    }

    [Fact]
    public void A_new_project_folder_forgets_the_miss()
    {
        Assert.Null(Resolve(@"C:\no\match", Long));

        // Creating a project folder bumps the projects root's mtime, which invalidates the cached miss.
        var fresh = Directory.CreateDirectory(Path.Combine(_projects, "C--brand-new")).FullName;
        File.WriteAllText(Path.Combine(fresh, _sid + ".jsonl"), "{}\n");
        Directory.SetLastWriteTimeUtc(_projects, DateTime.UtcNow.AddSeconds(5));   // don't depend on timestamp granularity
        Assert.Equal(Path.Combine(fresh, _sid + ".jsonl"), Resolve(@"C:\no\match", Long));
    }
}
