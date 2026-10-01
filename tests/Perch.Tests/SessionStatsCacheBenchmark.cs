using System.Diagnostics;
using Perch.Data;
using Xunit;
using Xunit.Abstractions;

namespace Perch.Tests;

/// <summary>
/// CP21's measurement (docs/review-fixes-plan.md): the all-time stats report over a real history. Opt-in (set
/// <c>PERCH_BENCH=1</c>; <c>PERCH_BENCH_PROJECTS</c> picks the projects folder, default
/// <c>~/.claude/projects</c>). It only reads the transcripts, prints aggregate timings and sizes (no content), and
/// writes its snapshot to a temp file it deletes. "Cold" is the same per-line parse of every transcript that the old
/// report did on every run; "warm" and "restart" are what a report costs now.
/// </summary>
public sealed class SessionStatsCacheBenchmark(ITestOutputHelper output)
{
    [Fact]
    public void AllTimeReport_over_a_real_history()
    {
        if (Environment.GetEnvironmentVariable("PERCH_BENCH") != "1") return;
        var projects = Environment.GetEnvironmentVariable("PERCH_BENCH_PROJECTS") is { Length: > 0 } p
            ? p
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects");
        if (!Directory.Exists(projects)) { output.WriteLine($"no projects folder at {projects}"); return; }

        var transcripts = Directory.GetDirectories(projects).SelectMany(d => Directory.GetFiles(d, "*.jsonl")).ToList();
        long bytes = Directory.GetFiles(projects, "*.jsonl", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);
        var today = DateOnly.FromDateTime(DateTime.Now);
        var snapshot = Path.Combine(Path.GetTempPath(), "perch-bench-stats-" + Guid.NewGuid().ToString("N") + ".bin");
        try
        {
            var sw = Stopwatch.StartNew();
            var cold = new SessionStatsCache(snapshot);
            var report = SessionStatsService.ReportAllTime(today, cold, transcripts);   // builds, then saves at once
            var coldTime = sw.Elapsed;

            sw.Restart();
            SessionStatsService.ReportAllTime(today, cold, transcripts);
            var warmTime = sw.Elapsed;

            sw.Restart();
            var restarted = new SessionStatsCache(snapshot);
            SessionStatsService.ReportAllTime(today, restarted, transcripts);
            var restartTime = sw.Elapsed;

            output.WriteLine($"history: {transcripts.Count:N0} session transcripts, {bytes / 1048576.0:N1} MB of .jsonl " +
                             $"({report.Totals.SessionCount:N0} sessions, {report.ActiveDays:N0} active days)");
            output.WriteLine($"cold (full parse, what every report used to cost): {coldTime.TotalMilliseconds:N0} ms");
            output.WriteLine($"warm (nothing changed): {warmTime.TotalMilliseconds:N1} ms");
            output.WriteLine($"restart (load snapshot + report): {restartTime.TotalMilliseconds:N0} ms, read {restarted.BytesRead:N0} transcript bytes");
            output.WriteLine($"snapshot: {new FileInfo(snapshot).Length / 1048576.0:N1} MB");
        }
        finally
        {
            try { File.Delete(snapshot); } catch { }
        }
    }
}
