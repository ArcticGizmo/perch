using Avalonia.Controls.Documents;
using Avalonia.Media;
using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace Perch.Avalonia.Rendering;

/// <summary>
/// Renders a Markdown message body into Avalonia <see cref="Inline"/>s for a <c>SelectableTextBlock</c> —
/// the Avalonia counterpart of <c>HistoryViewerForm</c>'s Markdig → RTF walk. Handles the block shapes
/// Claude's prose uses (headings, paragraphs, lists, quotes, fenced/indented code, tables, rules) and the
/// inline styles (bold, italic, strikethrough, inline code, links). Code and links are colour-coded
/// (Avalonia inline runs have no background/click, so a code box / clickable link aren't reproduced —
/// the text stays selectable and copyable, which is the point).
/// </summary>
internal sealed class MarkdownRender
{
    private static readonly MarkdownPipeline Pipeline =
        new MarkdownPipelineBuilder().UsePipeTables().UseEmphasisExtras().Build();

    private static readonly FontFamily Mono = new("Cascadia Code, Consolas, Menlo, monospace");

    private readonly InlineCollection _out;
    private readonly IBrush _fg, _muted, _code, _link, _title;

    private MarkdownRender(InlineCollection sink, IBrush fg, IBrush muted, IBrush code, IBrush link, IBrush title)
    {
        _out = sink; _fg = fg; _muted = muted; _code = code; _link = link; _title = title;
    }

    /// <summary>Parses <paramref name="md"/> and appends its rendered inlines to <paramref name="sink"/>.
    /// On a parse failure the raw text is appended verbatim (best-effort, never throws).</summary>
    public static void Append(InlineCollection sink, string md,
        IBrush fg, IBrush muted, IBrush code, IBrush link, IBrush title)
    {
        var r = new MarkdownRender(sink, fg, muted, code, link, title);
        if (string.IsNullOrWhiteSpace(md)) return;
        MarkdownDocument doc;
        try { doc = Markdown.Parse(md, Pipeline); }
        catch { sink.Add(new Run(md.TrimEnd()) { Foreground = fg }); return; }
        foreach (var block in doc) r.Block(block, 0);
    }

    private void Block(Block block, int indent)
    {
        switch (block)
        {
            case HeadingBlock h:
                double size = h.Level switch { 1 => 18.5, 2 => 16.5, 3 => 15, _ => 14 };
                Break();
                if (h.Inline != null) Inlines(h.Inline, new Style(size, Bold: true, Brush: _title));
                Break();
                break;

            case ParagraphBlock p:
                Indent(indent);
                if (p.Inline != null) Inlines(p.Inline, new Style(13));
                Break();
                break;

            case ListBlock list:
                List(list, indent);
                break;

            case QuoteBlock q:
                foreach (var child in q) Block(child, indent + 2);
                break;

            case Table table:
                Table(table, indent);
                break;

            case CodeBlock code:  // FencedCodeBlock derives from this
                Code(code);
                break;

            case ThematicBreakBlock:
                Add(new Run(new string('─', 40) + "\n") { Foreground = _muted });
                break;

            case ContainerBlock cb:
                foreach (var child in cb) Block(child, indent);
                break;
        }
    }

    private void List(ListBlock list, int indent)
    {
        int number = list.OrderedStart != null && int.TryParse(list.OrderedStart, out var s) ? s : 1;
        foreach (var itemObj in list)
        {
            if (itemObj is not ListItemBlock item) continue;
            string bullet = list.IsOrdered ? $"{number}. " : "•  ";
            bool first = true;
            foreach (var child in item)
            {
                if (child is ParagraphBlock p)
                {
                    Indent(indent + 2);
                    Add(new Run(first ? bullet : "   ") { Foreground = _muted });
                    if (p.Inline != null) Inlines(p.Inline, new Style(13));
                    Break();
                }
                else if (child is ListBlock nested) List(nested, indent + 3);
                else Block(child, indent + 3);
                first = false;
            }
            number++;
        }
    }

    private void Code(CodeBlock code)
    {
        var lines = code.Lines.ToString().Replace("\r", "").Split('\n').ToList();
        while (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        if (lines.Count == 0) return;
        Break();
        foreach (var l in lines)
            Add(new Run("  " + l + "\n") { FontFamily = Mono, Foreground = _fg });
        Break();
    }

    private void Table(Table table, int indent)
    {
        var rows = new List<List<string>>();
        foreach (var rowObj in table)
        {
            if (rowObj is not TableRow row) continue;
            var cells = new List<string>();
            foreach (var cellObj in row) if (cellObj is TableCell cell) cells.Add(CellText(cell));
            rows.Add(cells);
        }
        if (rows.Count == 0) return;

        int cols = rows.Max(r => r.Count);
        var widths = new int[cols];
        foreach (var r in rows) for (int i = 0; i < r.Count; i++) widths[i] = Math.Max(widths[i], r[i].Length);

        Break();
        for (int ri = 0; ri < rows.Count; ri++)
        {
            var r = rows[ri];
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < cols; i++)
            {
                sb.Append((i < r.Count ? r[i] : "").PadRight(widths[i]));
                if (i < cols - 1) sb.Append("  |  ");
            }
            Add(new Run(sb + "\n") { FontFamily = Mono, Foreground = _fg });
            if (ri == 0)
            {
                var sep = new System.Text.StringBuilder();
                for (int i = 0; i < cols; i++) { sep.Append(new string('─', widths[i])); if (i < cols - 1) sep.Append("──┼──"); }
                Add(new Run(sep + "\n") { FontFamily = Mono, Foreground = _muted });
            }
        }
        Break();
    }

    private readonly record struct Style(double Size = 13, bool Bold = false, bool Italic = false,
        bool Strike = false, IBrush? Brush = null);

    private void Inlines(ContainerInline container, Style style)
    {
        foreach (var inline in container)
        {
            switch (inline)
            {
                case LiteralInline lit:
                    Add(Styled(lit.Content.ToString(), style));
                    break;
                case CodeInline code:
                    Add(new Run(code.Content) { FontFamily = Mono, Foreground = _code, FontSize = style.Size });
                    break;
                case EmphasisInline em:
                    var s = em.DelimiterChar == '~' ? style with { Strike = true }
                          : em.DelimiterCount >= 2 ? style with { Bold = true }
                          : style with { Italic = true };
                    Inlines(em, s);
                    break;
                case LinkInline link:
                    if (link.IsImage) Add(Styled($"🖼 {link.Url}", style with { Brush = _link }));
                    else Inlines(link, style with { Brush = _link, Strike = false });
                    break;
                case AutolinkInline auto:
                    Add(Styled(auto.Url, style with { Brush = _link }));
                    break;
                case LineBreakInline br:
                    Add(new Run(br.IsHard ? "\n" : " ") { Foreground = _fg });
                    break;
                case ContainerInline cc:
                    Inlines(cc, style);
                    break;
            }
        }
    }

    private Run Styled(string text, Style style)
    {
        var run = new Run(text)
        {
            Foreground = style.Brush ?? _fg,
            FontSize = style.Size,
            FontWeight = style.Bold ? FontWeight.Bold : FontWeight.Normal,
            FontStyle = style.Italic ? FontStyle.Italic : FontStyle.Normal,
        };
        var deco = new TextDecorationCollection();
        if (style.Strike) deco.Add(new TextDecoration { Location = TextDecorationLocation.Strikethrough });
        if (ReferenceEquals(style.Brush, _link)) deco.Add(new TextDecoration { Location = TextDecorationLocation.Underline });
        if (deco.Count > 0) run.TextDecorations = deco;
        return run;
    }

    private void Add(global::Avalonia.Controls.Documents.Inline inline) => _out.Add(inline);
    private void Break() => _out.Add(new Run("\n") { Foreground = _fg });
    private void Indent(int spaces) { if (spaces > 0) _out.Add(new Run(new string(' ', spaces)) { Foreground = _fg }); }

    private static string CellText(TableCell cell)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var b in cell) if (b is LeafBlock { Inline: { } inline }) sb.Append(PlainText(inline));
        return sb.ToString().Trim();
    }

    private static string PlainText(ContainerInline container)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var inline in container)
            switch (inline)
            {
                case LiteralInline lit: sb.Append(lit.Content.ToString()); break;
                case CodeInline code: sb.Append(code.Content); break;
                case LineBreakInline: sb.Append(' '); break;
                case ContainerInline cc: sb.Append(PlainText(cc)); break;
            }
        return sb.ToString();
    }
}
