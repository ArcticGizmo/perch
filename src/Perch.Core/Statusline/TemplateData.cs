namespace Perch.Statusline;

using System.Globalization;
using System.Text.Json.Nodes;

/// <summary>
/// The value bag a statusline template renders against — a thin dotted-path accessor over the JSON
/// Claude Code sends its statusLine command on stdin (model, workspace, cost, context_window,
/// prompt_cache, rate_limits, pr, …), plus any Perch-injected extras merged onto the same object
/// (e.g. <c>git.branch</c>). Kept as raw <see cref="JsonNode"/> so the engine can distinguish
/// <em>absent</em> (key missing) from <em>present-but-null</em> — a real distinction in this payload
/// (<c>context_window.used_percentage</c> is null early in a session; <c>current_usage</c> is null
/// after <c>/compact</c>). Missing/null both render empty and read as falsy.
/// </summary>
internal sealed class TemplateData
{
    private readonly JsonObject _root;

    public TemplateData(JsonObject root) => _root = root;

    /// <summary>The backing object, so callers (the CLI) can merge in computed extras before rendering.</summary>
    public JsonObject Root => _root;

    /// <summary>Parses a JSON object literal. Throws on malformed input or a non-object root — callers
    /// that read untrusted stdin should catch and fall back.</summary>
    public static TemplateData Parse(string json) =>
        new(JsonNode.Parse(json) as JsonObject
            ?? throw new System.FormatException("statusline payload is not a JSON object"));

    /// <summary>Resolves a dotted path (<c>a.b.c</c>). Returns true with the node (which may itself be a
    /// JSON null) when every segment exists; false when any segment is missing or a non-object is
    /// traversed into.</summary>
    public bool TryGet(string path, out JsonNode? node)
    {
        node = _root;
        foreach (var part in path.Split('.'))
        {
            if (node is JsonObject o && o.TryGetPropertyValue(part, out var next))
                node = next;
            else
            {
                node = null;
                return false;
            }
        }
        return true;
    }

    // ── value coercion (present node -> number / string / truthiness) ──────────────────

    /// <summary>The node as a number: JSON numbers directly, numeric strings parsed, booleans as 1/0.
    /// Null for a null node or a non-numeric string.</summary>
    public static double? Num(JsonNode? n)
    {
        if (n is null) return null;
        return n.GetValueKind() switch
        {
            System.Text.Json.JsonValueKind.Number => n.GetValue<double>(),
            System.Text.Json.JsonValueKind.True   => 1,
            System.Text.Json.JsonValueKind.False  => 0,
            System.Text.Json.JsonValueKind.String =>
                double.TryParse(n.GetValue<string>(), NumberStyles.Any, CultureInfo.InvariantCulture, out var d)
                    ? d : null,
            _ => null,
        };
    }

    /// <summary>The node as display text. Integers print without a decimal point; other numbers use the
    /// invariant round-trip form; a null/absent node is the empty string.</summary>
    public static string Str(JsonNode? n)
    {
        if (n is null) return "";
        switch (n.GetValueKind())
        {
            case System.Text.Json.JsonValueKind.String: return n.GetValue<string>();
            case System.Text.Json.JsonValueKind.True:   return "true";
            case System.Text.Json.JsonValueKind.False:  return "false";
            case System.Text.Json.JsonValueKind.Number:
                var d = n.GetValue<double>();
                return d == System.Math.Floor(d) && !double.IsInfinity(d)
                    ? ((long)d).ToString(CultureInfo.InvariantCulture)
                    : d.ToString(CultureInfo.InvariantCulture);
            default: return n.ToJsonString();
        }
    }

    /// <summary>Truthiness for sections/conditionals: absent or JSON null is false; false, 0, "" and
    /// empty arrays are false; everything else is true.</summary>
    public static bool Truthy(JsonNode? n)
    {
        if (n is null) return false;
        return n.GetValueKind() switch
        {
            System.Text.Json.JsonValueKind.Null   => false,
            System.Text.Json.JsonValueKind.False  => false,
            System.Text.Json.JsonValueKind.True    => true,
            System.Text.Json.JsonValueKind.Number => n.GetValue<double>() != 0,
            System.Text.Json.JsonValueKind.String => n.GetValue<string>().Length > 0,
            System.Text.Json.JsonValueKind.Array  => (n as JsonArray)?.Count > 0,
            System.Text.Json.JsonValueKind.Object => true,
            _ => false,
        };
    }
}
