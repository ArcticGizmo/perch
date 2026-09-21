namespace Perch.Statusline;

using System.Collections.Generic;
using System.Globalization;
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
/// <para>Filters: <c>money</c> (2dp), <c>round</c>, <c>pct</c> (rounded + "%"), <c>k</c> (1.2k),
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
        var nodes = Parse(Tokenize(template));
        var segments = new List<StatuslineSegment>();
        RenderNodes(nodes, data, segments);
        return segments;
    }

    /// <summary>Convenience: render straight to an ANSI (or plain) string.</summary>
    public static string RenderToString(string template, TemplateData data, bool color = true) =>
        StatuslineRenderer.ToAnsi(Render(template, data), color);

    // ── tokenising ─────────────────────────────────────────────────────────────────────
    private enum Kind { Text, Var, Open, Inverted, Close }
    private readonly record struct Token(Kind Kind, string Value);

    private static List<Token> Tokenize(string tpl)
    {
        var toks = new List<Token>();
        int last = 0;
        foreach (Match m in Tag.Matches(tpl))
        {
            if (m.Index > last)
                toks.Add(new Token(Kind.Text, tpl[last..m.Index]));

            var sig = m.Groups[1].Value;
            var body = m.Groups[2].Value.Trim();
            switch (sig)
            {
                case "!": break;                                   // comment — dropped
                case "#": toks.Add(new Token(Kind.Open, body)); break;
                case "^": toks.Add(new Token(Kind.Inverted, body)); break;
                case "/": toks.Add(new Token(Kind.Close, body)); break;
                default:  toks.Add(new Token(Kind.Var, body)); break;
            }
            last = m.Index + m.Length;
        }
        if (last < tpl.Length)
            toks.Add(new Token(Kind.Text, tpl[last..]));
        return toks;
    }

    // ── parsing to a node tree ──────────────────────────────────────────────────────────
    private enum Mode { If, Unless, Truthy, InvertedTruthy }

    private abstract class Node { }
    private sealed class TextNode : Node { public string Text = ""; }
    private sealed class VarNode : Node { public string Spec = ""; }
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
                    var section = new SectionNode { Children = Walk() };
                    if (tk.Kind == Kind.Inverted) { section.Mode = Mode.InvertedTruthy; section.Expr = tk.Value; }
                    else if (tk.Value.StartsWith("if ", System.StringComparison.Ordinal))
                        { section.Mode = Mode.If; section.Expr = tk.Value[3..].Trim(); }
                    else if (tk.Value.StartsWith("unless ", System.StringComparison.Ordinal))
                        { section.Mode = Mode.Unless; section.Expr = tk.Value[7..].Trim(); }
                    else { section.Mode = Mode.Truthy; section.Expr = tk.Value; }
                    nodes.Add(section);
                }
                else if (tk.Kind == Kind.Var) { nodes.Add(new VarNode { Spec = tk.Value }); i++; }
                else { nodes.Add(new TextNode { Text = tk.Value }); i++; }
            }
            return nodes;
        }
        return Walk();
    }

    // ── rendering ─────────────────────────────────────────────────────────────────────
    private static void RenderNodes(List<Node> nodes, TemplateData data, List<StatuslineSegment> outp)
    {
        foreach (var n in nodes)
        {
            switch (n)
            {
                case TextNode t:
                    Add(outp, t.Text, StatusColor.Default);
                    break;
                case VarNode v:
                    var (text, color) = ApplyVar(v.Spec, data);
                    if (text.Length > 0) Add(outp, text, color);
                    break;
                case SectionNode s:
                    if (SectionActive(s, data)) RenderNodes(s.Children, data, outp);
                    break;
            }
        }
    }

    // One segment per node (no coalescing): keeps the ANSI byte stream identical to the generated
    // standalone Node script, which wraps each token separately — so the parity test can diff them.
    private static void Add(List<StatuslineSegment> outp, string text, StatusColor color)
    {
        if (text.Length == 0) return;
        outp.Add(new StatuslineSegment(text, color));
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
    private static (string Text, StatusColor Color) ApplyVar(string spec, TemplateData data)
    {
        var parts = spec.Split('|');
        var found = data.TryGet(parts[0].Trim(), out var node);
        var text = found ? TemplateData.Str(node) : "";
        var num = found ? TemplateData.Num(node) : null;
        var color = StatusColor.Default;

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
                case "round": text = Round(num ?? 0).ToString("0", CultureInfo.InvariantCulture); break;
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
                case "color": color = StatusColors.Parse(arg); break;
                case "pace":  color = Pace(num ?? 0, arg, data); break;
            }
        }
        return (text, color);
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
