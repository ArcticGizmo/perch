namespace Perch.Statusline;

using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

/// <summary>
/// The mustache-ish template engine that turns a statusline template string into coloured segments.
/// It is deliberately tiny and total — a malformed template renders as much as it can rather than
/// throwing, because the output feeds a status bar, not a compiler.
///
/// <para>Syntax:</para>
/// <list type="bullet">
/// <item><c>{{path.to.value}}</c> — interpolate a payload field (dotted path).</item>
/// <item><c>{{value | filter | filter:arg}}</c> — pipe through formatters, left to right.</item>
/// <item><c>{{#if EXPR}}…{{/if}}</c> / <c>{{#unless EXPR}}…{{/unless}}</c> — conditionals, where EXPR
///   is a truthy path or a comparison (<c>a &gt; 80</c>, <c>pr.review_state == 'approved'</c>).</item>
/// <item><c>{{#path}}…{{/path}}</c> — render the body when the path is truthy; <c>{{^path}}…{{/path}}</c>
///   renders it when falsy (inverted).</item>
/// <item><c>{{! comment }}</c> — dropped.</item>
/// </list>
///
/// <para>Filters: <c>money</c> (2dp), <c>round</c> (<c>round:N</c> for N decimals, default 0),
///   <c>pct</c> (rounded + "%"), <c>k</c> (1.2k),
/// <c>upper</c>/<c>lower</c>, <c>bar:N</c> (an N-cell block bar from a 0–100 percentage),
/// <c>trunc:N</c> (ellipsised), <c>default:X</c> (fallback when empty) and <c>color:NAME</c> (paints
/// the run — see <see cref="StatusColor"/>). Colour applies to just the token it decorates.</para>
/// </summary>
internal static class StatuslineTemplate
{
    private static readonly Regex Tag = new(@"\{\{([#/^!]?)([^}]*)\}\}", RegexOptions.Compiled);
    private static readonly Regex Comparison =
        new(@"^(.+?)\s*(==|!=|>=|<=|>|<)\s*(.+)$", RegexOptions.Compiled);

    /// <summary>Renders <paramref name="template"/> against <paramref name="data"/> into coloured
    /// segments.</summary>
    public static IReadOnlyList<StatuslineSegment> Render(string template, TemplateData data)
    {
        var (stripped, _) = StripSoftBreaks(template);
        var pieces = RenderPieces(Parse(Tokenize(stripped)), data);
        var segments = new List<StatuslineSegment>(pieces.Count);
        foreach (var p in pieces) segments.Add(new StatuslineSegment(p.Text, p.Color, p.Rgb));
        return segments;
    }

    /// <summary>Renders like <see cref="Render"/>, but keeps every rendered segment tagged with the
    /// <em>source span</em> of the template token it came from (in the ORIGINAL template's character
    /// coordinates, before soft-break stripping). The designer preview uses this to light up the element
    /// under the editor caret. Segments from a <c>{{…}}</c> token carry <see cref="PlacedSegment.IsTag"/>.</summary>
    public static IReadOnlyList<PlacedSegment> RenderPlaced(string template, TemplateData data)
    {
        var (stripped, map) = StripSoftBreaks(template);
        var pieces = RenderPieces(Parse(Tokenize(stripped)), data);
        var placed = new List<PlacedSegment>(pieces.Count);
        foreach (var p in pieces)
        {
            int a = p.SrcStart < 0 ? 0 : MapIndex(map, p.SrcStart);
            int b = p.SrcStart < 0 ? 0 : MapIndex(map, p.SrcStart + p.SrcLen);
            placed.Add(new PlacedSegment(new StatuslineSegment(p.Text, p.Color, p.Rgb), a, System.Math.Max(0, b - a), p.IsTag));
        }
        return placed;
    }

    /// <summary>Convenience: render straight to an ANSI (or plain) string.</summary>
    public static string RenderToString(string template, TemplateData data, bool color = true) =>
        StatuslineRenderer.ToAnsi(Render(template, data), color);

    // ── soft breaks ─────────────────────────────────────────────────────────────────────
    // A backslash immediately before a newline is a *soft break*: a readability wrap the editor inserts
    // (Shift+Enter) that must NOT split the rendered status line. We drop the "\<newline>" pair before
    // tokenising so the surrounding text joins seamlessly. The map records, for each surviving character
    // (and a trailing sentinel), its index in the ORIGINAL template, so RenderPlaced can report source
    // spans in original coordinates for caret hit-testing. The generated Node script mirrors this strip.
    private static (string Stripped, int[] Map) StripSoftBreaks(string t)
    {
        var sb = new StringBuilder(t.Length);
        var map = new List<int>(t.Length + 1);
        int i = 0;
        while (i < t.Length)
        {
            if (t[i] == '\\' && i + 1 < t.Length && t[i + 1] == '\r' && i + 2 < t.Length && t[i + 2] == '\n') { i += 3; continue; }
            if (t[i] == '\\' && i + 1 < t.Length && t[i + 1] == '\n') { i += 2; continue; }
            map.Add(i);
            sb.Append(t[i]);
            i++;
        }
        map.Add(t.Length);   // sentinel: end-of-stripped maps to end-of-original
        return (sb.ToString(), map.ToArray());
    }

    private static int MapIndex(int[] map, int strippedIndex) =>
        strippedIndex < 0 ? 0 : strippedIndex >= map.Length ? map[^1] : map[strippedIndex];

    /// <summary>The default glyph a bare <c>{{sep}}</c> renders (surrounded by single spaces). A
    /// <c>{{sep:X}}</c> uses <c>X</c> instead.</summary>
    private const string DefaultSepGlyph = "|";

    // Reads the display text of a {{sep}} / {{sep:glyph}} token — always " glyph " so the caller doesn't
    // need to add spacing. Returns null when the trimmed body isn't a separator token.
    private static string? SepText(string body)
    {
        if (body == "sep") return " " + DefaultSepGlyph + " ";
        if (body.StartsWith("sep:", System.StringComparison.Ordinal))
        {
            var glyph = body[4..].Trim();
            return glyph.Length == 0 ? " " + DefaultSepGlyph + " " : " " + glyph + " ";
        }
        return null;
    }

    // ── tokenising ─────────────────────────────────────────────────────────────────────
    private enum Kind { Text, Var, Open, Inverted, Close, Separator }
    // Start/Len are the token's span in the (soft-break-stripped) template — carried so RenderPlaced can
    // report which source characters produced each rendered segment.
    private readonly record struct Token(Kind Kind, string Value, int Start, int Len);

    private static List<Token> Tokenize(string tpl)
    {
        var toks = new List<Token>();
        int last = 0;
        foreach (Match m in Tag.Matches(tpl))
        {
            if (m.Index > last)
                toks.Add(new Token(Kind.Text, tpl[last..m.Index], last, m.Index - last));

            var sig = m.Groups[1].Value;
            var body = m.Groups[2].Value.Trim();
            switch (sig)
            {
                case "!": break;                                   // comment — dropped
                case "#": toks.Add(new Token(Kind.Open, body, m.Index, m.Length)); break;
                case "^": toks.Add(new Token(Kind.Inverted, body, m.Index, m.Length)); break;
                case "/": toks.Add(new Token(Kind.Close, body, m.Index, m.Length)); break;
                default:
                    // {{sep}} / {{sep:glyph}} is a smart separator (parsed before the var/filter split so a
                    // pipe glyph doesn't read as a filter); everything else is a value interpolation.
                    if (SepText(body) is { } sep) toks.Add(new Token(Kind.Separator, sep, m.Index, m.Length));
                    else toks.Add(new Token(Kind.Var, body, m.Index, m.Length));
                    break;
            }
            last = m.Index + m.Length;
        }
        if (last < tpl.Length)
            toks.Add(new Token(Kind.Text, tpl[last..], last, tpl.Length - last));
        return toks;
    }

    // ── parsing to a node tree ──────────────────────────────────────────────────────────
    private enum Mode { If, Unless, Truthy, InvertedTruthy }

    private abstract class Node { public int SrcStart = -1, SrcLen; }
    private sealed class TextNode : Node { public string Text = ""; }
    private sealed class VarNode : Node { public string Spec = ""; }
    private sealed class SepNode : Node { public string Text = ""; }   // a {{sep}} smart divider
    private sealed class SectionNode : Node
    {
        public Mode Mode;
        public string Expr = "";
        public List<Node> Children = new();
    }

    private static List<Node> Parse(List<Token> toks)
    {
        int i = 0;
        List<Node> Walk()
        {
            var nodes = new List<Node>();
            while (i < toks.Count)
            {
                var tk = toks[i];
                if (tk.Kind == Kind.Close) { i++; return nodes; }   // loose close (name not verified)
                if (tk.Kind == Kind.Open || tk.Kind == Kind.Inverted)
                {
                    i++;
                    var section = new SectionNode { Children = Walk(), SrcStart = tk.Start, SrcLen = tk.Len };
                    if (tk.Kind == Kind.Inverted) { section.Mode = Mode.InvertedTruthy; section.Expr = tk.Value; }
                    else if (tk.Value.StartsWith("if ", System.StringComparison.Ordinal))
                        { section.Mode = Mode.If; section.Expr = tk.Value[3..].Trim(); }
                    else if (tk.Value.StartsWith("unless ", System.StringComparison.Ordinal))
                        { section.Mode = Mode.Unless; section.Expr = tk.Value[7..].Trim(); }
                    else { section.Mode = Mode.Truthy; section.Expr = tk.Value; }
                    nodes.Add(section);
                }
                else if (tk.Kind == Kind.Var) { nodes.Add(new VarNode { Spec = tk.Value, SrcStart = tk.Start, SrcLen = tk.Len }); i++; }
                else if (tk.Kind == Kind.Separator) { nodes.Add(new SepNode { Text = tk.Value, SrcStart = tk.Start, SrcLen = tk.Len }); i++; }
                else { nodes.Add(new TextNode { Text = tk.Value, SrcStart = tk.Start, SrcLen = tk.Len }); i++; }
            }
            return nodes;
        }
        return Walk();
    }

    // ── rendering ─────────────────────────────────────────────────────────────────────
    // One rendered atom before it becomes a StatuslineSegment/PlacedSegment. Carrying the sep flag + source
    // span here lets Render and RenderPlaced share the same walk (and the same {{sep}} collapse), so the
    // preview, the ANSI string and the parity path can never diverge. IsTag = came from a {{…}} token.
    private readonly record struct Piece(string Text, StatusColor Color, int Rgb, bool IsSep, bool IsTag, int SrcStart, int SrcLen);

    private static List<Piece> RenderPieces(List<Node> nodes, TemplateData data)
    {
        var pieces = new List<Piece>();
        Walk(nodes);
        return CollapseSeps(pieces);

        void Walk(List<Node> ns)
        {
            foreach (var n in ns)
            {
                switch (n)
                {
                    case TextNode t:
                        if (t.Text.Length > 0)
                            pieces.Add(new Piece(t.Text, StatusColor.Default, -1, false, false, t.SrcStart, t.SrcLen));
                        break;
                    case VarNode v:
                        var (text, color, rgb) = ApplyVar(v.Spec, data);
                        if (text.Length > 0)
                            pieces.Add(new Piece(text, color, rgb, false, true, v.SrcStart, v.SrcLen));
                        break;
                    case SepNode sp:
                        pieces.Add(new Piece(sp.Text, StatusColor.Default, -1, true, true, sp.SrcStart, sp.SrcLen));
                        break;
                    case SectionNode s:
                        if (SectionActive(s, data)) Walk(s.Children);
                        break;
                }
            }
        }
    }

    // Smart-separator collapse: split the rendered pieces into cells at each {{sep}}, drop cells whose text
    // is empty/whitespace, and rejoin the surviving cells with a single separator between them. This makes a
    // {{sep}} vanish when the content on either side renders nothing — no leading, trailing or doubled
    // dividers (the classic "aaaa |  | bbbb" problem). Templates with no {{sep}} pass through unchanged.
    private static List<Piece> CollapseSeps(List<Piece> pieces)
    {
        bool hasSep = false;
        foreach (var p in pieces) if (p.IsSep) { hasSep = true; break; }
        if (!hasSep) return pieces;

        var cells = new List<List<Piece>> { new() };
        var seps = new List<Piece>();
        foreach (var p in pieces)
        {
            if (p.IsSep) { seps.Add(p); cells.Add(new List<Piece>()); }
            else cells[^1].Add(p);
        }

        static bool NonEmpty(List<Piece> cell)
        {
            foreach (var x in cell) if (!string.IsNullOrWhiteSpace(x.Text)) return true;
            return false;
        }

        var outp = new List<Piece>();
        bool any = false;
        for (int i = 0; i < cells.Count; i++)
        {
            if (!NonEmpty(cells[i])) continue;
            if (any) outp.Add(seps[i - 1]);   // the separator immediately preceding this surviving cell
            outp.AddRange(cells[i]);
            any = true;
        }
        return outp;
    }

    private static bool SectionActive(SectionNode s, TemplateData data) => s.Mode switch
    {
        Mode.If             => EvalExpr(s.Expr, data),
        Mode.Unless         => !EvalExpr(s.Expr, data),
        Mode.Truthy         => TruthyPath(s.Expr, data),
        Mode.InvertedTruthy => !TruthyPath(s.Expr, data),
        _                   => false,
    };

    private static bool TruthyPath(string path, TemplateData data) =>
        data.TryGet(path.Trim(), out var node) && TemplateData.Truthy(node);

    private static bool EvalExpr(string expr, TemplateData data)
    {
        expr = expr.Trim();
        var m = Comparison.Match(expr);
        if (!m.Success) return TruthyPath(expr, data);

        data.TryGet(m.Groups[1].Value.Trim(), out var left);
        var op = m.Groups[2].Value;
        var rhs = m.Groups[3].Value.Trim();

        // numeric compare when both sides look numeric; otherwise string equality
        var ln = TemplateData.Num(left);
        double rn;
        bool rhsNumeric = double.TryParse(rhs, NumberStyles.Any, CultureInfo.InvariantCulture, out rn);
        if (ln is { } l && rhsNumeric)
        {
            return op switch
            {
                "==" => l == rn, "!=" => l != rn,
                ">"  => l > rn,  "<"  => l < rn,
                ">=" => l >= rn, "<=" => l <= rn,
                _ => false,
            };
        }

        var ls = TemplateData.Str(left);
        var rs = Unquote(rhs);
        return op switch { "==" => ls == rs, "!=" => ls != rs, _ => false };
    }

    private static string Unquote(string s) =>
        s.Length >= 2 && ((s[0] == '\'' && s[^1] == '\'') || (s[0] == '"' && s[^1] == '"'))
            ? s[1..^1] : s;

    // ── the {{value|filters}} pipeline ──────────────────────────────────────────────────
    private static (string Text, StatusColor Color, int Rgb) ApplyVar(string spec, TemplateData data)
    {
        var parts = spec.Split('|');
        var found = data.TryGet(parts[0].Trim(), out var node);
        var text = found ? TemplateData.Str(node) : "";
        var num = found ? TemplateData.Num(node) : null;
        var color = StatusColor.Default;
        var rgb = -1;   // a custom truecolor from color:#rrggbb; -1 = none (the named `color` applies)

        for (int i = 1; i < parts.Length; i++)
        {
            var f = parts[i].Trim();
            if (f.Length == 0) continue;
            var colon = f.IndexOf(':');
            var name = (colon < 0 ? f : f[..colon]).Trim();
            var arg = colon < 0 ? null : f[(colon + 1)..].Trim();

            switch (name)
            {
                case "money": text = (num ?? 0).ToString("0.00", CultureInfo.InvariantCulture); break;
                case "round": text = RoundFixed(num ?? 0, ParseInt(arg, 0)); break;
                case "pct":   text = Round(num ?? 0).ToString("0", CultureInfo.InvariantCulture) + "%"; break;
                case "k":     text = FmtK(num ?? 0); break;
                case "upper": text = text.ToUpperInvariant(); break;
                case "lower": text = text.ToLowerInvariant(); break;
                case "bar":   text = Bar(num ?? 0, ParseInt(arg, 10)); break;
                case "trunc": text = Trunc(text, ParseInt(arg, 20)); break;
                case "human": text = Human(num ?? 0); break;
                case "dur":   text = Dur(num ?? 0); break;
                case "until": text = Until(num); break;
                case "default": if (text.Length == 0) text = arg ?? ""; break;
                case "color":
                    // A hex arg (#rrggbb / rrggbb / #rgb) is a custom truecolor; anything else is a named
                    // role. Setting one clears the other so the last color: filter wins.
                    var hex = StatusColors.ParseHex(arg);
                    if (hex >= 0) { rgb = hex; }
                    else { color = StatusColors.Parse(arg); rgb = -1; }
                    break;
                case "pace":  color = Pace(num ?? 0, arg, data); rgb = -1; break;
            }
        }
        return (text, color, rgb);
    }

    private static int ParseInt(string? s, int fallback) =>
        int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) && v > 0 ? v : fallback;

    private static string FmtK(double n) =>
        System.Math.Abs(n) >= 1000
            ? (n / 1000).ToString(System.Math.Abs(n) >= 10000 ? "0" : "0.0", CultureInfo.InvariantCulture) + "k"
            : ((long)n).ToString(CultureInfo.InvariantCulture);

    // Round half away from zero, matching JavaScript's Math.round for the (non-negative) values a
    // status line deals in — so the C# preview and the generated Node script agree.
    private static double Round(double v) => System.Math.Round(v, System.MidpointRounding.AwayFromZero);

    // Round to `places` decimals (half up, like Round above), trimming trailing zeros — so {{x|round}} is a
    // whole number (places 0, the default) and {{x|round:2}} keeps up to two decimals. Formatted from
    // integer/string ops, NOT double.ToString, so the C# preview and the generated Node script emit
    // identical bytes regardless of each platform's float-to-string rounding. Mirrored by roundFixed() in
    // StatuslineScript's Node body.
    internal static string RoundFixed(double value, int places)
    {
        if (places < 0) places = 0;
        if (places > 15) places = 15;   // past double's precision; clamp so the scale stays sane
        bool neg = value < 0;
        double scale = System.Math.Pow(10, places);
        long r = (long)System.Math.Floor(System.Math.Abs(value) * scale + 0.5);   // half up
        var digits = r.ToString(CultureInfo.InvariantCulture);
        string text;
        if (places == 0)
            text = digits;
        else
        {
            if (digits.Length <= places) digits = new string('0', places - digits.Length + 1) + digits;
            int dot = digits.Length - places;
            var frac = digits[dot..].TrimEnd('0');
            text = frac.Length == 0 ? digits[..dot] : digits[..dot] + "." + frac;
        }
        return neg && text != "0" ? "-" + text : text;
    }

    private static string Bar(double pct, int cells)
    {
        int filled = (int)Round(System.Math.Clamp(pct, 0, 100) / 100.0 * cells);
        filled = System.Math.Clamp(filled, 0, cells);
        return new string('█', filled) + new string('░', cells - filled);
    }

    private static string Trunc(string s, int max) =>
        s.Length <= max ? s : (max <= 1 ? s[..max] : s[..(max - 1)] + "…");

    // Humanise a count the way a status line does: 999 -> "999", 68000 -> "68k", 2_500_000 -> "2M".
    private static string Human(double n)
    {
        if (n < 1000) return ((long)System.Math.Floor(n)).ToString(CultureInfo.InvariantCulture);
        if (n < 1_000_000) return ((long)System.Math.Floor(n / 1000)).ToString(CultureInfo.InvariantCulture) + "k";
        return ((long)System.Math.Floor(n / 1_000_000)).ToString(CultureInfo.InvariantCulture) + "M";
    }

    // Humanise a millisecond duration: 850 -> "850ms", 45000 -> "45s", 300000 -> "5m", 4_500_000 -> "1h 15m".
    private static string Dur(double ms)
    {
        if (ms < 1000) return ((long)System.Math.Floor(ms)).ToString(CultureInfo.InvariantCulture) + "ms";
        long s = (long)System.Math.Floor(ms / 1000);
        if (s < 60) return s.ToString(CultureInfo.InvariantCulture) + "s";
        if (s < 3600) return (s / 60).ToString(CultureInfo.InvariantCulture) + "m";
        return (s / 3600).ToString(CultureInfo.InvariantCulture) + "h " + ((s % 3600) / 60).ToString(CultureInfo.InvariantCulture) + "m";
    }

    // Time remaining until a unix-epoch-seconds reset, as "1h 30m" / "12m". Empty for an absent value or a
    // reset already in the past (clamped to 0). Reads the wall clock — the one non-deterministic filter.
    private static string Until(double? epochSeconds)
    {
        if (epochSeconds is not { } epoch) return "";
        long m = (long)System.Math.Floor((epoch - UnixNow()) / 60);
        if (m < 0) m = 0;
        return m >= 60
            ? (m / 60).ToString(CultureInfo.InvariantCulture) + "h " + (m % 60).ToString(CultureInfo.InvariantCulture) + "m"
            : m.ToString(CultureInfo.InvariantCulture) + "m";
    }

    // Colour a rate-limit reading by pace: compares the actual used-percentage against the percentage
    // *expected* by now (how far through the window we are), using the same thresholds as the overlay's
    // usage bars (Palette.PaceColor). <paramref name="arg"/> is "<resetsPath>:<windowSeconds>". Returns
    // Default (uncoloured) when the reset time isn't available, so it degrades gracefully.
    private static StatusColor Pace(double actual, string? arg, TemplateData data)
    {
        if (arg is null) return StatusColor.Default;
        int cut = arg.LastIndexOf(':');
        if (cut < 0) return StatusColor.Default;
        if (!int.TryParse(arg[(cut + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var window) || window <= 0)
            return StatusColor.Default;
        if (!data.TryGet(arg[..cut], out var node) || TemplateData.Num(node) is not { } resets)
            return StatusColor.Default;

        double remaining = resets - UnixNow();
        double expected = System.Math.Clamp((window - remaining) / window * 100.0, 0, 100);
        return PaceColor(actual, expected);
    }

    // Mirrors Perch.Theming.Palette.PaceColor: green well behind or barely-started, yellow around pace,
    // red over. Kept here (not referencing the UI Palette) so Perch.Core stays UI-free and the generated
    // Node script can port the exact same rule.
    internal static StatusColor PaceColor(double actual, double expected)
    {
        if (expected < 15) return StatusColor.Green;   // safe zone while the window has barely begun
        double d = actual - expected;
        if (d < -10) return StatusColor.Green;         // more than 10 points behind the expected pace
        if (d <= 1) return StatusColor.Yellow;         // from 10 under up to 1 over
        return StatusColor.Red;                        // more than 1 point over
    }

    private static double UnixNow() => System.DateTimeOffset.UtcNow.ToUnixTimeSeconds();
}
