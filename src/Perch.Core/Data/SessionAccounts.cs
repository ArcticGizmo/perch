using System.Text.Json;

namespace Perch.Data;

/// <summary>
/// Which config dir (account) each session ran under, remembered by session id and persisted per-profile as
/// <c>session-accounts.json</c>, so a later <c>claude --resume</c> runs under that account
/// (<see cref="TranscriptLocator.ResumeConfigRoot"/>).
///
/// <para>Nothing on disk says this once a session has ended. When several config dirs junction <c>projects/</c> onto
/// one folder, the transcript is found under the first of them whoever ran it. The hook's <c>{sessionId}.configdir</c>
/// marker is deleted by its <c>SessionEnd</c> cleanup, which also fires for the old id on <c>/clear</c>. So Perch
/// writes it down while the session is live: <see cref="SessionMonitor"/> records each live session's attributed dir,
/// and <c>PerchSession</c> records the dir it launched under.</para>
///
/// <para>Only kept when the set has more than one config dir (with one there's nothing to choose). The last account
/// seen wins. Bounded to <see cref="MaxEntries"/>, oldest dropped first. Best-effort: an unreadable file starts
/// empty, and losing it only means falling back to the marker and the transcript's owner. Never touches disk under a
/// pinned <c>CLAUDE_CONFIG_DIR</c> (tests, replay) or with persistence disabled (render).</para>
/// </summary>
internal static class SessionAccounts
{
    internal const int MaxEntries = 2000;

    // An unchanged account is re-stamped at most this often, so the monitor's per-scan calls don't write the file.
    private static readonly TimeSpan RestampAfter = TimeSpan.FromDays(1);

    private sealed record Entry(string Root, DateTime SeenUtc);

    private static readonly Lock Gate = new();
    private static readonly Lock SaveGate = new();
    private static Dictionary<string, Entry>? _map;   // loaded on first use
    private static int _saveQueued;

    private static string? PersistencePath =>
        PersistencePathForTesting
        ?? (ClaudeConfigSet.IsPinned || AppSettings.PersistenceDisabled
            ? null
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                AppProfile.DataFolderName, "session-accounts.json"));

    /// <summary>Records that <paramref name="sessionId"/> runs under the config dir at <paramref name="root"/>; null
    /// means Perch's own environment, i.e. the primary. No-op with a single config dir.</summary>
    public static void Remember(string? sessionId, string? root)
    {
        if (string.IsNullOrEmpty(sessionId) || !ClaudeConfigSet.Instance.IsMulti) return;
        root = string.IsNullOrWhiteSpace(root) ? ClaudeConfigSet.Instance.Primary.Root : root;
        var now = DateTime.UtcNow;
        lock (Gate)
        {
            var map = Map();
            if (map.TryGetValue(sessionId, out var known)
                && ClaudeConfigDir.PathComparer.Equals(known.Root, root) && now - known.SeenUtc < RestampAfter)
                return;
            map[sessionId] = new Entry(root, now);
            if (map.Count > MaxEntries)
                foreach (var old in map.OrderBy(kv => kv.Value.SeenUtc).Take(map.Count - MaxEntries).Select(kv => kv.Key).ToList())
                    map.Remove(old);
        }
        SaveSoon();
    }

    /// <summary>The config-dir root <paramref name="sessionId"/> last ran under, or null when it was never seen.</summary>
    public static string? Recall(string sessionId)
    {
        if (string.IsNullOrEmpty(sessionId)) return null;
        lock (Gate) return Map().TryGetValue(sessionId, out var e) ? e.Root : null;
    }

    private static Dictionary<string, Entry> Map()
    {
        if (_map is { } map) return map;
        map = new Dictionary<string, Entry>(StringComparer.Ordinal);
        if (PersistencePath is { } path && AtomicFile.TryRead(path, out var json) == AtomicFile.ReadResult.Ok)
        {
            try
            {
                foreach (var (id, e) in JsonSerializer.Deserialize<Dictionary<string, Entry>>(json) ?? [])
                    if (!string.IsNullOrEmpty(id) && !string.IsNullOrWhiteSpace(e?.Root))
                        map[id] = e with { SeenUtc = DateTime.SpecifyKind(e.SeenUtc, DateTimeKind.Utc) };
            }
            catch { /* a corrupt file starts empty */ }
        }
        return _map = map;
    }

    // Off the calling thread (the monitor's scan, or the UI thread at launch); a burst becomes one write.
    private static void SaveSoon()
    {
        if (PersistencePath is null || Interlocked.Exchange(ref _saveQueued, 1) == 1) return;
        Task.Run(() =>
        {
            Interlocked.Exchange(ref _saveQueued, 0);
            Save();
        });
    }

    private static void Save()
    {
        try
        {
            lock (SaveGate)
            {
                if (PersistencePath is not { } path) return;
                string json;
                lock (Gate) json = JsonSerializer.Serialize(Map());
                AtomicFile.Write(path, json);
            }
        }
        catch { /* best-effort: the account is re-learned the next time the session is seen live */ }
    }

    // --- Test seams ---------------------------------------------------------------------------------------------

    /// <summary>Redirects the file at a temp path so a round-trip can run under the pinned test env.</summary>
    internal static string? PersistencePathForTesting { get; set; }

    /// <summary>Writes the file now (a test can't wait on the background save).</summary>
    internal static void SaveNowForTesting() => Save();

    /// <summary>Drops the in-memory map, so the next use reloads it from <see cref="PersistencePathForTesting"/>.</summary>
    internal static void ReloadForTesting()
    {
        lock (Gate) _map = null;
    }

    internal static void ResetForTesting()
    {
        lock (Gate) _map = null;
        PersistencePathForTesting = null;
    }
}
