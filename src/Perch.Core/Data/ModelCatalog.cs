using System.Text.RegularExpressions;

namespace Perch.Data;

/// <summary>One selectable model release: its family's display name, the version label and the id the CLI
/// takes (<c>--model</c> / <c>set_model</c>).</summary>
public sealed record ModelVersion(string Family, string Version, string Id)
{
    /// <summary>"Opus 5.5".</summary>
    public string DisplayName => $"{Family} {Version}";
}

/// <summary>A model family (Opus, Sonnet, …): the CLI alias that resolves to its newest release, and every
/// release Perch offers, oldest first.</summary>
public sealed record ModelFamily(string Alias, string Name, IReadOnlyList<ModelVersion> Versions)
{
    public ModelVersion Latest => Versions[^1];
}

/// <summary>
/// The models the session window's model picker offers, and the display name for any model string a session
/// reports. A known id or alias reads as "Opus 5.5"; anything the catalogue doesn't know (a model newer than
/// this list, a custom alias) is shown exactly as given rather than guessed at.
/// </summary>
public static class ModelCatalog
{
    // Oldest → newest within each family; the last entry is what the family's bare alias resolves to.
    public static IReadOnlyList<ModelFamily> Families { get; } =
    [
        Family("haiku", "Haiku", ("4.5", "claude-haiku-4-5")),
        Family("sonnet", "Sonnet", ("4.6", "claude-sonnet-4-6"), ("5", "claude-sonnet-5"), ("5.5", "claude-sonnet-5-5")),
        Family("opus", "Opus", ("4.6", "claude-opus-4-6"), ("4.7", "claude-opus-4-7"), ("4.8", "claude-opus-4-8"),
            ("5", "claude-opus-5"), ("5.5", "claude-opus-5-5")),
        Family("fable", "Fable", ("5", "claude-fable-5"), ("5.1", "claude-fable-5-1")),
    ];

    // A dated snapshot suffix ("claude-haiku-4-5-20251001") names the same release as the bare id.
    private static readonly Regex DateSuffix = new(@"-\d{8}$", RegexOptions.Compiled);

    /// <summary>The catalogue release a model string names — a full id (with or without a <c>[1m]</c> variant
    /// marker or a date suffix) or a family alias ("opus" → its newest release). Null when it isn't one we know.</summary>
    public static ModelVersion? Find(string? model)
    {
        if (string.IsNullOrWhiteSpace(model)) return null;
        var key = model.Trim();
        int marker = key.IndexOf('[');
        if (marker > 0) key = key[..marker];
        key = DateSuffix.Replace(key, "");

        foreach (var family in Families)
        {
            if (key.Equals(family.Alias, StringComparison.OrdinalIgnoreCase)) return family.Latest;
            foreach (var version in family.Versions)
                if (key.Equals(version.Id, StringComparison.OrdinalIgnoreCase)) return version;
        }
        return null;
    }

    /// <summary>"claude-opus-5-5" / "opus" → "Opus 5.5"; an unknown model is returned as given.</summary>
    public static string DisplayName(string model) => Find(model)?.DisplayName ?? model.Trim();

    private static ModelFamily Family(string alias, string name, params (string Version, string Id)[] versions) =>
        new(alias, name, versions.Select(v => new ModelVersion(name, v.Version, v.Id)).ToList());
}
