using System.Text.RegularExpressions;

namespace Perch.Data.Control;

/// <summary>A kind of span the composer highlights. Extensible — add a kind here, a matcher in
/// <see cref="ComposerHighlighter.Tokenize"/>, and a colour in the renderer.</summary>
internal enum InputTokenKind
{
    /// <summary>Plain, unhighlighted text.</summary>
    Text,
    /// <summary>A leading slash command (<c>/context</c>), the first token only.</summary>
    Command,
    /// <summary>An http(s) URL anywhere in the text.</summary>
    Link,
}

/// <summary>A contiguous run of composer text of one <see cref="InputTokenKind"/>. Half-open
/// <c>[Start, Start+Length)</c> indices into the original string.</summary>
internal readonly record struct InputToken(int Start, int Length, InputTokenKind Kind);

/// <summary>
/// Tokenises composer input into typed spans so the input box can highlight the "special" parts — slash
/// commands today, hyperlinks and (later) mentions and the like. UI-free and deterministic so the rules are
/// unit-testable; the renderer maps each <see cref="InputToken"/> to a coloured run.
/// </summary>
internal static partial class ComposerHighlighter
{
    // http/https URLs. Deliberately liberal on the body (any non-space); trailing punctuation is trimmed below
    // so a link at the end of a sentence doesn't swallow the full stop or closing bracket.
    [GeneratedRegex(@"https?://\S+", RegexOptions.IgnoreCase)]
    private static partial Regex LinkRegex();

    private const string TrailingTrim = ").],;:!?\"'";

    /// <summary>Splits <paramref name="text"/> into an ordered, gap-free list of tokens covering the whole
    /// string (plain stretches come back as <see cref="InputTokenKind.Text"/>). A leading slash token is only
    /// marked a <see cref="InputTokenKind.Command"/> when <paramref name="isKnownCommand"/> recognises its
    /// name — so an unknown <c>/foo</c> stays plain text; the default recogniser is the built-in catalogue.
    /// Never throws; returns empty for empty input.</summary>
    public static IReadOnlyList<InputToken> Tokenize(string? text, Func<string, bool>? isKnownCommand = null)
    {
        if (string.IsNullOrEmpty(text)) return [];
        isKnownCommand ??= SlashCommandCatalog.IsBuiltIn;

        var specials = new List<InputToken>();

        // A leading slash command — the first token only (a "/word" mid-message isn't a command), and only when
        // it names a real command. Leading whitespace is allowed before it, matching the palette's detection.
        int ws = 0;
        while (ws < text.Length && char.IsWhiteSpace(text[ws])) ws++;
        if (ws + 1 < text.Length && text[ws] == '/' && char.IsLetter(text[ws + 1]))
        {
            int end = ws + 1;
            while (end < text.Length && !char.IsWhiteSpace(text[end])) end++;
            if (isKnownCommand(text[(ws + 1)..end]))
                specials.Add(new InputToken(ws, end - ws, InputTokenKind.Command));
        }

        // Links anywhere in the text.
        foreach (Match m in LinkRegex().Matches(text))
        {
            int len = m.Length;
            while (len > 0 && TrailingTrim.IndexOf(text[m.Index + len - 1]) >= 0) len--;
            if (len > 0) specials.Add(new InputToken(m.Index, len, InputTokenKind.Link));
        }

        specials.Sort((a, b) => a.Start.CompareTo(b.Start));

        // Stitch into full coverage: fill the gaps between specials with Text spans, and skip any special that
        // overlaps one already emitted (the command always wins at the start).
        var result = new List<InputToken>(specials.Count * 2 + 1);
        int pos = 0;
        foreach (var s in specials)
        {
            if (s.Start < pos) continue;
            if (s.Start > pos) result.Add(new InputToken(pos, s.Start - pos, InputTokenKind.Text));
            result.Add(s);
            pos = s.Start + s.Length;
        }
        if (pos < text.Length) result.Add(new InputToken(pos, text.Length - pos, InputTokenKind.Text));
        return result;
    }
}
