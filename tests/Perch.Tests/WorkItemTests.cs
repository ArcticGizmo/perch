using Perch.Data.Work;
using Perch.Theming;
using Xunit;

namespace Perch.Tests;

public class WorkItemTests
{
    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"perch-work-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void Cleanup(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch { }
    }

    [Fact]
    public void Create_WritesSlugNamedFile_AndLoadsBack()
    {
        var dir = TempDir();
        try
        {
            var store = new WorkItemStore(dir);
            var t0 = new DateTime(2026, 10, 16, 1, 0, 0, DateTimeKind.Utc);
            var a = store.Create("Payments refactor", nowUtc: t0)!;
            var b = store.Create("macOS port", nowUtc: t0.AddMinutes(1))!;

            Assert.Equal($"payments-refactor-{a.Id}.md", Path.GetFileName(a.FilePath));
            Assert.Equal(8, a.Id.Length);
            Assert.NotEqual(a.Colour, b.Colour);   // a new item takes a hue nobody else has

            var all = new WorkItemStore(dir).LoadAll();
            Assert.Equal(["Payments refactor", "macOS port"], all.Select(i => i.Name));
            Assert.All(all, i => Assert.Equal(WorkItemStatus.Active, i.Status));
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public void Create_RefusesABlankName()
    {
        var dir = TempDir();
        try
        {
            Assert.Null(new WorkItemStore(dir).Create("   "));
            Assert.Empty(Directory.GetFiles(dir));
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public void Serialize_RoundTripsEveryField()
    {
        var at = new DateTimeOffset(2026, 10, 16, 13, 42, 0, TimeSpan.FromHours(11));
        var item = new WorkItem
        {
            Id = "3f9c1a2b",
            Name = "Payments \"v2\" refactor",
            Colour = "violet",
            Status = WorkItemStatus.Paused,
            CreatedUtc = new DateTime(2026, 10, 16, 2, 0, 0, DateTimeKind.Utc),
            Notes = "# Goal\n\nMove charges behind the ledger API.\n\n- step one\n- step two",
            Links = [new WorkLink { Label = "Design doc", Url = "https://example.com/docs?a=1&b=2" }],
        };
        item.AddMember("4f1c0000-aaaa-bbbb-cccc-000000000001", WorkJoin.Terminal, "api · worktree-a", @"C:\src\payments-wt-a",
            new DateTime(2026, 10, 16, 2, 5, 0, DateTimeKind.Utc));
        item.Log.Add(new WorkLogEntry(at, "Ledger API agreed with the platform team."));
        item.Log.Add(new WorkLogEntry(at.AddMinutes(5), "api · worktree-a joined (terminal)", ByPerch: true));

        var text = WorkItemFile.Serialize(item);
        var back = WorkItemFile.Parse(text, "fallback")!;

        Assert.Equal(item.Id, back.Id);
        Assert.Equal(item.Name, back.Name);
        Assert.Equal("violet", back.Colour);
        Assert.Equal(WorkItemStatus.Paused, back.Status);
        Assert.Equal(item.CreatedUtc, back.CreatedUtc);
        Assert.Equal(item.Notes, back.Notes);
        var link = Assert.Single(back.Links);
        Assert.Equal("https://example.com/docs?a=1&b=2", link.Url);
        var m = Assert.Single(back.Members);
        Assert.Equal(WorkJoin.Terminal, m.Via);
        Assert.Equal(@"C:\src\payments-wt-a", m.Cwd);
        Assert.Equal("api · worktree-a", m.Title);
        Assert.Equal(new DateTime(2026, 10, 16, 2, 5, 0, DateTimeKind.Utc), m.AddedUtc);
        Assert.Equal(2, back.Log.Count);
        Assert.Equal(at, back.Log[0].At);
        Assert.False(back.Log[0].ByPerch);
        Assert.True(back.Log[1].ByPerch);
        Assert.Equal("api · worktree-a joined (terminal)", back.Log[1].Text);

        // And writing it again is stable.
        Assert.Equal(text, WorkItemFile.Serialize(back));
    }

    [Fact]
    public void Parse_ReadsAHandWrittenFile()
    {
        const string text = """
            ---
            id: abc12345
            name: 'Tom''s spike'
            color: sky
            status: DONE
            tags: [infra, spike]
            links:
              - label: PR
                url: https://github.com/acme/billing/pull/412
              -
                url: https://example.com/bare
            sessions: []
            ---
            Some notes.

            ## Log

            - 2026-10-01 09:00 +10:00 Started.
            - this line has no time
            """;
        var item = WorkItemFile.Parse(text, "stem")!;

        Assert.Equal("abc12345", item.Id);
        Assert.Equal("Tom's spike", item.Name);
        Assert.Equal("sky", item.Colour);
        Assert.Equal(WorkItemStatus.Done, item.Status);
        Assert.Equal(2, item.Links.Count);
        Assert.Equal("", item.Links[1].Label);
        Assert.Equal("Some notes.", item.Notes);
        Assert.Equal(2, item.Log.Count);
        Assert.False(item.Log[0].IsRaw);
        Assert.True(item.Log[1].IsRaw);

        // The unknown key and the unparsed log line survive a rewrite.
        var again = WorkItemFile.Serialize(item);
        Assert.Contains("tags: [infra, spike]", again);
        Assert.Contains("- this line has no time", again);
    }

    [Fact]
    public void Parse_FileWithoutFrontMatter_IsNamedAfterTheFile()
    {
        var item = WorkItemFile.Parse("Just some notes\n\nabout things.", "loose-notes")!;
        Assert.Equal("loose-notes", item.Id);
        Assert.Equal("loose-notes", item.Name);
        Assert.Equal("Just some notes\n\nabout things.", item.Notes);
    }

    [Fact]
    public void Parse_LastLogHeadingSplitsNotesFromLog()
    {
        var item = WorkItemFile.Parse("---\nid: x1\nname: n\n---\n## Log\nan old heading in the notes\n\n## Log\n\n- 2026-10-01 09:00 +00:00 Real entry\n", "x")!;
        Assert.Equal("## Log\nan old heading in the notes", item.Notes);
        Assert.Equal("Real entry", Assert.Single(item.Log).Text);
    }

    [Fact]
    public void Parse_UnterminatedFrontMatter_IsUnreadable_AndTheStoreLeavesItAlone()
    {
        var dir = TempDir();
        try
        {
            var path = Path.Combine(dir, "broken.md");
            const string text = "---\nid: broken1\nname: Broken\nno closing fence here\n";
            File.WriteAllText(path, text);

            Assert.Null(WorkItemFile.Parse(text, "broken"));
            var store = new WorkItemStore(dir);
            Assert.Empty(store.LoadAll());
            Assert.Equal([path], store.Unreadable);
            Assert.Null(store.Mutate("broken1", i => i.Name = "Overwritten"));
            Assert.Equal(text, File.ReadAllText(path));
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public void Mutate_ReadsFresh_SoTwoWritersDontUndoEachOther()
    {
        var dir = TempDir();
        try
        {
            var tray = new WorkItemStore(dir);
            var item = tray.Create("Shared")!;
            var cli = new WorkItemStore(dir);
            cli.LoadAll();

            // The CLI adds a member; then the tray, which never reloaded, adds a link.
            cli.Mutate(item.Id, i => i.AddMember("s-0000001", WorkJoin.Manual, null, null));
            tray.Mutate(item.Id, i => i.Links.Add(new WorkLink { Label = "x", Url = "https://x" }));

            var back = new WorkItemStore(dir).LoadAll().Single();
            Assert.Single(back.Members);
            Assert.Single(back.Links);
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public void Mutate_ThatReportsNoChange_DoesNotTouchTheFile()
    {
        var dir = TempDir();
        try
        {
            var store = new WorkItemStore(dir);
            var item = store.Create("Quiet")!;
            var stamp = File.GetLastWriteTimeUtc(item.FilePath!);
            File.SetLastWriteTimeUtc(item.FilePath!, stamp.AddMinutes(-5));

            Assert.NotNull(store.Mutate(item.Id, i => i.RefreshMember("nobody", "t", "c")));
            Assert.Equal(stamp.AddMinutes(-5), File.GetLastWriteTimeUtc(item.FilePath!));
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public void Members_AddRemoveRefresh()
    {
        var item = new WorkItem { Id = "a", Name = "a" };
        Assert.True(item.AddMember("s1", WorkJoin.Manual, " t ", null));
        Assert.False(item.AddMember("s1", WorkJoin.Launched, null, null));   // already in
        Assert.Equal("t", item.Members[0].Title);
        Assert.True(item.RefreshMember("s1", "renamed", @"C:\x"));
        Assert.False(item.RefreshMember("s1", "renamed", @"C:\x"));
        Assert.False(item.RefreshMember("s1", null, null));                   // blanks never clear a snapshot
        Assert.True(item.RemoveMember("s1"));
        Assert.False(item.HasMember("s1"));
    }

    [Fact]
    public void Resolve_TriesIdThenSlugThenNameThenUniquePrefix()
    {
        var items = new List<WorkItem>
        {
            new() { Id = "aaaa1111", Name = "Payments refactor", FilePath = @"C:\w\payments-refactor-aaaa1111.md" },
            new() { Id = "bbbb2222", Name = "macOS port", FilePath = @"C:\w\macos-port-bbbb2222.md" },
            new() { Id = "cccc3333", Name = "Payments spike", Status = WorkItemStatus.Done },
        };

        Assert.Equal("aaaa1111", WorkItemStore.Resolve("AAAA1111", items)?.Id);
        Assert.Equal("bbbb2222", WorkItemStore.Resolve("macos-port-bbbb2222", items)?.Id);
        Assert.Equal("aaaa1111", WorkItemStore.Resolve("payments-refactor", items)?.Id);
        Assert.Equal("bbbb2222", WorkItemStore.Resolve("MACOS PORT", items)?.Id);
        Assert.Equal("bbbb2222", WorkItemStore.Resolve("mac", items)?.Id);
        Assert.Null(WorkItemStore.Resolve("pay", items));        // ambiguous prefix
        Assert.Equal("cccc3333", WorkItemStore.Resolve("payments spike", items)?.Id);   // done items still match
        Assert.Null(WorkItemStore.Resolve("nothing like it", items));
        Assert.Null(WorkItemStore.Resolve("  ", items));
    }

    [Theory]
    [InlineData("Payments refactor", "payments-refactor")]
    [InlineData("  Café — v2!  ", "cafe-v2")]
    [InlineData("***", "work")]
    [InlineData("DEV-123: fix the thing", "dev-123-fix-the-thing")]
    public void Slug_IsFileNameSafe(string name, string expected) => Assert.Equal(expected, WorkItemStore.Slug(name));

    [Fact]
    public void Hues_NextFreeSkipsUsedAndCycles()
    {
        Assert.Equal("teal", WorkHues.NextFree([]));
        Assert.Equal("amber", WorkHues.NextFree(["teal", "violet"]));
        Assert.Equal("teal", WorkHues.NextFree(WorkHues.Names));   // all taken: cycle
        Assert.Equal(0, WorkHues.IndexOf("no-such-colour"));
    }

    [Fact]
    public void Hues_ClearTheNonTextFloorOnDarkAndLight()
    {
        Assert.Equal(WorkHues.Names.Length, FixedColors.Default.WorkHues.Count);
        foreach (var theme in new[] { Themes.Midnight, Themes.Daylight })
            foreach (var hue in FixedColors.Default.WorkHues)
                Assert.True(Contrast.Ratio(hue, theme.OverlaySurface) >= 3.0,
                    $"{hue} on {theme.Name}: {Contrast.Ratio(hue, theme.OverlaySurface):0.00}");
    }
}
