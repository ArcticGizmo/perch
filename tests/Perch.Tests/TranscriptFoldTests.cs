using System.Text;
using Perch.Data;
using Xunit;

namespace Perch.Tests;

/// <summary>
/// <see cref="TranscriptFold"/> is the CP20 primitive (docs/review-fixes-plan.md): an append-only file is read once,
/// then only its new bytes, and the accumulated state always equals folding the file from scratch — across splits
/// mid-line, a record whose newline hasn't landed, truncation, and replacement.
/// </summary>
public sealed class TranscriptFoldTests : IDisposable
{
    private readonly string _dir = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), "perch-fold-" + Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string P(string name = "t.jsonl") => Path.Combine(_dir, name);

    // A folder that just records every non-empty line it's fed, so a test can compare what was folded.
    private static readonly LineFolder<List<string>> Lines = new(() => new(), (s, l) => { if (l.Length > 0) s.Add(l); });

    private static List<string> Get(TranscriptFold fold, string path) => fold.Get(path, Lines, s => s.ToList(), ["<fallback>"]);

    private static void Append(string path, string text)
    {
        using var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        var b = Encoding.UTF8.GetBytes(text);
        fs.Write(b, 0, b.Length);
    }

    private static string Rec(int i) => $$"""{"n":{{i}},"text":"line {{i}} — ünïcödé"}""";

    [Fact]
    public void Growing_in_arbitrary_chunks_folds_exactly_the_complete_lines_so_far()
    {
        var all = string.Concat(Enumerable.Range(0, 40).Select(i => Rec(i) + "\n"));
        var bytes = Encoding.UTF8.GetBytes(all);
        var fold = new TranscriptFold(Lines);
        File.WriteAllBytes(P(), []);

        var rng = new Random(20);
        int at = 0;
        while (at < bytes.Length)
        {
            int n = Math.Min(bytes.Length - at, rng.Next(1, 90));   // splits land mid-line and mid-UTF-8-char
            using (var fs = new FileStream(P(), FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
                fs.Write(bytes, at, n);
            at += n;

            var complete = Encoding.UTF8.GetString(bytes, 0, at).Split('\n');
            var expected = complete.Take(complete.Length - 1).ToList();   // everything before the last newline
            // …plus a trailing fragment that is already a whole record (its newline just hasn't landed).
            if (complete[^1].StartsWith('{') && complete[^1].EndsWith('}')) expected.Add(complete[^1]);
            Assert.Equal(expected, Get(fold, P()));
        }
        Assert.Equal(40, Get(fold, P()).Count);
    }

    [Fact]
    public void An_unchanged_file_costs_no_read()
    {
        File.WriteAllText(P(), Rec(1) + "\n" + Rec(2) + "\n");
        var fold = new TranscriptFold(Lines);
        Get(fold, P());
        long before = fold.BytesRead;

        Get(fold, P());
        Assert.Equal(before, fold.BytesRead);
    }

    [Fact]
    public void An_append_reads_only_the_new_bytes()
    {
        var big = string.Concat(Enumerable.Range(0, 20_000).Select(i => Rec(i) + "\n"));   // ~1 MB
        File.WriteAllText(P(), big);
        var fold = new TranscriptFold(Lines);
        Get(fold, P());
        long before = fold.BytesRead;

        var extra = Rec(99_999) + "\n";
        Append(P(), extra);
        Assert.Equal(20_001, Get(fold, P()).Count);

        long delta = fold.BytesRead - before;
        Assert.InRange(delta, Encoding.UTF8.GetByteCount(extra), Encoding.UTF8.GetByteCount(extra) + 256);   // + the head check
    }

    [Fact]
    public void A_record_without_its_newline_is_taken_once_and_not_again()
    {
        File.WriteAllText(P(), """{"a":1}""");
        var fold = new TranscriptFold(Lines);
        Assert.Equal(["""{"a":1}"""], Get(fold, P()));

        Append(P(), "\n" + """{"b":2}""" + "\n");
        Assert.Equal(["""{"a":1}""", """{"b":2}"""], Get(fold, P()));
    }

    [Fact]
    public void A_partial_record_waits_until_it_is_complete()
    {
        File.WriteAllText(P(), """{"a":""");
        var fold = new TranscriptFold(Lines);
        Assert.Empty(Get(fold, P()));

        Append(P(), "1}\n");
        Assert.Equal(["""{"a":1}"""], Get(fold, P()));
    }

    [Fact]
    public void Truncation_resets_and_refolds_from_the_start()
    {
        File.WriteAllText(P(), Rec(1) + "\n" + Rec(2) + "\n" + Rec(3) + "\n");
        var fold = new TranscriptFold(Lines);
        Assert.Equal(3, Get(fold, P()).Count);

        File.WriteAllText(P(), Rec(7) + "\n");
        Assert.Equal([Rec(7)], Get(fold, P()));
    }

    [Fact]
    public void A_replaced_file_that_is_no_shorter_resets_too()
    {
        File.WriteAllText(P(), Rec(1) + "\n" + Rec(2) + "\n");
        var fold = new TranscriptFold(Lines);
        Get(fold, P());

        // Longer, but a different file: the head no longer matches what was consumed.
        File.WriteAllText(P(), Rec(5) + "\n" + Rec(6) + "\n" + Rec(8) + "\n");
        Assert.Equal([Rec(5), Rec(6), Rec(8)], Get(fold, P()));
    }

    [Fact]
    public void A_leading_bom_is_stripped_like_StreamReader_does()
    {
        File.WriteAllText(P(), Rec(1) + "\n", new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        Assert.Equal([Rec(1)], Get(new TranscriptFold(Lines), P()));
    }

    [Fact]
    public void A_missing_file_falls_back_and_a_later_one_is_read()
    {
        var fold = new TranscriptFold(Lines);
        Assert.Equal(["<fallback>"], Get(fold, P("later.jsonl")));

        File.WriteAllText(P("later.jsonl"), Rec(1) + "\n");
        Assert.Equal([Rec(1)], Get(fold, P("later.jsonl")));
    }

    [Fact]
    public void A_throwing_folder_does_not_starve_the_others()
    {
        var boom = new LineFolder<List<string>>(() => new(), (_, l) => { if (l.Contains("\"n\":2")) throw new InvalidOperationException(); });
        var fold = new TranscriptFold(boom, Lines);
        File.WriteAllText(P(), Rec(1) + "\n" + Rec(2) + "\n" + Rec(3) + "\n");
        Assert.Equal(3, Get(fold, P()).Count);
    }

    [Fact]
    public void FoldAll_is_the_same_reducer_from_scratch()
    {
        var lines = Enumerable.Range(0, 5).Select(Rec).ToList();
        Assert.Equal(lines, Lines.FoldAll(lines));
    }
}
