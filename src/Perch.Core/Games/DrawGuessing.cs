using System.Text;

namespace Perch.Games;

/// <summary>
/// The pure guess-matching + hint logic for "Draw with Perch" (see <c>DrawWithPerchWindow</c>). A guesser types
/// free text and it's compared to the drawer's word forgivingly — case, spacing and punctuation don't matter, so
/// "Ice-Cream!" matches "ice cream". The guesser is also shown a <see cref="LetterHint"/> (the letter counts per
/// word) but never the word itself. Kept in <c>Perch.Core</c> so both the client and the backend agree on what
/// "correct" means — the same normalisation is mirrored in the <c>submit_draw_guess</c> SQL RPC so a hacked
/// client can't disagree with the server about a solve.
/// </summary>
public static class DrawGuessing
{
    /// <summary>Folds a word or guess to its comparison form: lower-cased, with everything that isn't a letter
    /// or digit removed. So spacing, hyphens and punctuation are ignored ("ice cream" == "icecream" == "Ice-Cream").</summary>
    public static string Normalize(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var sb = new StringBuilder(text.Length);
        foreach (char c in text)
            if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
        return sb.ToString();
    }

    /// <summary>True when <paramref name="guess"/> matches <paramref name="word"/> under <see cref="Normalize"/>.
    /// An empty normalised form never matches (so a blank/punctuation-only guess is always wrong).</summary>
    public static bool IsCorrect(string? guess, string? word)
    {
        var w = Normalize(word);
        return w.Length > 0 && Normalize(guess) == w;
    }

    /// <summary>The hint shown to the guesser: the letter count of each whitespace-separated token, joined by
    /// spaces. "ice cream" → "3 5", "cat" → "3". Counts only letters/digits, so punctuation inside a token isn't
    /// counted ("t-rex" → "4"). Reveals the shape of the answer without the answer itself.</summary>
    public static string LetterHint(string? word)
    {
        if (string.IsNullOrWhiteSpace(word)) return "";
        var counts = new List<int>();
        foreach (var token in word.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            int n = 0;
            foreach (char c in token) if (char.IsLetterOrDigit(c)) n++;
            if (n > 0) counts.Add(n);
        }
        return string.Join(' ', counts);
    }
}
