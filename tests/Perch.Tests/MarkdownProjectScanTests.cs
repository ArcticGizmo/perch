using Perch.Data;
using Xunit;

namespace Perch.Tests;

public class MarkdownProjectScanTests
{
    // Builds a throwaway directory tree under the system temp dir (deliberately NOT a git repo, so the scan
    // takes its bounded-walk fallback), runs the scan, and cleans up.
    private static MarkdownProjectFiles ScanTempTree(Action<string> build)
    {
        var root = Path.Combine(Path.GetTempPath(), "perch-mdscan-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            build(root);
            return MarkdownProjectScan.Scan(root);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private static void Write(string root, string relative, string content = "x")
    {
        var full = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    [Fact]
    public void Scan_FindsMarkdownRecursively_RelativeAndSorted()
    {
        var result = ScanTempTree(root =>
        {
            Write(root, "README.md");
            Write(root, "docs/plan.md");
            Write(root, "docs/guide.markdown");
            Write(root, "src/Program.cs");        // non-md — ignored
            Write(root, "notes.txt");             // non-md — ignored
        });

        Assert.False(result.Truncated);
        Assert.Equal(
            new[] { "docs/guide.markdown", "docs/plan.md", "README.md" },
            result.RelativePaths);   // forward-slashed, ordinal-ignore-case sort ('docs' < 'README')
    }

    [Fact]
    public void Scan_SkipsNoiseDirectories()
    {
        var result = ScanTempTree(root =>
        {
            Write(root, "keep.md");
            Write(root, "node_modules/pkg/readme.md");   // dependency cache — skipped
            Write(root, "bin/output.md");                // build output — skipped
            Write(root, ".git/hooks/note.md");           // VCS metadata — skipped
        });

        Assert.Equal(new[] { "keep.md" }, result.RelativePaths);
    }

    [Fact]
    public void Scan_EmptyWhenNoMarkdownAndWhenMissing()
    {
        var noMd = ScanTempTree(root => Write(root, "src/Program.cs"));
        Assert.Empty(noMd.RelativePaths);
        Assert.False(noMd.Truncated);

        Assert.Empty(MarkdownProjectScan.Scan(@"C:\no\such\dir\perch-test").RelativePaths);
        Assert.Empty(MarkdownProjectScan.Scan("").RelativePaths);
    }
}
