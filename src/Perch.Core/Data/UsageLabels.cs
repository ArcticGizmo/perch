namespace Perch.Data;

/// <summary>
/// Condensed captions for the overlay usage bars. The two fixed windows and the credits bar are literals at
/// the call site (<c>5h</c> / <c>7d</c> / <c>$</c>); the model-scoped weekly buckets carry a model display
/// name from the endpoint, which <see cref="Short"/> collapses to a single-letter code so the caption column
/// can stay narrow.
/// </summary>
internal static class UsageLabels
{
    /// <summary>A one- or two-character code for a model-scoped bucket: known families map to their initial
    /// (<c>Fable→F</c>, <c>Opus→O</c>, <c>Sonnet→S</c>, <c>Haiku→H</c>); anything else falls back to the first
    /// letter of the name. Empty in, empty out.</summary>
    public static string Short(string? modelDisplayName)
    {
        var name = modelDisplayName?.Trim();
        if (string.IsNullOrEmpty(name))
            return "";

        if (name.Contains("Fable", StringComparison.OrdinalIgnoreCase)) return "F";
        if (name.Contains("Opus", StringComparison.OrdinalIgnoreCase)) return "O";
        if (name.Contains("Sonnet", StringComparison.OrdinalIgnoreCase)) return "S";
        if (name.Contains("Haiku", StringComparison.OrdinalIgnoreCase)) return "H";

        return name[..1].ToUpperInvariant();
    }
}
