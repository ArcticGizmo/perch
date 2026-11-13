using System.Text;
using Perch.Data;
using Xunit;

namespace Perch.Tests;

/// <summary>
/// The session monitor folds each transcript once for every reader that needs it (perf review PF15): the shared fold
/// must give each reader exactly what its own fold would, while reading the file's bytes once rather than once per
/// reader — on the first read and again as the transcript grows.
/// </summary>
public sealed class SharedTranscriptFoldTests : IDisposable
{
    private readonly string _dir = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), "perch-sharedfold-" + Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    // Records that touch every reader: a title, a task, an edited Markdown file, an Agent launch with no result yet
    // (the legacy sub-agent read), a /model-free assistant turn with usage, and a permission-mode record.
    private static readonly string[] Turn1 =
    [
        """{"type":"custom-title","customTitle":"Shared fold"}""",
        """{"type":"permission-mode","permissionMode":"acceptEdits"}""",
        """{"type":"user","message":{"role":"user","content":"write the plan"}}""",
        """{"type":"assistant","message":{"role":"assistant","model":"claude-opus-5-5","usage":{"input_tokens":10,"cache_read_input_tokens":2000},"content":[{"type":"tool_use","id":"w1","name":"Write","input":{"file_path":"C:\\fixtures\\proj\\docs\\plan.md","content":"# Plan"}}]}}""",
        """{"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"w1","content":"File created successfully.","is_error":false}]}}""",
        """{"type":"assistant","message":{"role":"assistant","content":[{"type":"tool_use","id":"t1","name":"TaskCreate","input":{"subject":"Ship it","activeForm":"Shipping it"}}]}}""",
    ];

    private static readonly string[] Turn2 =
    [
        """{"type":"assistant","message":{"role":"assistant","content":[{"type":"tool_use","id":"a1","name":"Agent","input":{"description":"Explore the repo","subagent_type":"Explore","prompt":"look"}}]}}""",
        """{"type":"assistant","message":{"role":"assistant","content":[{"type":"tool_use","id":"e1","name":"Edit","input":{"file_path":"C:\\fixtures\\proj\\README.md","old_string":"a","new_string":"b"}}]}}""",
    ];

    private static void Append(string path, IEnumerable<string> lines) =>
        File.AppendAllText(path, string.Join("\n", lines) + "\n", new UTF8Encoding(false));

    [Fact]
    public void Shared_fold_matches_each_readers_own_fold_and_reads_the_file_once()
    {
        var path = Path.Combine(_dir, "s.jsonl");
        Append(path, Turn1);

        var shared = SessionMonitor.NewSharedTranscriptFold();
        var (t, m, w, s) = (new TranscriptReader(shared), new MarkdownFilesReader(shared), new SessionWorktree(shared),
            new SubAgentReader(parentFold: shared));
        var (t0, m0, w0, s0) = (new TranscriptReader(), new MarkdownFilesReader(), new SessionWorktree(), new SubAgentReader());

        for (int round = 0; round < 2; round++)
        {
            if (round == 1) Append(path, Turn2);
            long before = shared.BytesRead;

            Assert.Equal(t0.TitleAt(path), t.TitleAt(path));
            Assert.Equal(t0.PermissionModeAt(path), t.PermissionModeAt(path));
            Assert.Equal(t0.TasksAt(path), t.TasksAt(path));
            Assert.Equal(t0.ArtifactsAt(path), t.ArtifactsAt(path));
            Assert.Equal(t0.HasOutstandingAsyncAgentAt(path), t.HasOutstandingAsyncAgentAt(path));
            Assert.Equal(t0.ContextFillAt(path, @"C:\fixtures\proj"), t.ContextFillAt(path, @"C:\fixtures\proj"));
            Assert.Equal(t0.RunningBackgroundTasksAt(path), t.RunningBackgroundTasksAt(path));
            Assert.Equal(m0.FileSetsAt(path).Produced, m.FileSetsAt(path).Produced);
            Assert.Equal(m0.FileSetsAt(path).Referenced, m.FileSetsAt(path).Referenced);
            Assert.Equal(w0.WritesAt(path), w.WritesAt(path));
            Assert.Equal(s0.LegacyAt(path).Select(a => a.AgentId), s.LegacyAt(path).Select(a => a.AgentId));

            // Every reader asked, but the new bytes were read once (round 0 reads the whole file, round 1 only Turn2).
            long newBytes = round == 0 ? new FileInfo(path).Length : Encoding.UTF8.GetByteCount(string.Join("\n", Turn2) + "\n");
            Assert.InRange(shared.BytesRead - before, newBytes, newBytes + 256);   // + the replaced-file head check
        }

        Assert.Equal("Shared fold", t.TitleAt(path));
        Assert.NotEmpty(m.FileSetsAt(path).Produced);
        Assert.Single(s.LegacyAt(path));
    }

    // The project-folder encoding is Claude Code's rule; the loop that replaced the regex must give the same name for
    // any path, including non-ASCII letters and characters outside the BMP (each UTF-16 unit becomes a dash).
    [Theory]
    [InlineData(@"C:\Users\JonHowell\Documents\git\personal\perch")]
    [InlineData("/home/dev/my.project_v2")]
    [InlineData(@"D:\Projekte\Grüße\naïve café")]
    [InlineData("C:\\src\\emoji-😀-dir")]
    [InlineData("")]
    public void Project_dir_encoding_matches_the_rule(string cwd) =>
        Assert.Equal(System.Text.RegularExpressions.Regex.Replace(cwd, "[^A-Za-z0-9]", "-"), TranscriptLocator.EncodeProjectDir(cwd));
}
