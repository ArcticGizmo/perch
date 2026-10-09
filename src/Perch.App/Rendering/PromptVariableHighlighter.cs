using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Utilities;
using Perch.Avalonia.Theming;
using Perch.Data;

namespace Perch.Avalonia.Rendering;

/// <summary>
/// Highlighter for a PR session prompt (fed to <see cref="HighlightTextBox"/>, like the statusline editor's
/// <see cref="StatuslineSourceHighlighter"/>): each <c>{variable}</c> gets a tinted wash so it reads as a
/// placeholder Perch fills in, not literal text. A known one (<see cref="PrSessionPrompts.Variables"/>) is in the
/// accent with muted braces; an unknown <c>{word}</c> (a typo, which would reach Claude as written) is in the warning
/// colour. Only the variables get spans; the rest keeps the box's own foreground. Pure.
/// </summary>
internal static class PromptVariableHighlighter
{
    private const int MaxLength = 40_000;

    public static IReadOnlyList<ValueSpan<TextRunProperties>>? Highlight(string text, Typeface face, double fontSize)
    {
        if (text.Length == 0 || text.Length > MaxLength) return null;
        // Theme brushes are re-tinted in place on a theme swap; the washes are made per layout from the current colours.
        var known = new SolidColorBrush(Palette.Accent, 0.16);
        var unknown = new SolidColorBrush(Palette.Active.StatusWarn.ToColor(), 0.16);

        var runs = new List<ValueSpan<TextRunProperties>>();
        void Run(int start, int len, IBrush fg, IBrush bg) => runs.Add(new ValueSpan<TextRunProperties>(start, len,
            new GenericTextRunProperties(face, fontRenderingEmSize: fontSize, foregroundBrush: fg, backgroundBrush: bg)));

        foreach (var (start, name) in Find(text))
        {
            bool ok = PrSessionPrompts.IsVariable(name);
            var bg = ok ? known : unknown;
            Run(start, 1, Palette.MutedBrush, bg);                                         // {
            Run(start + 1, name.Length, ok ? Palette.AccentBrush : Palette.WarnBrush, bg);   // name
            Run(start + 1 + name.Length, 1, Palette.MutedBrush, bg);                       // }
        }
        return runs;
    }

    /// <summary>Every <c>{word}</c> in <paramref name="text"/> (lowercase letters only, as the variables are), with
    /// where it starts. A brace around anything else (JSON in a prompt, say) isn't a placeholder.</summary>
    internal static IEnumerable<(int Start, string Name)> Find(string text)
    {
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] != '{') continue;
            int j = i + 1;
            while (j < text.Length && text[j] is >= 'a' and <= 'z') j++;
            if (j > i + 1 && j < text.Length && text[j] == '}')
            {
                yield return (i, text[(i + 1)..j]);
                i = j;
            }
        }
    }
}
