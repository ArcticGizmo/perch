using System.Diagnostics;
using System.Text;
using Perch.Data;
using Xunit;
using Xunit.Abstractions;

namespace Perch.Tests;

/// <summary>
/// CP20's before/after measurement (docs/review-fixes-plan.md): what one append to a 50 MB transcript costs the
/// whole-file readers. "Before" is a fresh reader — exactly what the old length+mtime cache did on every append —
/// and "after" is the incremental fold. Opt-in (set <c>PERCH_BENCH=1</c>); otherwise a no-op, so the suite stays fast.
/// </summary>
public sealed class TranscriptFoldBenchmark(ITestOutputHelper output)
{
    [Fact]
    public void Append_to_a_50MB_transcript()
    {
        if (Environment.GetEnvironmentVariable("PERCH_BENCH") != "1") return;

        var proj = Path.Combine(TestEnvironment.FixtureConfigDir, "projects", "C--fixtures-proj");
        var block = Encoding.UTF8.GetBytes(string.Concat(
            new[] { "sessA", "sessMarkdown", "sessCtxSwitch", "sessTasksBatches", "sessB" }
                .Select(n => File.ReadAllText(Path.Combine(proj, n + ".jsonl")).TrimEnd('\r', '\n') + "\n")));
        var path = Path.Combine(Path.GetTempPath(), "perch-bench-" + Guid.NewGuid().ToString("N") + ".jsonl");
        try
        {
            using (var fs = new FileStream(path, FileMode.Create))
                while (fs.Length < 50L * 1024 * 1024) fs.Write(block);
            const string append = """{"type":"assistant","message":{"role":"assistant","model":"claude-opus-4-8","content":[{"type":"text","text":"ok"}],"usage":{"input_tokens":5,"output_tokens":7}}}""";

            // Every whole-file value the scan reads per session.
            static void ReadAll(TranscriptReader t, MarkdownFilesReader m, SubAgentReader s, string p)
            {
                t.TasksAt(p); t.ArtifactsAt(p); t.TitleAt(p); t.HasOutstandingAsyncAgentAt(p);
                t.ContextFillAt(p, TestEnvironment.FixtureCwd); m.FileSetsAt(p); s.LegacyAt(p);
            }

            var t = new TranscriptReader(); var m = new MarkdownFilesReader(); var s = new SubAgentReader();
            var sw = Stopwatch.StartNew();
            ReadAll(t, m, s, path);
            var first = sw.Elapsed;

            var before = TimeSpan.Zero; var after = TimeSpan.Zero;
            const int rounds = 5;
            for (int i = 0; i < rounds; i++)
            {
                File.AppendAllText(path, append + "\n");
                sw.Restart(); ReadAll(new(), new(), new(), path); before += sw.Elapsed;   // old behaviour: full re-read
                sw.Restart(); ReadAll(t, m, s, path); after += sw.Elapsed;                // incremental
            }

            output.WriteLine($"50 MB transcript, all folded readers: first read {first.TotalMilliseconds:N0} ms");
            output.WriteLine($"per append — before (full re-read): {before.TotalMilliseconds / rounds:N1} ms, after (fold): {after.TotalMilliseconds / rounds:N2} ms");
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }
}
