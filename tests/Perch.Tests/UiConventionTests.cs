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

    // The feeds story card renders untrusted content, so it must never hand MarkdownView a file-reference context —
    // that would arm path-like code spans and links against the local disk (docs/feeds-plan.md §3.4.2, "MarkdownView
    // wiring"). FeedCardTests covers the converter's side; this pins the window's.
    [Fact]
    public void Feed_story_window_never_arms_file_references()
    {
        var file = Path.Combine(RepoRoot(), "src", "Perch.App", "Windows", "FeedStoryWindow.cs");
        var source = File.ReadAllText(file);
        Assert.Contains("MarkdownView.Build(", source);
        Assert.DoesNotContain("FileRefContext(", source);
        Assert.DoesNotContain("FileRef.", source);
        foreach (var call in source.Split("MarkdownView.Build(").Skip(1))
        {
            // Two arguments only: the Markdown and the style (the overload that passes no FileRefContext).
            var args = call[..call.IndexOf(')')];
            Assert.True(args.Count(c => c == ',') == 1, "FeedStoryWindow must call MarkdownView.Build(md, style) only: " + args);
        }
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
