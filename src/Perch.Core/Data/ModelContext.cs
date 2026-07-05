using System.Text.RegularExpressions;

namespace Perch.Data;

/// <summary>
/// Resolves the context-window size (in tokens) of the model a session is currently running.
///
/// Claude Code's <c>message.model</c> field (e.g. <c>claude-sonnet-4-6</c>) is ambiguous: it does
/// <b>not</b> distinguish the standard 200k-token variant from the 1M-token beta. The only place that
/// distinction surfaces is the <c>/model</c> command's confirmation line, recorded in the transcript as
/// a synthetic user record whose content is a terminal string of the form:
/// <c>"Set model to ESC[1mSonnet 4.6 (1M context)ESC[22m for this session only · …"</c>.
/// The human display name between the ANSI bold markers (<c>Sonnet 4.6</c> vs <c>Sonnet 4.6 (1M
/// context)</c>) is what tells the two apart.
///
/// We read the display name from that record (the most recent one wins) and map it to a window size.
/// Any name carrying the <c>(1M context)</c> marker is 1M; a small <see cref="Overrides"/> table
/// covers models that are 1M without the marker; everything else falls back to the 200k standard window.
/// </summary>
internal static class ModelContext
{
    /// <summary>Standard context window. Fallback for any unrecognised model name.</summary>
    public const int DefaultWindow = 200_000;

    /// <summary>Extended (1M-token beta) context window, advertised by the "(1M context)" suffix.</summary>
    public const int ExtendedWindow = 1_000_000;

    // Models that are 1M *without* carrying the "(1M context)" display suffix — their plain name is
    // already the 1M variant, so the suffix check never fires and they'd otherwise fall through to the
    // 200k default. Keyed case-insensitively on the display name.
    //   • "Sonnet 5" ships with a 1M window by default in Claude Code (confirmed live).
    // (Unsuffixed "Opus 4.8" / "Haiku 4.5" are deliberately absent: Opus's 1M is an opt-in that carries
    // the marker / "[1m]" id suffix, and Haiku 4.5 is 200k-only — both must fall through to DefaultWindow.)
    private static readonly Dictionary<string, int> Overrides = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Sonnet 5"] = ExtendedWindow,
    };

    // Matches the human display name between the ANSI bold-on (ESC[1m) and bold-off (ESC[22m) markers.
    //  is the ESC character (U+001B); JSON parsing turns the transcript's  escapes into
    // real ESC bytes before we see the content string.
    private static readonly Regex SetModelRegex = new(
        "Set model to \\[1m(?<name>.*?)\\[22m",
        RegexOptions.Compiled);

    /// <summary>
    /// Extracts the model display name from a <c>/model</c> confirmation line, or returns null if the
    /// text is not one. Handles both the ANSI-marked form and a plain-text fallback, stripping the
    /// known trailing clauses ("for this session…", "and saved…", "· Draws from usage credits").
    /// </summary>
    public static string? ParseDisplayName(string? content)
    {
        if (string.IsNullOrEmpty(content))
            return null;

        // Primary path: ANSI bold markers present (the common case from real terminals).
        var m = SetModelRegex.Match(content);
        if (m.Success)
            return Clean(m.Groups["name"].Value);

        // Fallback: strip ANSI codes and parse the plain text.
        const string prefix = "Set model to ";
        var i = content.IndexOf(prefix, StringComparison.Ordinal);
        if (i < 0)
            return null;

        var rest = Clean(content[(i + prefix.Length)..]);
        foreach (var stop in new[] { " for this session", " and saved", " ·", "·" })
        {
            var s = rest.IndexOf(stop, StringComparison.Ordinal);
            if (s >= 0)
                rest = rest[..s];
        }
        rest = rest.Trim();
        return rest.Length > 0 ? rest : null;
    }

    /// <summary>Maps a model display name (e.g. "Sonnet 4.6 (1M context)") to its context window in
    /// tokens. Null or unrecognised names resolve to <see cref="DefaultWindow"/>.</summary>
    public static int WindowFor(string? displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName))
            return DefaultWindow;

        var name = displayName.Trim();

        // "(1M context)" suffix is the authoritative 1M signal.
        if (name.Contains("1M context", StringComparison.OrdinalIgnoreCase))
            return ExtendedWindow;

        return Overrides.TryGetValue(name, out var window) ? window : DefaultWindow;
    }

    /// <summary>
    /// Maps a model id as stored in <c>settings.json</c> — a short alias (<c>"sonnet"</c>,
    /// <c>"opus[1m]"</c>) or a full id (<c>"claude-opus-4-8[1m]"</c>) — to its context window. This is
    /// the fallback when a session never ran <c>/model</c>: it started on the configured default, whose
    /// transcript <c>message.model</c> field can't tell the 200k and 1M variants apart. The
    /// <c>[1m]</c> suffix is the authoritative 1M signal, and Sonnet 5 defaults to 1M with no suffix;
    /// everything else (including an unsuffixed Opus id) is the 200k standard window. Null/blank
    /// resolves to <see cref="DefaultWindow"/>.
    /// </summary>
    public static int WindowForConfiguredModel(string? model)
    {
        if (string.IsNullOrWhiteSpace(model))
            return DefaultWindow;

        var m = model.Trim();

        // "[1m]" suffix is the authoritative 1M opt-in (e.g. "opus[1m]", "claude-opus-4-8[1m]").
        if (m.Contains("[1m]", StringComparison.OrdinalIgnoreCase))
            return ExtendedWindow;

        // Sonnet 5 is 1M by default. Matches the full id ("claude-sonnet-5") and the bare "sonnet"
        // alias, which Claude Code resolves to the current Sonnet (Sonnet 5).
        if (m.Contains("sonnet-5", StringComparison.OrdinalIgnoreCase) ||
            m.Equals("sonnet", StringComparison.OrdinalIgnoreCase))
            return ExtendedWindow;

        return DefaultWindow;
    }

    // Strips ANSI SGR escape sequences (ESC[…m) and trims whitespace.
    private static string Clean(string s) =>
        Regex.Replace(s, "\\[[0-9;]*m", "").Trim();
}
