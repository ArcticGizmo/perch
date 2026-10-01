namespace Perch.Data;

/// <summary>
/// The history behind the Stats and Achievements reports, folded once and kept (review fixes CP21,
/// docs/review-fixes-plan.md). Every transcript's per-(session, day) figures — and every Agent-Teams teammate
/// transcript's per-day tokens — live in two <see cref="TranscriptFold"/>s, so a report reads only the bytes
/// appended since the last one: an unchanged transcript costs a stat, a growing one its new lines. Before this,
/// an all-time report JSON-parsed every line of every transcript each time (tens of seconds on a large history,
/// every few minutes as sessions finished).
///
/// <para>The folds are persisted per profile to <c>stats-cache.bin</c> beside <c>settings.json</c>, so a restart
/// resumes where the last run left off rather than re-reading the whole history. The file is a versioned binary
/// snapshot: any mismatch (format, fold rules, time zone — the figures are bucketed by local day) or damage
/// discards it and the next report rebuilds from the transcripts. Nothing in it is authoritative. Saves are
/// atomic and throttled to one per <see cref="SaveInterval"/>; the first build saves at once. Honours
/// <see cref="AppSettings.PersistenceDisabled"/> (tests and the headless renderer never touch the real file).</para>
///
/// <para>Thread-safe: every report runs inside <see cref="Use"/>, which serialises them, so the Stats window, the
/// Achievements window and the tray's achievement check can overlap; a caller that arrives during a cold build
/// waits for it and then gets the warm result.</para>
/// </summary>
internal sealed class SessionStatsCache
{
    private const int Magic = 0x43545350;   // "PSTC"
    private const int FormatVersion = 1;
    internal static readonly TimeSpan SaveInterval = TimeSpan.FromMinutes(10);

    private static readonly Lazy<SessionStatsCache> SharedLazy = new(() =>
        new SessionStatsCache(AppSettings.PersistenceDisabled ? null : DefaultPath));

    /// <summary>The app-wide cache (persisted unless persistence is disabled for this process).</summary>
    public static SessionStatsCache Shared => SharedLazy.Value;

    private static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        AppProfile.DataFolderName, "stats-cache.bin");

    private readonly object _gate = new();
    private readonly string? _path;
    private readonly TranscriptFold _sessions = new(SessionStatsService.SessionFolder);
    private readonly TranscriptFold _teams = new(TeamReader.TeamFolder);
    // Teammate verdicts per meta sidecar, keyed by its (length, last write) so an edited sidecar is re-read.
    private readonly Dictionary<string, (long Length, DateTime WriteUtc, bool Teammate)> _metas = new();
    private readonly HashSet<string> _seenSessions = new();
    private readonly HashSet<string> _seenTeams = new();
    private readonly HashSet<string> _seenMetas = new();
    private bool _loaded;
    private long _savedGeneration = -1;
    private DateTime _lastSaveUtc = DateTime.MinValue;

    /// <param name="path">Where to persist, or null to keep the cache in memory only.</param>
    internal SessionStatsCache(string? path) => _path = path;

    /// <summary>Bytes read from transcripts so far — the probe that proves a report reads only appended bytes.</summary>
    internal long BytesRead
    {
        get { lock (_gate) return _sessions.BytesRead + _teams.BytesRead; }
    }

    /// <summary>How many transcripts (sessions + teammates) the cache holds state for — a pruning probe.</summary>
    internal int TrackedFiles
    {
        get { lock (_gate) return _sessions.Paths.Count + _teams.Paths.Count; }
    }

    /// <summary>What a report sees while it holds the cache.</summary>
    internal sealed class View(SessionStatsCache cache)
    {
        /// <summary>The session transcript's folded state, brought up to date; null when it can't be read.</summary>
        public SessionStatsService.SessionFoldState? Session(string transcript)
        {
            cache._seenSessions.Add(transcript);
            return cache._sessions.Get(transcript, SessionStatsService.SessionFolder, s => s, null);
        }

        /// <summary>The folded state of each teammate transcript under the session's <c>subagents/</c>.</summary>
        public IEnumerable<TeamReader.TeamFoldState> Teammates(string transcript)
        {
            foreach (var agent in TeamReader.TeammateTranscripts(transcript, cache.IsTeammate))
            {
                cache._seenTeams.Add(agent);
                if (cache._teams.Get(agent, TeamReader.TeamFolder, s => s, null) is { } state)
                    yield return state;
            }
        }

        /// <summary>Drops state for every transcript this report didn't visit — only valid after a report that
        /// visited every transcript on disk (the all-time one), where "not visited" means "gone".</summary>
        public void PruneUnseen()
        {
            foreach (var p in cache._sessions.Paths.Where(p => !cache._seenSessions.Contains(p)).ToList()) cache._sessions.Forget(p);
            foreach (var p in cache._teams.Paths.Where(p => !cache._seenTeams.Contains(p)).ToList()) cache._teams.Forget(p);
            foreach (var p in cache._metas.Keys.Where(p => !cache._seenMetas.Contains(p)).ToList()) cache._metas.Remove(p);
        }
    }

    /// <summary>Runs one report against the cache, exclusively, then saves if a save is due. The states the
    /// view hands out are live — use them only inside <paramref name="report"/>.</summary>
    internal void Use(Action<View> report)
    {
        lock (_gate)
        {
            if (!_loaded)
            {
                _loaded = true;
                Load();
            }
            _seenSessions.Clear();
            _seenTeams.Clear();
            _seenMetas.Clear();
            report(new View(this));
            if (_path is not null && Generation != _savedGeneration
                && (_savedGeneration < 0 || DateTime.UtcNow - _lastSaveUtc >= SaveInterval))
                SaveCore();
        }
    }

    /// <summary>Saves now if anything changed since the last save (tests; the app relies on the throttle).</summary>
    internal void SaveNow()
    {
        lock (_gate)
            if (_path is not null && Generation != _savedGeneration) SaveCore();
    }

    private long Generation => _sessions.Generation + _teams.Generation + _metaGeneration;
    private long _metaGeneration;

    private bool IsTeammate(string metaPath)
    {
        _seenMetas.Add(metaPath);
        try
        {
            var fi = new FileInfo(metaPath);
            if (!fi.Exists) return false;   // not written yet (or an ordinary agent) — asked again next report
            if (_metas.TryGetValue(metaPath, out var m) && m.Length == fi.Length && m.WriteUtc == fi.LastWriteTimeUtc)
                return m.Teammate;
            bool teammate = TeamReader.IsTeammateMeta(metaPath);
            _metas[metaPath] = (fi.Length, fi.LastWriteTimeUtc, teammate);
            _metaGeneration++;
            return teammate;
        }
        catch { return false; }
    }

    // ── Persistence ──────────────────────────────────────────────────────────────

    private void SaveCore()
    {
        try
        {
            AtomicFile.Write(_path!, stream =>
            {
                using var w = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);
                WriteHeader(w);
                WriteFold(w, _sessions, (s, o) => WriteSession(s, (SessionStatsService.SessionFoldState)o));
                WriteFold(w, _teams, (s, o) => WriteTeam(s, (TeamReader.TeamFoldState)o));
                w.Write(_metas.Count);
                foreach (var (path, m) in _metas)
                {
                    w.Write(path);
                    w.Write(m.Length);
                    w.Write(m.WriteUtc.Ticks);
                    w.Write(m.Teammate);
                }
            });
            _savedGeneration = Generation;
            _lastSaveUtc = DateTime.UtcNow;
        }
        catch { /* best-effort: the transcripts are the source of truth; try again at the next save */ }
    }

    // Reads the persisted snapshot, all or nothing: anything unexpected leaves the cache empty (the next report
    // rebuilds it from the transcripts).
    private void Load()
    {
        if (_path is null) return;
        try
        {
            if (!File.Exists(_path)) return;
            using var fs = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var r = new BinaryReader(fs, System.Text.Encoding.UTF8);
            if (!ReadHeader(r)) return;
            var sessions = ReadFold(r, ReadSession);
            var teams = ReadFold(r, ReadTeam);
            var metas = new List<(string, (long, DateTime, bool))>();
            for (int i = 0, n = r.ReadInt32(); i < n; i++)
                metas.Add((r.ReadString(), (r.ReadInt64(), new DateTime(r.ReadInt64(), DateTimeKind.Utc), r.ReadBoolean())));
            if (fs.Position != fs.Length) return;   // trailing bytes: not a file this code wrote

            foreach (var (path, cp) in sessions) _sessions.Import(path, cp);
            foreach (var (path, cp) in teams) _teams.Import(path, cp);
            foreach (var (path, m) in metas) _metas[path] = m;
            _savedGeneration = Generation;   // what's on disk is exactly what's loaded
            _lastSaveUtc = DateTime.UtcNow;
        }
        catch { /* damaged or foreign — start empty */ }
    }

    private static void WriteHeader(BinaryWriter w)
    {
        w.Write(Magic);
        w.Write(FormatVersion);
        w.Write(SessionStatsService.SessionFoldVersion);
        w.Write(TeamReader.TeamFoldVersion);
        w.Write(TimeZoneInfo.Local.Id);   // days and hours are local: another zone would bucket differently
    }

    private static bool ReadHeader(BinaryReader r) =>
        r.ReadInt32() == Magic
        && r.ReadInt32() == FormatVersion
        && r.ReadInt32() == SessionStatsService.SessionFoldVersion
        && r.ReadInt32() == TeamReader.TeamFoldVersion
        && r.ReadString() == TimeZoneInfo.Local.Id;

    private static void WriteFold(BinaryWriter w, TranscriptFold fold, Action<BinaryWriter, object> writeState)
    {
        var checkpoints = fold.Paths.Select(p => (Path: p, Cp: fold.Export(p))).Where(x => x.Cp is not null).ToList();
        w.Write(checkpoints.Count);
        foreach (var (path, cp) in checkpoints)
        {
            w.Write(path);
            w.Write(cp!.Offset);
            w.Write(cp.SeenLength);
            w.Write(cp.SeenWriteUtc.Ticks);
            w.Write(cp.Head.Length);
            w.Write(cp.Head);
            writeState(w, cp.States[0]);
        }
    }

    private static List<(string, TranscriptFold.Checkpoint)> ReadFold(BinaryReader r, Func<BinaryReader, object> readState)
    {
        var list = new List<(string, TranscriptFold.Checkpoint)>();
        for (int i = 0, n = r.ReadInt32(); i < n; i++)
        {
            var path = r.ReadString();
            long offset = r.ReadInt64(), seen = r.ReadInt64();
            var writeUtc = new DateTime(r.ReadInt64(), DateTimeKind.Utc);
            var head = r.ReadBytes(r.ReadInt32());
            list.Add((path, new TranscriptFold.Checkpoint(offset, seen, writeUtc, head, [readState(r)])));
        }
        return list;
    }

    private static void WriteSession(BinaryWriter w, SessionStatsService.SessionFoldState s)
    {
        w.Write(s.Project);
        w.Write(s.Branch);
        w.Write(s.Days.Count);
        foreach (var (day, d) in s.Days)
        {
            w.Write(day.DayNumber);
            // Timestamps as ascending tick deltas, 7-bit encoded: most records are seconds apart, so this is
            // roughly half the size of raw ticks.
            d.EnsureSorted();
            w.Write(d.Times.Count);
            long prev = 0;
            foreach (var t in d.Times)
            {
                w.Write7BitEncodedInt64(t.Ticks - prev);
                prev = t.Ticks;
            }
            w.Write(d.Prompts);
            w.Write(d.Swears);
            w.Write(d.ToolCalls);
            w.Write(d.SubAgents);
            WriteTokens(w, d.Tokens);
            WriteCounts(w, d.ToolCounts);
            w.Write(d.Models.Count);
            foreach (var (model, tt) in d.Models)
            {
                w.Write(model);
                WriteTokens(w, tt);
            }
            WriteCounts(w, d.PromptsByParent);
        }
    }

    private static object ReadSession(BinaryReader r)
    {
        var s = new SessionStatsService.SessionFoldState { Project = r.ReadString(), Branch = r.ReadString() };
        for (int i = 0, n = r.ReadInt32(); i < n; i++)
        {
            var day = DateOnly.FromDayNumber(r.ReadInt32());
            var d = new SessionStatsService.SessionDayData();
            long ticks = 0;
            for (int k = 0, m = r.ReadInt32(); k < m; k++)
            {
                ticks += r.Read7BitEncodedInt64();
                d.Times.Add(new DateTime(ticks, DateTimeKind.Local));
            }
            d.Prompts = r.ReadInt32();
            d.Swears = r.ReadInt32();
            d.ToolCalls = r.ReadInt32();
            d.SubAgents = r.ReadInt32();
            d.Tokens = ReadTokens(r);
            ReadCounts(r, d.ToolCounts);
            for (int k = 0, m = r.ReadInt32(); k < m; k++)
                d.Models[r.ReadString()] = ReadTokens(r);
            ReadCounts(r, d.PromptsByParent);
            s.Days[day] = d;
        }
        return s;
    }

    private static void WriteTeam(BinaryWriter w, TeamReader.TeamFoldState s)
    {
        w.Write(s.Lines);
        w.Write(s.Days.Count);
        foreach (var (day, d) in s.Days)
        {
            w.Write(day.DayNumber);
            w.Write(d.FirstLine);
            WriteTokens(w, d.Tokens);
        }
    }

    private static object ReadTeam(BinaryReader r)
    {
        var s = new TeamReader.TeamFoldState { Lines = r.ReadInt64() };
        for (int i = 0, n = r.ReadInt32(); i < n; i++)
            s.Days[DateOnly.FromDayNumber(r.ReadInt32())] = new TeamReader.TeamDay { FirstLine = r.ReadInt64(), Tokens = ReadTokens(r) };
        return s;
    }

    private static void WriteTokens(BinaryWriter w, TokenTotals t)
    {
        w.Write(t.Input);
        w.Write(t.Output);
        w.Write(t.CacheWrite);
        w.Write(t.CacheRead);
    }

    private static TokenTotals ReadTokens(BinaryReader r) => new(r.ReadInt64(), r.ReadInt64(), r.ReadInt64(), r.ReadInt64());

    private static void WriteCounts(BinaryWriter w, Dictionary<string, int> counts)
    {
        w.Write(counts.Count);
        foreach (var (k, v) in counts)
        {
            w.Write(k);
            w.Write(v);
        }
    }

    private static void ReadCounts(BinaryReader r, Dictionary<string, int> into)
    {
        for (int i = 0, n = r.ReadInt32(); i < n; i++)
            into[r.ReadString()] = r.ReadInt32();
    }
}
