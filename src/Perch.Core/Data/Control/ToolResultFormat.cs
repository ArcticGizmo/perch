using System.Text.Json.Nodes;

namespace Perch.Data.Control;

/// <summary>
/// Per-tool result formatting for the session UI's tool cards. Where a count reads better than the raw first
/// line of output, this turns a tool's result text into a compact one-liner for the collapsed card —
/// "Read 42 lines", "8 matching lines", "12 files". Returns <c>null</c> for tools with no natural count
/// summary (Bash, PowerShell, unknown), where the card keeps falling back to the first line of the output.
/// Pure and UI-free; covered by <c>ToolResultFormatTests</c>.
/// </summary>
internal static class ToolResultFormat
{
    /// <summary>A one-line summary of <paramref name="resultText"/> for the collapsed card, or <c>null</c> to
    /// let the card use the first line of the output instead.</summary>
    public static string? CollapsedSummary(string tool, JsonNode? input, string resultText)
    {
        if (string.IsNullOrWhiteSpace(resultText)) return null;
        var (n, trunc) = NonBlankLines(resultText);
        return tool switch
        {
            "Read"          => n == 0 ? null : "Read " + Count(n, trunc, "line"),
            "Glob"          => n == 0 || NothingFound(resultText, "No files found") ? "no files" : Count(n, trunc, "file"),
            "Grep"          => GrepSummary(input, resultText, n, trunc),
            _               => null,
        };
    }

    // True when the result is one of Claude Code's "nothing matched" sentinel messages, which would otherwise
    // count as a single file/match.
    private static bool NothingFound(string text, params string[] needles)
    {
        var t = text.TrimStart();
        foreach (var needle in needles)
            if (t.StartsWith(needle, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>The shell command a Bash/PowerShell card should show in full when expanded (its header clips
    /// it), or <c>null</c> for any other tool / missing command.</summary>
    public static string? Command(string tool, JsonNode? input)
    {
        if (tool is not ("Bash" or "PowerShell")) return null;
        var cmd = TranscriptJson.AsString(input?["command"]);
        return string.IsNullOrWhiteSpace(cmd) ? null : cmd;
    }

    private static string GrepSummary(JsonNode? input, string result, int lineCount, bool trunc)
    {
        if (lineCount == 0 || NothingFound(result, "No matches found", "No files found")) return "no matches";
        var mode = TranscriptJson.AsString(input?["output_mode"]);
        if (mode == "count")
        {
            // count mode lists "path:count" lines — sum the per-file counts for a true match total.
            long sum = 0; bool any = false;
            foreach (var raw in result.Replace("\r\n", "\n").Split('\n'))
            {
                var line = raw.Trim();
                int c = line.LastIndexOf(':');
                if (c >= 0 && long.TryParse(line[(c + 1)..].Trim(), out var v)) { sum += v; any = true; }
            }
            if (any) return sum == 0 ? "no matches" : CountLong(sum, trunc, "match");
        }
        return mode == "content"
            ? Count(lineCount, trunc, "matching line")
            : Count(lineCount, trunc, "file");   // files_with_matches (the default) — one path per line
    }

    // "42 lines", "1 line", "8000+ lines" (a "+" when the result was truncated so the count is a floor).
    private static string Count(int n, bool truncated, string noun) => CountLong(n, truncated, noun);

    private static string CountLong(long n, bool truncated, string noun)
    {
        var word = n == 1 && !truncated ? noun : Pluralize(noun);
        return $"{n:N0}{(truncated ? "+" : "")} {word}";
    }

    // Enough English to pluralise the handful of nouns used here ("match" → "matches", "file" → "files").
    private static string Pluralize(string noun) =>
        noun.EndsWith("ch", StringComparison.Ordinal) || noun.EndsWith("sh", StringComparison.Ordinal)
        || noun.EndsWith('s') || noun.EndsWith('x') || noun.EndsWith('z')
            ? noun + "es"
            : noun + "s";

    // Counts non-blank lines, ignoring the parser's "… (+N more characters)" truncation note and flagging
    // that the count is a floor when that note is present.
    private static (int Count, bool Truncated) NonBlankLines(string text)
    {
        int n = 0; bool trunc = false;
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith("… (+", StringComparison.Ordinal)) { trunc = true; continue; }
            n++;
        }
        return (n, trunc);
    }
}
