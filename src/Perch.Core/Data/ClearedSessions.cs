namespace Perch.Data;

/// <summary>
/// The conversations a <c>/clear</c> replaced inside a process that is still running. <c>/clear</c> keeps the process
/// and gives it a new session id, so the old id leaves the live scan without having ended — and would otherwise come
/// back as a not-running Recent row (and a dormant Roost pane) right beside the session it turned into, where a reply
/// resumes it in a second process. Each scan is folded in with <see cref="Update"/>: a process seen under a new id
/// marks its previous id cleared, and the mark goes when that process leaves the scan, after which the old
/// conversation lists like any other past session.
/// </summary>
internal sealed class ClearedSessions
{
    private Dictionary<string, string> _idByPid = new(StringComparer.Ordinal);
    // cleared session id -> the pid that cleared it
    private readonly Dictionary<string, string> _clearedBy = new(StringComparer.Ordinal);

    /// <summary>Folds one scan in.</summary>
    public void Update(IEnumerable<ClaudeSession> live)
    {
        var idByPid = new Dictionary<string, string>(StringComparer.Ordinal);
        var liveIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var s in live)
        {
            if (string.IsNullOrEmpty(s.Pid) || string.IsNullOrEmpty(s.SessionId) || s.IsDormant) continue;
            if (!idByPid.TryAdd(s.Pid, s.SessionId)) continue;
            liveIds.Add(s.SessionId);
        }

        foreach (var (pid, id) in idByPid)
            if (_idByPid.TryGetValue(pid, out var previous) && previous != id)
                _clearedBy.TryAdd(previous, pid);

        // The clearing process ended, or the old conversation is running again (resumed somewhere): no longer hidden.
        foreach (var (id, pid) in _clearedBy.ToList())
            if (!idByPid.ContainsKey(pid) || liveIds.Contains(id))
                _clearedBy.Remove(id);

        _idByPid = idByPid;
    }

    /// <summary>True when <paramref name="sessionId"/> was cleared away by a process that is still running.</summary>
    public bool Contains(string sessionId) => _clearedBy.ContainsKey(sessionId);

    /// <summary>A copy of the cleared ids, for a build running off the UI thread.</summary>
    public IReadOnlySet<string> Snapshot() => new HashSet<string>(_clearedBy.Keys, StringComparer.Ordinal);
}
