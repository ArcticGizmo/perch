namespace Perch.Data.Control;

/// <summary>A kind of span the composer highlights. Extensible — add a kind here, a matcher in
/// <see cref="ComposerHighlighter.Tokenize"/>, and a colour in the renderer.</summary>
internal enum InputTokenKind
{
    /// <summary>Plain, unhighlighted text.</summary>
    Text,
    /// <summary>A slash command: a leading one (<c>/context</c>), or a mid-text one that can run inline (a skill).</summary>
    Command,
    /// <summary>An http(s) URL anywhere in the text.</summary>
    Link,
    /// <summary>An <c>[Image #N]</c> marker standing in for an attached image (see <see cref="ImageMarker"/>).</summary>
    Image,
}

/// <summary>A contiguous run of composer text of one <see cref="InputTokenKind"/>. Half-open
/// <c>[Start, Start+Length)</c> indices into the original string.</summary>
internal readonly record struct InputToken(int Start, int Length, InputTokenKind Kind);

/// <summary>
/// Tokenises composer input into typed spans so the input box can highlight the "special" parts — slash
/// commands today, hyperlinks and (later) mentions and the like. UI-free and deterministic so the rules are
/// unit-testable; the renderer maps each <see cref="InputToken"/> to a coloured run.
/// </summary>
internal static class ComposerHighlighter
{
    /// <summary>Splits <paramref name="text"/> into an ordered, gap-free list of tokens covering the whole
    /// string (plain stretches come back as <see cref="InputTokenKind.Text"/>). A leading slash token is only
    /// marked a <see cref="InputTokenKind.Command"/> when <paramref name="isKnownCommand"/> recognises its
    /// name — so an unknown <c>/foo</c> stays plain text; the default recogniser is the built-in catalogue.
    /// A <c>/word</c> later in the text (after whitespace) is a command only when <paramref name="isInlineCommand"/>
    /// recognises it — built-ins only run as the whole message, but a skill can be invoked mid-prompt; the default
    /// recognises none. Never throws; returns empty for empty input.</summary>
    public static IReadOnlyList<InputToken> Tokenize(string? text, Func<string, bool>? isKnownCommand = null,
        Func<string, bool>? isInlineCommand = null)
    {
        if (string.IsNullOrEmpty(text)) return [];
        isKnownCommand ??= SlashCommandCatalog.IsBuiltIn;

        var specials = new List<InputToken>();

        // Slash tokens: a "/" that opens the text (after any leading whitespace) or follows whitespace, then a
        // letter. The leading one is checked against every known command, later ones only against inline-capable
        // ones, so a "/word" mid-sentence stays plain unless it really names a skill.
        bool leading = true;
        for (int i = 0; i + 1 < text.Length; i++)
        {
            if (char.IsWhiteSpace(text[i])) continue;
            if (i > 0 && !char.IsWhiteSpace(text[i - 1])) continue;   // not at a word start
            int end = i;
            while (end < text.Length && !char.IsWhiteSpace(text[end])) end++;
            if (text[i] == '/' && char.IsLetter(text[i + 1]))
            {
                var name = text[(i + 1)..end];
                if (leading ? isKnownCommand(name) : isInlineCommand?.Invoke(name) == true)
                    specials.Add(new InputToken(i, end - i, InputTokenKind.Command));
            }
            leading = false;
            i = end - 1;
        }

        // Links anywhere in the text (shared detector, so composer highlighting and clickable links agree).
        foreach (var u in UrlDetect.Find(text))
            specials.Add(new InputToken(u.Start, u.Length, InputTokenKind.Link));

        // Attached-image markers.
        foreach (var (start, length) in ImageMarker.Find(text))
            specials.Add(new InputToken(start, length, InputTokenKind.Image));

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
