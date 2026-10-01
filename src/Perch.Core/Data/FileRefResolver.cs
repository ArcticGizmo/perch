using System.Collections.Concurrent;

namespace Perch.Data;

/// <summary>
/// Resolves a transcript's inline-code span (or markdown link target) to the absolute path of a real local file,
/// so the session UI can make it clickable. The text is untrusted, so only paths <see cref="LocalPath"/> deems
/// safe are probed — a <c>`\\attacker\s\a.md`</c> span never touches the network (review fixes CP9). Results
/// are cached for a few seconds per (cwd, text): a streaming reply re-renders the same spans ~25 times a second,
/// and each re-render would otherwise hit the disk again. Never throws.
/// </summary>
public static class FileRefResolver
{
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(5);
    private const int MaxEntries = 1024;
    private static readonly ConcurrentDictionary<(string Cwd, string Text), (string? Path, long AtTicks)> Cache = new();

    /// <summary>The absolute path <paramref name="text"/> names, or null when it isn't an existing local file. A
    /// cheap pre-filter (must contain a '.', '/' or '\') skips the disk check for plainly non-path code like
    /// <c>true</c> or <c>SessionStart</c>.</summary>
    public static string? Resolve(string cwd, string text) => Resolve(cwd, text, File.Exists, cache: true);

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

            // Judged against the session cwd: a user working on a share may open files on that same share.
            if (!LocalPath.IsSafeToProbe(abs, cwd)) return null;
            return exists(abs) ? abs : null;
        }
        catch { return null; }
    }
}
