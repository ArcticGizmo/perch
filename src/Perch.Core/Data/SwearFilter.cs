namespace Perch.Data;

/// <summary>
/// Tallies profanity in user-prompt text for the "Fowl Mouthed" achievement family. The word list is
/// deliberately opaque: it appears nowhere in source — and never in memory — as readable text. Each stem
/// is packed into a 64-bit value (up to eight lowercase ASCII bytes, little-endian) and XOR-masked; those
/// masked values are the <em>only</em> representation kept, so there is no plaintext blocklist to read off
/// a disassembly or a heap dump. Matching packs-and-masks each candidate word the same way and does an
/// O(1) set lookup, so a whole prompt is one cheap linear scan with no allocation.
///
/// The table is generated, not hand-written. The plaintext word list lives nowhere in the shipped source —
/// only ROT13'd, in the test project (SwearFilterTests.Rot13Stems). Run that project's skipped
/// GenerateStemTable to reproduce the constants below; Table_MatchesEncodeOfEveryStem guards them.
/// </summary>
internal static class SwearFilter
{
    // Arbitrary non-zero 64-bit XOR mask applied after packing. Changing it invalidates the table below.
    private const ulong Mask = 0x5A3C9E1742D8B6F1UL;

    // Masked, packed stems — plus a handful of compounds that suffix-stripping can't reduce to a stem.
    // Every entry is Encode(word) for one common English swear; the plaintext is never materialised.
    private static readonly HashSet<ulong> Stems =
    [
        0x5A3C9E1729BBC397UL,
        0x5A3C9E1736B1DE82UL,
        0x5A3C9E1742ABC590UL,
        0x5A3C9E7F21ACDF93UL,
        0x5A3C9E1729BBDF95UL,
        0x5A3C9E1731ABDF81UL,
        0x5A3C9E1729BBD992UL,
        0x5A3C9E7C21B1C481UL,
        0x5A3C9E1736B9C185UL,
        0x5A3C9E1729B6D786UL,
        0x5A3CEC7229B6D786UL,
        0x5A3CEC7225BFC393UL,
        0x5A3C9E1727ABC490UL,
        0x5A3C9E1736B6C392UL,
        0x5A3C9E1732B9C492UL,
        0x5A3C9E172CB5D795UL,
        0x5A58EC7636ABD793UL,
        0x5A57FD782EB4D993UL,
        0x2957FD782EB4D993UL,
        0x3F50F17F27ABC490UL,
        0x5A59F2782AABC590UL,
        0x5A4FED7620B5C395UL,
        0x5A4FED7629BBD79BUL,
        0x2E55F6642EB4C393UL,
        0x5A52F37626BCD996UL,
        0x5A3CFB7F21ADD995UL,
        0x5A3C9E1736ADDA82UL,
        0x5A3C9E7230B7DE86UL,
        0x5A3C9E7236B1DE82UL,
    ];

    // Longest packable stem is eight bytes; a token can inflect one suffix longer than that ("ing"/"ers"),
    // so anything over 11 letters can't be a stem or a one-suffix inflection of one. Cheap early bail.
    private const int MaxToken = 11;

    // Peeled off a token before lookup so inflected forms (…s, …ing, …ed, …y) reduce to their stem. Peeling
    // only counts when the remainder is itself a known stem, so ordinary words never false-match ("class"
    // never becomes a swear). Longest suffix is three, which sets MaxToken above.
    private static readonly string[] Suffixes = ["s", "es", "ed", "ing", "in", "er", "ers", "y", "ty"];

    /// <summary>Number of profane words in one prompt. Splits on non-letters, lowercases, and matches whole
    /// tokens (optionally after peeling a single inflection suffix) against the masked stem set. Null/empty
    /// text scores zero.</summary>
    public static int Count(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return 0;

        int count = 0, i = 0, n = text.Length;
        while (i < n)
        {
            while (i < n && !IsLetter(text[i])) i++;      // skip the gap between words
            int start = i;
            while (i < n && IsLetter(text[i])) i++;       // consume the word
            if (i > start && Matches(text.AsSpan(start, i - start)))
                count++;
        }
        return count;
    }

    private static bool IsLetter(char c) => (uint)((c | 0x20) - 'a') <= 'z' - 'a';

    // A token matches when it — or its stem after peeling one suffix — is in the masked set. Works on a
    // lowercased stack copy so nothing is allocated and mixed-case prompts still count.
    private static bool Matches(ReadOnlySpan<char> token)
    {
        if (token.Length is 0 or > MaxToken)
            return false;

        Span<char> lower = stackalloc char[MaxToken];
        for (int i = 0; i < token.Length; i++)
        {
            char c = token[i];
            lower[i] = c is >= 'A' and <= 'Z' ? (char)(c + 32) : c;
        }
        var word = lower[..token.Length];

        if (word.Length <= 8 && Stems.Contains(Encode(word)))
            return true;

        foreach (var suffix in Suffixes)
        {
            if (word.Length > suffix.Length && word.EndsWith(suffix))
            {
                var stem = word[..^suffix.Length];
                if (stem.Length <= 8 && Stems.Contains(Encode(stem)))
                    return true;
            }
        }
        return false;
    }

    /// <summary>Packs up to eight lowercase ASCII bytes little-endian into a 64-bit value and XOR-masks it —
    /// the transform that both seeds the table and probes it. Uppercase is folded to lowercase defensively.
    /// (internal only so the table generator/tests can reproduce the constants.)</summary>
    internal static ulong Encode(ReadOnlySpan<char> word)
    {
        ulong v = 0;
        int n = Math.Min(word.Length, 8);
        for (int i = 0; i < n; i++)
        {
            char c = word[i];
            if (c is >= 'A' and <= 'Z') c = (char)(c + 32);
            v |= (ulong)(byte)c << (i * 8);
        }
        return v ^ Mask;
    }
}
