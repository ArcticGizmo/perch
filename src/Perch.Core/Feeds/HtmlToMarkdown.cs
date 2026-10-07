using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Perch.Feeds;

/// <summary>
/// Turns an entry's untrusted HTML into Markdown that <c>MarkdownView</c> can only render as text, a fixed set of
/// structure, and vetted web links (docs/feeds-plan.md §3.4.2). Pure; never throws; never recurses on input
/// structure (an explicit frame stack, capped at <see cref="MaxDepth"/> — deeper markup is flattened).
///
/// <para>The rules, in one place:</para>
/// <list type="bullet">
///   <item><b>Allowlist.</b> Only <c>p br h1–h6 strong b em i code pre blockquote ul ol li a img hr table tr td
///   th</c> produce structure. Block-ish containers (<c>div</c>, <c>section</c>, …) become paragraph breaks;
///   everything else unwraps to its text. Dangerous elements never reach here — <see cref="HtmlTokenizer"/>
///   drops them with their content.</item>
///   <item><b>Every text character is escaped.</b> Entities are decoded exactly once, invisible/bidi characters
///   stripped, and every ASCII punctuation character backslash-escaped, so text can never become a link,
///   heading, list, table, emphasis, entity or raw HTML — structure comes only from allowlisted tags. No
///   <c>&lt;</c> ever reaches Markdig, so its HTML paths are unreachable.</item>
///   <item><b>Links</b> go through <see cref="FeedUrl.Safe"/>; a refused one becomes its text. Destinations are
///   percent-encoded so they can't close early, and link text that names a different host than the target gets
///   the real host appended.</item>
///   <item><b>Images are never loaded</b> (v1): each becomes a "🖼 alt" link stub.</item>
///   <item><b>Code</b> is fenced with a backtick run longer than any inside it.</item>
/// </list>
/// </summary>
internal static class HtmlToMarkdown
{
    /// <summary>Input beyond this is cut (the tail is "continued in the browser").</summary>
    public const int MaxInput = 512 * 1024;

    /// <summary>Open-element frames beyond this are flattened into their parent.</summary>
    public const int MaxDepth = 32;

    private const int MaxTableColumns = 20;

    public static string Convert(string? html, Uri? baseUri)
    {
        if (string.IsNullOrWhiteSpace(html)) return "";
        bool cut = html.Length > MaxInput;
        if (cut) html = html[..MaxInput];

        var w = new Writer(baseUri);
        foreach (var tok in HtmlTokenizer.Tokenize(html)) w.Accept(tok);
        var md = w.Finish();
        if (cut) md += "\n\n*" + Escape("… continued in the browser") + "*";
        return md;
    }

    /// <summary>Backslash-escapes every ASCII punctuation character (CommonMark allows escaping any of them), so
    /// the text renders literally whatever it contains.</summary>
    internal static string Escape(string text)
    {
        var sb = new StringBuilder(text.Length + 8);
        foreach (char c in text)
        {
            if (IsAsciiPunctuation(c)) sb.Append('\\');
            sb.Append(c);
        }
        return sb.ToString();
    }

    private static bool IsAsciiPunctuation(char c) =>
        c is >= '!' and <= '/' or >= ':' and <= '@' or >= '[' and <= '`' or >= '{' and <= '~';

    // A destination Markdig can't misread: no spaces, parens, angle brackets or pipes left to close or split it.
    private static string Dest(Uri uri) => uri.AbsoluteUri
        .Replace(" ", "%20").Replace("(", "%28").Replace(")", "%29")
        .Replace("<", "%3C").Replace(">", "%3E").Replace("|", "%7C").Replace("\"", "%22").Replace("'", "%27");

    // Text that claims to be a URL or bare host ("https://bank.example/login", "bank.example").
    private static readonly Regex UrlLike = new(
        @"^(?:https?://)?(?:[\p{L}\p{N}-]+\.)+[\p{L}]{2,}(?:[/:?#]\S*)?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));

    private sealed class Frame(string tag)
    {
        public readonly string Tag = tag;
        public readonly StringBuilder Md = new();
        public readonly StringBuilder Plain = new();
        public bool Raw;                         // pre / code: text kept verbatim, nested tags ignored
        public Uri? Href;                        // a
        public List<string>? Items;              // ul / ol
        public int Start = 1;                    // ol
        public List<List<string>>? Rows;         // table
        public List<string>? Cells;              // tr
    }

    private sealed class Writer(Uri? baseUri)
    {
        private readonly List<Frame> _stack = [new Frame("#root")];

        private Frame Top => _stack[^1];
        private bool InRaw => _stack.Exists(f => f.Raw);
        private bool InLink => _stack.Exists(f => f.Tag == "a");

        private static readonly HashSet<string> BlockUnwrap = new(StringComparer.Ordinal)
        {
            "p", "div", "section", "article", "header", "footer", "main", "aside", "figure", "figcaption", "nav",
            "details", "summary", "dl", "dt", "dd", "center", "address", "caption",
        };

        // Openers that end an open paragraph (HTML's implied </p>).
        private static readonly HashSet<string> ClosesParagraph = new(StringComparer.Ordinal)
        {
            "p", "div", "section", "article", "header", "footer", "main", "aside", "figure", "nav", "details", "dl",
            "center", "address", "h1", "h2", "h3", "h4", "h5", "h6", "pre", "blockquote", "ul", "ol", "table", "hr",
        };

        // Frames a paragraph search must not cross.
        private static readonly HashSet<string> Containers = new(StringComparer.Ordinal)
        {
            "#root", "li", "blockquote", "td", "th", "ul", "ol", "table", "tr",
        };

        public void Accept(HtmlToken t)
        {
            switch (t.Kind)
            {
                case HtmlTokenKind.Text: AcceptText(t.Text); break;
                case HtmlTokenKind.StartTag: Open(t); break;
                case HtmlTokenKind.EndTag: Close(t.Name); break;
            }
        }

        public string Finish()
        {
            while (_stack.Count > 1) PopTop();
            return _stack[0].Md.ToString().Trim();
        }

        private void AcceptText(string raw)
        {
            var text = FeedText.StripInvisible(WebUtility.HtmlDecode(raw));
            if (InRaw)
            {
                text = text.Replace("\r\n", "\n").Replace('\r', '\n');
                Top.Md.Append(text);
                Top.Plain.Append(text);
                return;
            }

            var collapsed = CollapseWhitespace(text);
            if (collapsed.Length == 0) return;
            // Leading whitespace at the very start of a block is meaningless; skip it so a block can't start with
            // indentation.
            if (collapsed == " " && Top.Md.Length == 0) return;
            Top.Md.Append(Escape(collapsed));
            Top.Plain.Append(collapsed);
        }

        private static string CollapseWhitespace(string s)
        {
            var sb = new StringBuilder(s.Length);
            bool space = false;
            foreach (char c in s)
            {
                if (char.IsWhiteSpace(c)) { space = true; continue; }
                if (space) { sb.Append(' '); space = false; }
                sb.Append(c);
            }
            if (space) sb.Append(' ');
            return sb.ToString();
        }

        private void Open(HtmlToken t)
        {
            string name = t.Name;

            if (InRaw)
            {
                if (name == "br") { Top.Md.Append('\n'); Top.Plain.Append('\n'); }
                return;   // markup inside pre/code is ignored; its text still arrives
            }

            if (ClosesParagraph.Contains(name)) CloseOpenParagraph();

            switch (name)
            {
                case "br":
                    Top.Md.Append("  \n");
                    Top.Plain.Append(' ');
                    return;
                case "hr":
                    Top.Md.Append("\n\n* * *\n\n");   // never "---": the pipeline reads a leading --- block as frontmatter
                    return;
                case "img":
                    AppendImage(t);
                    return;
            }

            if (!IsFrameTag(name) || t.SelfClosing) return;   // unknown tags unwrap to their text
            if (_stack.Count >= MaxDepth) return;            // flatten: content lands in the current frame

            switch (name)
            {
                case "li": CloseUpToWithin("li", "ul", "ol"); break;
                case "tr": CloseUpToWithin("tr", "table"); break;
                case "td" or "th": CloseUpToWithin(name == "td" ? "td" : "th", "tr"); CloseUpToWithin(name == "td" ? "th" : "td", "tr"); break;
                case "a": if (FindFrame("a") is int ai) CloseDownTo(ai); break;
                case "h1" or "h2" or "h3" or "h4" or "h5" or "h6":
                    for (int i = _stack.Count - 1; i > 0; i--)
                        if (IsHeading(_stack[i].Tag)) { CloseDownTo(i); break; }
                    break;
            }

            var f = new Frame(name);
            switch (name)
            {
                case "pre" or "code": f.Raw = true; break;
                case "a": f.Href = FeedUrl.Safe(Decode(t.Href), baseUri, allowMailto: true); break;
                case "ul": f.Items = []; break;
                case "ol":
                    f.Items = [];
                    if (int.TryParse(Decode(t.Start), out int st)) f.Start = Math.Clamp(st, 0, 999_999);
                    break;
                case "table": f.Rows = []; break;
                case "tr": f.Cells = []; break;
                case "li": FlushStrayListText(); break;
            }
            _stack.Add(f);
        }

        private void Close(string name)
        {
            if (InRaw)
            {
                // Only the raw element's own close ends raw mode (an inner </code> inside <pre> is markup we ignore).
                int raw = _stack.FindLastIndex(f => f.Raw);
                if (_stack[raw].Tag == name) CloseDownTo(raw);
                return;
            }
            if (FindFrame(name) is int i) CloseDownTo(i);
        }

        private static bool IsFrameTag(string n) =>
            BlockUnwrap.Contains(n) || IsHeading(n) || n is "strong" or "b" or "em" or "i" or "code" or "pre" or "a"
                or "blockquote" or "ul" or "ol" or "li" or "table" or "tr" or "td" or "th";

        private static bool IsHeading(string n) => n.Length == 2 && n[0] == 'h' && n[1] is >= '1' and <= '6';

        private int? FindFrame(string tag)
        {
            for (int i = _stack.Count - 1; i > 0; i--)
                if (_stack[i].Tag == tag) return i;
            return null;
        }

        // Closes the nearest open `tag` that sits above the nearest of `scopes` (so a new <li> closes the previous
        // item of the same list but never an outer list's item).
        private void CloseUpToWithin(string tag, params string[] scopes)
        {
            for (int i = _stack.Count - 1; i > 0; i--)
            {
                if (Array.IndexOf(scopes, _stack[i].Tag) >= 0) return;
                if (_stack[i].Tag == tag) { CloseDownTo(i); return; }
            }
        }

        private void CloseOpenParagraph()
        {
            for (int i = _stack.Count - 1; i > 0; i--)
            {
                if (_stack[i].Tag == "p") { CloseDownTo(i); return; }
                if (Containers.Contains(_stack[i].Tag)) return;
            }
        }

        private void CloseDownTo(int index)
        {
            while (_stack.Count > index) PopTop();
        }

        // Text typed straight into a <ul>/<ol> (outside any <li>) becomes its own item rather than vanishing.
        private void FlushStrayListText()
        {
            if (Top.Items is { } items && Top.Md.ToString().Trim() is { Length: > 0 } stray)
            {
                items.Add(stray);
                Top.Md.Clear();
            }
        }

        private void AppendImage(HtmlToken t)
        {
            var alt = FeedText.Clean(Decode(t.Alt), 100);
            var label = Escape("🖼 " + (alt.Length > 0 ? alt : "image"));
            var src = FeedUrl.Safe(Decode(t.Src), baseUri);
            Top.Md.Append(src is null || InLink ? label : $"[{label}]({Dest(src)})");
            Top.Plain.Append(' ').Append(alt).Append(' ');
        }

        private static string? Decode(string? attr) => attr is null ? null : WebUtility.HtmlDecode(attr);

        // Renders the top frame into its parent.
        private void PopTop()
        {
            var f = _stack[^1];
            _stack.RemoveAt(_stack.Count - 1);
            var parent = Top;
            string md = f.Md.ToString();
            string plain = f.Plain.ToString();

            switch (f.Tag)
            {
                case "strong" or "b": Inline(parent, Wrap(md, "**"), plain); break;
                case "em" or "i": Inline(parent, Wrap(md, "*"), plain); break;
                case "code": Inline(parent, CodeSpan(md), plain); break;
                case "a": Inline(parent, Link(f.Href, md, plain), plain); break;
                case "pre": Block(parent, CodeBlock(md), plain); break;
                case "blockquote": Block(parent, Quote(md), plain); break;
                case "ul" or "ol":
                    if (md.Trim() is { Length: > 0 } stray) f.Items!.Add(stray);
                    Block(parent, List(f.Items!, f.Tag == "ol", f.Start), plain);
                    break;
                case "li":
                    if (parent.Items is { } items) { items.Add(md.Trim()); parent.Plain.Append(' ').Append(plain); }
                    else Block(parent, md.Trim(), plain);
                    break;
                case "td" or "th":
                    if (parent.Cells is { } cells) { cells.Add(OneLine(md)); parent.Plain.Append(' ').Append(plain); }
                    else Block(parent, md.Trim(), plain);
                    break;
                case "tr":
                    if (parent.Rows is { } rows) { rows.Add(f.Cells!); parent.Plain.Append(' ').Append(plain); }
                    else Block(parent, string.Join(" ", f.Cells!), plain);
                    break;
                case "table":
                    Block(parent, Table(f.Rows!), plain);
                    if (md.Trim() is { Length: > 0 } strayText) Block(parent, strayText, "");
                    break;
                default:
                    if (IsHeading(f.Tag)) Block(parent, Heading(f.Tag[1] - '0', md), plain);
                    else Block(parent, md.Trim(), plain);   // p and the block-unwrapped containers
                    break;
            }
        }

        private static void Inline(Frame parent, string md, string plain)
        {
            parent.Md.Append(md);
            parent.Plain.Append(plain);
        }

        private static void Block(Frame parent, string md, string plain)
        {
            if (md.Length > 0) parent.Md.Append("\n\n").Append(md).Append("\n\n");
            parent.Plain.Append(' ').Append(plain).Append(' ');
        }

        private static string Wrap(string md, string mark)
        {
            var inner = md.Trim();
            if (inner.Length == 0 || inner.Contains('\n')) return md;   // empty, or blocks inside: no emphasis
            int lead = md.Length - md.TrimStart().Length, trail = md.Length - md.TrimEnd().Length;
            return md[..lead] + mark + inner + mark + md[(md.Length - trail)..];
        }

        private static string CodeSpan(string raw)
        {
            var content = raw.Replace('\n', ' ');
            if (content.Trim().Length == 0) return content.Length > 0 ? " " : "";
            var fence = new string('`', LongestRun(content, '`') + 1);
            string pad = content.StartsWith('`') || content.EndsWith('`') ? " " : "";
            return fence + pad + content + pad + fence;
        }

        private static string CodeBlock(string raw)
        {
            var content = raw.StartsWith('\n') ? raw[1..] : raw;
            content = content.TrimEnd('\n');
            if (content.Trim().Length == 0) return "";
            var fence = new string('`', Math.Max(3, LongestRun(content, '`') + 1));
            return fence + "\n" + content + "\n" + fence;
        }

        private static int LongestRun(string s, char c)
        {
            int best = 0, run = 0;
            foreach (char x in s) { run = x == c ? run + 1 : 0; if (run > best) best = run; }
            return best;
        }

        private static string Link(Uri? href, string md, string plain)
        {
            if (href is null || md.Contains('\n')) return md;   // refused target, or blocks inside: keep the text
            var text = md.Trim();
            bool mailto = href.Scheme == Uri.UriSchemeMailto;
            if (text.Length == 0)
                text = Escape(mailto ? FeedText.Clean(Uri.UnescapeDataString(href.UserInfo + "@" + href.Host), 100)
                                     : FeedUrl.DisplayHost(href));
            var link = $"[{text}]({Dest(href)})";

            // Link text that names a host (or URL) other than the real target gets the real host spelled out.
            var claimed = plain.Trim();
            if (!mailto && claimed.Length is > 0 and < 2048 && IsUrlLike(claimed)
                && Uri.TryCreate(claimed.Contains("://") ? claimed : "http://" + claimed, UriKind.Absolute, out var c)
                && FeedUrl.DisplayHost(c) != FeedUrl.DisplayHost(href))
                link += " " + Escape("(↗ " + FeedUrl.DisplayHost(href) + ")");
            return link;
        }

        private static bool IsUrlLike(string s)
        {
            try { return UrlLike.IsMatch(s); }
            catch (RegexMatchTimeoutException) { return false; }
        }

        private static string Quote(string md)
        {
            var content = md.Trim();
            if (content.Length == 0) return "";
            return string.Join("\n", content.Split('\n').Select(l => l.Length == 0 ? ">" : "> " + l));
        }

        private static string List(List<string> items, bool ordered, int start)
        {
            var sb = new StringBuilder();
            int n = 0;
            foreach (var item in items)
            {
                if (item.Length == 0) continue;
                string marker = ordered ? $"{start + n}. " : "- ";
                string indent = new(' ', marker.Length);
                var lines = item.Split('\n');
                if (sb.Length > 0) sb.Append('\n');
                sb.Append(marker).Append(lines[0]);
                for (int i = 1; i < lines.Length; i++)
                    sb.Append('\n').Append(lines[i].Length == 0 ? "" : indent + lines[i]);
                n++;
            }
            return sb.ToString();
        }

        private static string Heading(int level, string md)
        {
            var content = OneLine(md);
            return content.Length == 0 ? "" : new string('#', level) + " " + content;
        }

        private static string OneLine(string md)
        {
            var sb = new StringBuilder(md.Length);
            bool space = false;
            foreach (char c in md)
            {
                if (c is '\n' or '\r' || c == ' ') { space = sb.Length > 0; continue; }
                if (space) { sb.Append(' '); space = false; }
                sb.Append(c);
            }
            return sb.ToString();
        }

        private static string Table(List<List<string>> rows)
        {
            rows.RemoveAll(r => r.Count == 0);
            if (rows.Count == 0) return "";
            int cols = Math.Min(MaxTableColumns, rows.Max(r => r.Count));
            string Row(List<string> r) =>
                "| " + string.Join(" | ", Enumerable.Range(0, cols).Select(i => i < r.Count ? r[i] : "")) + " |";
            var sb = new StringBuilder();
            sb.Append(Row(rows[0])).Append('\n');
            sb.Append("| ").Append(string.Join(" | ", Enumerable.Repeat("---", cols))).Append(" |");
            foreach (var r in rows.Skip(1)) sb.Append('\n').Append(Row(r));
            return sb.ToString();
        }
    }
}
