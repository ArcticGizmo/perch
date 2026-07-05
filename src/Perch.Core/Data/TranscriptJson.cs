using System.Text.Json.Nodes;

namespace Perch.Data;

/// <summary>
/// Small shared helpers for reading the loosely-typed JSON of a Claude Code transcript record.
/// Centralises the defensive coercions every reader needs — the "tolerate ints-or-doubles" token read
/// (previously duplicated as <c>TokenLong</c> / <c>LongOf</c>) and the ISO-timestamp parse (duplicated
/// across <see cref="TranscriptParser"/> and <see cref="SessionStatsService"/>) — plus the two tiny
/// accessors for walking <c>message.content</c> block arrays.
/// </summary>
internal static class TranscriptJson
{
    /// <summary>Reads a JSON number as a <see cref="long"/>, tolerating values serialised as doubles;
    /// returns 0 when the node is absent or unparseable. (Token counts are occasionally written as
    /// <c>1.0</c> rather than <c>1</c>.)</summary>
    public static long AsLong(JsonNode? n)
    {
        if (n == null) return 0;
        try { return n.GetValue<long>(); }
        catch { try { return (long)n.GetValue<double>(); } catch { return 0; } }
    }

    /// <summary>Parses an ISO-8601 timestamp to local time, or null when absent/unparseable.</summary>
    public static DateTime? ParseTimestamp(string? iso) =>
        !string.IsNullOrEmpty(iso) && DateTimeOffset.TryParse(iso, out var dto) ? dto.LocalDateTime : null;

    /// <summary>The <c>message.content</c> array of a record, or null when the body is not a block array
    /// (e.g. a plain-string user prompt, or a metadata record).</summary>
    public static JsonArray? ContentArray(JsonNode? record) => record?["message"]?["content"] as JsonArray;

    /// <summary>The <c>type</c> discriminator of a content block (<c>tool_use</c>, <c>text</c>,
    /// <c>tool_result</c>, …), or null.</summary>
    public static string? BlockType(JsonNode? block) => block?["type"]?.GetValue<string>();
}
