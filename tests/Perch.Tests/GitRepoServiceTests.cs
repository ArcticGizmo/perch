using Perch.Data;
using Xunit;

namespace Perch.Tests;

/// <summary>
/// Covers the pure parsers behind <see cref="GitRepoService"/> — the read side of Session Change Review.
/// The process spawning, timeouts and <c>--no-optional-locks</c> plumbing can only be exercised against a
/// real repo, so they're left to manual testing; this pins the three parsers that would otherwise silently
/// misread git's output. All input is canned command output, exactly as the real <c>git</c> commands emit
/// it (unit separator U+001F for the log delimiter).
/// </summary>
public class GitRepoServiceTests
{
    private const char Us = '\u001f';

    // ---- ParseStatusV2 --------------------------------------------------------------------------------

    [Fact]
    public void Status_ReadsBranchUpstreamAndAheadBehind()
    {
        var s = GitRepoService.ParseStatusV2(
            "# branch.oid abc123\n" +
            "# branch.head main\n" +
            "# branch.upstream origin/main\n" +
            "# branch.ab +2 -3\n");

        Assert.Equal("main", s.Branch);
        Assert.Equal("origin/main", s.Upstream);
        Assert.Equal(2, s.Ahead);
        Assert.Equal(3, s.Behind);
        Assert.True(s.IsClean);
    }

    [Fact]
    public void Status_DetachedHeadIsNullBranch()
    {
        var s = GitRepoService.ParseStatusV2("# branch.head (detached)\n");
        Assert.Null(s.Branch);
    }

    [Fact]
    public void Status_OrdinaryChangesMapStagedAndUnstaged()
    {
        // "1 <XY> <sub> <mH> <mI> <mW> <hH> <hI> <path>"
        var s = GitRepoService.ParseStatusV2(
            "# branch.head main\n" +
            "1 M. N... 100644 100644 100644 1111111 2222222 src/Staged.cs\n" +   // staged modify only
            "1 .M N... 100644 100644 100644 3333333 4444444 src/Worktree.cs\n" + // unstaged modify only
            "1 A. N... 000000 100644 100644 0000000 5555555 src/New Added.cs\n"); // staged add, path w/ space

        Assert.Equal(3, s.Changes.Count);
        Assert.False(s.IsClean);

        var staged = s.Changes[0];
        Assert.Equal("src/Staged.cs", staged.Path);
        Assert.Equal(GitChangeKind.Modified, staged.Staged);
        Assert.Equal(GitChangeKind.None, staged.Unstaged);

        var worktree = s.Changes[1];
        Assert.Equal(GitChangeKind.None, worktree.Staged);
        Assert.Equal(GitChangeKind.Modified, worktree.Unstaged);

        var added = s.Changes[2];
        Assert.Equal("src/New Added.cs", added.Path); // path with a space survives the field-limited split
        Assert.Equal(GitChangeKind.Added, added.Staged);
    }

    [Fact]
    public void Status_RenameCarriesOriginalPath()
    {
        // "2 <XY> <sub> <mH> <mI> <mW> <hH> <hI> <score> <newPath>\t<origPath>"
        var s = GitRepoService.ParseStatusV2(
            "# branch.head main\n" +
            "2 R. N... 100644 100644 100644 1111111 2222222 R100 src/New.cs\tsrc/Old.cs\n");

        var c = Assert.Single(s.Changes);
        Assert.Equal("src/New.cs", c.Path);
        Assert.Equal("src/Old.cs", c.OrigPath);
        Assert.Equal(GitChangeKind.Renamed, c.Staged);
    }

    [Fact]
    public void Status_UntrackedAndUnmergedAndIgnored()
    {
        var s = GitRepoService.ParseStatusV2(
            "# branch.head main\n" +
            "? build/output.txt\n" +                                              // untracked
            "u UU N... 100644 100644 100644 100644 111 222 333 src/Conflict.cs\n" + // unmerged
            "! .idea/workspace.xml\n");                                            // ignored -> skipped

        Assert.Equal(2, s.Changes.Count);

        var untracked = s.Changes[0];
        Assert.Equal("build/output.txt", untracked.Path);
        Assert.True(untracked.Untracked);

        var conflict = s.Changes[1];
        Assert.Equal("src/Conflict.cs", conflict.Path);
        Assert.Equal(GitChangeKind.Unmerged, conflict.Staged);
        Assert.Equal(GitChangeKind.Unmerged, conflict.Unstaged);
    }

    [Fact]
    public void Status_ToleratesCrlfBlankAndMalformedLines()
    {
        var s = GitRepoService.ParseStatusV2(
            "# branch.head main\r\n" +
            "\r\n" +                 // blank
            "1 M.\r\n" +             // too few fields -> skipped, no throw
            "garbage\n" +            // unknown leading char -> skipped
            "1 .M N... 100644 100644 100644 3333333 4444444 ok.cs\r\n");

        Assert.Equal("main", s.Branch);
        var c = Assert.Single(s.Changes);
        Assert.Equal("ok.cs", c.Path);
    }

    [Fact]
    public void Status_EmptyOutputIsCleanTree()
    {
        var s = GitRepoService.ParseStatusV2("");
        Assert.True(s.IsClean);
        Assert.Null(s.Branch);
    }

    // ---- ParseLog -------------------------------------------------------------------------------------

    // git log -z separates commits with NUL; the last field is %B (the full multi-line message).
    private const char Nul = '\0';

    [Fact]
    public void Log_ParsesRecordsNewestFirstWithBodyAndParents()
    {
        // Format is H h an aI s P B: %P (space-separated parents) sits before the multi-line %B body.
        var output =
            $"aaaaaaaaaaaa{Us}aaaaaaa{Us}Ada Lovelace{Us}2026-08-05T10:30:00+10:00{Us}Add the widget{Us}bbbbbbbbbbbb{Us}Add the widget\n\nWith a longer body.\n{Nul}" +
            $"bbbbbbbbbbbb{Us}bbbbbbb{Us}Alan Turing{Us}2026-08-04T09:00:00Z{Us}Merge branch 'x'{Us}cccccccccccc dddddddddddd{Us}Merge branch 'x'\n{Nul}";

        var log = GitRepoService.ParseLog(output);

        Assert.Equal(2, log.Count);
        Assert.Equal("aaaaaaaaaaaa", log[0].Hash);
        Assert.Equal("aaaaaaa", log[0].ShortHash);
        Assert.Equal("Ada Lovelace", log[0].Author);
        Assert.Equal("Add the widget", log[0].Subject);
        Assert.Equal("Add the widget\n\nWith a longer body.", log[0].Body); // trailing newline trimmed, body kept
        Assert.Equal(new DateTimeOffset(2026, 8, 5, 10, 30, 0, TimeSpan.FromHours(10)), log[0].Date);

        Assert.Equal(["bbbbbbbbbbbb"], log[0].ParentHashes);      // single parent
        Assert.False(log[0].IsMerge);
        Assert.Equal("Merge branch 'x'", log[1].Subject);
        Assert.Equal(["cccccccccccc", "dddddddddddd"], log[1].ParentHashes); // two parents
        Assert.True(log[1].IsMerge);
    }

    [Fact]
    public void Log_RootCommitHasNoParents()
    {
        // A root commit's %P is empty — the field is present but blank.
        var output = $"h{Us}sh{Us}Auth{Us}2026-01-02T03:04:05Z{Us}Initial commit{Us}{Us}Initial commit{Nul}";
        var c = Assert.Single(GitRepoService.ParseLog(output));
        Assert.Empty(c.ParentHashes);
        Assert.False(c.IsMerge);
    }

    [Fact]
    public void Log_BodyFallsBackToSubjectWhenAbsent()
    {
        // Five-field record (no %B captured) still parses; Body falls back to Subject.
        var output = $"h{Us}sh{Us}Auth{Us}2026-01-02T03:04:05Z{Us}Only a subject{Nul}";
        var c = Assert.Single(GitRepoService.ParseLog(output));
        Assert.Equal("Only a subject", c.Subject);
        Assert.Equal("Only a subject", c.Body);
    }

    [Fact]
    public void Log_SkipsShortAndUndatedRecords()
    {
        var output =
            $"hash{Us}short{Us}Author{Nul}" +                                      // too few fields -> skipped
            $"hash2{Us}short2{Us}Author2{Us}not-a-date{Us}Subject{Us}Subject{Nul}" + // bad date -> skipped
            $"hash3{Us}short3{Us}Author3{Us}2026-01-02T03:04:05Z{Us}Good{Us}Good{Nul}";

        var log = GitRepoService.ParseLog(output);

        var c = Assert.Single(log);
        Assert.Equal("Good", c.Subject);
    }

    [Fact]
    public void Log_EmptyOutputIsEmptyList()
    {
        Assert.Empty(GitRepoService.ParseLog(""));
    }

    // ---- ParseUnifiedDiff -----------------------------------------------------------------------------

    [Fact]
    public void Diff_ModifiedFileHunkAndTypedLines()
    {
        var output =
            "diff --git a/src/Foo.cs b/src/Foo.cs\n" +
            "index 1111111..2222222 100644\n" +
            "--- a/src/Foo.cs\n" +
            "+++ b/src/Foo.cs\n" +
            "@@ -1,3 +1,3 @@ namespace Foo\n" +
            " context line\n" +
            "-old line\n" +
            "+new line\n" +
            "\\ No newline at end of file\n";

        var diff = GitRepoService.ParseUnifiedDiff(output);

        var f = Assert.Single(diff.Files);
        Assert.Equal("src/Foo.cs", f.OldPath);
        Assert.Equal("src/Foo.cs", f.NewPath);
        Assert.False(f.IsBinary);

        var h = Assert.Single(f.Hunks);
        Assert.StartsWith("@@ -1,3 +1,3 @@", h.Header);
        Assert.Equal(4, h.Lines.Count);
        Assert.Equal(GitDiffLineKind.Context, h.Lines[0].Kind);
        Assert.Equal("context line", h.Lines[0].Text);
        Assert.Equal(GitDiffLineKind.Removed, h.Lines[1].Kind);
        Assert.Equal("old line", h.Lines[1].Text);
        Assert.Equal(GitDiffLineKind.Added, h.Lines[2].Kind);
        Assert.Equal("new line", h.Lines[2].Text);
        Assert.Equal(GitDiffLineKind.Meta, h.Lines[3].Kind);
    }

    [Fact]
    public void Diff_StripsLeadingBomFromFirstLineContent()
    {
        // A UTF-8 file carries a BOM (U+FEFF) at the very start; when its first line shows in a diff, that
        // BOM rides along on the line content. It must not survive into the parsed text (else it renders as
        // a stray glyph and gets copied). Built via (char)0xFEFF so this test's own source stays pure ASCII.
        var bom = ((char)0xFEFF).ToString();
        var output =
            "diff --git a/src/Foo.cs b/src/Foo.cs\n" +
            "index 1111111..2222222 100644\n" +
            "--- a/src/Foo.cs\n" +
            "+++ b/src/Foo.cs\n" +
            "@@ -1,2 +1,2 @@\n" +
            "-" + bom + "using System;\n" +
            "+" + bom + "using System.Linq;\n" +
            " namespace Foo\n";

        var h = Assert.Single(Assert.Single(GitRepoService.ParseUnifiedDiff(output).Files).Hunks);
        Assert.Equal("using System;", h.Lines[0].Text);       // removed - BOM stripped
        Assert.Equal("using System.Linq;", h.Lines[1].Text);  // added   - BOM stripped
        Assert.Equal("namespace Foo", h.Lines[2].Text);       // context - untouched
        Assert.DoesNotContain(h.Lines, l => l.Text.Contains((char)0xFEFF));
    }

    [Fact]
    public void HasNoTextChange_TrueForBomOnlyAndEmpty_FalseForRealAndBinary()
    {
        var bom = ((char)0xFEFF).ToString();

        // BOM-only: line 1's sole difference is a leading BOM the parser strips -> removed == added.
        var bomOnly = GitRepoService.ParseUnifiedDiff(
            "diff --git a/A.cs b/A.cs\n--- a/A.cs\n+++ b/A.cs\n@@ -1,2 +1,2 @@\n" +
            "-" + bom + "line one\n+" + bom + "line one\n line two\n");
        Assert.True(GitRepoService.HasNoTextChange(bomOnly));

        // Empty diff (git normalised a line-ending-only change to nothing).
        Assert.True(GitRepoService.HasNoTextChange(GitRepoService.ParseUnifiedDiff("")));

        // A genuine one-line edit -> real change.
        var real = GitRepoService.ParseUnifiedDiff(
            "diff --git a/A.cs b/A.cs\n--- a/A.cs\n+++ b/A.cs\n@@ -1 +1 @@\n-old line\n+new line\n");
        Assert.False(GitRepoService.HasNoTextChange(real));

        // Binary changes are never "no change".
        var binary = GitRepoService.ParseUnifiedDiff(
            "diff --git a/img.png b/img.png\nindex 111..222 100644\nBinary files a/img.png and b/img.png differ\n");
        Assert.False(GitRepoService.HasNoTextChange(binary));
    }

    [Fact]
    public void Diff_AddedAndDeletedFilesMapDevNullToNull()
    {
        var output =
            "diff --git a/added.txt b/added.txt\n" +
            "new file mode 100644\n" +
            "--- /dev/null\n" +
            "+++ b/added.txt\n" +
            "@@ -0,0 +1 @@\n" +
            "+hello\n" +
            "diff --git a/gone.txt b/gone.txt\n" +
            "deleted file mode 100644\n" +
            "--- a/gone.txt\n" +
            "+++ /dev/null\n" +
            "@@ -1 +0,0 @@\n" +
            "-bye\n";

        var diff = GitRepoService.ParseUnifiedDiff(output);

        Assert.Equal(2, diff.Files.Count);

        var added = diff.Files[0];
        Assert.Null(added.OldPath);            // /dev/null
        Assert.Equal("added.txt", added.NewPath);
        Assert.Equal(GitDiffLineKind.Added, Assert.Single(added.Hunks).Lines[0].Kind);

        var deleted = diff.Files[1];
        Assert.Equal("gone.txt", deleted.OldPath);
        Assert.Null(deleted.NewPath);          // /dev/null
    }

    [Fact]
    public void Diff_UntrackedFileViaNoIndexIsAllAdded()
    {
        // The shape `git diff --no-index -- /dev/null <path>` emits for an untracked file (what
        // GetUntrackedDiff runs): a single added-file diff with every line an addition.
        var output =
            "diff --git a/dev/null b/src/New.cs\n" +
            "new file mode 100644\n" +
            "index 0000000..abc1234\n" +
            "--- /dev/null\n" +
            "+++ b/src/New.cs\n" +
            "@@ -0,0 +1,3 @@\n" +
            "+namespace Foo;\n" +
            "+\n" +
            "+public class New { }\n";

        var f = Assert.Single(GitRepoService.ParseUnifiedDiff(output).Files);
        Assert.Null(f.OldPath);                 // /dev/null
        Assert.Equal("src/New.cs", f.NewPath);
        Assert.False(f.IsBinary);
        var lines = Assert.Single(f.Hunks).Lines;
        Assert.Equal(3, lines.Count);
        Assert.All(lines, l => Assert.Equal(GitDiffLineKind.Added, l.Kind));
        Assert.Equal("namespace Foo;", lines[0].Text);
        Assert.Equal("", lines[1].Text);        // blank added line ("+") -> empty payload, still Added
    }

    [Fact]
    public void Diff_RenameWithoutContentHasPathsAndNoHunks()
    {
        var output =
            "diff --git a/old/name.cs b/new/name.cs\n" +
            "similarity index 100%\n" +
            "rename from old/name.cs\n" +
            "rename to new/name.cs\n";

        var f = Assert.Single(GitRepoService.ParseUnifiedDiff(output).Files);
        Assert.Equal("old/name.cs", f.OldPath);
        Assert.Equal("new/name.cs", f.NewPath);
        Assert.Empty(f.Hunks);
    }

    [Fact]
    public void Diff_BinaryFileFlagged()
    {
        var output =
            "diff --git a/logo.png b/logo.png\n" +
            "index 1111111..2222222 100644\n" +
            "Binary files a/logo.png and b/logo.png differ\n";

        var f = Assert.Single(GitRepoService.ParseUnifiedDiff(output).Files);
        Assert.True(f.IsBinary);
        Assert.Empty(f.Hunks);
    }

    [Fact]
    public void Diff_RemovedContentLineStartingWithDashesNotMistakenForHeader()
    {
        // A removed line whose content is "-- a comment" appears as "--- a comment" in the diff. Inside an
        // open hunk it must be read as a Removed line, not a "--- " file header.
        var output =
            "diff --git a/f.txt b/f.txt\n" +
            "--- a/f.txt\n" +
            "+++ b/f.txt\n" +
            "@@ -1 +1 @@\n" +
            "--- a comment\n" +
            "+kept\n";

        var f = Assert.Single(GitRepoService.ParseUnifiedDiff(output).Files);
        Assert.Equal("f.txt", f.OldPath);
        var lines = Assert.Single(f.Hunks).Lines;
        Assert.Equal(GitDiffLineKind.Removed, lines[0].Kind);
        Assert.Equal("-- a comment", lines[0].Text);
        Assert.Equal(GitDiffLineKind.Added, lines[1].Kind);
    }

    [Fact]
    public void Diff_ToleratesCrlfAndEmptyOutput()
    {
        Assert.Empty(GitRepoService.ParseUnifiedDiff("").Files);

        var output =
            "diff --git a/f.txt b/f.txt\r\n" +
            "--- a/f.txt\r\n" +
            "+++ b/f.txt\r\n" +
            "@@ -1 +1 @@\r\n" +
            " ctx\r\n" +
            "+add\r\n";

        var f = Assert.Single(GitRepoService.ParseUnifiedDiff(output).Files);
        var lines = Assert.Single(f.Hunks).Lines;
        Assert.Equal("ctx", lines[0].Text);   // trailing \r stripped
        Assert.Equal("add", lines[1].Text);
    }

    // ---- PickBaseRefCandidates ------------------------------------------------------------------------

    [Fact]
    public void BaseRef_ForkPrefersUpstreamThenOriginThenLocal()
    {
        // The fork convention: branch from upstream, commit to origin (your fork). upstream/main should win.
        string[] refs = ["feature/x", "main", "origin/main", "origin/feature/x", "upstream/main", "origin/HEAD"];
        var picks = GitRepoService.PickBaseRefCandidates("feature/x", refs);

        Assert.Equal(["upstream/main", "origin/main", "main"], picks);
    }

    [Fact]
    public void BaseRef_RanksTrunkNamesWithinATier()
    {
        // Within one tier, main beats master beats develop; non-trunk names never qualify.
        string[] refs = ["develop", "master", "main", "some-feature", "topic/work"];
        var picks = GitRepoService.PickBaseRefCandidates("some-feature", refs);

        Assert.Equal(["main", "master", "develop"], picks);
    }

    [Fact]
    public void BaseRef_ExcludesCurrentBranchAndHeadPseudoRefs()
    {
        // A branch can't be its own base; */HEAD symbolic refs are dropped.
        string[] refs = ["main", "origin/main", "origin/HEAD", "upstream/HEAD"];
        var picks = GitRepoService.PickBaseRefCandidates("main", refs);

        // Local "main" excluded (it's the current branch); origin/main survives; the HEAD refs are dropped.
        Assert.Equal(["origin/main"], picks);
    }

    [Fact]
    public void BaseRef_EmptyWhenNoTrunkRefsExist()
    {
        string[] refs = ["feature/a", "topic/b", "origin/feature/a"];
        Assert.Empty(GitRepoService.PickBaseRefCandidates("feature/a", refs));
    }
}
