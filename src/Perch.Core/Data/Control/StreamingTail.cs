namespace Perch.Data.Control;

/// <summary>
/// The UI-free policy for rendering the still-forming tail of a streaming assistant reply — the last top-level
/// Markdown block, which the session view re-renders as text arrives (everything before it has settled and is
/// built once). Two things keep that tail cheap on a long reply:
/// <list type="bullet">
/// <item><b>An open code fence renders as plain text.</b> Until its closing fence arrives, a fenced block is
/// shown unhighlighted and updated in place (<see cref="TryOpenFence"/>); it gets its Markdown build and syntax
/// colours once it closes. While it's open no later block can start, so the settle scan is skipped too.</item>
/// <item><b>A large tail re-renders at most every <see cref="SlowCadence"/></b> (<see cref="MinInterval"/>)
/// instead of every paced frame.</item>
/// </list>
/// </summary>
internal static class StreamingTail
{
    /// <summary>A rich (Markdown) tail over this many chars re-renders at the slow cadence.</summary>
    public const int RichBudget = 4 * 1024;

    /// <summary>An open-fence (plain text) tail over this many chars re-renders at the slow cadence.</summary>
    public const int PlainBudget = 16 * 1024;

    public static readonly TimeSpan SlowCadence = TimeSpan.FromMilliseconds(250);

    /// <summary>The minimum gap between two renders of a tail this size: zero (every frame) while it's small,
    /// else <see cref="SlowCadence"/>.</summary>
    public static TimeSpan MinInterval(int tailLength, bool openFence) =>
        tailLength > (openFence ? PlainBudget : RichBudget) ? SlowCadence : TimeSpan.Zero;

    /// <summary>
    /// True when <paramref name="tail"/> is a fenced code block (CommonMark: up to three spaces, then three or
    /// more backticks or tildes) whose closing fence hasn't arrived yet. <paramref name="code"/> is the code
    /// streamed so far — everything after the opening fence line, carriage returns dropped and trailing
    /// newlines trimmed — and is empty while the opening line itself is still arriving. Leading blank lines are
    /// skipped. Best-effort; never throws.
    /// </summary>
    public static bool TryOpenFence(string tail, out string code)
    {
        code = "";
        if (string.IsNullOrEmpty(tail)) return false;

        // The first non-blank line is the candidate opener.
        int ls = 0;
        while (ls < tail.Length)
        {
            int nl = tail.IndexOf('\n', ls);
            int end = nl < 0 ? tail.Length : nl;
            if (!IsBlank(tail, ls, end)) break;
            if (nl < 0) return false;
            ls = nl + 1;
        }
        if (ls >= tail.Length) return false;

        int p = ls, indent = 0;
        while (p < tail.Length && tail[p] == ' ' && indent < 4) { p++; indent++; }
        if (indent > 3 || p >= tail.Length) return false;
        char fence = tail[p];
        if (fence != '`' && fence != '~') return false;
        int run = 0;
        while (p + run < tail.Length && tail[p + run] == fence) run++;
        if (run < 3) return false;

        int eol = tail.IndexOf('\n', p + run);
        int infoEnd = eol < 0 ? tail.Length : eol;
        // A backtick fence's info string can't contain a backtick (that's inline code, not a fence).
        if (fence == '`' && tail.IndexOf('`', p + run, infoEnd - (p + run)) >= 0) return false;
        if (eol < 0) return true;   // the opening line is still arriving

        int body = eol + 1;
        for (int line = body; line <= tail.Length;)
        {
            int nl = tail.IndexOf('\n', line);
            int end = nl < 0 ? tail.Length : nl;
            if (IsClosingFence(tail, line, end, fence, run)) return false;
            if (nl < 0) break;
            line = nl + 1;
        }

        code = tail[body..].Replace("\r", "").TrimEnd('\n');
        return true;
    }

    // A closing fence: up to three spaces, at least `run` of the opening char, then only whitespace.
    private static bool IsClosingFence(string s, int start, int end, char fence, int run)
    {
        int p = start, indent = 0;
        while (p < end && s[p] == ' ' && indent < 4) { p++; indent++; }
        if (indent > 3) return false;
        int n = 0;
        while (p < end && s[p] == fence) { p++; n++; }
        if (n < run) return false;
        return IsBlank(s, p, end);
    }

    private static bool IsBlank(string s, int start, int end)
    {
        for (int i = start; i < end; i++)
            if (!char.IsWhiteSpace(s[i])) return false;
        return true;
    }
}
