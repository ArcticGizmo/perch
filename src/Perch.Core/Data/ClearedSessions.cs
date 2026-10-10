using Perch.Platform;

namespace Perch.Data;

/// <summary>
/// The conversations a <c>/clear</c> replaced inside a process that is still running. <c>/clear</c> keeps the process
/// and gives it a new session id, so the old id leaves the live scan without having ended — and would otherwise come
/// back as a not-running Recent row (and a dormant Roost pane) right beside the session it turned into, where a reply
/// resumes it in a second process. Each scan is folded in with <see cref="Update"/>: a process seen under a new id
/// marks its previous id cleared, and the mark goes when that process ends, after which the old conversation lists
/// like any other past session.
///
/// <para>A process missing from one scan hasn't necessarily ended: a read that catches its session file mid-rewrite
/// (which is exactly what <c>/clear</c> does to it) drops it from that scan. So a pid is remembered until its process
/// is gone, or it has been missing for <see cref="MissingGrace"/> (a recycled pid can't hold a mark forever).</para>
/// </summary>
internal sealed class ClearedSessions(IProcessProbe? probe = null)
{
    /// <summary>How long a pid missing from the scan is remembered while the OS still reports a process for it.</summary>
    internal static readonly TimeSpan MissingGrace = TimeSpan.FromMinutes(2);

    private readonly IProcessProbe _probe = probe ?? SystemProcessProbe.Instance;
    // pid -> the session id it last ran as, and when the scan last saw it
    private readonly Dictionary<string, (string Id, DateTime Seen)> _byPid = new(StringComparer.Ordinal);
    // cleared session id -> the pid that cleared it
    private readonly Dictionary<string, string> _clearedBy = new(StringComparer.Ordinal);

    /// <summary>Folds one scan in. True when the cleared set changed, so the Recent list needs rebuilding.</summary>
    public bool Update(IEnumerable<ClaudeSession> live, DateTime now)
    {
        bool changed = false;
        var seenPids = new HashSet<string>(StringComparer.Ordinal);
        var liveIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var s in live)
        {
            if (string.IsNullOrEmpty(s.Pid) || string.IsNullOrEmpty(s.SessionId) || s.IsDormant) continue;
            if (!seenPids.Add(s.Pid)) continue;
            liveIds.Add(s.SessionId);
            if (_byPid.TryGetValue(s.Pid, out var previous) && previous.Id != s.SessionId)
                changed |= _clearedBy.TryAdd(previous.Id, s.Pid);
            _byPid[s.Pid] = (s.SessionId, now);
        }

        foreach (var (pid, entry) in _byPid.ToList())
            if (!seenPids.Contains(pid) && (now - entry.Seen >= MissingGrace || !IsAlive(pid)))
                _byPid.Remove(pid);

        // The clearing process ended, or the old conversation is running again (resumed somewhere): no longer hidden.
        foreach (var (id, pid) in _clearedBy.ToList())
            if (!_byPid.ContainsKey(pid) || liveIds.Contains(id))
                changed |= _clearedBy.Remove(id);

        return changed;
    }

    private bool IsAlive(string pid) => int.TryParse(pid, out var n) && _probe.IsAlive(n);

    /// <summary>True when <paramref name="sessionId"/> was cleared away by a process that is still running.</summary>
    public bool Contains(string sessionId) => _clearedBy.ContainsKey(sessionId);

    /// <summary>A copy of the cleared ids, for a build running off the UI thread.</summary>
    public IReadOnlySet<string> Snapshot() => new HashSet<string>(_clearedBy.Keys, StringComparer.Ordinal);
}
