namespace Perch.Data.Mermaid;

using System.Net;
using System.Text.RegularExpressions;

/// <summary>
/// Parses the source of a <c>```mermaid</c> block. Only flowcharts (<c>graph</c>/<c>flowchart</c>) and sequence
/// diagrams are understood; any other diagram type comes back with no diagram and its header keyword, so the preview
/// can show the source and say why. Parsing is best-effort, as the Markdown preview is everywhere else: a statement it
/// can't read is skipped rather than failing the whole diagram, and styling statements (<c>classDef</c>,
/// <c>style</c>, <c>click</c>, …) are ignored because the diagram takes its colours from the preview's theme.
/// </summary>
internal static partial class MermaidParser
{
    public static MermaidParse Parse(string source)
    {
        try { return ParseCore(source ?? ""); }
        catch (Exception ex) { return new MermaidParse(null, "", ex.Message); }
    }

    private static MermaidParse ParseCore(string source)
    {
        var lines = Preprocess(source);
        int first = lines.FindIndex(l => l.Length > 0);
        if (first < 0) return new MermaidParse(null, "", "The diagram is empty.");

        var header = lines[first];
        var keyword = header.Split((char[])[' ', '\t', ';'], 2)[0];
        var body = lines.Skip(first + 1).ToList();
        switch (keyword)
        {
            case "graph" or "flowchart" or "flowchart-elk":
                // A one-liner (`graph LR;A-->B`) carries statements after the direction.
                var head = header[keyword.Length..];
                int semi = head.IndexOf(';');
                if (semi >= 0)
                {
                    body.Insert(0, head[(semi + 1)..]);
                    head = head[..semi];
                }
                var fc = ParseFlowchart(head.Trim(), body);
                return fc.Nodes.Count == 0
                    ? new MermaidParse(null, keyword, "No nodes found.")
                    : new MermaidParse(fc, keyword, null);
            case "erDiagram":
                var er = ParseEr(body);
                return er.Entities.Count == 0
                    ? new MermaidParse(null, keyword, "No entities found.")
                    : new MermaidParse(er, keyword, null);
            case "sequenceDiagram":
                var seq = ParseSequence(body);
                return seq.Participants.Count == 0
                    ? new MermaidParse(null, keyword, "No participants found.")
                    : new MermaidParse(seq, keyword, null);
            default:
                return new MermaidParse(null, keyword, null);
        }
    }

    // Strips a leading `---` config/title block, `%%{init}%%` directives and `%%` comments, and trims each line.
    private static List<string> Preprocess(string source)
    {
        var lines = source.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').ToList();
        int start = 0;
        while (start < lines.Count && lines[start].Trim().Length == 0) start++;
        if (start < lines.Count && lines[start].Trim() == "---")
        {
            int close = lines.FindIndex(start + 1, l => l.Trim() == "---");
            if (close > 0) start = close + 1;
        }
        var outp = new List<string>();
        for (int i = start; i < lines.Count; i++)
        {
            var l = lines[i].Trim();
            if (l.StartsWith("%%", StringComparison.Ordinal)) l = "";
            outp.Add(l);
        }
        return outp;
    }

    // ── Flowchart ──────────────────────────────────────────────────────────────────────────────────────

    private static MermaidFlowchart ParseFlowchart(string direction, List<string> body)
    {
        var fc = new MermaidFlowchart { Direction = ParseDirection(direction) };
        var stack = new Stack<FlowSubgraph>();
        int anon = 0;

        foreach (var line in body)
            foreach (var raw in SplitStatements(line))
            {
                var st = raw.Trim();
                if (st.Length == 0) continue;
                var word = FirstWord(st);
                switch (word)
                {
                    case "subgraph":
                    {
                        var (id, title) = SubgraphHeader(st["subgraph".Length..].Trim(), ref anon);
                        var sg = new FlowSubgraph(id, title, stack.Count > 0 ? stack.Peek() : null);
                        fc.Subgraphs.Add(sg);
                        stack.Push(sg);
                        continue;
                    }
                    case "end":
                        if (stack.Count > 0) stack.Pop();
                        continue;
                    case "direction" or "classDef" or "class" or "style" or "linkStyle" or "click" or "accTitle"
                        or "accDescr" or "title":
                        continue;
                }
                new FlowStatement(fc, st, stack.Count > 0 ? stack.Peek() : null).Parse();
            }

        // A bare reference that names a subgraph is an edge to that subgraph, not a node of its own.
        fc.Nodes.RemoveAll(n => !n.Explicit && fc.Subgraphs.Any(s => s.Id == n.Id));
        return fc;
    }

    private static FlowDirection ParseDirection(string d) => d.ToUpperInvariant() switch
    {
        "LR" => FlowDirection.LeftRight,
        "RL" => FlowDirection.RightLeft,
        "BT" => FlowDirection.BottomUp,
        _ => FlowDirection.TopDown,   // TB, TD, or none
    };

    private static string FirstWord(string st)
    {
        int i = 0;
        while (i < st.Length && !char.IsWhiteSpace(st[i])) i++;
        return st[..i];
    }

    // `subgraph id [title]`, `subgraph id["title"]`, `subgraph "title"`, `subgraph title words` or `subgraph id`.
    private static (string Id, string Title) SubgraphHeader(string rest, ref int anon)
    {
        var m = SubgraphIdTitle().Match(rest);
        if (m.Success) return (m.Groups["id"].Value, CleanLabel(m.Groups["t"].Value));
        if (rest.Length == 0) return ($"subgraph{++anon}", "");
        var title = CleanLabel(rest);
        return (rest.StartsWith('"') ? $"subgraph{++anon}" : rest, title);
    }

    [GeneratedRegex(@"^(?<id>[^\s\[""]+)\s*\[(?<t>.*)\]$")]
    private static partial Regex SubgraphIdTitle();

    // Splits a line on `;` outside quotes and brackets, so a `;` inside a label survives.
    private static IEnumerable<string> SplitStatements(string line)
    {
        int depth = 0, start = 0;
        bool quoted = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (c == '"') quoted = !quoted;
            else if (quoted) continue;
            else if (c is '[' or '(' or '{') depth++;
            else if (c is ']' or ')' or '}') depth = Math.Max(0, depth - 1);
            else if (c == ';' && depth == 0)
            {
                yield return line[start..i];
                start = i + 1;
            }
        }
        yield return line[start..];
    }

    /// <summary>Turns a label as written into the text to show: unquoted, markdown-string backticks dropped,
    /// <c>&lt;br&gt;</c> as a line break, other tags stripped, and entities (<c>#quot;</c>, <c>&amp;amp;</c>) decoded.</summary>
    internal static string CleanLabel(string s)
    {
        s = s.Trim();
        if (s.Length >= 2 && s[0] == '"' && s[^1] == '"') s = s[1..^1];
        if (s.Length >= 2 && s[0] == '`' && s[^1] == '`') s = s[1..^1];
        s = LineBreakTag().Replace(s, "\n").Replace("\\n", "\n");
        s = OtherTag().Replace(s, "");
        s = FontAwesome().Replace(s, "");
        s = MermaidEntity().Replace(s, m =>
            int.TryParse(m.Groups[1].Value, out var code) ? char.ConvertFromUtf32(code) : $"&{m.Groups[1].Value};");
        s = WebUtility.HtmlDecode(s);
        return string.Join('\n', s.Split('\n').Select(l => l.Trim())).Trim();
    }

    [GeneratedRegex(@"<br\s*/?>", RegexOptions.IgnoreCase)]
    private static partial Regex LineBreakTag();

    [GeneratedRegex(@"</?[a-zA-Z][^<>]*>")]
    private static partial Regex OtherTag();

    [GeneratedRegex(@"fa[bsrl]?:fa-[\w-]+\s*")]
    private static partial Regex FontAwesome();

    // Mermaid's own entity escapes: `#quot;`, `#35;`.
    [GeneratedRegex(@"#(\w+);")]
    private static partial Regex MermaidEntity();

    /// <summary>One flowchart statement: a node definition, or a chain of node groups joined by links
    /// (<c>A &amp; B --> C -.->|why| D</c>).</summary>
    private sealed partial class FlowStatement(MermaidFlowchart fc, string s, FlowSubgraph? subgraph)
    {
        private int _i;

        private sealed record Link(MermaidStroke Stroke, MermaidHead Start, MermaidHead End, int Length, string? Label);

        public void Parse()
        {
            var left = Group();
            if (left is null) return;
            while (true)
            {
                SkipWs();
                if (_i >= s.Length) return;
                var link = ReadLink();
                if (link is null) return;   // unreadable remainder: keep what parsed so far
                SkipWs();
                var right = Group();
                if (right is null) return;
                foreach (var a in left)
                    foreach (var b in right)
                        fc.Edges.Add(new FlowEdge(a, b, link.Label, link.Stroke, link.Start, link.End, link.Length));
                left = right;
            }
        }

        private void SkipWs()
        {
            while (_i < s.Length && char.IsWhiteSpace(s[_i])) _i++;
        }

        // `A`, or `A & B & C`.
        private List<string>? Group()
        {
            var ids = new List<string>();
            while (true)
            {
                SkipWs();
                var id = Node();
                if (id is null) return ids.Count > 0 ? ids : null;
                ids.Add(id);
                int save = _i;
                SkipWs();
                if (_i < s.Length && s[_i] == '&') { _i++; continue; }
                _i = save;
                return ids;
            }
        }

        private string? Node()
        {
            int start = _i;
            while (_i < s.Length)
            {
                char c = s[_i];
                if (char.IsLetterOrDigit(c) || c == '_') { _i++; continue; }
                // A hyphen belongs to the id only between word characters (`node-1`), so `A-->B` still reads as a link.
                if (c == '-' && _i > start && _i + 1 < s.Length && char.IsLetterOrDigit(s[_i + 1])) { _i++; continue; }
                break;
            }
            if (_i == start) return null;
            var id = s[start.._i];

            var (label, shape) = Shape();
            SkipClass();

            var node = fc.Find(id);
            if (node is null)
            {
                node = new FlowNode(id, label ?? id, shape ?? FlowShape.Rect);
                fc.Nodes.Add(node);
            }
            if (label is not null || shape is not null)
            {
                node.Label = label ?? node.Label;
                node.Shape = shape ?? node.Shape;
                node.Explicit = true;
            }
            if (subgraph is not null && node.Subgraph is null) node.Subgraph = subgraph;
            return id;
        }

        private void SkipClass()
        {
            if (_i + 2 < s.Length && s[_i] == ':' && s[_i + 1] == ':' && s[_i + 2] == ':')
            {
                _i += 3;
                // Same hyphen rule as ids, so `A:::hot-->B` leaves the link alone.
                while (_i < s.Length && (char.IsLetterOrDigit(s[_i]) || s[_i] == '_'
                       || s[_i] == '-' && _i + 1 < s.Length && char.IsLetterOrDigit(s[_i + 1])))
                    _i++;
            }
        }

        // The shape opener/closer pairs, longest opener first so `((` wins over `(`.
        private static readonly (string Open, string Close, FlowShape Shape)[] Shapes =
        [
            ("(((", ")))", FlowShape.DoubleCircle),
            ("((", "))", FlowShape.Circle),
            ("([", "])", FlowShape.Stadium),
            ("[[", "]]", FlowShape.Subroutine),
            ("[(", ")]", FlowShape.Cylinder),
            ("{{", "}}", FlowShape.Hexagon),
            ("[/", "/]", FlowShape.Parallelogram),
            ("[/", "\\]", FlowShape.Trapezoid),
            ("[\\", "\\]", FlowShape.ParallelogramAlt),
            ("[\\", "/]", FlowShape.TrapezoidAlt),
            ("[", "]", FlowShape.Rect),
            ("(", ")", FlowShape.Round),
            ("{", "}", FlowShape.Rhombus),
            (">", "]", FlowShape.Asymmetric),
        ];

        private (string? Label, FlowShape? Shape) Shape()
        {
            if (_i >= s.Length) return (null, null);
            if (s[_i] == '@' && _i + 1 < s.Length && s[_i + 1] == '{') return ShapeData();

            // Several shapes share an opener (`[/` closes as `/]` or `\]`): take the closer that comes first.
            (string? Label, FlowShape? Shape, int End) best = (null, null, int.MaxValue);
            string? opener = null;
            foreach (var (open, close, shape) in Shapes)
            {
                if (opener is not null && open != opener) break;
                if (string.CompareOrdinal(s, _i, open, 0, open.Length) != 0) continue;
                opener = open;
                int from = _i + open.Length;
                int textEnd = FindClose(from, close);
                if (textEnd >= 0 && textEnd < best.End)
                    best = (CleanLabel(s[from..textEnd]), shape, textEnd + close.Length);
            }
            if (best.Shape is null) return (null, null);
            _i = best.End;
            return (best.Label, best.Shape);
        }

        // The index where `close` ends the label starting at `from`, skipping over a quoted label.
        private int FindClose(int from, string close)
        {
            int p = from;
            while (p < s.Length && char.IsWhiteSpace(s[p])) p++;
            if (p < s.Length && s[p] == '"')
            {
                int q = s.IndexOf('"', p + 1);
                if (q > 0) p = q + 1;
            }
            else p = from;
            return s.IndexOf(close, p, StringComparison.Ordinal);
        }

        // The newer `A@{ shape: diamond, label: "Decide" }` form.
        private (string? Label, FlowShape? Shape) ShapeData()
        {
            int close = s.IndexOf('}', _i);
            if (close < 0) return (null, null);
            var body = s[(_i + 2)..close];
            _i = close + 1;
            string? label = null;
            FlowShape shape = FlowShape.Rect;
            foreach (Match m in ShapeDataPair().Matches(body))
            {
                var key = m.Groups["k"].Value;
                var val = m.Groups["v"].Value.Trim();
                if (key == "label") label = CleanLabel(val);
                else if (key == "shape") shape = NamedShape(val.Trim('"'));
            }
            return (label, shape);
        }

        [GeneratedRegex(@"(?<k>\w+)\s*:\s*(?<v>""[^""]*""|[^,]+)")]
        private static partial Regex ShapeDataPair();

        private static FlowShape NamedShape(string name) => name switch
        {
            "rounded" or "event" => FlowShape.Round,
            "stadium" or "pill" or "terminal" => FlowShape.Stadium,
            "subroutine" or "subproc" or "fr-rect" or "framed-rectangle" => FlowShape.Subroutine,
            "cyl" or "cylinder" or "database" or "db" => FlowShape.Cylinder,
            "circle" or "circ" => FlowShape.Circle,
            "dbl-circ" or "double-circle" => FlowShape.DoubleCircle,
            "diamond" or "decision" or "diam" or "question" => FlowShape.Rhombus,
            "hex" or "hexagon" or "prepare" => FlowShape.Hexagon,
            "lean-r" or "lean-right" or "in-out" => FlowShape.Parallelogram,
            "lean-l" or "lean-left" or "out-in" => FlowShape.ParallelogramAlt,
            "trap-b" or "trapezoid" or "priority" or "trapezoid-bottom" => FlowShape.Trapezoid,
            "trap-t" or "inv-trapezoid" or "manual" or "trapezoid-top" => FlowShape.TrapezoidAlt,
            "odd" => FlowShape.Asymmetric,
            _ => FlowShape.Rect,
        };

        // A link, optionally labelled either inline (`-- yes -->`) or after it (`-->|yes|`).
        private Link? ReadLink()
        {
            Link? link = null;
            foreach (var (rx, stroke) in Labelled)
            {
                var m = rx.Match(s, _i);
                if (!m.Success) continue;
                var close = m.Groups["close"].Value;
                link = new Link(stroke, StartHead(m), EndHead(close), LengthOf(close, stroke), CleanLabel(m.Groups["t"].Value));
                _i = m.Index + m.Length;
                break;
            }
            if (link is null)
                foreach (var (rx, stroke) in Plain)
                {
                    var m = rx.Match(s, _i);
                    if (!m.Success) continue;
                    var body = m.Groups["body"].Value;
                    var head = m.Groups["h"].Value;
                    // `--` or `==` alone isn't a link; it needs a head or a third character.
                    if (head.Length == 0 && stroke is MermaidStroke.Solid or MermaidStroke.Thick && body.Length < 3) return null;
                    link = new Link(stroke, StartHead(m), EndHead(head), LengthOf(body + head, stroke), null);
                    _i = m.Index + m.Length;
                    break;
                }
            if (link is null) return null;

            int save = _i;
            SkipWs();
            if (_i < s.Length && s[_i] == '|')
            {
                int end = s.IndexOf('|', _i + 1);
                if (end > 0)
                {
                    link = link with { Label = CleanLabel(s[(_i + 1)..end]) };
                    _i = end + 1;
                    return link;
                }
            }
            _i = save;
            return link;
        }

        private static MermaidHead StartHead(Match m) => m.Groups["s"].Value switch
        {
            "<" => MermaidHead.Arrow,
            "o" => MermaidHead.Circle,
            "x" => MermaidHead.Cross,
            _ => MermaidHead.None,
        };

        private static MermaidHead EndHead(string close) => close.Length == 0 ? MermaidHead.None : close[^1] switch
        {
            '>' => MermaidHead.Arrow,
            'o' => MermaidHead.Circle,
            'x' => MermaidHead.Cross,
            _ => MermaidHead.None,
        };

        // `-->` and `---` both span one rank; each extra dash (or dot, for dotted links) adds one.
        private static int LengthOf(string token, MermaidStroke stroke)
        {
            if (stroke == MermaidStroke.Dotted) return Math.Max(1, token.Count(c => c == '.'));
            char body = stroke == MermaidStroke.Thick ? '=' : stroke == MermaidStroke.Invisible ? '~' : '-';
            int n = token.Count(c => c == body);
            bool headed = token.Length > 0 && token[^1] is '>' or 'o' or 'x';
            return Math.Max(1, headed ? n - 1 : n - 2);
        }

        private static readonly (Regex, MermaidStroke)[] Labelled =
        [
            (LabelledSolid(), MermaidStroke.Solid),
            (LabelledDotted(), MermaidStroke.Dotted),
            (LabelledThick(), MermaidStroke.Thick),
        ];

        private static readonly (Regex, MermaidStroke)[] Plain =
        [
            (PlainDotted(), MermaidStroke.Dotted),
            (PlainSolid(), MermaidStroke.Solid),
            (PlainThick(), MermaidStroke.Thick),
            (PlainInvisible(), MermaidStroke.Invisible),
        ];

        [GeneratedRegex(@"\G(?<s>[<ox](?=-))?--\s+(?<t>.+?)\s*(?<close>-{2,}>|-{2,}[ox](?![\w])|-{3,})")]
        private static partial Regex LabelledSolid();

        [GeneratedRegex(@"\G(?<s>[<ox](?=-))?-\.\s+(?<t>.+?)\s*(?<close>\.+-[>ox]?)")]
        private static partial Regex LabelledDotted();

        [GeneratedRegex(@"\G(?<s>[<ox](?==))?==\s+(?<t>.+?)\s*(?<close>={2,}>|={2,}[ox](?![\w])|={3,})")]
        private static partial Regex LabelledThick();

        [GeneratedRegex(@"\G(?<s>[<ox](?=-))?(?<body>-\.+-)(?<h>>|[ox](?![\w]))?")]
        private static partial Regex PlainDotted();

        [GeneratedRegex(@"\G(?<s>[<ox](?=-))?(?<body>-{2,})(?<h>>|[ox](?![\w]))?")]
        private static partial Regex PlainSolid();

        [GeneratedRegex(@"\G(?<s>[<ox](?==))?(?<body>={2,})(?<h>>|[ox](?![\w]))?")]
        private static partial Regex PlainThick();

        [GeneratedRegex(@"\G(?<body>~{3,})")]
        private static partial Regex PlainInvisible();
    }

    // ── Entity-relationship diagram ───────────────────────────────────────────────────────────────────

    private static MermaidEr ParseEr(List<string> body)
    {
        var er = new MermaidEr();
        ErEntity? open = null;   // the entity whose `{ … }` attribute block we're inside

        foreach (var raw in body)
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (open is not null)
            {
                if (line.StartsWith('}')) { open = null; continue; }
                if (ErAttributeLine().Match(line) is { Success: true } a)
                    open.Attributes.Add(new ErAttribute(a.Groups["type"].Value, a.Groups["name"].Value,
                        string.Join(", ", a.Groups["keys"].Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)),
                        a.Groups["c"].Value));
                continue;
            }

            var word = FirstWord(line);
            if (word == "direction") { er.Direction = ParseDirection(line[word.Length..].Trim()); continue; }
            if (word is "title" or "accTitle" or "accDescr" or "classDef" or "class" or "style") continue;

            if (ErRelationSymbols().Match(line) is { Success: true } m)
            {
                AddRelationship(er, m, LeftCard(m.Groups["l"].Value), RightCard(m.Groups["r"].Value), m.Groups["line"].Value == "--");
                continue;
            }
            if (ErRelationWords().Match(line) is { Success: true } w)
            {
                AddRelationship(er, w, WordCard(w.Groups["l"].Value), WordCard(w.Groups["r"].Value), w.Groups["rel"].Value == "to");
                continue;
            }
            if (ErEntityLine().Match(line) is { Success: true } en)
            {
                var entity = Entity(er, en.Groups["n"].Value, en.Groups["al"]);
                if (en.Groups["open"].Success && !en.Groups["rest"].Value.Contains('}')) open = entity;
            }
        }
        return er;
    }

    private static void AddRelationship(MermaidEr er, Match m, MermaidHead atA, MermaidHead atB, bool identifying)
    {
        var a = Entity(er, m.Groups["a"].Value, m.Groups["aa"]);
        var b = Entity(er, m.Groups["b"].Value, m.Groups["ba"]);
        er.Relationships.Add(new ErRelationship(a.Id, b.Id, atA, atB, identifying, CleanLabel(m.Groups["label"].Value)));
    }

    private static ErEntity Entity(MermaidEr er, string name, Group alias)
    {
        var e = er.Ensure(name.Length >= 2 && name[0] == '"' ? name[1..^1] : name);
        if (alias.Success && alias.Value.Trim().Length > 0) e.Label = CleanLabel(alias.Value);
        return e;
    }

    private static MermaidHead LeftCard(string s) => s switch
    {
        "|o" => MermaidHead.ErZeroOrOne,
        "||" => MermaidHead.ErExactlyOne,
        "}o" => MermaidHead.ErZeroOrMore,
        _ => MermaidHead.ErOneOrMore,   // }|
    };

    private static MermaidHead RightCard(string s) => s switch
    {
        "o|" => MermaidHead.ErZeroOrOne,
        "||" => MermaidHead.ErExactlyOne,
        "o{" => MermaidHead.ErZeroOrMore,
        _ => MermaidHead.ErOneOrMore,   // |{
    };

    private static MermaidHead WordCard(string s) => s switch
    {
        "zero or one" or "one or zero" => MermaidHead.ErZeroOrOne,
        "one or more" or "one or many" or "many(1)" or "1+" => MermaidHead.ErOneOrMore,
        "zero or more" or "zero or many" or "many(0)" or "0+" => MermaidHead.ErZeroOrMore,
        _ => MermaidHead.ErExactlyOne,   // only one, 1
    };

    // An entity name, bare (`CUSTOMER`, `NAMED-DRIVER`) or quoted, with an optional `[alias]`.
    private const string ErName = @"""[^""]+""|[\p{L}_][\p{L}\p{N}_-]*";

    [GeneratedRegex(@"^(?<a>" + ErName + @")(?:\[(?<aa>[^\]]*)\])?\s*(?<l>\|o|\|\||\}o|\}\|)(?<line>--|\.\.)(?<r>o\||\|\||o\{|\|\{)\s*(?<b>" + ErName + @")(?:\[(?<ba>[^\]]*)\])?\s*(?::\s*(?<label>.*))?$")]
    private static partial Regex ErRelationSymbols();

    private const string ErWordCard = @"only one|zero or one|one or zero|one or more|one or many|many\(1\)|1\+|zero or more|zero or many|many\(0\)|0\+|1";

    [GeneratedRegex(@"^(?<a>" + ErName + @")(?:\[(?<aa>[^\]]*)\])?\s+(?<l>" + ErWordCard + @")\s+(?<rel>optionally to|to)\s+(?<r>" + ErWordCard + @")\s+(?<b>" + ErName + @")(?:\[(?<ba>[^\]]*)\])?\s*(?::\s*(?<label>.*))?$")]
    private static partial Regex ErRelationWords();

    [GeneratedRegex(@"^(?<n>" + ErName + @")(?:\[(?<al>[^\]]*)\])?\s*(?<open>\{(?<rest>.*))?$")]
    private static partial Regex ErEntityLine();

    // `type name [PK|FK|UK[, …]] ["comment"]`.
    [GeneratedRegex(@"^(?<type>\S+)\s+(?<name>[^\s""]+)(?<keys>(?:\s+(?:PK|FK|UK)(?:\s*,\s*(?:PK|FK|UK))*)?)\s*(?:""(?<c>[^""]*)"")?\s*$")]
    private static partial Regex ErAttributeLine();

    // ── Sequence diagram ───────────────────────────────────────────────────────────────────────────────

    private static MermaidSequence ParseSequence(List<string> body)
    {
        var seq = new MermaidSequence();
        // `box` groups participants but draws nothing here; its `end` must not close a real block.
        var opens = new Stack<bool>();

        foreach (var raw in body)
        {
            var line = raw.TrimEnd(';').Trim();
            if (line.Length == 0) continue;
            var word = FirstWord(line);
            var rest = line[word.Length..].Trim();
            switch (word)
            {
                case "participant" or "actor":
                    Participant(seq, rest, word == "actor");
                    continue;
                case "create":
                    var kind = FirstWord(rest);
                    if (kind is "participant" or "actor")
                        Participant(seq, rest[kind.Length..].Trim(), kind == "actor");
                    continue;
                case "autonumber":
                    seq.AutoNumber = true;
                    continue;
                case "activate" or "deactivate":
                    if (rest.Length > 0)
                    {
                        seq.Ensure(rest);
                        seq.Items.Add(new SeqActivation(rest, word == "activate"));
                    }
                    continue;
                case "box":
                    opens.Push(false);
                    continue;
                case "loop" or "alt" or "opt" or "par" or "critical" or "break" or "rect":
                    opens.Push(true);
                    seq.Items.Add(new SeqBlockStart(word, word == "rect" ? "" : CleanLabel(rest)));
                    continue;
                case "else" or "and" or "option":
                    seq.Items.Add(new SeqBlockDivider(CleanLabel(rest)));
                    continue;
                case "end":
                    if (opens.Count > 0 && opens.Pop()) seq.Items.Add(new SeqBlockEnd());
                    continue;
                case "destroy" or "title" or "accTitle" or "accDescr" or "link" or "links" or "properties" or "details":
                    continue;
            }

            if (word.Equals("note", StringComparison.OrdinalIgnoreCase) && NoteLine().Match(line) is { Success: true } n)
            {
                var placement = n.Groups["p"].Value.ToLowerInvariant() switch
                {
                    "left of" => SeqNotePlacement.LeftOf,
                    "right of" => SeqNotePlacement.RightOf,
                    _ => SeqNotePlacement.Over,
                };
                var who = n.Groups["who"].Value.Split(',', 2);
                var a = who[0].Trim();
                var b = who.Length > 1 ? who[1].Trim() : null;
                seq.Ensure(a);
                if (b is not null) seq.Ensure(b);
                seq.Items.Add(new SeqNote(placement, a, b, CleanLabel(n.Groups["t"].Value)));
                continue;
            }

            if (MessageLine().Match(line) is { Success: true } m)
            {
                var from = m.Groups["from"].Value.Trim();
                var to = m.Groups["to"].Value.Trim();
                if (from.Length == 0 || to.Length == 0) continue;
                seq.Ensure(from);
                seq.Ensure(to);
                var arrow = m.Groups["arrow"].Value;
                var act = m.Groups["act"].Value;
                seq.Items.Add(new SeqMessage(from, to, CleanLabel(m.Groups["t"].Value),
                    arrow.Contains("--") ? MermaidStroke.Dotted : MermaidStroke.Solid,
                    arrow[^1] switch { '>' when arrow.EndsWith(">>", StringComparison.Ordinal) => MermaidHead.Arrow, 'x' => MermaidHead.Cross, ')' => MermaidHead.Open, _ => MermaidHead.None },
                    arrow.StartsWith("<<", StringComparison.Ordinal), act == "+", act == "-"));
            }
        }
        return seq;
    }

    private static void Participant(MermaidSequence seq, string rest, bool actor)
    {
        // `A`, `A as Alice`, `A@{ "type": "database" }` (the metadata form is read as a plain participant).
        int meta = rest.IndexOf("@{", StringComparison.Ordinal);
        if (meta >= 0) rest = rest[..meta].Trim();
        var m = ParticipantAlias().Match(rest);
        var id = m.Success ? m.Groups["id"].Value : rest;
        if (id.Length == 0) return;
        var p = seq.Ensure(id);
        p.IsActor = actor;
        if (m.Success) p.Label = CleanLabel(m.Groups["label"].Value);
    }

    [GeneratedRegex(@"^(?<id>\S+)\s+as\s+(?<label>.+)$")]
    private static partial Regex ParticipantAlias();

    [GeneratedRegex(@"^note\s+(?<p>left of|right of|over)\s+(?<who>[^:]+?)\s*:(?<t>.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex NoteLine();

    // Longest arrows first, so `-->>` isn't read as `-->` followed by `>`.
    [GeneratedRegex(@"^(?<from>[^:]+?)\s*(?<arrow><<-->>|<<->>|-->>|->>|--x|-x|--\)|-\)|-->|->)\s*(?<act>[+-])?\s*(?<to>[^:]+?)\s*(?::(?<t>.*))?$")]
    private static partial Regex MessageLine();
}
