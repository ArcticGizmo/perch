using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Utilities;

namespace Perch.Avalonia.Rendering;

/// <summary>The editor-side syntax colours for the Markdown source box, mapped from the ArcticGizmo
/// <b>Aurora</b> family's editor/syntax tokens (Keyword/Str/Function/Comment/… — the package folds a full
/// code palette into every scheme). Markdown isn't one of the package's languages, so we <em>map</em> its
/// constructs onto those code tokens: headings→Function, inline/fenced code→Str, links→Link, list markers
/// →Operator, emphasis delimiters/rules→Punctuation, block-quote/URLs→Comment, HTML→Tag.</summary>
internal readonly record struct EditorSyntax(
    IBrush Fg, IBrush Heading, IBrush ListMark, IBrush Code, IBrush Punct,
    IBrush Link, IBrush Url, IBrush Quote, IBrush Tag)
{
    private static SolidColorBrush B(byte r, byte g, byte b) => new(Color.FromRgb(r, g, b));

    // Aurora (Dark) editor tokens: EditorFg + Function/Str/Operator/Comment/Link/Punctuation/Tag.
    public static EditorSyntax Dark() => new(
        Fg: B(0xE1, 0xE1, 0xEB), Heading: B(0x82, 0xAA, 0xFF), ListMark: B(0x89, 0xDD, 0xFF),
        Code: B(0xA5, 0xD6, 0xA7), Punct: B(0xA6, 0xAC, 0xCD), Link: B(0x93, 0xC5, 0xFD),
        Url: B(0x8A, 0x91, 0xA6), Quote: B(0x8A, 0x91, 0xA6), Tag: B(0xF0, 0x71, 0x78));

    // Aurora (Light) editor tokens — vivid, saturated mid-tones (Tailwind 600) so each reads clearly as its
    // OWN hue against the near-black body text (deeper shades looked black-ish). Code green, headings purple,
    // links/list-markers blue.
    public static EditorSyntax Light() => new(
        Fg: B(0x24, 0x29, 0x2F), Heading: B(0x93, 0x33, 0xEA), ListMark: B(0x25, 0x63, 0xEB),
        Code: B(0x16, 0xA3, 0x4A), Punct: B(0x64, 0x74, 0x8B), Link: B(0x25, 0x63, 0xEB),
        Url: B(0x64, 0x74, 0x8B), Quote: B(0x64, 0x74, 0x8B), Tag: B(0x16, 0xA3, 0x4A));

    public static EditorSyntax For(bool light) => light ? Light() : Dark();
}

/// <summary>Heuristic single-pass highlighter for Markdown <em>source</em> text: it walks the text line by
/// line (tracking fenced-code state) and paints a per-character brush/style map, then coalesces that into the
/// <see cref="ValueSpan{T}"/> run list Avalonia's text layout consumes. Not a full CommonMark parse — the
/// usual editor trade-off — but it covers headings, fences, inline code, links/images, emphasis, list
/// markers, block quotes, rules, HTML tags and autolinks. Pure and side-effect-free.</summary>
internal static class MarkdownSourceHighlighter
{
    // Skip highlighting very large buffers so a huge file can't stall per-keystroke re-layout (falls back to
    // plain text). Markdown docs are almost always far under this.
    private const int MaxLength = 120_000;

    private const RegexOptions Opts = RegexOptions.Compiled | RegexOptions.CultureInvariant;
    private static readonly Regex Fence     = new(@"^\s{0,3}(`{3,}|~{3,})", Opts);
    private static readonly Regex Heading   = new(@"^\s{0,3}#{1,6}(\s.*)?$", Opts);
    private static readonly Regex Quote     = new(@"^\s*>+", Opts);
    private static readonly Regex ListMark  = new(@"^(\s*)([-*+]|\d{1,9}[.)])(\s)", Opts);
    private static readonly Regex Rule      = new(@"^\s{0,3}([-*_])[ \t]*(?:\1[ \t]*){2,}$", Opts);
    private static readonly Regex Bold      = new(@"(\*\*|__)(?=\S)(.+?\S)\1", Opts);
    private static readonly Regex Italic    = new(@"(?<![\w*_])([*_])(?=\S)(.+?\S)\1(?![\w*_])", Opts);
    private static readonly Regex Strike    = new(@"(~~)(?=\S)(.+?\S)~~", Opts);
    private static readonly Regex Html      = new(@"</?[a-zA-Z][^>\n]*>", Opts);
    private static readonly Regex Autolink  = new(@"<(?:https?://|mailto:)[^>\s]+>", Opts);
    private static readonly Regex Link      = new(@"(!?)\[([^\]\n]*)\]\(([^)\n]+)\)", Opts);
    private static readonly Regex Code      = new(@"(`{1,3})(.+?)\1", Opts);

    private const byte Bold_ = 1, Italic_ = 2;

    public static IReadOnlyList<ValueSpan<TextRunProperties>>? Highlight(
        string text, EditorSyntax s, Typeface baseFace, double fontSize)
    {
        int n = text.Length;
        if (n == 0 || n > MaxLength)
            return null;

        var fg = new IBrush?[n];      // null ⇒ leave as the layout's default foreground
        var st = new byte[n];         // Bold_ | Italic_ bit flags

        void Paint(int start, int len, IBrush? brush, byte style)
        {
            int end = Math.Min(start + len, n);
            for (int i = Math.Max(0, start); i < end; i++) { if (brush is not null) fg[i] = brush; st[i] = style; }
        }

        bool inFence = false;
        int pos = 0;
        while (pos < n)
        {
            int nl = text.IndexOf('\n', pos);
            int lineEnd = nl < 0 ? n : nl;         // exclusive of the newline
            int ls = pos, len = lineEnd - ls;
            var line = text.Substring(ls, len);

            if (Fence.IsMatch(line))
            {
                Paint(ls, len, s.Code, 0);         // the ``` / ~~~ fence line itself
                inFence = !inFence;
            }
            else if (inFence)
            {
                Paint(ls, len, s.Code, 0);         // verbatim fenced content
            }
            else
            {
                HighlightLine(line, ls, s, Paint);
            }

            pos = nl < 0 ? n : nl + 1;
        }

        return Coalesce(fg, st, s.Fg, baseFace, fontSize);
    }

    // Paint one non-fence line. Lower-precedence rules first; later ones overwrite (inline code wins last).
    private static void HighlightLine(string line, int ls, EditorSyntax s, Action<int, int, IBrush?, byte> Paint)
    {
        // Structural prefixes.
        if (Heading.IsMatch(line))
            Paint(ls, line.Length, s.Heading, Bold_);
        else if (Rule.IsMatch(line))
            Paint(ls, line.Length, s.Punct, 0);
        else
        {
            if (Quote.Match(line) is { Success: true } q)
                Paint(ls + q.Index, q.Length, s.Quote, Italic_);
            if (ListMark.Match(line) is { Success: true } lm)
                Paint(ls + lm.Groups[2].Index, lm.Groups[2].Length, s.ListMark, 0);
        }

        // Inline emphasis.
        foreach (Match m in Bold.Matches(line))
        {
            Paint(ls + m.Index, m.Length, s.Punct, 0);                                   // the ** / __ markers
            Paint(ls + m.Groups[2].Index, m.Groups[2].Length, null, Bold_);              // bold the inner text
        }
        foreach (Match m in Italic.Matches(line))
        {
            Paint(ls + m.Index, m.Length, s.Punct, 0);
            Paint(ls + m.Groups[2].Index, m.Groups[2].Length, null, Italic_);
        }
        foreach (Match m in Strike.Matches(line))
            Paint(ls + m.Index, m.Length, s.Punct, 0);

        // Tags / autolinks / links.
        foreach (Match m in Html.Matches(line))
            Paint(ls + m.Index, m.Length, s.Tag, 0);
        foreach (Match m in Autolink.Matches(line))
            Paint(ls + m.Index, m.Length, s.Link, 0);
        foreach (Match m in Link.Matches(line))
        {
            Paint(ls + m.Index, m.Length, s.Punct, 0);                                   // brackets/parens
            Paint(ls + m.Groups[2].Index, m.Groups[2].Length, s.Link, 0);               // [text]
            Paint(ls + m.Groups[3].Index, m.Groups[3].Length, s.Url, 0);                // (url)
        }

        // Inline code — highest precedence.
        foreach (Match m in Code.Matches(line))
            Paint(ls + m.Index, m.Length, s.Code, 0);
    }

    // Merge the per-char map into ordered, non-overlapping style runs (only where something was set).
    private static List<ValueSpan<TextRunProperties>> Coalesce(
        IBrush?[] fg, byte[] st, IBrush baseFg, Typeface baseFace, double fontSize)
    {
        var runs = new List<ValueSpan<TextRunProperties>>();
        int n = fg.Length, i = 0;
        while (i < n)
        {
            var f = fg[i]; var style = st[i];
            if (f is null && style == 0) { i++; continue; }
            int j = i + 1;
            while (j < n && ReferenceEquals(fg[j], f) && st[j] == style) j++;

            var weight = (style & Bold_) != 0 ? FontWeight.Bold : FontWeight.Normal;
            var fstyle = (style & Italic_) != 0 ? FontStyle.Italic : FontStyle.Normal;
            var tf = weight == FontWeight.Normal && fstyle == FontStyle.Normal
                ? baseFace
                : new Typeface(baseFace.FontFamily, fstyle, weight, baseFace.Stretch);
            runs.Add(new ValueSpan<TextRunProperties>(i, j - i,
                new GenericTextRunProperties(tf, fontRenderingEmSize: fontSize, foregroundBrush: f ?? baseFg)));
            i = j;
        }
        return runs;
    }
}

/// <summary>A <see cref="TextPresenter"/> that injects per-token style spans into its text layout — the seam
/// Avalonia leaves open via the <c>protected virtual</c> <see cref="CreateTextLayout"/> plus the layout's
/// <c>textStyleOverrides</c>. Because the presenter <em>is</em> the layout the caret, selection and our gutter
/// all read, colouring can never drift from the geometry.</summary>
internal sealed class HighlightTextPresenter : TextPresenter
{
    // The base reads its wrap constraint from this private field before building the layout; we mirror it so
    // wrapping matches exactly. Pinned to Avalonia 12.0.5.
    private static readonly System.Reflection.FieldInfo? ConstraintField =
        typeof(TextPresenter).GetField("_constraint",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

    /// <summary>Force the (cached) text layout to rebuild — call after swapping the highlighter on a theme
    /// change, so the new palette actually repaints (otherwise the stale layout keeps the old colours).</summary>
    public void RefreshHighlight()
    {
        InvalidateTextLayout();
        InvalidateMeasure();
        InvalidateVisual();
    }

    protected override TextLayout CreateTextLayout()
    {
        var text = Text;
        // Read the highlighter live from the owning box (via TemplatedParent) rather than a field on this
        // presenter: a theme toggle can re-template the TextBox and hand us a *fresh* presenter, so a field
        // would read null and silently drop to plain text. The box outlives every presenter, so this can't stale.
        var highlighter = (TemplatedParent as HighlightTextBox)?.Highlighter;
        if (string.IsNullOrEmpty(text) || highlighter is null)
            return base.CreateTextLayout();

        IReadOnlyList<ValueSpan<TextRunProperties>>? spans;
        try { spans = highlighter(text); }
        catch { return base.CreateTextLayout(); }
        if (spans is null || spans.Count == 0)
            return base.CreateTextLayout();

        var typeface = new Typeface(FontFamily, FontStyle, FontWeight, FontStretch);
        var constraint = ConstraintField?.GetValue(this) is Size c
            ? c : new Size(double.PositiveInfinity, double.PositiveInfinity);
        return new TextLayout(
            text, typeface,
            fontSize: FontSize, foreground: Foreground,
            textAlignment: TextAlignment, textWrapping: TextWrapping,
            flowDirection: FlowDirection.LeftToRight,
            maxWidth: constraint.Width, maxHeight: constraint.Height,
            lineHeight: LineHeight, letterSpacing: LetterSpacing,
            fontFeatures: FontFeatures, textStyleOverrides: spans);
    }
}

/// <summary>A <see cref="TextBox"/> whose source text is syntax-highlighted. It uses a minimal code-built
/// template that swaps in a <see cref="HighlightTextPresenter"/> for <c>PART_TextPresenter</c> (the only way to
/// colour an editable TextBox in Avalonia — bindings mirror the Fluent template exactly); all editing, undo,
/// selection and IME behaviour still comes from the base <see cref="TextBox"/>.</summary>
internal sealed class HighlightTextBox : TextBox
{
    private HighlightTextPresenter? _presenter;

    /// <summary>The live highlighter the presenter reads on every layout (see <see cref="HighlightTextPresenter"/>).</summary>
    public Func<string, IReadOnlyList<ValueSpan<TextRunProperties>>?>? Highlighter { get; private set; }

    public HighlightTextBox()
    {
        Template = BuildTemplate();
        // Our minimal template replaces the Fluent theme's, which carried the I-beam cursor setter — restore it.
        Cursor = new Cursor(StandardCursorType.Ibeam);
    }

    /// <summary>Set (or replace) the highlighter and rebuild the layout — used on the window's light/dark toggle.
    /// The presenter reads <see cref="Highlighter"/> live, so this works even if the box has re-templated.</summary>
    public void SetHighlighter(Func<string, IReadOnlyList<ValueSpan<TextRunProperties>>?>? h)
    {
        Highlighter = h;
        _presenter?.RefreshHighlight();
        InvalidateMeasure();   // belt-and-suspenders: guarantee a re-measure even if the presenter ref is stale
    }

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);
        _presenter = e.NameScope.Find<HighlightTextPresenter>("PART_TextPresenter");
        _presenter?.RefreshHighlight();
    }

    private static FuncControlTemplate BuildTemplate() => new((_, ns) =>
    {
        var presenter = new HighlightTextPresenter
        {
            Name = "PART_TextPresenter",
            Background = Brushes.Transparent,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Top,
        };
        // Mirror the Fluent TextBox template's PART_TextPresenter bindings (Text is TwoWay; the rest OneWay).
        Bind(presenter, TextPresenter.TextProperty, TextProperty, twoWay: true);
        Bind(presenter, TextPresenter.CaretBlinkIntervalProperty, CaretBlinkIntervalProperty);
        Bind(presenter, TextPresenter.CaretIndexProperty, CaretIndexProperty);
        Bind(presenter, TextPresenter.SelectionStartProperty, SelectionStartProperty);
        Bind(presenter, TextPresenter.SelectionEndProperty, SelectionEndProperty);
        Bind(presenter, TextPresenter.TextAlignmentProperty, TextAlignmentProperty);
        Bind(presenter, TextPresenter.TextWrappingProperty, TextWrappingProperty);
        Bind(presenter, TextPresenter.LineHeightProperty, LineHeightProperty);
        Bind(presenter, TextPresenter.LetterSpacingProperty, LetterSpacingProperty);
        Bind(presenter, TextPresenter.PasswordCharProperty, PasswordCharProperty);
        Bind(presenter, TextPresenter.RevealPasswordProperty, RevealPasswordProperty);
        Bind(presenter, TextPresenter.SelectionBrushProperty, SelectionBrushProperty);
        Bind(presenter, TextPresenter.SelectionForegroundBrushProperty, SelectionForegroundBrushProperty);
        Bind(presenter, TextPresenter.CaretBrushProperty, CaretBrushProperty);
        ns.Register("PART_TextPresenter", presenter);

        var scroll = new ScrollViewer { Name = "PART_ScrollViewer", Content = presenter };
        scroll[!ScrollViewer.HorizontalScrollBarVisibilityProperty] =
            new TemplateBinding(ScrollViewer.HorizontalScrollBarVisibilityProperty);
        scroll[!ScrollViewer.VerticalScrollBarVisibilityProperty] =
            new TemplateBinding(ScrollViewer.VerticalScrollBarVisibilityProperty);
        scroll[!ScrollViewer.PaddingProperty] = new TemplateBinding(PaddingProperty);
        ns.Register("PART_ScrollViewer", scroll);

        var border = new Border { Name = "PART_BorderElement", Child = scroll };
        border[!Border.BackgroundProperty] = new TemplateBinding(BackgroundProperty);
        border[!Border.BorderBrushProperty] = new TemplateBinding(BorderBrushProperty);
        border[!Border.BorderThicknessProperty] = new TemplateBinding(BorderThicknessProperty);
        border[!Border.CornerRadiusProperty] = new TemplateBinding(CornerRadiusProperty);
        ns.Register("PART_BorderElement", border);
        return border;
    });

    private static void Bind(AvaloniaObject target, AvaloniaProperty to, AvaloniaProperty from, bool twoWay = false)
    {
        var b = new TemplateBinding(from);
        if (twoWay) b.Mode = BindingMode.TwoWay;
        target[!to] = b;
    }
}
