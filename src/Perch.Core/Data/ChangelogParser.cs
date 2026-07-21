using System.Text.RegularExpressions;

namespace Perch.Data;

/// <summary>
/// One version block from <c>CHANGELOG.md</c>: the raw markdown lines under a <c>## </c> heading (heading
/// included, trailing blank/rule lines trimmed) plus the version parsed out of that heading. Unversioned
/// sections (e.g. <c>## [Unreleased]</c>) keep a null <see cref="Version"/> so they can be parsed but
/// filtered out of any "what's new" range.
/// </summary>
public sealed record ChangelogSection(string Heading, string Display, Version? Version, IReadOnlyList<string> Block);

/// <summary>
/// Splits the (embedded) <c>CHANGELOG.md</c> into per-version sections and picks out the ones newer than a
/// given "last seen" version. Pure and UI-free so it can be unit-tested; the app reads the embedded text
/// and hands it in. Headings look like <c>## [v0.2.11] - 2026-07-21</c>; the <c>v0.2.11</c> is the version.
/// </summary>
public static class ChangelogParser
{
    private static readonly Regex VersionRx = new(@"v?(\d+(?:\.\d+){1,3})", RegexOptions.Compiled);

    /// <summary>Parses every <c>## </c> section, in document order (newest first, as the file is written).</summary>
    public static IReadOnlyList<ChangelogSection> Parse(string markdown)
    {
        var sections = new List<ChangelogSection>();
        if (string.IsNullOrEmpty(markdown)) return sections;

        List<string>? block = null;
        string heading = "";

        void Flush()
        {
            if (block is null) return;
            // Drop trailing blank lines and the "---" rule that separates this section from the next.
            int end = block.Count;
            while (end > 0 && (block[end - 1].Trim().Length == 0 || block[end - 1].Trim() == "---")) end--;
            var trimmed = block.GetRange(0, end);
            TryParseVersion(heading, out var version, out var display);
            sections.Add(new ChangelogSection(heading, display, version, trimmed));
        }

        foreach (var raw in markdown.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("## "))
            {
                Flush();
                heading = line[3..].Trim();
                block = new List<string> { line };
            }
            else
            {
                block?.Add(line);
            }
        }
        Flush();
        return sections;
    }

    /// <summary>
    /// The sections strictly newer than <paramref name="lastSeen"/> and no newer than
    /// <paramref name="current"/>, in the file's own (newest-first) order. Returns nothing when
    /// <paramref name="lastSeen"/> is null/blank/unparseable (a fresh install has no history to diff
    /// against) or equals <paramref name="current"/>. Unversioned sections are never included.
    /// </summary>
    public static IReadOnlyList<ChangelogSection> UnseenSince(string markdown, string? lastSeen, string current)
    {
        if (!TryParse(lastSeen, out var low)) return [];
        _ = TryParse(current, out var high); // high null → no upper bound (defensive)

        var result = new List<ChangelogSection>();
        foreach (var s in Parse(markdown))
        {
            if (s.Version is null) continue;
            if (s.Version <= low) continue;
            if (high is not null && s.Version > high) continue;
            result.Add(s);
        }
        return result;
    }

    /// <summary>Pulls a <see cref="System.Version"/> out of a heading like <c>[v0.2.11] - 2026-07-21</c>.</summary>
    public static bool TryParseVersion(string heading, out Version? version, out string display)
    {
        version = null;
        display = heading;
        var m = VersionRx.Match(heading ?? "");
        if (!m.Success) return false;
        if (!System.Version.TryParse(m.Groups[1].Value, out var v)) return false;
        version = v;
        display = "v" + m.Groups[1].Value;
        return true;
    }

    private static bool TryParse(string? version, out Version? parsed)
    {
        parsed = null;
        if (string.IsNullOrWhiteSpace(version)) return false;
        var m = VersionRx.Match(version);
        if (!m.Success) return false;
        if (!System.Version.TryParse(m.Groups[1].Value, out var v)) return false;
        parsed = v;
        return true;
    }
}
