namespace Perch.Data.Control;

/// <summary>How a built-in slash command is surfaced in the Perch rich session UI (see
/// docs/session-slash-commands-plan.md).</summary>
internal enum SlashCommandTier
{
    /// <summary>Executes when sent as plain text over stream-json; its output comes back as an assistant
    /// markdown block, which the thread already renders. Interop = palette entry + send + render.</summary>
    PlainText,
    /// <summary>Perch already has a richer native surface (a pill, Settings, the launcher); the palette
    /// should route there rather than dump the CLI's markdown.</summary>
    Native,
    /// <summary>Mutates the session (clears / compacts / renames): Perch must <em>react</em> — reset the
    /// thread, drop a marker, update the title — not just render the output.</summary>
    SessionMutating,
    /// <summary>TUI-only or not meaningful headless. Shown for discoverability but may not do anything over
    /// stream-json; needs a native reimplementation or is simply N/A.</summary>
    TuiOnly,
}

/// <summary>One built-in slash command in the palette: its name (no leading slash), an argument hint, a
/// one-line description, and how Perch should surface it.</summary>
internal sealed record SlashCommandInfo(string Name, string ArgHint, string Description, SlashCommandTier Tier)
{
    /// <summary><c>/name</c> or <c>/name &lt;arg&gt;</c> for display.</summary>
    public string Display => ArgHint.Length > 0 ? $"/{Name} {ArgHint}" : $"/{Name}";

    /// <summary>Whether the command takes (optional or required) arguments.</summary>
    public bool TakesArgs => ArgHint.Length > 0;
}

/// <summary>
/// The curated catalogue of Claude Code's <em>built-in</em> (special) slash commands — the palette's backbone.
/// Skills (user/project/plugin commands like <c>/grill-me</c> or <c>/nexus:*</c>) are deliberately out of
/// scope here; they flow as plain text with no special interop.
///
/// <para>Why a curated list rather than the CLI's advertised <c>slash_commands</c> (from the <c>init</c>
/// record): that list is a <em>subset</em> in <c>-p</c> mode (many commands that work as text aren't
/// advertised) and it is <em>polluted</em> with the account's skills. So the built-in palette is sourced from
/// this catalogue; <c>init</c>'s list is only a merge/availability hint for later.</para>
/// </summary>
internal static class SlashCommandCatalog
{
    /// <summary>The curated built-ins, in a sensible default order (shown when the query is empty).</summary>
    public static IReadOnlyList<SlashCommandInfo> BuiltIns { get; } = new SlashCommandInfo[]
    {
        // Tier 1 — plain text; output renders as markdown.
        new("context",       "",              "Show what's filling the context window",       SlashCommandTier.PlainText),
        new("cost",          "",              "Session cost and token usage",                 SlashCommandTier.PlainText),
        new("status",        "",              "Account, model and session status",            SlashCommandTier.PlainText),
        new("recap",         "",              "Summarise the session so far",                 SlashCommandTier.PlainText),
        new("insights",      "",              "Session insights",                             SlashCommandTier.PlainText),
        new("init",          "",              "Generate or refresh CLAUDE.md",                SlashCommandTier.PlainText),
        new("review",        "",              "Review the working tree's changes",            SlashCommandTier.PlainText),
        new("pr-comments",   "",              "Fetch pull-request comments",                  SlashCommandTier.PlainText),
        new("release-notes", "",              "Show the release notes",                        SlashCommandTier.PlainText),
        new("export",        "",              "Export the conversation",                       SlashCommandTier.PlainText),
        new("memory",        "",              "View project and user memory",                  SlashCommandTier.PlainText),
        new("goal",          "[text]",        "Set or show the session goal",                  SlashCommandTier.PlainText),
        new("import",        "<path>",        "Import a file's contents into the context",     SlashCommandTier.PlainText),
        new("add-dir",       "<path>",        "Add a working directory",                       SlashCommandTier.PlainText),

        // Tier 2 — Perch already has a native surface; the palette routes there.
        new("model",         "[name]",        "Switch the model",                              SlashCommandTier.Native),
        new("effort",        "<level>",       "Set reasoning effort for this model",           SlashCommandTier.Native),
        new("usage",         "",              "Plan usage and limits",                         SlashCommandTier.Native),
        new("config",        "",              "Open Claude Desktop",                           SlashCommandTier.Native),
        new("theme",         "",              "Change the colour theme",                       SlashCommandTier.Native),
        new("mcp",           "",              "MCP server status",                             SlashCommandTier.Native),
        new("resume",        "",              "Resume another session",                        SlashCommandTier.Native),
        new("login",         "",              "Sign in (opens a terminal)",                    SlashCommandTier.Native),
        new("logout",        "",              "Sign out (opens a terminal)",                   SlashCommandTier.Native),

        // Tier 3 — mutates the session; Perch must react.
        new("clear",         "",              "Start a fresh conversation",                    SlashCommandTier.SessionMutating),
        new("compact",       "[instructions]","Compact the conversation to free up context",   SlashCommandTier.SessionMutating),
        new("autocompact",   "[on|off]",      "Toggle automatic compaction near the limit",    SlashCommandTier.SessionMutating),
        new("rename",        "[title]",       "Rename this session",                           SlashCommandTier.SessionMutating),

        // Tier 4 — TUI-only / defer.
        new("doctor",        "",              "Diagnose the installation",                      SlashCommandTier.TuiOnly),
        new("vim",           "",              "Vim editing mode (terminal only)",              SlashCommandTier.TuiOnly),
    };

    private static readonly HashSet<string> BuiltInNames =
        BuiltIns.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>True if <paramref name="name"/> (no leading slash) is a curated built-in.</summary>
    public static bool IsBuiltIn(string name) => BuiltInNames.Contains(name);

    /// <summary>Internal/plumbing commands the CLI advertises but that must never be surfaced.</summary>
    public static bool IsInternal(string name) =>
        name.StartsWith("__", StringComparison.Ordinal) || name is "workflow-launch-exec";

    /// <summary>Ranks the built-ins for the palette against what the user has typed after the <c>/</c>
    /// (case-insensitive fuzzy match on the command name). A blank query returns <em>all</em> commands
    /// alphabetically (so the palette is a browsable list on a bare <c>/</c>); a non-blank query returns the
    /// fuzzy-ranked matches capped to <paramref name="limit"/>.</summary>
    public static IReadOnlyList<SlashCommandInfo> Search(string query, int limit = 8)
    {
        query = query.Trim();
        if (query.Length == 0)
            return BuiltIns.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();

        var byName = BuiltIns.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);
        return FuzzyMatch.Rank(query, BuiltIns.Select(c => c.Name).ToList(), limit)
            .Select(r => byName[r.Path])
            .ToList();
    }

    /// <summary>Whether composer text should be treated as a slash command: a leading <c>/</c> followed by a
    /// letter (so a lone <c>/</c>, or a path/regex the user is pasting, isn't mistaken for one).</summary>
    public static bool LooksLikeCommand(string? text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        var t = text.TrimStart();
        return t.Length >= 2 && t[0] == '/' && char.IsLetter(t[1]);
    }

    /// <summary>The command name from composer text (the token after <c>/</c>, lower-cased), or null when the
    /// text isn't a command. E.g. <c>"/compact keep tests"</c> → <c>"compact"</c>.</summary>
    public static string? CommandName(string? text)
    {
        if (!LooksLikeCommand(text)) return null;
        var t = text!.TrimStart()[1..];
        int end = 0;
        while (end < t.Length && !char.IsWhiteSpace(t[end])) end++;
        return t[..end].ToLowerInvariant();
    }
}
