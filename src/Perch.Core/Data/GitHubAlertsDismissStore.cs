namespace Perch.Data;

using System.Text.Json;

/// <summary>One dismissal: the PR's <see cref="GitHubAlertsClassifier.Fingerprint"/> when it was dismissed, and when.</summary>
public sealed record GhDismissal(string Fingerprint, DateTime AtUtc);

/// <summary>
/// The pull requests you dismissed from the GitHub dashboard ("out of my hands"), keyed by PR URL and persisted
/// per-profile as <c>github-alerts-dismissed.json</c>. A dismissal holds only while the PR's fingerprint matches the
/// one it was taken at; <see cref="Reconcile"/> deletes it once the state moves on (so a PR whose checks go red and
/// then green again doesn't quietly vanish a second time) or the PR leaves the open set.
///
/// <para>Low-stakes state like <see cref="GitHubAlertsSeenStore"/> (losing it just brings a few PRs back), so IO
/// is plain best-effort: an unreadable file starts empty.</para>
/// </summary>
internal sealed class GitHubAlertsDismissStore
{
    private static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        AppProfile.DataFolderName, "github-alerts-dismissed.json");

    private readonly string _path;
    private readonly Dictionary<string, GhDismissal> _dismissed;

    private GitHubAlertsDismissStore(string path, Dictionary<string, GhDismissal> dismissed)
    {
        _path = path;
        _dismissed = dismissed;
    }

    public static GitHubAlertsDismissStore Load() => LoadFrom(DefaultPath);

    /// <summary>A store that never reads or writes a file — for the headless render and tests.</summary>
    internal static GitHubAlertsDismissStore InMemory() => new("", new(StringComparer.OrdinalIgnoreCase));

    // internal for tests — round-trips a temp path without touching the real profile.
    internal static GitHubAlertsDismissStore LoadFrom(string path)
    {
        var dismissed = new Dictionary<string, GhDismissal>(StringComparer.OrdinalIgnoreCase);
        if (AtomicFile.TryRead(path, out var json) == AtomicFile.ReadResult.Ok)
        {
            try
            {
                foreach (var (url, d) in JsonSerializer.Deserialize<Dictionary<string, GhDismissal>>(json) ?? [])
                    if (d is { Fingerprint.Length: > 0 })
                        dismissed[url] = d with { AtUtc = DateTime.SpecifyKind(d.AtUtc, DateTimeKind.Utc) };
            }
            catch { }
        }
        return new GitHubAlertsDismissStore(path, dismissed);
    }

    public IReadOnlyDictionary<string, GhDismissal> All => _dismissed;

    /// <summary>URL → fingerprint, the shape <see cref="GitHubAlertsClassifier.Build"/> takes.</summary>
    public IReadOnlyDictionary<string, string> Fingerprints =>
        _dismissed.ToDictionary(kv => kv.Key, kv => kv.Value.Fingerprint, StringComparer.OrdinalIgnoreCase);

    /// <summary>Dismisses the PR at <paramref name="url"/> while its state matches <paramref name="fingerprint"/>.</summary>
    public void Dismiss(string url, string fingerprint, DateTime atUtc) => _dismissed[url] = new(fingerprint, atUtc);

    /// <summary>Brings a dismissed PR back. Returns whether it was dismissed.</summary>
    public bool Restore(string url) => _dismissed.Remove(url);

    /// <summary>
    /// Deletes every dismissal whose PR is not in <paramref name="current"/> (URL → its fingerprint now: closed or
    /// merged) or whose fingerprint has moved on. Call only with a successful poll's full open set. Returns whether
    /// anything was dropped.
    /// </summary>
    public bool Reconcile(IReadOnlyDictionary<string, string> current)
    {
        var gone = _dismissed
            .Where(kv => !current.TryGetValue(kv.Key, out var fp) || fp != kv.Value.Fingerprint)
            .Select(kv => kv.Key)
            .ToList();
        foreach (var k in gone) _dismissed.Remove(k);
        return gone.Count > 0;
    }

    public void Save()
    {
        if (_path.Length == 0) return;
        try { AtomicFile.Write(_path, JsonSerializer.Serialize(_dismissed)); }
        catch { }
    }
}
