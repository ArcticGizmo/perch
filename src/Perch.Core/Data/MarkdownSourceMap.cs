using System.Text;
using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Extensions.TaskLists;
using Markdig.Extensions.Yaml;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace Perch.Data;

/// <summary>One parsed Markdown text: the source every <see cref="MarkdownSourceMap"/> built from it indexes into.
/// Its identity, not its text, is what groups selected blocks into one contiguous source slice — two messages
/// that happen to say the same thing are still two documents.</summary>
internal sealed class MarkdownCopyDoc(string text)
{
    public string Text { get; } = text;
}

/// <summary>The inline emphasis a rendered run carries (the renderer picks the brush/size; this is what varies).</summary>
internal readonly record struct MarkdownInlineStyle(bool Bold = false, bool Italic = false, bool Strike = false, bool Link = false);

/// <summary>Receives a block's inlines as <see cref="MarkdownSourceMap.Walk"/> lays them out, in order. Positions
/// are offsets into the block's rendered text, which is exactly the concatenation of what's reported here (a
/// checkbox counts as one position).</summary>
internal interface IMarkdownInlineSink
{
    void Text(string text, MarkdownInlineStyle style);
    void Code(int pos, string code, MarkdownInlineStyle style);
    void Checkbox(bool isChecked, MarkdownInlineStyle style);
    void LineBreak(string text, MarkdownInlineStyle style);
    /// <summary>A link's rendered range [start, end) and its raw target, reported after its content.</summary>
    void Link(int start, int end, string? url);
}

/// <summary>
/// Maps the text of one rendered Markdown block (a heading, a paragraph, a list item's paragraph, a table cell, a
/// code block) back to the Markdown source it came from, so a selection over the rendered view can be copied as
/// the Markdown that produced it — <c>**bold**</c>, <c>[links](url)</c>, list markers, fences and table pipes
/// included — and a whole-message selection copies exactly what the message's copy button does.
///
/// <para>Built from Markdig's precise source spans: each run of rendered text records the source range of its
/// content (a leaf), and each emphasis/link/code span records its full source range, delimiters included (a
/// container). A selection boundary that falls exactly on a span's edge takes the delimiters with it; one inside
/// a run maps character for character. A block's start maps to its source line start (taking a list marker,
/// heading hashes or quote bar along) and its end to its source end (a closing fence, a row's trailing pipe).</para>
/// </summary>
internal sealed class MarkdownSourceMap
{
    /// <summary>The pipeline every Markdown surface parses with — one place, so the rendered view and the source map
    /// agree on the AST. YAML frontmatter parses as its own block (rather than a rule + setext heading); precise
    /// source locations feed the map.</summary>
    public static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseYamlFrontMatter()
        .UsePipeTables().UseEmphasisExtras().UseTaskLists().UseAutoLinks().UsePreciseSourceLocation().Build();

    private readonly List<(int R, int Len, int S, int SLen)> _leaves = new();     // rendered run → content source
    private readonly List<(int R0, int R1, int S0, int S1)> _spans = new();       // rendered span → full source

    public MarkdownCopyDoc Doc { get; }
    public string Source => Doc.Text;

    /// <summary>The rendered text's length so far (the next run's position).</summary>
    public int Length { get; private set; }

    /// <summary>Source offset the block's rendered start maps to (-1 when unknown).</summary>
    public int Lead { get; private set; } = -1;

    /// <summary>Source offset (exclusive) the block's rendered end maps to (-1 when unknown).</summary>
    public int Tail { get; private set; } = -1;

    private MarkdownSourceMap(MarkdownCopyDoc doc) => Doc = doc;

    // ── Building ─────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Lays out the inlines of a prose block (a heading, a paragraph, or every leaf of a table cell),
    /// reporting each run to <paramref name="sink"/> (null to only measure) and recording the map.
    /// <paramref name="extendLead"/> takes the block's start back to its line start (a list marker, hashes, a
    /// quote bar) — off for a table cell after the first, whose line start is the row's.</summary>
    public static MarkdownSourceMap ForInlines(MarkdownCopyDoc doc, Block owner, IEnumerable<ContainerInline> inlines,
        MarkdownInlineStyle style, IMarkdownInlineSink? sink, bool extendLead = true)
    {
        var map = new MarkdownSourceMap(doc);
        map.SetBounds(owner.Span, extendLead);
        foreach (var inline in inlines)
            map.Walk(inline, style, sink);
        return map;
    }

    /// <summary>A verbatim block (fenced/indented code, raw HTML): its lines joined by '\n' (trailing blank lines
    /// dropped) as <paramref name="text"/>, each line mapped to its source. The block's start/end take the
    /// fences with them.</summary>
    public static MarkdownSourceMap ForLines(MarkdownCopyDoc doc, LeafBlock block, out string text)
    {
        var map = new MarkdownSourceMap(doc);
        map.SetBounds(block.Span, extendLead: true);
        var sb = new StringBuilder();
        var lines = block.Lines;
        for (int i = 0; i < lines.Count; i++)
        {
            if (i > 0) { sb.Append('\n'); map.Length++; }
            var slice = lines.Lines[i].Slice;
            string line = slice.ToString().Replace("\r", "");
            int s = slice.Start;
            // Trust the slice's offset only if the source really holds this text there.
            if (s < 0 || s + line.Length > doc.Text.Length || string.CompareOrdinal(doc.Text, s, line, 0, line.Length) != 0)
                s = -1;
            map.Leaf(line.Length, s, line.Length);
            sb.Append(line);
        }
        text = sb.ToString().TrimEnd('\n');
        map.Length = text.Length;   // trimmed trailing blank lines are never reached
        return map;
    }

    /// <summary>A block rendered as derived text with no per-character source (the frontmatter's YAML body):
    /// only its whole extent maps, which is all a copy of it needs.</summary>
    public static MarkdownSourceMap ForWhole(MarkdownCopyDoc doc, Block block, int renderedLength)
    {
        var map = new MarkdownSourceMap(doc) { Length = renderedLength };
        map.SetBounds(block.Span, extendLead: true);
        return map;
    }

    private void SetBounds(SourceSpan span, bool extendLead)
    {
        string src = Source;
        if (span.Start < 0 || span.End < span.Start || span.Start >= src.Length)
            return;
        int lead = span.Start;
        if (extendLead)
            while (lead > 0 && src[lead - 1] != '\n') lead--;
        int tail = Math.Min(span.End + 1, src.Length);
        // A row's trailing " |" (or trailing spaces) belongs to the block's line: take it when nothing else follows.
        int t = tail;
        while (t < src.Length && src[t] is ' ' or '\t' or '|') t++;
        if (t == src.Length || src[t] is '\r' or '\n') tail = t;
        else   // mid-line (a table cell before the next pipe): its span carries the padding space, which isn't content
            while (tail > lead && src[tail - 1] is ' ' or '\t') tail--;
        Lead = lead;
        Tail = tail;
    }

    private void Leaf(int len, int s, int slen)
    {
        if (len > 0) _leaves.Add((Length, len, s, Math.Max(0, slen)));
        Length += len;
    }

    private void Leaf(int len, SourceSpan span) => Leaf(len, Valid(span) ? span.Start : -1, span.Length);

    private void Span(int r0, SourceSpan span)
    {
        if (Valid(span) && Length > r0) _spans.Add((r0, Length, span.Start, Math.Min(span.End + 1, Source.Length)));
    }

    private bool Valid(SourceSpan span) => span.Start >= 0 && span.End >= span.Start && span.End < Source.Length;

    // The one inline layout pass: the renderer builds its runs from these callbacks and the map records where each
    // came from, so the rendered text a selection indexes and the map can never disagree.
    private void Walk(ContainerInline container, MarkdownInlineStyle style, IMarkdownInlineSink? sink)
    {
        foreach (var inline in container)
        {
            switch (inline)
            {
                case LiteralInline lit:
                    string text = lit.Content.ToString();
                    sink?.Text(text, style);
                    Leaf(text.Length, lit.Span);
                    break;
                case CodeInline code:
                {
                    int r0 = Length;
                    sink?.Code(r0, code.Content, style);
                    Leaf(code.Content.Length, Valid(code.Span) ? code.Span.Start + code.DelimiterCount : -1, code.Content.Length);
                    Span(r0, code.Span);
                    break;
                }
                case EmphasisInline em:
                {
                    var s = em.DelimiterChar == '~' ? style with { Strike = true }
                          : em.DelimiterCount >= 2 ? style with { Bold = true }
                          : style with { Italic = true };
                    int r0 = Length;
                    Walk(em, s, sink);
                    Span(r0, em.Span);
                    break;
                }
                case LinkInline link:
                {
                    int r0 = Length;
                    if (link.IsImage)
                    {
                        string img = $"🖼 {link.Url}";
                        sink?.Text(img, style with { Link = true });
                        Leaf(img.Length, -1, 0);
                    }
                    else
                        Walk(link, style with { Link = true, Strike = false }, sink);
                    sink?.Link(r0, Length, link.Url);
                    Span(r0, link.Span);
                    break;
                }
                case AutolinkInline auto:
                {
                    int r0 = Length;
                    sink?.Text(auto.Url, style with { Link = true });
                    int s = Valid(auto.Span) ? auto.Span.Start + (Source[auto.Span.Start] == '<' ? 1 : 0) : -1;
                    Leaf(auto.Url.Length, s, auto.Url.Length);
                    sink?.Link(r0, Length, auto.IsEmail ? "mailto:" + auto.Url : auto.Url);
                    Span(r0, auto.Span);
                    break;
                }
                case TaskList task:
                    sink?.Checkbox(task.Checked, style);
                    Leaf(1, task.Span);
                    break;
                case LineBreakInline br:
                    string brText = br.IsHard ? "\n" : " ";
                    sink?.LineBreak(brText, style);
                    Leaf(1, br.Span);
                    break;
                case ContainerInline cc:
                    Walk(cc, style, sink);
                    break;
            }
        }
    }

    // ── Mapping ──────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The source offset a selection starting at rendered offset <paramref name="o"/> begins at. On a
    /// span's opening edge it takes the outermost span's delimiters (<c>**</c>, <c>[</c>, a backtick).</summary>
    public int MapStart(int o)
    {
        if (o <= 0 && Lead >= 0) return Lead;
        int best = int.MaxValue;
        foreach (var sp in _spans)
            if (sp.R0 == o) best = Math.Min(best, sp.S0);
        foreach (var l in _leaves)
            if (l.S >= 0 && l.R == o) best = Math.Min(best, l.S);
        if (best != int.MaxValue) return best;
        foreach (var l in _leaves)
            if (l.S >= 0 && l.R < o && o < l.R + l.Len) return l.S + Math.Min(o - l.R, l.SLen);
        foreach (var l in _leaves)   // in a run with no source (an image label): start at the next one that has some
            if (l.S >= 0 && l.R >= o) return l.S;
        return Tail >= 0 ? Tail : Source.Length;
    }

    /// <summary>The source offset (exclusive) a selection ending at rendered offset <paramref name="o"/> ends at.
    /// On a span's closing edge it takes the outermost span's delimiters (<c>**</c>, <c>](url)</c>).</summary>
    public int MapEnd(int o)
    {
        if (o >= Length && Tail >= 0) return Tail;
        if (o <= 0) return Math.Max(0, Lead);
        int best = -1;
        foreach (var sp in _spans)
            if (sp.R1 == o) best = Math.Max(best, sp.S1);
        foreach (var l in _leaves)
            if (l.S >= 0 && l.R + l.Len == o) best = Math.Max(best, l.S + l.SLen);
        if (best >= 0) return best;
        foreach (var l in _leaves)
            if (l.S >= 0 && l.R < o && o < l.R + l.Len) return l.S + Math.Min(o - l.R, l.SLen);
        for (int i = _leaves.Count - 1; i >= 0; i--)
            if (_leaves[i].S >= 0 && _leaves[i].R + _leaves[i].Len <= o) return _leaves[i].S + _leaves[i].SLen;
        return Math.Max(0, Lead);
    }
}

/// <summary>Turns a selection over rendered blocks into copied text.</summary>
internal static class MarkdownCopy
{
    /// <summary>One selected block: its source map (null for text that isn't rendered Markdown — tool output, a
    /// user bubble), its rendered text, and the selected rendered range [Start, End).</summary>
    public readonly record struct Piece(MarkdownSourceMap? Map, string Rendered, int Start, int End);

    /// <summary>The copied text for <paramref name="pieces"/> (in document order). As Markdown, consecutive
    /// pieces from the same document become the one contiguous source slice from the first's start to the last's
    /// end — so everything between them (blank lines, list markers, fences, table rules) comes along verbatim, and
    /// a whole message copies exactly as written. As plain text, each piece is its rendered characters. Separate
    /// documents and non-Markdown pieces join with a blank line.</summary>
    public static string Compose(IReadOnlyList<Piece> pieces, bool markdown = true)
    {
        var parts = new List<string>();
        for (int i = 0; i < pieces.Count; i++)
        {
            var p = pieces[i];
            if (!markdown || p.Map is not { } map)
            {
                int s0 = Math.Clamp(p.Start, 0, p.Rendered.Length), e0 = Math.Clamp(p.End, s0, p.Rendered.Length);
                parts.Add(p.Rendered[s0..e0].Replace("￼", ""));
                continue;
            }
            int j = i;
            while (j + 1 < pieces.Count && pieces[j + 1].Map is { } next && ReferenceEquals(next.Doc, map.Doc))
                j++;
            int s = map.MapStart(p.Start), e = pieces[j].Map!.MapEnd(pieces[j].End);
            if (e > s) parts.Add(map.Source[s..e]);
            i = j;
        }
        return string.Join("\n\n", parts.Where(t => t.Length > 0));
    }

    /// <summary>The frontmatter's YAML body as the viewer shows it: the block's lines without the <c>---</c> fences
    /// or trailing blank lines.</summary>
    public static string FrontmatterText(YamlFrontMatterBlock yaml)
    {
        var lines = yaml.Lines.ToString().Replace("\r", "").Split('\n').ToList();
        while (lines.Count > 0 && lines[0].Trim() == "---") lines.RemoveAt(0);
        while (lines.Count > 0 && (lines[^1].Trim() == "---" || lines[^1].Length == 0)) lines.RemoveAt(lines.Count - 1);
        return string.Join("\n", lines);
    }

    /// <summary>The selectable blocks a Markdown view renders for <paramref name="md"/>, in document order, each with
    /// its rendered text and source map — the same blocks, built by the same calls, that <c>MarkdownView</c> turns
    /// into text controls. What a cross-block selection indexes; the tests drive selections over it.</summary>
    public static IReadOnlyList<(MarkdownSourceMap Map, string Text)> Blocks(string md)
    {
        var doc = new MarkdownCopyDoc(md);
        var list = new List<(MarkdownSourceMap, string)>();
        foreach (var block in Markdown.Parse(md, MarkdownSourceMap.Pipeline))
            Visit(block);
        return list;

        void Inlines(Block owner, IEnumerable<ContainerInline> inlines, bool extendLead = true)
        {
            var sink = new TextSink();
            list.Add((MarkdownSourceMap.ForInlines(doc, owner, inlines, default, sink, extendLead), sink.Sb.ToString()));
        }

        void Visit(Block block)
        {
            switch (block)
            {
                case YamlFrontMatterBlock y:
                    string yaml = FrontmatterText(y);
                    list.Add((MarkdownSourceMap.ForWhole(doc, y, yaml.Length), yaml));
                    break;
                case HeadingBlock h:
                    Inlines(h, h.Inline is { } hi ? [hi] : []);
                    break;
                case ParagraphBlock p:
                    Inlines(p, p.Inline is { } pi ? [pi] : []);
                    break;
                case Table table:
                    foreach (var row in table.OfType<TableRow>())
                    {
                        int ci = 0;
                        foreach (var cellObj in row)
                        {
                            if (cellObj is TableCell cell)
                                Inlines(cell, cell.OfType<LeafBlock>().Select(b => b.Inline).OfType<ContainerInline>(), extendLead: ci == 0);
                            ci++;
                        }
                    }
                    break;
                case CodeBlock or HtmlBlock:
                    var map = MarkdownSourceMap.ForLines(doc, (LeafBlock)block, out var text);
                    list.Add((map, text));
                    break;
                case ContainerBlock cb:   // lists, list items, quotes
                    foreach (var child in cb) Visit(child);
                    break;
            }
        }
    }

    // Collects the rendered text only (a checkbox as the object-replacement char, as the text layout counts it).
    private sealed class TextSink : IMarkdownInlineSink
    {
        public readonly StringBuilder Sb = new();
        public void Text(string text, MarkdownInlineStyle style) => Sb.Append(text);
        public void Code(int pos, string code, MarkdownInlineStyle style) => Sb.Append(code);
        public void Checkbox(bool isChecked, MarkdownInlineStyle style) => Sb.Append('￼');
        public void LineBreak(string text, MarkdownInlineStyle style) => Sb.Append(text);
        public void Link(int start, int end, string? url) { }
    }
}
