using Perch.Data;
using Xunit;

namespace Perch.Tests;

/// <summary>The composer's @-mention file source. The git path is exercised by the real repo in dev; here we
/// cover the non-git bounded-walk fallback (deterministic, no git dependency) and the empty/missing cases.</summary>
public class ProjectFileScanTests
{
    [Fact]
    public void Scan_NonGitDirectory_WalksFilesRelativeAndForwardSlashed()
    {
        var root = Path.Combine(Path.GetTempPath(), "perch-scan-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "src"));
            File.WriteAllText(Path.Combine(root, "README.md"), "x");
            File.WriteAllText(Path.Combine(root, "src", "Program.cs"), "x");
            // A noise directory the walk must skip.
            Directory.CreateDirectory(Path.Combine(root, "node_modules"));
            File.WriteAllText(Path.Combine(root, "node_modules", "junk.js"), "x");

            var files = ProjectFileScan.Scan(root).RelativePaths;

            Assert.Contains("README.md", files);
            Assert.Contains("src/Program.cs", files);                       // forward-slashed
            Assert.DoesNotContain(files, f => f.Contains("node_modules"));  // skipped
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Scan_MissingDirectory_IsEmpty()
    {
        var missing = Path.Combine(Path.GetTempPath(), "perch-scan-missing-" + Guid.NewGuid().ToString("N"));
        Assert.Empty(ProjectFileScan.Scan(missing).RelativePaths);
    }
}
