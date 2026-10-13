using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace Perch.Data;

/// <summary>A resolved file reference: the absolute path of a real local file, and the 1-based line it points at
/// (0 when the reference named no line).</summary>
public readonly record struct FileRefTarget(string Path, int Line);

/// <summary>
/// Resolves a transcript's inline-code span (or markdown link target) to the absolute path of a real local file,
/// so the session UI can make it clickable. The text is untrusted, so only paths <see cref="LocalPath"/> deems
/// safe are probed — a <c>`\\attacker\s\a.md`</c> span never touches the network (review fixes CP9). Results
/// are cached for a few seconds per (cwd, text): a streaming reply re-renders the same spans ~25 times a second,
/// and each re-render would otherwise hit the disk again. Never throws.
/// </summary>
public static partial class FileRefResolver
{
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(5);
    private const int MaxEntries = 1024;
    private static readonly ConcurrentDictionary<(string Cwd, string Text), (string? Path, long AtTicks)> Cache = new();

    /// <summary>The absolute path <paramref name="text"/> names, or null when it isn't an existing local file. A
    /// cheap pre-filter (must contain a '.', '/' or '\') skips the disk check for plainly non-path code like
    /// <c>true</c> or <c>SessionStart</c>.</summary>
    public static string? Resolve(string cwd, string text) => Resolve(cwd, text, File.Exists, cache: true);

    /// <summary>As <see cref="Resolve(string,string)"/>, also accepting a line suffix — <c>src/a.cs:42</c>,
    /// <c>src/a.cs:42:7</c>, <c>src/a.cs#L42</c> — the way Claude cites code. The suffix is split off before the
    /// path is judged and probed, so it never widens what may be touched.</summary>
    public static FileRefTarget? ResolveRef(string cwd, string text) => ResolveRef(cwd, text, File.Exists, cache: true);

    /// <summary><see cref="ResolveRef(string,string)"/> with the existence check injected, for tests.</summary>
    internal static FileRefTarget? ResolveRef(string cwd, string text, Func<string, bool> exists, bool cache = false)
    {
        var (path, line) = SplitLine(text.Trim());
        return Resolve(cwd, path, exists, cache) is { } abs ? new FileRefTarget(abs, line) : null;
    }

    /// <summary>Splits a trailing <c>:line</c>, <c>:line:col</c>, <c>:line-line</c> or <c>#Lline</c> off
    /// <paramref name="text"/>; the line is 0 when there is none.</summary>
    public static (string Path, int Line) SplitLine(string text)
    {
        var m = LineSuffix().Match(text);
        if (!m.Success || !int.TryParse(m.Groups["l"].Value, out int line) || line <= 0) return (text, 0);
        return (m.Groups["p"].Value, line);
    }

    [GeneratedRegex(@"^(?<p>.*?[^:])(?::(?<l>\d{1,7})(?:[:-]\d{1,7})?|#L(?<l>\d{1,7})(?:-L?\d{1,7})?)$")]
    private static partial Regex LineSuffix();

    /// <summary><see cref="Resolve(string,string)"/> with the existence check injected — for tests, which assert
    /// that an unsafe path is never probed at all. Uncached unless asked.</summary>
    internal static string? Resolve(string cwd, string text, Func<string, bool> exists, bool cache = false)
    {
        text = text.Trim();
        if (text.Length is 0 or > 260 || text.IndexOfAny(['.', '/', '\\']) < 0)
            return null;

        var key = (cwd, text);
        long now = Environment.TickCount64;
        if (cache && Cache.TryGetValue(key, out var hit) && now - hit.AtTicks < Ttl.TotalMilliseconds)
            return hit.Path;

        string? result = Probe(cwd, text, exists);
        if (cache)
        {
            if (Cache.Count >= MaxEntries) Cache.Clear();   // crude bound; entries are cheap to recompute
            Cache[key] = (result, now);
        }
        return result;
    }

    private static string? Probe(string cwd, string text, Func<string, bool> exists)
    {
        try
        {
            string abs;
            if (Path.IsPathRooted(text) || LocalPath.IsNetworkShaped(text)) abs = text;
            else if (string.IsNullOrEmpty(cwd)) return null;
            else abs = Path.GetFullPath(Path.Combine(cwd, text));

            // An NTFS alternate stream ("a.md:payload") would hide the real target behind the visible name.
            if (OperatingSystem.IsWindows() && abs.Length > 2 && abs.IndexOf(':', 2) >= 0) return null;
            // Judged against the session cwd: a user working on a share may open files on that same share.
            if (!LocalPath.IsSafeToProbe(abs, cwd)) return null;
            return exists(abs) ? abs : null;
        }
        catch { return null; }
    }
}
