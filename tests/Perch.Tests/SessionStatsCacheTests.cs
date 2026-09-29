using System.Text;
using System.Text.Json;
using Perch.Data;
using Xunit;

namespace Perch.Tests;

/// <summary>
/// CP21 (docs/review-fixes-plan.md): the all-time stats report reads its history through
/// <see cref="SessionStatsCache"/> — transcripts folded incrementally and persisted — instead of re-parsing every
/// line of every transcript each time. The contract: a cached report always equals a fresh one, a grown file
/// costs only its new bytes, and the on-disk snapshot round-trips (or is discarded, never trusted, when damaged).
/// </summary>
public sealed class SessionStatsCacheTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "perch-statscache-" + Guid.NewGuid().ToString("N"));
    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.Now);

    // A representative slice of the fixture history (kept small: every file written under %TEMP% costs a scan on
    // an endpoint-protected machine): prompts, tools and models; a session with ordinary sub-agents; one with an
    // Agent-Teams roster (two teammates beside two plain agents); and a second project folder.
    private static readonly (string Project, string Session)[] Slice =
    [
        ("C--fixtures-proj", "sessA"), ("C--fixtures-proj", "sessB"), ("C--fixtures-proj", "sessMarkdown"),
        ("C--fixtures-proj", "sessTasksBatches"), ("C--fixtures-proj", "sessCtxSwitch"), ("C--fixtures-proj", "sessIterating"),
        ("C--fixtures-proj", "sessBg"), ("C--fixtures-proj", "sessTeam"), ("C--scan-target", "sessScan"),
    ];

    public SessionStatsCacheTests()
    {
        SessionStatsService.IdleThreshold = TimeSpan.FromMinutes(5);
        var source = Path.Combine(TestEnvironment.FixtureConfigDir, "projects");
        foreach (var (project, session) in Slice)
        {
            var dir = Path.Combine(Projects, project);
            Directory.CreateDirectory(dir);
            File.Copy(Path.Combine(source, project, session + ".jsonl"), Path.Combine(dir, session + ".jsonl"));
            if (Directory.Exists(Path.Combine(source, project, session)))
                CopyTree(Path.Combine(source, project, session), Path.Combine(dir, session));
        }
    }

    public void Dispose()
    {
        SessionStatsService.IdleThreshold = TimeSpan.FromMinutes(5);
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private string Projects => Path.Combine(_root, "projects");
    private string CachePath => Path.Combine(_root, "stats-cache.bin");

    // Session transcripts: the *.jsonl directly in each project dir (as TranscriptLocator enumerates them).
    private List<string> Transcripts() =>
        Directory.GetDirectories(Projects).SelectMany(d => Directory.GetFiles(d, "*.jsonl")).OrderBy(p => p).ToList();

    // Every transcript in the tree, teammates' included (they live under {session}/subagents/).
    private List<string> AllJsonl() =>
        Directory.GetFiles(Projects, "*.jsonl", SearchOption.AllDirectories).OrderBy(p => p).ToList();

    private string Report(SessionStatsCache cache) =>
        JsonSerializer.Serialize(SessionStatsService.ReportAllTime(Today, cache, Transcripts()));

    private string Fresh() => Report(new SessionStatsCache(null));

    [Fact]
    public void FixtureReport_MatchesTheOneShotParsers()
    {
        // The cache and the one-shot golden parsers share their step functions; check the per-day figures agree.
        var cache = new SessionStatsCache(null);
        var report = SessionStatsService.ReportAllTime(Today, cache, Transcripts());
        int sessions = 0, prompts = 0, teammates = 0;
        foreach (var file in Transcripts())
        {
            var days = SessionStatsService.ParseSession(file, null, DateTime.MaxValue);
            if (days.Count > 0) sessions++;
            prompts += days.Values.Sum(d => d.Prompts);
            teammates += TeamReader.ParseContributions(file, null, DateTime.MaxValue).Values.Sum(d => d.Teammates);
        }
        Assert.True(sessions >= 6);
        Assert.Equal(sessions, report.Totals.SessionCount);
        Assert.Equal(prompts, report.Totals.Prompts);
        Assert.Equal(2, teammates);
        Assert.Equal(teammates, report.Totals.Teammates);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void GrowingHistory_CachedReportEqualsFreshReport_AfterEveryStep(int seed)
    {
        // Every transcript (sessions and teammates) starts empty and grows in random chunks — mid-line and
        // mid-character splits included. After each step, the long-lived cache must equal a from-scratch one.
        var rng = new Random(seed);
        var full = AllJsonl().ToDictionary(p => p, File.ReadAllBytes);
        var written = full.Keys.ToDictionary(p => p, _ => 0);
        foreach (var p in full.Keys) File.WriteAllBytes(p, []);

        var warm = new SessionStatsCache(null);
        for (int step = 0; written.Any(kv => kv.Value < full[kv.Key].Length); step++)
        {
            foreach (var (path, bytes) in full)
            {
                int at = written[path];
                if (at >= bytes.Length || rng.Next(3) == 0) continue;
                int n = Math.Min(bytes.Length - at, rng.Next(1, 1500));
                using (var fs = new FileStream(path, FileMode.Append)) fs.Write(bytes, at, n);
                written[path] = at + n;
            }
            Assert.Equal(Fresh(), Report(warm));
            Assert.True(step < 10_000);
        }
        Assert.Equal(Fresh(), Report(warm));
    }

    [Fact]
    public void ChangingTheIdleThreshold_IsHonouredFromTheCache()
    {
        // Active time depends on a user setting, so the cache keeps raw timestamps rather than baked spans.
        var warm = new SessionStatsCache(null);
        Report(warm);
        SessionStatsService.IdleThreshold = TimeSpan.FromMinutes(1);
        Assert.Equal(Fresh(), Report(warm));
    }

    [Fact]
    public void OutOfOrderRecords_AreSortedWhenAppended()
    {
        var file = Transcripts()[0];
        var warm = new SessionStatsCache(null);
        Report(warm);
        // A record earlier than everything already folded, then a later one.
        File.AppendAllText(file, Line("2020-01-01T00:00:00Z") + "\n");
        Assert.Equal(Fresh(), Report(warm));
        File.AppendAllText(file, Line("2020-01-01T00:02:00Z") + "\n" + Line("2020-01-01T00:01:00Z") + "\n");
        Assert.Equal(Fresh(), Report(warm));
    }

    [Fact]
    public void AnAppend_ReadsOnlyTheNewBytes()
    {
        var file = Transcripts()[0];
        var block = File.ReadAllText(file).TrimEnd('\r', '\n') + "\n";
        using (var w = new StreamWriter(file, append: false))
            while (w.BaseStream.Length < 2 * 1024 * 1024) w.Write(block);

        var cache = new SessionStatsCache(null);
        Report(cache);
        long before = cache.BytesRead;
        Assert.True(before >= 2 * 1024 * 1024);

        Report(cache);
        Assert.Equal(before, cache.BytesRead);   // nothing changed: every transcript costs a stat, no reads

        var line = Line("2026-01-02T12:00:00Z") + "\n";
        File.AppendAllText(file, line);
        Report(cache);
        long delta = cache.BytesRead - before;
        Assert.InRange(delta, Encoding.UTF8.GetByteCount(line), Encoding.UTF8.GetByteCount(line) + 256);   // + the head check
        Assert.Equal(Fresh(), Report(cache));
    }

    [Fact]
    public void Snapshot_RoundTrips_AndResumesWithoutRereading()
    {
        var first = new SessionStatsCache(CachePath);
        var expected = Report(first);   // a first build saves at once
        Assert.True(File.Exists(CachePath));

        var restarted = new SessionStatsCache(CachePath);
        Assert.Equal(expected, Report(restarted));
        Assert.Equal(0, restarted.BytesRead);   // resumed from the snapshot: stats only

        var file = Transcripts()[0];
        var line = Line("2026-01-02T12:00:00Z") + "\n";
        File.AppendAllText(file, line);
        Assert.Equal(Fresh(), Report(restarted));
        Assert.InRange(restarted.BytesRead, Encoding.UTF8.GetByteCount(line), Encoding.UTF8.GetByteCount(line) + 256);
    }

    [Fact]
    public void Snapshot_ResetsAFileThatWasReplacedWhileClosed()
    {
        var first = new SessionStatsCache(CachePath);
        Report(first);

        // Replaced, not appended to: different leading bytes and a different length.
        var file = Transcripts()[0];
        File.WriteAllText(file, Line("2025-06-01T10:00:00Z") + "\n" + Line("2025-06-01T10:01:00Z") + "\n");
        Assert.Equal(Fresh(), Report(new SessionStatsCache(CachePath)));
    }

    [Theory]
    [InlineData("garbage")]
    [InlineData("truncated")]
    [InlineData("empty")]
    public void DamagedSnapshot_IsDiscarded(string damage)
    {
        var seeded = new SessionStatsCache(CachePath);
        var expected = Report(seeded);
        var bytes = File.ReadAllBytes(CachePath);
        File.WriteAllBytes(CachePath, damage switch
        {
            "garbage" => Encoding.UTF8.GetBytes("not a stats cache at all"),
            "truncated" => bytes[..^1],
            _ => [],
        });

        var cache = new SessionStatsCache(CachePath);
        Assert.Equal(expected, Report(cache));
        Assert.True(cache.BytesRead > 0);   // it rebuilt from the transcripts rather than trusting the file
    }

    [Fact]
    public void DeletedTranscripts_ArePrunedByTheAllTimeReport()
    {
        var cache = new SessionStatsCache(null);
        Report(cache);
        int tracked = cache.TrackedFiles;

        var team = Transcripts().Single(p => Path.GetFileNameWithoutExtension(p) == "sessTeam");
        File.Delete(team);
        Directory.Delete(Path.Combine(Path.GetDirectoryName(team)!, "sessTeam"), recursive: true);

        Assert.Equal(Fresh(), Report(cache));
        Assert.Equal(tracked - 3, cache.TrackedFiles);   // the session and its two teammates
    }

    private static string Line(string timestamp) =>
        $$$"""{"type":"user","timestamp":"{{{timestamp}}}","cwd":"C:/fixtures/proj","message":{"role":"user","content":"late record"}}""";

    private static void CopyTree(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var f in Directory.GetFiles(from)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)));
        foreach (var d in Directory.GetDirectories(from)) CopyTree(d, Path.Combine(to, Path.GetFileName(d)));
    }
}
