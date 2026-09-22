using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Utilities;
using Perch.Statusline;

namespace Perch.Avalonia.Rendering;

/// <summary>
/// Syntax highlighter for statusline <em>template</em> source, feeding the shared
/// <see cref="HighlightTextBox"/> (which colours a TextBox by injecting these spans straight into its
/// text layout, so the colouring can never drift from the caret/selection geometry). It paints a faint
/// background behind each <c>{{…}}</c> block and colours the pieces inside: the <c>{{ }}</c>/<c>|</c>/<c>:</c>
/// delimiters, the <c>#if</c>/<c>#unless</c>/<c>^</c> keywords, the field path, the filter names, and the
/// filter args (a <c>color:</c> arg is drawn in the colour it names). Pure and side-effect-free.
/// </summary>
internal static class StatuslineSourceHighlighter
{
    private const int MaxLength = 40_000;

    private static SolidColorBrush B(byte r, byte g, byte b) => new(Color.FromRgb(r, g, b));

    private static readonly IBrush BaseFg  = B(0xc9, 0xd3, 0xe0);   // matches the terminal preview foreground
    private static readonly IBrush Delim   = B(0x8b, 0x95, 0xa6);   // {{ }} | : and the muted punctuation
    private static readonly IBrush Keyword = B(0xb0, 0x85, 0xe0);   // #if / #unless / ^ / /
    private static readonly IBrush Var     = B(0x46, 0xc6, 0xb8);   // the field path
    private static readonly IBrush Filter  = B(0xe3, 0xa8, 0x4e);   // filter names
    private static readonly IBrush Arg     = B(0x8f, 0xbf, 0x7f);   // filter args
    private static readonly IBrush BlockBg = new SolidColorBrush(Color.FromArgb(0x26, 0x7f, 0x8a, 0xa0));

    public static IReadOnlyList<ValueSpan<TextRunProperties>>? Highlight(string text, Typeface face, double fontSize)
    {
        int n = text.Length;
        if (n == 0 || n > MaxLength) return null;

        var fg = new IBrush?[n];
        var bg = new IBrush?[n];
        void Paint(int start, int len, IBrush? f, IBrush? b)
        {
            int end = Math.Min(start + len, n);
            for (int i = Math.Max(0, start); i < end; i++) { if (f is not null) fg[i] = f; if (b is not null) bg[i] = b; }
        }

        int i = 0;
        while (i < n)
        {
            int open = text.IndexOf("{{", i, StringComparison.Ordinal);
            if (open < 0) break;
            int close = text.IndexOf("}}", open + 2, StringComparison.Ordinal);
            int contentEnd = close < 0 ? n : close;         // an unclosed trailing {{ (mid-type) still highlights
            int blockEnd = close < 0 ? n : close + 2;

            Paint(open, blockEnd - open, null, BlockBg);      // faint block wash over the whole {{ … }}
            Paint(open, 2, Delim, null);                      // {{
            if (close >= 0) Paint(close, 2, Delim, null);     // }}
            ColorInside(text, open + 2, contentEnd, Paint);
            i = blockEnd;
        }

        return Coalesce(fg, bg, face, fontSize);
    }

    // Colour the content between {{ and }}: an optional sigil (#/^///!), then the head (a path, or an
    // if/unless expression), then any | filters.
    private static void ColorInside(string t, int s, int e, Action<int, int, IBrush?, IBrush?> Paint)
    {
        int p = s;
        while (p < e && char.IsWhiteSpace(t[p])) p++;
        if (p < e && (t[p] == '#' || t[p] == '^' || t[p] == '/' || t[p] == '!'))
        {
            Paint(p, 1, Keyword, null);
            char sig = t[p];
            p++;
            if (sig == '!') { Paint(p, e - p, Delim, null); return; }   // {{! comment }}
        }

        int bar = IndexOfIn(t, '|', p, e);
        int headEnd = bar < 0 ? e : bar;
        ColorHead(t, p, headEnd, Paint);

        int q = headEnd;
        while (q < e && t[q] == '|')
        {
            Paint(q, 1, Delim, null);
            q++;
            int nb = IndexOfIn(t, '|', q, e);
            int fe = nb < 0 ? e : nb;
            ColorFilter(t, q, fe, Paint);
            q = fe;
        }
    }

    private static void ColorHead(string t, int a, int b, Action<int, int, IBrush?, IBrush?> Paint)
    {
        int k = a;
        while (k < b && char.IsWhiteSpace(t[k])) k++;
        int w = k;
        while (w < b && char.IsLetter(t[w])) w++;
        var kw = t[k..w];
        if (kw is "if" or "unless") { Paint(k, w - k, Keyword, null); Paint(w, b - w, Var, null); }
        else Paint(a, b - a, Var, null);
    }

    private static void ColorFilter(string t, int a, int b, Action<int, int, IBrush?, IBrush?> Paint)
    {
        int k = a;
        while (k < b && char.IsWhiteSpace(t[k])) k++;
        int colon = IndexOfIn(t, ':', k, b);
        int nameEnd = colon < 0 ? b : colon;
        Paint(k, nameEnd - k, Filter, null);
        if (colon < 0) return;

        Paint(colon, 1, Delim, null);
        var argBrush = Arg;
        if (t[k..nameEnd].Trim().Equals("color", StringComparison.OrdinalIgnoreCase))
        {
            var role = StatusColors.Parse(t[(colon + 1)..b].Trim());
            if (role != StatusColor.Default) { var (r, g, bl) = StatusColors.Rgb(role); argBrush = B(r, g, bl); }
        }
        Paint(colon + 1, b - (colon + 1), argBrush, null);
    }

    private static int IndexOfIn(string t, char c, int a, int b)
    {
        for (int i = a; i < b; i++) if (t[i] == c) return i;
        return -1;
    }

    // Coalesce the per-char maps into ordered, non-overlapping runs wherever a colour or background was set.
    private static List<ValueSpan<TextRunProperties>> Coalesce(IBrush?[] fg, IBrush?[] bg, Typeface face, double fontSize)
    {
        var runs = new List<ValueSpan<TextRunProperties>>();
        int n = fg.Length, i = 0;
        while (i < n)
        {
            var f = fg[i]; var b = bg[i];
            if (f is null && b is null) { i++; continue; }
            int j = i + 1;
            while (j < n && ReferenceEquals(fg[j], f) && ReferenceEquals(bg[j], b)) j++;
            runs.Add(new ValueSpan<TextRunProperties>(i, j - i,
                new GenericTextRunProperties(face, fontRenderingEmSize: fontSize, foregroundBrush: f ?? BaseFg, backgroundBrush: b)));
            i = j;
        }
        return runs;
    }
}
