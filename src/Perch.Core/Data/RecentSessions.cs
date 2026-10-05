namespace Perch.Data;

/// <summary>One row of the Recent list: a finished session, how it ended, and whether that was just before a
/// shutdown.</summary>
internal sealed record RecentSession(HistoryEntry Entry, SessionEnd End, bool JustBeforeShutdown)
{
    /// <summary>When it ended; the transcript's last change when the ending couldn't be read.</summary>
    public DateTime EndedAt => End.EndedAt ?? Entry.LastUpdated;

    /// <summary>Badged and sorted first (Q1/Q4): it died without a clean exit, or ended just before a shutdown.</summary>
    public bool IsFlagged => End.Kind == SessionEndKind.Abrupt || JustBeforeShutdown;
}

/// <summary>
/// Builds the Recent list (docs/session-recovery-plan.md, Q1/Q2/Q4): sessions that ended in the last
/// <see cref="SessionRecovery.RecentWindow"/>, flagged ones first, then newest first. Leaves out sessions that are
/// still running, the ones Perch holds itself (they're shown as Perch's own), ones whose transcript has no message
/// (started and ended without a prompt), and dismissed endings — a dismissal covers the ending it was made on, so a
/// session that's resumed and ends again comes back.
/// <para>Holds one <see cref="ExitCommandIndex"/> per config dir, found from each transcript's path, so the prompt
/// history is read incrementally across builds; and each transcript's tail facts by (length, last write), so a rebuild
/// (one per session ending) re-reads only the transcripts that changed — a finished one never does. Reads files: call
/// it off the UI thread. Never throws.</para>
/// </summary>
internal sealed class RecentSessions
{
    private readonly Dictionary<string, ExitCommandIndex> _exitIndexes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (long Length, DateTime Write, SessionEndReader.TailFacts Tail)> _tails =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _gate = new();

    /// <param name="entries">Every transcript, e.g. from <see cref="SessionHistory.ListAll"/>.</param>
    /// <param name="heldByPerch">The sessions Perch holds (<see cref="SessionLedger.HeldIds"/>).</param>
    /// <param name="dismissed">Session id → the end time of the ending the user dismissed.</param>
    /// <param name="shutdowns">Recent shutdowns (<see cref="SessionLedger.ShutdownsSnapshot"/>).</param>
    public IReadOnlyList<RecentSession> Build(
        IEnumerable<HistoryEntry> entries,
        IReadOnlySet<string> heldByPerch,
        IReadOnlyDictionary<string, DateTime> dismissed,
        IReadOnlyList<DateTime> shutdowns,
        DateTime now)
    {
        var since = now - SessionRecovery.RecentWindow;
        var refreshed = new HashSet<ExitCommandIndex>();
        var rows = new List<RecentSession>();
        var considered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in entries)
        {
            try
            {
                if (entry.IsPlaceholder || entry.IsActive || heldByPerch.Contains(entry.SessionId)) continue;
                if (entry.LastUpdated < since) continue;   // cheap pre-filter; the end time is checked below

                var index = IndexFor(entry.Path);
                if (index is not null && refreshed.Add(index)) index.Refresh();
                considered.Add(entry.Path);
                var row = new RecentSession(entry, EndOf(entry.Path, index?.LastExit(entry.SessionId)), false);

                if (!row.End.HasConversation) continue;   // never got a prompt: nothing to resume
                if (row.EndedAt < since) continue;
                if (dismissed.TryGetValue(entry.SessionId, out var dismissedEnd) && row.EndedAt <= dismissedEnd) continue;
                rows.Add(row with { JustBeforeShutdown = SessionRecovery.JustBeforeShutdown(row.EndedAt, shutdowns) });
            }
            catch
            {
                // One unreadable transcript never sinks the list.
            }
        }

        // Only what this build looked at stays cached, so the cache never outgrows the Recent window.
        lock (_gate)
            foreach (var path in _tails.Keys.Where(p => !considered.Contains(p)).ToList()) _tails.Remove(path);

        return rows
            .OrderByDescending(r => r.IsFlagged)
            .ThenByDescending(r => r.EndedAt)
            .ToList();
    }

    /// <summary>How the session at <paramref name="path"/> ended (<see cref="SessionEndReader.Read"/>), reading its tail
    /// only when the file changed since this builder last read it. Never throws.</summary>
    public SessionEnd EndOf(string path, DateTime? lastExitCommand)
    {
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists || file.Length == 0) return SessionEnd.Unknown;
            long length = file.Length;
            var write = file.LastWriteTime;
            lock (_gate)
            {
                if (_tails.TryGetValue(path, out var hit) && hit.Length == length && hit.Write == write)
                    return SessionEndReader.Classify(hit.Tail, lastExitCommand, write);
            }
            var tail = SessionEndReader.ScanTail(path, length);
            lock (_gate) _tails[path] = (length, write, tail);
            return SessionEndReader.Classify(tail, lastExitCommand, write);
        }
        catch
        {
            return SessionEnd.Unknown;
        }
    }

    // {root}/projects/{encoded-cwd}/{sessionId}.jsonl → the index over {root}/history.jsonl. Null when the path isn't
    // laid out that way.
    private ExitCommandIndex? IndexFor(string transcriptPath)
    {
        var projectDir = Path.GetDirectoryName(transcriptPath);
        var projectsDir = projectDir is null ? null : Path.GetDirectoryName(projectDir);
        if (projectsDir is null || !string.Equals(Path.GetFileName(projectsDir), "projects", StringComparison.OrdinalIgnoreCase))
            return null;
        var root = Path.GetDirectoryName(projectsDir);
        if (string.IsNullOrEmpty(root)) return null;

        lock (_gate)
        {
            if (!_exitIndexes.TryGetValue(root, out var index))
                _exitIndexes[root] = index = new ExitCommandIndex(new ClaudeConfigDir(root).HistoryFile);
            return index;
        }
    }
}
