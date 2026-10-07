using System.Text;
using System.Text.Json;
using Perch.Data;
using Xunit;

namespace Perch.Tests;

/// <summary>
/// CP20 (docs/review-fixes-plan.md): the whole-file transcript readers now fold incrementally. These grow a real
/// transcript in arbitrary chunks and assert that, at every step, the readers that watched it grow report exactly
/// what fresh readers report for the same bytes — for every folded value (tasks, artifacts, title, async agents,
/// context, Markdown sets, legacy sub-agents) — then do the same across truncation and replacement.
/// </summary>
public sealed class TranscriptFoldEquivalenceTests : IDisposable
{
    private static readonly string ProjDir = Path.Combine(TestEnvironment.FixtureConfigDir, "projects", "C--fixtures-proj");

    private readonly string _dir = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), "perch-foldeq-" + Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string P => Path.Combine(_dir, "grow.jsonl");

    // An async agent launched and later notified back, so the async folder has something to track.
    private const string AsyncLaunch =
        """{"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"toolu_async1","content":"Async agent launched successfully"}]},"toolUseResult":{"isAsync":true}}""";
    private const string AsyncNotified =
        """{"type":"user","message":{"role":"user","content":"<task-notification><tool-use-id>toolu_async1</tool-use-id></task-notification>"}}""";

    // Several fixtures back to back, exercising every folder at once (reducers don't care whose lines they are).
    private static byte[] Combined(params string[] extraLines)
    {
        // sessB holds a still-running legacy sub-agent; the task fixture goes last so no later prompt stales it.
        var parts = new[] { "sessA", "sessMarkdown", "sessCtxSwitch", "sessSubReturned", "sessPendingTool", "sessB", "sessTasksBatches" }
            .Select(n => File.ReadAllText(Path.Combine(ProjDir, n + ".jsonl")).TrimEnd('\r', '\n') + "\n");
        return Encoding.UTF8.GetBytes(string.Concat(parts) + string.Concat(extraLines.Select(l => l + "\n")));
    }

    private sealed record Readers(TranscriptReader T, MarkdownFilesReader M, SubAgentReader S)
    {
        public static Readers Fresh() => new(new(), new(), new());
    }

    // Everything the folded readers report for `path`, as one comparable string.
    private static string Snapshot(Readers r, string path)
    {
        var md = r.M.FileSetsAt(path);
        var (fill, window) = r.T.ContextFillAt(path, TestEnvironment.FixtureCwd);
        return JsonSerializer.Serialize(new
        {
            Tasks = r.T.TasksAt(path),
            Artifacts = r.T.ArtifactsAt(path),
            Title = r.T.TitleAt(path),
            Async = r.T.HasOutstandingAsyncAgentAt(path),
            Background = r.T.RunningBackgroundTasksAt(path),
            Fill = fill,
            Window = window,
            Produced = md.Produced,
            Referenced = md.Referenced,
            Legacy = r.S.LegacyAt(path),
        });
    }

    private void GrowAndCompare(byte[] bytes, Readers watching, int seed)
    {
        var rng = new Random(seed);
        int at = 0;
        while (at < bytes.Length)
        {
            int n = Math.Min(bytes.Length - at, rng.Next(1, 700));
            using (var fs = new FileStream(P, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
                fs.Write(bytes, at, n);
            at += n;
            Assert.Equal(Snapshot(Readers.Fresh(), P), Snapshot(watching, P));
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(42)]
    public void Growing_in_chunks_matches_a_fresh_read_at_every_step(int seed)
    {
        File.WriteAllBytes(P, []);
        var watching = Readers.Fresh();
        GrowAndCompare(Combined(AsyncLaunch, AsyncNotified), watching, seed);

        // Sanity: the combined transcript really exercises the folders being compared. (Its tasks end up stale —
        // other fixtures' prompts post-date the batch — so the task folder gets its own growth test below.)
        var t = watching.T;
        Assert.NotEmpty(watching.S.LegacyAt(P));
        Assert.NotEmpty(t.ArtifactsAt(P));
        Assert.NotNull(t.TitleAt(P));
        Assert.NotNull(t.ContextFillAt(P, TestEnvironment.FixtureCwd).Fill);
        Assert.NotEmpty(watching.M.FileSetsAt(P).Produced);
        Assert.False(t.HasOutstandingAsyncAgentAt(P));   // launched, then notified
    }

    [Theory]
    [InlineData("sessTasksBatches")]
    [InlineData("sessTasksRealSchema")]
    public void A_growing_task_checklist_matches_a_fresh_read_at_every_step(string fixture)
    {
        File.WriteAllBytes(P, []);
        var watching = Readers.Fresh();
        GrowAndCompare(File.ReadAllBytes(Path.Combine(ProjDir, fixture + ".jsonl")), watching, seed: 3);
        Assert.NotEmpty(watching.T.TasksAt(P));
    }

    [Fact]
    public void Async_agent_is_outstanding_between_launch_and_notification()
    {
        var r = Readers.Fresh();
        File.WriteAllText(P, AsyncLaunch + "\n");
        Assert.True(r.T.HasOutstandingAsyncAgentAt(P));
        File.AppendAllText(P, AsyncNotified + "\n");
        Assert.False(r.T.HasOutstandingAsyncAgentAt(P));
    }

    [Fact]
    public void Truncation_and_replacement_match_a_fresh_read()
    {
        var watching = Readers.Fresh();
        File.WriteAllBytes(P, Combined());
        Snapshot(watching, P);

        // Truncated to a different, shorter transcript.
        File.Copy(Path.Combine(ProjDir, "sessTasks.jsonl"), P, overwrite: true);
        Assert.Equal(Snapshot(Readers.Fresh(), P), Snapshot(watching, P));

        // Replaced by a different, longer one (a rotation the length check alone can't see).
        File.WriteAllBytes(P, Combined(AsyncLaunch));
        Assert.Equal(Snapshot(Readers.Fresh(), P), Snapshot(watching, P));
        Assert.True(watching.T.HasOutstandingAsyncAgentAt(P));
    }

    [Fact]
    public void An_append_to_a_large_transcript_reads_only_the_append()
    {
        // ~5 MB of real records: the old readers re-read all of it on every append, five times over.
        var one = Combined();
        using (var fs = new FileStream(P, FileMode.Create))
            for (int i = 0; i < 5 * 1024 * 1024 / one.Length + 1; i++) fs.Write(one);
        var r = Readers.Fresh();
        Snapshot(r, P);
        long before = r.T.FoldBytesRead;

        File.AppendAllText(P, AsyncLaunch + "\n");
        Assert.True(r.T.HasOutstandingAsyncAgentAt(P));
        Assert.Equal(Snapshot(Readers.Fresh(), P), Snapshot(r, P));

        long delta = r.T.FoldBytesRead - before;
        Assert.InRange(delta, 1, Encoding.UTF8.GetByteCount(AsyncLaunch) + 1 + 256);   // the append + the head check
    }
}
