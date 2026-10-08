using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Extensions.TaskLists;
using Markdig.Extensions.Yaml;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Perch.Avalonia.Views;
using Perch.Data;
using Perch.Data.Control;

namespace Perch.Avalonia.Rendering;

/// <summary>The colours a <see cref="MarkdownView"/> paints with — supplied by the caller so the preview
/// can carry its own (light-by-default) theme independent of the surrounding window.</summary>
internal sealed record MarkdownStyle(
    IBrush Fg, IBrush Muted, IBrush Title, IBrush Link,
    IBrush CodeFg, IBrush CodeBg, IBrush QuoteBar, IBrush Rule, IBrush TableBorder, IBrush TableHeaderBg,
    CodeSyntax Syntax)
{
    // Type scale / spacing / face — defaults are the viewer's VS Code-preview look; the rich session UI
    // overrides them (larger prose, its own body face) without forking the renderer.

    /// <summary>Body text size in DIPs (paragraphs, lists, table cells).</summary>
    public double BodySize { get; init; } = 13.5;

    /// <summary>Vertical space below each block.</summary>
    public double BlockGap { get; init; } = 12;

    /// <summary>The prose face; null inherits the window's. Code keeps its monospace face regardless.</summary>
    public FontFamily? BodyFont { get; init; }

    /// <summary>Padding around the whole document.</summary>
    public Thickness RootMargin { get; init; } = new(22, 16);

    /// <summary>Foreground for inline <c>`code`</c> spans; null falls back to <see cref="CodeFg"/>. Kept
    /// separate from the fenced-block <see cref="CodeFg"/> so inline code can read as a distinct coloured
    /// monospace (the Claude-desktop look) instead of the block's plain body text.</summary>
    public IBrush? InlineCode { get; init; }

    /// <summary>Optional fill behind inline <c>`code`</c> spans; null (the default) paints none, so inline
    /// code reads as a text-colour change rather than a highlighted block. The doc viewer opts back into a
    /// subtle fill for its VS Code-preview look.</summary>
    public IBrush? InlineCodeBg { get; init; }

    /// <summary>A plain left-click follows a link (no Ctrl needed) — for reading surfaces like the feed story
    /// player. Off by default, so selecting link text in a session stays a plain drag.</summary>
    public bool PlainClickLinks { get; init; }
}

/// <summary>The per-token-kind colours for fenced-code syntax highlighting (<see cref="CodeHighlight"/>),
/// keyed to the preview's own light/dark palette. Modelled on VS Code's default light/dark themes; plain
/// text falls back to <see cref="MarkdownStyle.CodeFg"/>.</summary>
internal sealed record CodeSyntax(
    IBrush Plain, IBrush Keyword, IBrush Type, IBrush Str, IBrush Number, IBrush Comment, IBrush Function)
{
    private static SolidColorBrush B(byte r, byte g, byte b) => new(Color.FromRgb(r, g, b));

    // Comments read as a muted grey — deliberately the lowest-contrast span so the eye skips over
    // decoration and lands on the code itself (rather than the usual bright comment-green).
    public static CodeSyntax Dark() => new(
        Plain: B(0xD4, 0xD4, 0xD4), Keyword: B(0x56, 0x9C, 0xD6), Type: B(0x4E, 0xC9, 0xB0),
        Str: B(0xCE, 0x91, 0x78), Number: B(0xB5, 0xCE, 0xA8), Comment: B(0x76, 0x7C, 0x8A),
        Function: B(0xDC, 0xDC, 0xAA));

    public static CodeSyntax Light() => new(
        Plain: B(0x1F, 0x23, 0x28), Keyword: B(0x00, 0x33, 0xB3), Type: B(0x1B, 0x6E, 0x8C),
        Str: B(0xA3, 0x15, 0x15), Number: B(0x09, 0x86, 0x58), Comment: B(0x8B, 0x90, 0x9B),
        Function: B(0x79, 0x5E, 0x26));
}

/// <summary>
/// A richer Markdown renderer than <see cref="MarkdownRender"/>: instead of flattening everything into one
/// <c>SelectableTextBlock</c> of inline runs, it walks the Markdig AST into a tree of real Avalonia
/// controls — headings with an underline rule, fenced code in a rounded panel, block quotes with a left
/// bar, bordered tables, styled lists and inline-code chips — for a VS Code-style enhanced-preview look.
/// Blocks stay selectable/copyable. Best-effort: a parse failure falls back to the raw text.
///
/// Each top-level block is wrapped in a thin anchor container tagged with its source line range (see
/// <see cref="PreviewAnchor"/>), so the caller can map between the source editor and the preview — highlight
/// the block under the caret, and jump the caret to a clicked block — for two-way cursor sync.
/// </summary>
internal sealed class MarkdownView
{
    // Shared with the source map (Perch.Core), so the rendered view and the copy-as-Markdown mapping parse the same
    // AST. It parses a leading `---`…`---` block as YAML frontmatter rather than a thematic break + setext heading,
    // so RenderBlock can collapse it — the GitHub/VS Code convention of hiding the metadata header of SKILL.md-style
    // files in the rendered view (the raw source editor still shows it).
    private static MarkdownPipeline Pipeline => MarkdownSourceMap.Pipeline;
    private static readonly FontFamily Mono = new("Cascadia Code, Consolas, Menlo, monospace");

    /// <summary>Optional wiring that makes inline-code file references clickable: the session cwd to resolve
    /// a relative path against, and the routed open/diff actions. Only a code span that resolves to a real
    /// file on disk is armed (so arbitrary code — <c>SessionStart</c>, <c>foo()</c> — stays inert).</summary>
    internal sealed record FileRefContext(string Cwd, Action<string> OpenViewer, Action<string> ViewDiff);

    private readonly MarkdownStyle _s;
    private readonly FileRefContext? _files;
    private readonly MarkdownCopyDoc _doc;   // the source every block's copy map indexes into

    private double BodySize => _s.BodySize;
    private double BlockGap => _s.BlockGap;   // vertical space below a block

    private MarkdownView(MarkdownStyle s, FileRefContext? files, string md)
    {
        _s = s;
        _files = files;
        _doc = new MarkdownCopyDoc(md);
    }

    /// <summary>The 0-based source line a rendered element came from, stamped on paragraphs, headings, list
    /// items, table rows and code blocks so a click can map to the right line (not just the enclosing block).
    /// -1 means unset.</summary>
    public static readonly AttachedProperty<int> SourceLineProperty =
        AvaloniaProperty.RegisterAttached<MarkdownView, Control, int>("SourceLine", -1);

    /// <summary>True on a code block's text, whose rendering is verbatim — so a click can be hit-tested to an
    /// exact line and column, giving cursor-level (not just block-level) placement.</summary>
    public static readonly AttachedProperty<bool> VerbatimProperty =
        AvaloniaProperty.RegisterAttached<MarkdownView, Control, bool>("Verbatim", false);

    public static int GetSourceLine(Control c) => c.GetValue(SourceLineProperty);
    public static bool GetVerbatim(Control c) => c.GetValue(VerbatimProperty);

    private static T Stamp<T>(T c, int line, bool verbatim = false) where T : Control
    {
        if (line >= 0) c.SetValue(SourceLineProperty, line);
        if (verbatim) c.SetValue(VerbatimProperty, true);
        return c;
    }

    /// <summary>Maps a rendered top-level block back to the source lines it came from. <see cref="Control"/>
    /// is the anchor container (a transparent <c>Border</c> the caller can tint to highlight the block).</summary>
    internal sealed class PreviewAnchor
    {
        public required Control Control { get; init; }
        public required int StartLine { get; init; }   // 0-based source line where the block starts
        public int EndLine { get; set; }               // inclusive; filled so the ranges tile the document
    }

    /// <summary>The source offset at which the last top-level block begins — so a streaming renderer can treat
    /// everything before it as settled (complete blocks whose meaning can't change as more text arrives) and
    /// only re-render the trailing, still-forming block. Returns 0 when there are fewer than two top-level
    /// blocks (nothing has settled yet). Best-effort: any parse trouble yields 0 (render the whole thing).</summary>
    public static int SettledPrefixLength(string md)
    {
        if (string.IsNullOrEmpty(md)) return 0;
        try
        {
            Block? last = null;
            int count = 0;
            foreach (var b in Markdown.Parse(md, Pipeline)) { last = b; count++; }
            if (count < 2 || last is null) return 0;
            int start = last.Span.Start;
            return start > 0 && start <= md.Length ? start : 0;
        }
        catch { return 0; }
    }

    /// <summary>Parses <paramref name="md"/> and returns a control tree ready to drop into a scroll viewer.</summary>
    public static Control Build(string md, MarkdownStyle style) => Build(md, style, null, out _);

    /// <summary>As <see cref="Build(string, MarkdownStyle)"/>, with inline-code file references made clickable
    /// via <paramref name="files"/> (null leaves them inert).</summary>
    public static Control Build(string md, MarkdownStyle style, FileRefContext? files) => Build(md, style, files, out _);

    /// <summary>As <see cref="Build(string, MarkdownStyle)"/>, also returning the source-line anchors for the
    /// top-level blocks (in document order) so the caller can wire two-way cursor sync.</summary>
    public static Control Build(string md, MarkdownStyle style, out IReadOnlyList<PreviewAnchor> anchors) =>
        Build(md, style, null, out anchors);

    /// <summary>The full builder: parses <paramref name="md"/>, optionally arms inline-code file references
    /// (<paramref name="files"/>), and returns the control tree plus its source-line anchors.</summary>
    public static Control Build(string md, MarkdownStyle style, FileRefContext? files, out IReadOnlyList<PreviewAnchor> anchors)
    {
        var view = new MarkdownView(style, files, md);
        var list = new List<PreviewAnchor>();
        anchors = list;
        var root = new StackPanel { Margin = style.RootMargin };
        // FontFamily is an inherited text property, so setting it on the root reaches every prose block
        // beneath; code panels set their monospace face explicitly and are unaffected.
        if (style.BodyFont is { } bodyFont) TextElement.SetFontFamily(root, bodyFont);
        if (string.IsNullOrWhiteSpace(md))
            return root;

        MarkdownDocument doc;
        try { doc = Markdown.Parse(md, Pipeline); }
        catch { root.Children.Add(view.Wrap(view.Paragraph(md, style.Fg))); return root; }

        foreach (var block in doc)
        {
            if (view.RenderBlock(block, style.Fg) is not { } c)
                continue;
            Stamp(c, block.Line);   // finer elements (list items, table rows, code) stamp themselves too
            var wrapper = view.Wrap(c);
            list.Add(new PreviewAnchor { Control = wrapper, StartLine = block.Line, EndLine = block.Line });
            root.Children.Add(wrapper);
        }

        // Extend each block's range to just before the next block starts, so every source line maps to a
        // block (the last block owns everything to the end).
        for (int i = 0; i < list.Count; i++)
            list[i].EndLine = i + 1 < list.Count
                ? System.Math.Max(list[i].StartLine, list[i + 1].StartLine - 1)
                : int.MaxValue;

        return root;
    }

    // Wrap a top-level block in a transparent anchor with a reserved left bar (so highlighting it never
    // shifts the layout). The caller tints BorderBrush/Background to mark the block under the caret.
    private Border Wrap(Control child) => new()
    {
        Padding = new Thickness(8, 0, 0, 0),
        BorderThickness = new Thickness(3, 0, 0, 0),
        BorderBrush = Brushes.Transparent,
        Background = Brushes.Transparent,
        Child = child,
    };

    private Control? RenderBlock(Block block, IBrush fg) => block switch
    {
        YamlFrontMatterBlock y => Frontmatter(y), // metadata header — a collapsed "Preamble" disclosure
        HeadingBlock h        => Heading(h),
        ParagraphBlock p      => Paragraph(p, fg),
        ListBlock list        => List(list, fg),
        QuoteBlock q          => Quote(q),
        Table table           => TableView(table),
        CodeBlock code        => Code(code),   // FencedCodeBlock derives from this
        ThematicBreakBlock    => Rule(),
        HtmlBlock html        => RawHtml(html),
        ContainerBlock cb     => Stack(cb, fg),
        _                     => null,
    };

    private Control Heading(HeadingBlock h)
    {
        double size = h.Level switch { 1 => 24, 2 => 19, 3 => 16, 4 => 14.5, 5 => 13.5, _ => 12.5 };
        var text = new SelectableTextBlock
        {
            FontSize = size, FontWeight = FontWeight.SemiBold, Foreground = _s.Title,
            TextWrapping = TextWrapping.Wrap,
        };
        Fill(text, h, h.Inline is { } inl ? [inl] : [], size, _s.Title, new MarkdownInlineStyle(Bold: true));

        // h1/h2 carry a bottom rule, like the GitHub/VS Code preview. Extra space above to set them apart.
        if (h.Level <= 2)
            return new Border
            {
                BorderBrush = _s.Rule, BorderThickness = new Thickness(0, 0, 0, 1),
                Padding = new Thickness(0, 0, 0, 5), Margin = new Thickness(0, 18, 0, 10),
                Child = text,
            };
        text.Margin = new Thickness(0, 14, 0, 6);
        return text;
    }

    private SelectableTextBlock Paragraph(ParagraphBlock p, IBrush fg)
    {
        var tb = Paragraph("", fg);
        Fill(tb, p, p.Inline is { } inl ? [inl] : [], BodySize, fg, default);
        return tb;
    }

    // Lay out a prose block's inlines into tb (one pass through the shared walker, which also records the block's
    // source map), arm its links and file references, and attach the map so a selection copies as Markdown.
    private void Fill(SelectableTextBlock tb, Block owner, IEnumerable<ContainerInline> inlines, double size,
        IBrush brush, MarkdownInlineStyle style, bool extendLead = true)
    {
        var sink = new InlineSink(this, size, brush);
        var map = MarkdownSourceMap.ForInlines(_doc, owner, inlines, style, sink, extendLead);
        tb.Inlines = sink.Inlines;
        LinkText.Attach(tb, sink.Links, _s.PlainClickLinks);
        AttachFileRefs(tb, sink);
        BlockSelection.SetSourceMap(tb, map);
    }

    private SelectableTextBlock Paragraph(string text, IBrush fg) => new()
    {
        Text = text, Foreground = fg, FontSize = BodySize, TextWrapping = TextWrapping.Wrap,
        LineHeight = BodySize * 1.55, Margin = new Thickness(0, 0, 0, BlockGap),
    };

    private Control List(ListBlock list, IBrush fg)
    {
        var panel = new StackPanel { Margin = new Thickness(2, 0, 0, BlockGap), Spacing = 3 };
        int number = list.OrderedStart != null && int.TryParse(list.OrderedStart, out var s) ? s : 1;

        foreach (var itemObj in list)
        {
            if (itemObj is not ListItemBlock item)
                continue;

            var content = new StackPanel();
            Stamp(content, item.Line);   // click maps to this item's line, not the list's first line
            foreach (var child in item)
                if (RenderBlock(child, fg) is { } c)
                {
                    // Tighten the paragraph spacing inside a list item; nested lists keep their own gap.
                    if (c is SelectableTextBlock stb) stb.Margin = new Thickness(0);
                    content.Children.Add(c);
                }

            // A task-list item leads with a drawn checkbox (in `content`), so drop the redundant bullet and
            // let the checkbox stand as the marker — the way GitHub/VS Code render checklists.
            bool taskItem = IsTaskItem(item);
            var marker = new TextBlock
            {
                Text = taskItem ? "" : list.IsOrdered ? $"{number}." : "•",
                Foreground = _s.Muted, FontSize = BodySize,
                Margin = new Thickness(0, 0, taskItem ? 0 : 8, 0),
                MinWidth = taskItem ? 0 : list.IsOrdered ? 18 : 10,
                TextAlignment = list.IsOrdered ? TextAlignment.Right : TextAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
            };

            var rowGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
            Grid.SetColumn(marker, 0);
            Grid.SetColumn(content, 1);
            rowGrid.Children.Add(marker);
            rowGrid.Children.Add(content);
            panel.Children.Add(rowGrid);
            number++;
        }
        return panel;
    }

    // A list item is a task item when its first paragraph opens with a GitHub task-list marker ([ ] / [x]).
    private static bool IsTaskItem(ListItemBlock item) =>
        item.FirstOrDefault() is ParagraphBlock { Inline: { } inl } && inl.FirstChild is TaskList;

    private Control Quote(QuoteBlock q)
    {
        var inner = new StackPanel();
        foreach (var child in q)
            if (RenderBlock(child, _s.Muted) is { } c)
            {
                if (c is SelectableTextBlock stb) stb.Margin = new Thickness(0, 0, 0, 4);
                inner.Children.Add(c);
            }
        return new Border
        {
            BorderBrush = _s.QuoteBar, BorderThickness = new Thickness(3, 0, 0, 0),
            Padding = new Thickness(12, 4, 8, 4), Margin = new Thickness(0, 0, 0, BlockGap),
            Child = inner,
        };
    }

    // A SKILL.md-style YAML frontmatter header, rendered as a collapsed disclosure: a one-line, muted
    // "Preamble" row (chevron + label) over the raw YAML in a code panel, hidden until the row is clicked.
    // This keeps the metadata out of the way (the GitHub/VS Code convention of hiding it) while leaving it
    // one click from view — rather than dropping it entirely. Themed from the MarkdownStyle brushes so it
    // follows the preview's own light/dark palette (a Fluent Expander would read the window's instead).
    private Control Frontmatter(YamlFrontMatterBlock yaml)
    {
        // The block's captured text, without the surrounding `---` fences, so only the YAML body shows.
        string body = MarkdownCopy.FrontmatterText(yaml);
        var yamlText = new SelectableTextBlock
        {
            Text = body, FontFamily = Mono, FontSize = 12.5,
            Foreground = _s.CodeFg, TextWrapping = TextWrapping.Wrap,
        };
        BlockSelection.SetSourceMap(yamlText, MarkdownSourceMap.ForWhole(_doc, yaml, body.Length));

        var content = new Border
        {
            Background = _s.CodeBg, CornerRadius = new CornerRadius(6),
            BorderBrush = _s.TableBorder, BorderThickness = new Thickness(1),
            Padding = new Thickness(12, 10), Margin = new Thickness(0, 6, 0, 0),
            IsVisible = false,
            // Wrap the YAML rather than scroll it: frontmatter lines (long descriptions especially) would
            // otherwise force a horizontal scrollbar on the whole preview, which is awkward to use.
            Child = yamlText,
        };

        var chevron = new TextBlock
        {
            Text = "▸",   // ▸ collapsed, ▾ expanded
            Foreground = _s.Muted, FontSize = 11, Margin = new Thickness(0, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var label = new TextBlock
        {
            Text = "Preamble", Foreground = _s.Muted, FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var header = new Border
        {
            Background = Brushes.Transparent, Padding = new Thickness(2, 3),
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { chevron, label } },
        };
        ToolTip.SetTip(header, "Expand to see the file's preamble (YAML frontmatter)");
        header.PointerPressed += (_, _) =>
        {
            content.IsVisible = !content.IsVisible;
            chevron.Text = content.IsVisible ? "▾" : "▸";
        };

        return new StackPanel { Margin = new Thickness(0, 0, 0, BlockGap), Children = { header, content } };
    }

    private Control Code(CodeBlock code)
    {
        // The code's text comes from its source map, line for line, so a selection inside it copies verbatim and
        // a whole block takes its fences along.
        var map = MarkdownSourceMap.ForLines(_doc, code, out var lines);
        var text = new SelectableTextBlock
        {
            FontFamily = Mono, FontSize = 12.5, Foreground = _s.CodeFg,
            TextWrapping = TextWrapping.NoWrap,
        };
        BlockSelection.SetSourceMap(text, map);
        // The rendered text is verbatim, so a click can be hit-tested to an exact line/column. Stamp the
        // first content line (the line after the opening fence, for a fenced block).
        Stamp(text, code is FencedCodeBlock ? code.Line + 1 : code.Line, verbatim: true);

        // Syntax-highlight by the fence's language tag (```bash → "bash"). Unknown/blank languages tokenize
        // to a single plain span, so they render exactly as before (one uncoloured block).
        var lang = (code as FencedCodeBlock)?.Info;
        var toks = CodeHighlight.Tokenize(lang, lines);
        if (toks.Count == 1 && toks[0].Kind == CodeToken.Plain)
        {
            text.Text = lines;
        }
        else
        {
            var inlines = new InlineCollection();
            foreach (var (span, kind) in toks)
                inlines.Add(new Run(span) { Foreground = SyntaxBrush(kind) });
            text.Inlines = inlines;
        }

        return CodePanel(text);
    }

    // The chrome around a code block's text: a rounded, bordered panel that scrolls sideways on long lines.
    private Border CodePanel(Control text) =>
        new()
        {
            Background = _s.CodeBg, CornerRadius = new CornerRadius(6),
            BorderBrush = _s.TableBorder, BorderThickness = new Thickness(1),
            Padding = new Thickness(12, 10), Margin = new Thickness(0, 0, 0, BlockGap),
            Child = new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                // Reserve a row for the horizontal bar instead of letting the Fluent auto-hide bar float over
                // the content: on a one-line code block the overlaid bar sits right on top of the only line
                // and swallows the text. AllowAutoHide=false lays the bar out beneath the code (and it only
                // appears at all when the line actually overflows, per the Auto visibility above).
                AllowAutoHide = false,
                Content = text,
            },
        };

    /// <summary>A code block still streaming (its closing fence hasn't arrived): the same panel a finished block
    /// gets, laid out as one <see cref="Build(string, MarkdownStyle)"/> block, but plain — no parse, no syntax
    /// colours. The caller updates <paramref name="text"/>'s <c>Text</c> in place as code arrives, and swaps in a
    /// real build once the fence closes.</summary>
    public static Control BuildStreamingCode(MarkdownStyle style, out SelectableTextBlock text)
    {
        var view = new MarkdownView(style, null, "");
        text = new SelectableTextBlock
        {
            FontFamily = Mono, FontSize = 12.5, Foreground = style.CodeFg, TextWrapping = TextWrapping.NoWrap,
        };
        var root = new StackPanel { Margin = style.RootMargin };
        if (style.BodyFont is { } bodyFont) TextElement.SetFontFamily(root, bodyFont);
        root.Children.Add(view.Wrap(view.CodePanel(text)));
        return root;
    }

    private IBrush SyntaxBrush(CodeToken kind) => kind switch
    {
        CodeToken.Keyword  => _s.Syntax.Keyword,
        CodeToken.Type     => _s.Syntax.Type,
        CodeToken.Str      => _s.Syntax.Str,
        CodeToken.Number   => _s.Syntax.Number,
        CodeToken.Comment  => _s.Syntax.Comment,
        CodeToken.Function => _s.Syntax.Function,
        _                  => _s.Syntax.Plain,
    };

    private Control Rule() => new Border
    {
        Height = 1, Background = _s.Rule, Margin = new Thickness(0, 6, 0, 14),
    };

    private Control RawHtml(HtmlBlock html)
    {
        var map = MarkdownSourceMap.ForLines(_doc, html, out var text);
        var tb = new SelectableTextBlock
        {
            Text = text,
            FontFamily = Mono, FontSize = 12, Foreground = _s.Muted, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, BlockGap),
        };
        BlockSelection.SetSourceMap(tb, map);
        return tb;
    }

    private Control Stack(ContainerBlock cb, IBrush fg)
    {
        var panel = new StackPanel();
        foreach (var child in cb)
            if (RenderBlock(child, fg) is { } c)
                panel.Children.Add(c);
        return panel;
    }

    private Control TableView(Table table)
    {
        var rows = table.OfType<TableRow>().ToList();
        if (rows.Count == 0)
            return new StackPanel();
        int cols = rows.Max(r => r.Count());

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions(string.Join(",", Enumerable.Repeat("*", cols))),
        };
        for (int r = 0; r < rows.Count; r++)
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

        for (int r = 0; r < rows.Count; r++)
        {
            var row = rows[r];
            bool header = row.IsHeader;
            int ci = 0;
            foreach (var cellObj in row)
            {
                if (cellObj is not TableCell cell) { ci++; continue; }
                var tb = new SelectableTextBlock
                {
                    FontSize = BodySize, Foreground = _s.Fg, TextWrapping = TextWrapping.Wrap,
                    FontWeight = header ? FontWeight.SemiBold : FontWeight.Normal,
                };
                // Only the row's first cell owns the line start (its leading pipe) in the source.
                Fill(tb, cell, cell.OfType<LeafBlock>().Select(b => b.Inline).OfType<ContainerInline>(),
                    BodySize, _s.Fg, new MarkdownInlineStyle(Bold: header), extendLead: ci == 0);

                var cellBorder = new Border
                {
                    BorderBrush = _s.TableBorder, BorderThickness = new Thickness(0, 0, 1, 1),
                    Background = header ? _s.TableHeaderBg : null,
                    Padding = new Thickness(9, 5), Child = tb,
                };
                Stamp(cellBorder, row.Line);   // click maps to this row's source line
                Grid.SetRow(cellBorder, r);
                Grid.SetColumn(cellBorder, ci);
                grid.Children.Add(cellBorder);
                ci++;
            }
        }

        return new Border
        {
            BorderBrush = _s.TableBorder, BorderThickness = new Thickness(1, 1, 0, 0),
            Margin = new Thickness(0, 0, 0, BlockGap), HorizontalAlignment = HorizontalAlignment.Left,
            Child = grid,
        };
    }

    // ── Inlines ────────────────────────────────────────────────────────────────────────────────────

    // Builds the runs of one prose block as the shared walker (MarkdownSourceMap.Walk) reports them, plus the
    // char-ranges of its links (so LinkText can make them clickable) and inline-code spans (file-ref candidates).
    // The walker owns the positions — every run by its text length, a checkbox as one position, exactly what the
    // block's TextLayout counts — so a hit-test index maps back to the right span, and to the right source.
    private sealed class InlineSink(MarkdownView view, double size, IBrush brush) : IMarkdownInlineSink
    {
        public readonly InlineCollection Inlines = new();
        public readonly List<UrlSpan> Links = new();
        public readonly List<(int Start, int Length, string Text)> Codes = new();

        private IBrush BrushFor(MarkdownInlineStyle style) => style.Link ? view._s.Link : brush;

        public void Text(string text, MarkdownInlineStyle style) => Inlines.Add(Styled(text, size, BrushFor(style), style));

        public void Code(int pos, string code, MarkdownInlineStyle style)
        {
            Codes.Add((pos, code.Length, code));   // a possible file reference
            var run = new Run(code)
            {
                FontFamily = Mono, Foreground = view._s.InlineCode ?? view._s.CodeFg, FontSize = size,
            };
            if (view._s.InlineCodeBg is { } codeBg) run.Background = codeBg;   // else: coloured text, no block
            Inlines.Add(run);
        }

        public void Checkbox(bool isChecked, MarkdownInlineStyle style) =>
            Inlines.Add(new InlineUIContainer(view.Checkbox(isChecked, size)) { BaselineAlignment = BaselineAlignment.Center });

        public void LineBreak(string text, MarkdownInlineStyle style) => Inlines.Add(new Run(text) { Foreground = BrushFor(style) });

        // Only an http(s)/mailto target becomes a browser link (review fixes CP8). A relative or file: target
        // joins the file-ref candidates instead, so it opens in the viewer only if it names a real local file;
        // any other scheme (javascript:, search-ms:, ms-*:) is left as inert text.
        public void Link(int start, int end, string? url)
        {
            if (OpenTargets.WebUrl(url) is { } web) Links.Add(new UrlSpan(start, end - start, web));
            else if (OpenTargets.LinkFilePath(url) is { } path) Codes.Add((start, end - start, path));
        }
    }

    // Arm any inline-code spans in this block that resolve to a real file (relative to the session cwd, or an
    // absolute path). Gating on existence keeps it accurate — arbitrary code spans never become file links.
    private void AttachFileRefs(SelectableTextBlock tb, InlineSink sink)
    {
        if (_files is not { } f || sink.Codes.Count == 0)
            return;
        List<FileRef.FileSpan>? spans = null;
        foreach (var (start, len, text) in sink.Codes)
            // Never probes a UNC/device/network-drive path (NTLM leak + UI hang), and caches per (cwd, text) so a
            // streaming re-render doesn't hit the disk again (review fixes CP9).
            if (FileRefResolver.Resolve(f.Cwd, text) is { } abs)
                (spans ??= new()).Add(new FileRef.FileSpan(start, len, abs));
        if (spans is { Count: > 0 })
            FileRef.AttachInline(tb, spans, f.OpenViewer, f.ViewDiff);
    }

    // A drawn task-list checkbox — a rounded, bordered box with a soft drop shadow (and a check mark when
    // ticked), so it reads as a real control rather than the flat ☐/☑ glyphs. Sized to the surrounding text.
    private Control Checkbox(bool isChecked, double fontSize)
    {
        double sz = System.Math.Round(fontSize);   // roughly the text's cap height
        var box = new Border
        {
            Width = sz, Height = sz,
            CornerRadius = new CornerRadius(4),
            BorderThickness = new Thickness(1.4),
            BorderBrush = isChecked ? _s.Link : _s.TableBorder,
            Background = isChecked ? _s.Link : _s.CodeBg,
            Margin = new Thickness(1, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center,
            BoxShadow = new BoxShadows(new BoxShadow
            {
                OffsetX = 0, OffsetY = 1, Blur = 2, Spread = 0, Color = Color.FromArgb(64, 0, 0, 0),
            }),
        };
        if (isChecked)
        {
            // A tick stroked white to read on the fill. Stretch=Uniform scales the geometry to fit and
            // centres it within the box (a raw Path would centre only its geometry's bounds, sitting off-
            // centre); a uniform margin keeps it clear of the rounded edges.
            box.Child = new global::Avalonia.Controls.Shapes.Path
            {
                Data = Geometry.Parse("M 0 3.5 L 2.8 6.5 L 8 0"),
                Stretch = Stretch.Uniform,
                Stroke = Brushes.White, StrokeThickness = 1.5,
                StrokeLineCap = PenLineCap.Round, StrokeJoin = PenLineJoin.Round,
                // Slightly more headroom than footroom, so the tick settles a touch lower and reads centred
                // (a checkmark's visual weight sits above its geometric middle).
                Margin = new Thickness(sz * 0.22, sz * 0.28, sz * 0.22, sz * 0.16),
                HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch,
            };
        }
        return box;
    }

    private static Run Styled(string text, double size, IBrush brush, MarkdownInlineStyle style)
    {
        var run = new Run(text)
        {
            Foreground = brush, FontSize = size,
            FontWeight = style.Bold ? FontWeight.Bold : FontWeight.Normal,
            FontStyle = style.Italic ? FontStyle.Italic : FontStyle.Normal,
        };
        var deco = new TextDecorationCollection();
        if (style.Strike) deco.Add(new TextDecoration { Location = TextDecorationLocation.Strikethrough });
        if (style.Link) deco.Add(new TextDecoration { Location = TextDecorationLocation.Underline });
        if (deco.Count > 0) run.TextDecorations = deco;
        return run;
    }
}
