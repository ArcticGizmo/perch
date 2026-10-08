using System.Globalization;
using System.Net;
using System.Text;

namespace Perch.Feeds;

/// <summary>
/// The plain-text sanitizer for every feed-supplied string shown outside an entry's rendered body: titles,
/// authors, summaries — the overlay tooltip, the story header, toasts, the Settings list. The parser runs it once
/// on the way in (docs/feeds-plan.md §3.4.4). Pure; never throws.
///
/// <para>It removes what can make text lie about itself: control characters, bidi overrides/isolates (reordering
/// "txt.exe" tricks), invisible zero-width and Unicode "tag" characters (ASCII smuggling), lone surrogates. It
/// collapses all whitespace — newlines included — to single spaces, so a title can't draw extra lines of fake
/// Perch UI, and caps the length at a grapheme boundary. The zero-width <em>joiner</em> is kept: it glues emoji
/// sequences together and hides nothing on its own.</para>
/// </summary>
internal static class FeedText
{
    public const int TitleMax = 300;
    public const int AuthorMax = 100;
    public const int FeedTitleMax = 100;
    public const int SummaryMax = 500;

    /// <summary>Cleans already-decoded plain text to one capped line.</summary>
    public static string Clean(string? s, int max)
    {
        if (string.IsNullOrEmpty(s)) return "";
        // Work on a bounded prefix: a 4 MB "title" shouldn't cost a 4 MB scan to keep 300 chars.
        if (s.Length > max * 8 + 64) s = s[..(max * 8 + 64)];
        var stripped = StripInvisible(s);

        var sb = new StringBuilder(stripped.Length);
        bool space = false;
        foreach (char c in stripped)
        {
            if (char.IsWhiteSpace(c)) { space = sb.Length > 0; continue; }
            if (space) { sb.Append(' '); space = false; }
            sb.Append(c);
        }
        return Truncate(sb.ToString(), max);
    }

    /// <summary>Cleans an HTML fragment (an Atom <c>type="html"</c> title, say) to plain text: tags are stripped —
    /// with dangerous elements' content dropped — <em>before</em> entities are decoded, and they're decoded exactly
    /// once, so <c>&amp;lt;b&amp;gt;</c> stays the literal text <c>&lt;b&gt;</c>.</summary>
    public static string FromHtml(string? html, int max)
    {
        if (string.IsNullOrEmpty(html)) return "";
        var sb = new StringBuilder();
        foreach (var tok in HtmlTokenizer.Tokenize(html.Length > HtmlToMarkdown.MaxInput ? html[..HtmlToMarkdown.MaxInput] : html))
        {
            if (tok.Kind == HtmlTokenKind.Text) sb.Append(WebUtility.HtmlDecode(tok.Text));
            else if (tok.Kind is HtmlTokenKind.StartTag or HtmlTokenKind.EndTag && HtmlTokenizer.IsBlockish(tok.Name)) sb.Append(' ');
            if (sb.Length > max * 8 + 64) break;
        }
        return Clean(sb.ToString(), max);
    }

    /// <summary>Removes the invisible/reordering characters without touching whitespace layout (for text that
    /// keeps its line structure, e.g. inside a <c>&lt;pre&gt;</c>). Tab, CR and LF survive; every other control
    /// character goes.</summary>
    public static string StripInvisible(string s)
    {
        StringBuilder? sb = null;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            int width = 1;
            bool drop;
            if (char.IsHighSurrogate(c))
            {
                if (i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
                {
                    width = 2;
                    int cp = char.ConvertToUtf32(c, s[i + 1]);
                    drop = cp is >= 0xE0000 and <= 0xE007F;   // Unicode tag characters
                }
                else drop = true;                              // lone high surrogate
            }
            else if (char.IsLowSurrogate(c)) drop = true;      // lone low surrogate
            else drop = IsInvisible(c);

            if (drop)
            {
                sb ??= new StringBuilder(s, 0, i, s.Length);
            }
            else sb?.Append(s, i, width);
            i += width - 1;
        }
        return sb?.ToString() ?? s;
    }

    private static bool IsInvisible(char c) => c switch
    {
        '\t' or '\n' or '\r' => false,                        // layout whitespace: Clean collapses it
        < ' ' => true,                                    // other C0 controls
        '\u007F' => true,
        >= '\u0080' and <= '\u009F' => true,                  // C1 controls (incl. NEL)
        '؜' or '‎' or '‏' => true,             // ALM, LRM, RLM
        >= '‪' and <= '‮' => true,                  // bidi embeddings/overrides
        >= '⁦' and <= '⁩' => true,                  // bidi isolates
        '​' or '‌' or '⁠' or '﻿' => true, // zero-width space/non-joiner, word joiner, BOM
        >= '￹' and <= '￻' => true,                  // interlinear annotation controls
        _ => false,
    };

    // Caps at max chars (counting the ellipsis) without splitting a grapheme cluster.
    private static string Truncate(string s, int max)
    {
        if (s.Length <= max) return s;
        var e = StringInfo.GetTextElementEnumerator(s);
        int end = 0;
        while (e.MoveNext())
        {
            int next = e.ElementIndex + e.GetTextElement().Length;
            if (next > max - 1) break;
            end = next;
        }
        return s[..end].TrimEnd() + "…";
    }
}
