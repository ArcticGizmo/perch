using Perch.Data;
using Xunit;

namespace Perch.Tests;

public class SessionHistoryTests
{
    [Fact]
    public void ListAll_PopulatesCwd_AndKeepsItOnCachedReList()
    {
        var none = new HashSet<string>();

        // First list reads the cwd from the transcript (a project-cache miss).
        var first = SessionHistory.ListAll(none).FirstOrDefault(e => e.SessionId == "sessA");
        Assert.NotNull(first);
        Assert.Equal(TestEnvironment.FixtureCwd, first!.Cwd);

        // Second list is served from the cache. Regression guard: the cache used to store only the project
        // name and return an empty cwd on every re-list, which made the switcher's reopen list disappear
        // after its first open (the reopen filter drops entries without a cwd).
        var second = SessionHistory.ListAll(none).FirstOrDefault(e => e.SessionId == "sessA");
        Assert.NotNull(second);
        Assert.Equal(TestEnvironment.FixtureCwd, second!.Cwd);
    }

    [Fact]
    public void ListAll_MarksActiveSessions()
    {
        var entries = SessionHistory.ListAll(new HashSet<string> { "sessA" });
        Assert.Contains(entries, e => e.SessionId == "sessA" && e.IsActive);
        Assert.Contains(entries, e => e.SessionId == "sessB" && !e.IsActive);
    }

    [Fact]
    public void DistinctFolders_DedupsCaseInsensitively_KeepsOrder_AndDropsMissing()
    {
        var real = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        var realUpper = real.ToUpperInvariant();
        var other = Directory.GetParent(real)!.FullName;
        var missing = Path.Combine(real, "does-not-exist-" + Guid.NewGuid().ToString("N"));

        HistoryEntry Entry(string cwd) =>
            new("id", "proj", cwd, "path.jsonl", DateTime.Now, false);

        var folders = SessionHistory.DistinctFolders(new[]
        {
            Entry(real),        // kept — exists
            Entry(realUpper),   // dropped — same folder, different case
            Entry(missing),     // dropped — no longer on disk
            Entry(""),          // dropped — empty
            Entry(other),       // kept — a second real folder, order preserved
        });

        Assert.Equal(new[] { real, other }, folders);
    }

    [Fact]
    public void ListAll_SurfacesRenameTitleAsDisplayName()
    {
        var entries = SessionHistory.ListAll(new HashSet<string>());

        // sessA was renamed via /rename ("Feature work") — DisplayName should be that title, not the project.
        var renamed = entries.First(e => e.SessionId == "sessA");
        Assert.Equal("Feature work", renamed.Title);
        Assert.Equal("Feature work", renamed.DisplayName);

        // sessB was never renamed — DisplayName falls back to the project name.
        var plain = entries.First(e => e.SessionId == "sessB");
        Assert.Null(plain.Title);
        Assert.Equal(plain.ProjectName, plain.DisplayName);
    }
}
