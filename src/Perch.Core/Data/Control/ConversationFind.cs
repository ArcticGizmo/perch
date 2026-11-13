namespace Perch.Data.Control;

/// <summary>
/// Which conversation items could hold a find query, judged from the model alone, so the thread view only builds
/// and walks those instead of rebuilding every dehydrated item on each search (perf review PF19). The view searches
/// rendered text, which differs from the model's (Markdown markers dropped, a tool card's derived summaries), so
/// this compares letters and digits only, lower-cased: a phrase split by formatting, punctuation or a line break
/// still counts. It errs towards "may match"; the view's own walk decides what really matches.
/// </summary>
internal static class ConversationFind
{
    /// <summary>The query as <see cref="MayContain"/> compares it, or null when it has no letters or digits (search
    /// everything, as there's nothing to narrow by).</summary>
    public static string? Key(string query) => Normalize(query) is { Length: > 0 } key ? key : null;

    /// <summary>Whether <paramref name="item"/>'s model text could render text containing the query whose
    /// <see cref="Key"/> is <paramref name="key"/>. Items whose shown text isn't in the model (permission prompts,
    /// compaction and task notices) always may.</summary>
    public static bool MayContain(ConversationItem item, string key) => item switch
    {
        UserMessageItem u      => Has(u.Text, key),
        NoteItem n             => Has(n.Text, key),
        AssistantMessageItem a => a.Parts.Any(p => p switch
        {
            TextPart t     => Has(t.Text, key),
            ThinkingPart t => Has(t.Text, key),
            ToolCallPart t => Has(t.Summary, key) || Has(t.ToolName, key) || Has(t.ResultText, key) || InputHas(t, key),
            _              => true,
        }),
        _ => true,
    };

    // A tool's input as the card shows it (a command, an edit's old and new text): its string values, decoded, rather
    // than the raw JSON, where a line break is the two characters "\n" and would glue an "n" into the comparison.
    private static bool InputHas(ToolCallPart t, string key)
    {
        if (t.Input is not { } input) return Has(t.InputJson, key);
        var sb = new System.Text.StringBuilder();
        Collect(input, sb);
        return Has(sb.ToString(), key);

        static void Collect(System.Text.Json.Nodes.JsonNode? node, System.Text.StringBuilder sb)
        {
            switch (node)
            {
                case System.Text.Json.Nodes.JsonObject o:
                    foreach (var (_, v) in o) Collect(v, sb);
                    break;
                case System.Text.Json.Nodes.JsonArray a:
                    foreach (var v in a) Collect(v, sb);
                    break;
                case System.Text.Json.Nodes.JsonValue v:
                    sb.Append(v.ToString()).Append(' ');
                    break;
            }
        }
    }

    private static bool Has(string? text, string key) =>
        !string.IsNullOrEmpty(text) && Normalize(text).Contains(key, StringComparison.Ordinal);

    internal static string Normalize(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (var c in s)
            if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
        return sb.ToString();
    }
}
