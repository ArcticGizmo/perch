using Xunit;

namespace Perch.Tests;

/// <summary>
/// Enforces UI conventions that the compiler alone won't. Currently: Avalonia's <c>TextBox.Watermark</c> is
/// obsolete — the placeholder text of an input must be set via <c>PlaceholderText</c> — so no source under
/// <c>src/</c> may mention <c>Watermark</c> at all. A grep-style guard (like the reflection-based settings
/// coverage tests) keeps the rule from silently regressing when someone reaches for the familiar name.
/// </summary>
public class UiConventionTests
{
    [Fact]
    public void No_source_uses_TextBox_Watermark()
    {
        var srcDir = Path.Combine(RepoRoot(), "src");
        var offenders = SourceFiles(srcDir)
            .Where(f => File.ReadAllText(f).Contains("Watermark", StringComparison.Ordinal))
            .Select(f => Path.GetRelativePath(RepoRoot(), f))
            .OrderBy(f => f)
            .ToList();

        Assert.True(offenders.Count == 0,
            "Avalonia's TextBox.Watermark is obsolete — set the input's placeholder via PlaceholderText " +
            "instead. Offending file(s): " + string.Join(", ", offenders));
    }

    // .cs / .axaml under src/, skipping the bin/obj build output.
    private static IEnumerable<string> SourceFiles(string root) =>
        Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                     || f.EndsWith(".axaml", StringComparison.OrdinalIgnoreCase))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                     && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 12 && dir is not null; i++, dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "perch.slnx"))) return dir.FullName;
        throw new InvalidOperationException("Could not locate the repo root (the directory with perch.slnx).");
    }
}
