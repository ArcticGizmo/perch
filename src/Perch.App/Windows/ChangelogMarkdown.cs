using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Perch.Avalonia.Theming;

namespace Perch.Avalonia.Windows;

/// <summary>
/// Loads the embedded <c>CHANGELOG.md</c> and renders its (lightweight) markdown into a stacked column of
/// themed text. Shared by the Settings → Changelog page and the post-update <see cref="ChangelogWindow"/>
/// so the two read identically. Handles just the subset the changelog uses: <c>## </c>/<c>### </c>
/// headings, <c>-</c>/<c>*</c> bullets, <c>&gt; </c> quotes, <c>---</c> rules, and inline emphasis/links.
/// </summary>
internal static class ChangelogMarkdown
{
    /// <summary>Reads the changelog embedded at build time (csproj: <c>Perch.CHANGELOG.md</c>), or null.</summary>
    public static string? LoadEmbedded()
    {
        try
        {
            using var s = typeof(ChangelogMarkdown).Assembly.GetManifestResourceStream("Perch.CHANGELOG.md");
            if (s is null) return null;
            using var reader = new StreamReader(s);
            return reader.ReadToEnd();
        }
        catch { return null; }
    }

    /// <summary>Appends one control per markdown line into <paramref name="page"/>.</summary>
    public static void Render(StackPanel page, IEnumerable<string> lines)
    {
        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd('\r');
            if (line.StartsWith("## "))
                page.Children.Add(SettingsUi.SectionTitle(StripInline(line[3..])));
            else if (line.StartsWith("### "))
                page.Children.Add(new TextBlock
                {
                    Text = StripInline(line[4..]), FontSize = 13, FontWeight = FontWeight.Bold,
                    Foreground = Palette.FgBrush, Margin = new Thickness(0, 6, 0, 4),
                });
            else if (line.StartsWith("# ")) { /* the H1 title is redundant here */ }
            else if (line.StartsWith("- ") || line.StartsWith("* "))
                page.Children.Add(SettingsUi.BodyText("•  " + StripInline(line[2..])));
            else if (line == "---")
                page.Children.Add(SettingsUi.Separator());
            else if (line.StartsWith("> "))
                page.Children.Add(new TextBlock
                {
                    Text = StripInline(line[2..]), TextWrapping = TextWrapping.Wrap, FontSize = 13,
                    FontStyle = FontStyle.Italic, Foreground = Palette.MutedBrush, Margin = new Thickness(12, 0, 0, 6),
                });
            else if (line.Trim().Length > 0)
                page.Children.Add(SettingsUi.BodyText(StripInline(line)));
        }
    }

    /// <summary>Strips inline markdown (bold/italic/code/links) down to its display text.</summary>
    public static string StripInline(string text)
    {
        text = Regex.Replace(text, @"\*\*(.*?)\*\*", "$1");
        text = Regex.Replace(text, @"__(.*?)__", "$1");
        text = Regex.Replace(text, @"\*(.*?)\*", "$1");
        text = Regex.Replace(text, @"_(.*?)_", "$1");
        text = Regex.Replace(text, @"`([^`]+)`", "$1");
        text = Regex.Replace(text, @"\[([^\]]+)\]\([^)]+\)", "$1");
        return text;
    }
}
