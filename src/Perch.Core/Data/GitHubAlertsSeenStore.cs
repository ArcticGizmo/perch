namespace Perch.Data;

using System.Text.Json;

/// <summary>
/// When you last opened each pull request from the GitHub alerts window, keyed by PR URL and persisted per-profile
/// as <c>github-alerts-seen.json</c>. <see cref="GitHubAlertsClassifier"/> reads it so a PR whose new comments
/// you've already gone to read stops counting as "needs you", without Perch writing anything to GitHub.
///
/// <para>Low-stakes state (losing it just re-raises a few alerts), so IO is plain best-effort: an unreadable
/// file starts empty. <see cref="Prune"/> drops PRs that have left the open set, so the file stays small.</para>
/// </summary>
internal sealed class GitHubAlertsSeenStore
{
    private static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        AppProfile.DataFolderName, "github-alerts-seen.json");

    private readonly string _path;
    private readonly Dictionary<string, DateTime> _seen;

    private GitHubAlertsSeenStore(string path, Dictionary<string, DateTime> seen)
    {
        _path = path;
        _seen = seen;
    }

    public static GitHubAlertsSeenStore Load() => LoadFrom(DefaultPath);

    /// <summary>A store that never reads or writes a file — for the headless render and tests.</summary>
    internal static GitHubAlertsSeenStore InMemory() => new("", new(StringComparer.OrdinalIgnoreCase));

    // internal for tests — round-trips a temp path without touching the real profile.
    internal static GitHubAlertsSeenStore LoadFrom(string path)
    {
        var seen = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        if (AtomicFile.TryRead(path, out var json) == AtomicFile.ReadResult.Ok)
        {
            try
            {
                foreach (var (url, at) in JsonSerializer.Deserialize<Dictionary<string, DateTime>>(json) ?? [])
                    seen[url] = DateTime.SpecifyKind(at, DateTimeKind.Utc);
            }
            catch { }
        }
        return new GitHubAlertsSeenStore(path, seen);
    }

    public IReadOnlyDictionary<string, DateTime> All => _seen;

    /// <summary>Records that the PR at <paramref name="url"/> was opened at <paramref name="atUtc"/>.</summary>
    public void MarkSeen(string url, DateTime atUtc) => _seen[url] = atUtc;

    /// <summary>Forgets every PR not in <paramref name="openUrls"/>. Returns whether anything was dropped.</summary>
    public bool Prune(IEnumerable<string> openUrls)
    {
        var keep = new HashSet<string>(openUrls, StringComparer.OrdinalIgnoreCase);
        var gone = _seen.Keys.Where(k => !keep.Contains(k)).ToList();
        foreach (var k in gone) _seen.Remove(k);
        return gone.Count > 0;
    }

    public void Save()
    {
        if (_path.Length == 0) return;
        try { AtomicFile.Write(_path, JsonSerializer.Serialize(_seen)); }
        catch { }
    }
}
