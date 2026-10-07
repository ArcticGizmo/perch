namespace Perch.Feeds;

internal enum HtmlTokenKind { Text, StartTag, EndTag }

/// <summary>One HTML token. <see cref="Text"/> is raw (entities still encoded) for text tokens; tag names are
/// lower-case. Attributes are only kept for the few the converter reads (<c>href</c>, <c>src</c>, <c>alt</c>,
/// <c>start</c>) — the rest are discarded at tokenize time, so nothing downstream can forward them.</summary>
internal readonly record struct HtmlToken(
    HtmlTokenKind Kind, string Text, string Name, bool SelfClosing,
    string? Href = null, string? Src = null, string? Alt = null, string? Start = null);

/// <summary>
/// A small, tolerant, <b>iterative</b> HTML tokenizer for feed content (docs/feeds-plan.md §3.4.2). It never
/// recurses and never throws: malformed markup degrades to text. Two things are decided here rather than later,
/// so no caller can forget them:
/// <list type="bullet">
///   <item>Raw-text elements (<c>script</c>, <c>style</c>, <c>textarea</c>, <c>title</c>, …) are consumed up to
///   their real closing tag and produce <em>no</em> token at all — their content can't leak as text.</item>
///   <item>Elements dropped with their content (<c>svg</c>, <c>math</c>, <c>iframe</c>, <c>form</c>, …) are
///   skipped whole, honouring same-name nesting; an unclosed one swallows the rest of the input (fail closed).</item>
/// </list>
/// Comments, doctypes, CDATA and processing instructions are skipped.
/// </summary>
internal static class HtmlTokenizer
{
    // Content is raw text up to "</name": never markup, never shown.
    private static readonly HashSet<string> RawTextDropped = new(StringComparer.Ordinal)
    {
        "script", "style", "textarea", "title", "xmp", "iframe", "noembed", "noframes", "noscript", "plaintext",
    };

    // Ordinary elements whose whole subtree is dropped.
    private static readonly HashSet<string> SubtreeDropped = new(StringComparer.Ordinal)
    {
        "svg", "math", "object", "applet", "form", "template", "select", "button", "frameset", "head", "video",
        "audio", "canvas", "map", "datalist", "dialog", "portal", "picture",
    };

    // Void elements that are dropped outright (no content to keep, attributes never wanted).
    private static readonly HashSet<string> VoidDropped = new(StringComparer.Ordinal)
    {
        "input", "link", "meta", "base", "embed", "frame", "param", "source", "track", "area", "keygen", "col", "wbr",
    };

    private static readonly HashSet<string> Blockish = new(StringComparer.Ordinal)
    {
        "p", "div", "br", "hr", "li", "ul", "ol", "h1", "h2", "h3", "h4", "h5", "h6", "blockquote", "pre", "table",
        "tr", "td", "th", "section", "article", "header", "footer", "main", "aside", "figure", "figcaption", "nav",
        "details", "summary", "dl", "dt", "dd", "center", "address",
    };

    /// <summary>Whether a tag ends a run of inline text (a paragraph/line boundary when flattening to text).</summary>
    public static bool IsBlockish(string name) => Blockish.Contains(name);

    public static IEnumerable<HtmlToken> Tokenize(string html)
    {
        int i = 0, n = html.Length, textStart = 0;
        while (i < n)
        {
            if (html[i] != '<') { i++; continue; }

            // Comment / doctype / CDATA / processing instruction / bogus comment: skip, no token.
            if (i + 1 < n && html[i + 1] is '!' or '?')
            {
                if (i > textStart) yield return Text(html, textStart, i);
                int end;
                if (string.CompareOrdinal(html, i, "<!--", 0, 4) == 0)
                {
                    end = html.IndexOf("-->", i + 4, StringComparison.Ordinal);
                    i = end < 0 ? n : end + 3;
                }
                else
                {
                    end = html.IndexOf('>', i + 2);
                    i = end < 0 ? n : end + 1;
                }
                textStart = i;
                continue;
            }

            bool closing = i + 1 < n && html[i + 1] == '/';
            int nameStart = i + (closing ? 2 : 1);
            if (nameStart >= n || !char.IsAsciiLetter(html[nameStart])) { i++; continue; }   // a literal '<'

            int p = nameStart;
            while (p < n && (char.IsAsciiLetterOrDigit(html[p]) || html[p] is '-' or ':')) p++;
            string name = html[nameStart..p].ToLowerInvariant();

            if (!TryReadAttributes(html, p, out int tagEnd, out bool selfClosing, out var attrs))
            {
                // No closing '>' anywhere: the rest is a broken tag — drop it rather than show half-markup.
                if (i > textStart) yield return Text(html, textStart, i);
                yield break;
            }

            if (i > textStart) yield return Text(html, textStart, i);
            i = tagEnd;
            textStart = i;

            if (closing)
            {
                if (!SubtreeDropped.Contains(name) && !RawTextDropped.Contains(name) && !VoidDropped.Contains(name))
                    yield return new HtmlToken(HtmlTokenKind.EndTag, "", name, false);
                continue;
            }

            if (RawTextDropped.Contains(name))
            {
                int close = IndexOfCloseTag(html, name, i);
                i = close < 0 ? n : SkipPastGt(html, close);
                textStart = i;
                continue;
            }

            if (SubtreeDropped.Contains(name))
            {
                if (!selfClosing) i = SkipSubtree(html, name, i);
                textStart = i;
                continue;
            }

            if (VoidDropped.Contains(name)) continue;

            yield return new HtmlToken(HtmlTokenKind.StartTag, "", name, selfClosing,
                attrs.GetValueOrDefault("href"), attrs.GetValueOrDefault("src"),
                attrs.GetValueOrDefault("alt"), attrs.GetValueOrDefault("start"));
        }
        if (n > textStart) yield return Text(html, textStart, n);
    }

    private static HtmlToken Text(string html, int start, int end) =>
        new(HtmlTokenKind.Text, html[start..end], "", false);

    // Reads attributes from just after the tag name to the closing '>'. Quoted values may contain '>'. Only the
    // attributes the converter uses are kept; values are raw (entity-decoded later, once).
    private static bool TryReadAttributes(string html, int p, out int tagEnd, out bool selfClosing,
        out Dictionary<string, string> attrs)
    {
        attrs = new Dictionary<string, string>(StringComparer.Ordinal);
        selfClosing = false;
        tagEnd = -1;
        int n = html.Length;
        while (p < n)
        {
            char c = html[p];
            if (c == '>') { tagEnd = p + 1; return true; }
            if (c == '/' ) { selfClosing = p + 1 < n && html[p + 1] == '>'; p++; continue; }
            if (char.IsWhiteSpace(c)) { p++; continue; }

            int ns = p;
            while (p < n && !char.IsWhiteSpace(html[p]) && html[p] is not ('=' or '>' or '/')) p++;
            string an = html[ns..p].ToLowerInvariant();
            while (p < n && char.IsWhiteSpace(html[p])) p++;
            string value = "";
            if (p < n && html[p] == '=')
            {
                p++;
                while (p < n && char.IsWhiteSpace(html[p])) p++;
                if (p < n && html[p] is '"' or '\'')
                {
                    char q = html[p];
                    int close = html.IndexOf(q, p + 1);
                    if (close < 0) return false;
                    value = html[(p + 1)..close];
                    p = close + 1;
                }
                else
                {
                    int vs = p;
                    while (p < n && !char.IsWhiteSpace(html[p]) && html[p] != '>') p++;
                    value = html[vs..p];
                }
            }
            if (an is "href" or "src" or "alt" or "start" && !attrs.ContainsKey(an)) attrs[an] = value;
        }
        return false;
    }

    // Index of the "</name" that closes a raw-text element (case-insensitive, followed by a delimiter), or -1.
    private static int IndexOfCloseTag(string html, string name, int from)
    {
        int n = html.Length;
        while (from < n)
        {
            int k = html.IndexOf("</", from, StringComparison.Ordinal);
            if (k < 0) return -1;
            int e = k + 2 + name.Length;
            if (e <= n && string.Compare(html, k + 2, name, 0, name.Length, StringComparison.OrdinalIgnoreCase) == 0
                && (e == n || html[e] is '>' or '/' || char.IsWhiteSpace(html[e])))
                return k;
            from = k + 2;
        }
        return -1;
    }

    private static int SkipPastGt(string html, int at)
    {
        int gt = html.IndexOf('>', at);
        return gt < 0 ? html.Length : gt + 1;
    }

    // Skips a dropped element's subtree, counting nested same-name opens so "<svg><svg></svg>leak</svg>" can't
    // surface "leak". Unclosed: everything to the end is dropped.
    private static int SkipSubtree(string html, string name, int from)
    {
        int depth = 1, n = html.Length, i = from;
        while (i < n)
        {
            int k = html.IndexOf('<', i);
            if (k < 0) return n;
            bool closing = k + 1 < n && html[k + 1] == '/';
            int ns = k + (closing ? 2 : 1);
            int e = ns + name.Length;
            if (e <= n && string.Compare(html, ns, name, 0, name.Length, StringComparison.OrdinalIgnoreCase) == 0
                && (e == n || html[e] is '>' or '/' || char.IsWhiteSpace(html[e])))
            {
                int gt = html.IndexOf('>', e);
                if (gt < 0) return n;
                bool selfClose = !closing && gt > 0 && html[gt - 1] == '/';
                if (closing) { if (--depth == 0) return gt + 1; }
                else if (!selfClose) depth++;
                i = gt + 1;
                continue;
            }
            i = k + 1;
        }
        return n;
    }
}
