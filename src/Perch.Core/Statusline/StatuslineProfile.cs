namespace Perch.Statusline;

using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

/// <summary>Whether a profile is a Perch mustache template (Perch renders it) or an imported command
/// line Perch runs verbatim (so you can carry over a setup from ccstatusline, a bash script, etc.).</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
internal enum ProfileKind
{
    Perch,
    External,
}

/// <summary>One saved statusline configuration. A <see cref="ProfileKind.Perch"/> profile carries a
/// <see cref="Template"/> that Perch renders on each refresh; a <see cref="ProfileKind.External"/> one
/// carries the raw <see cref="Command"/> that goes into <c>settings.json</c> unchanged.</summary>
internal sealed class StatuslineProfile
{
    public string Name { get; set; } = "";
    public ProfileKind Kind { get; set; } = ProfileKind.Perch;

    /// <summary>The mustache template (Perch profiles).</summary>
    public string? Template { get; set; }

    /// <summary>The verbatim command written to <c>settings.json → statusLine.command</c> (External
    /// profiles). Perch does not parse or render it.</summary>
    public string? Command { get; set; }

    /// <summary>Extra left/right padding Claude Code adds around the line, mirrored into
    /// <c>settings.json → statusLine.padding</c>. 0 = flush.</summary>
    public int Padding { get; set; }

    public bool IsPerch => Kind == ProfileKind.Perch;
}

/// <summary>The persisted statusline configuration: the saved profiles plus which one is active. Stored
/// as <c>statusline.json</c> in Perch's per-profile app-data folder (see <see cref="StatuslineStore"/>),
/// independent of Claude Code's own <c>settings.json</c> — Perch owns the profile library; applying a
/// profile is what writes the <c>statusLine</c> block into <c>settings.json</c>.</summary>
internal sealed class StatuslineConfig
{
    public List<StatuslineProfile> Profiles { get; set; } = new();
    public string? ActiveName { get; set; }

    public StatuslineProfile? Find(string name) =>
        Profiles.FirstOrDefault(p => string.Equals(p.Name, name, System.StringComparison.OrdinalIgnoreCase));

    /// <summary>The active profile, falling back to the first profile when the active name is unset or
    /// dangling, and null only when there are no profiles at all.</summary>
    public StatuslineProfile? Active =>
        (ActiveName is { } n ? Find(n) : null) ?? Profiles.FirstOrDefault();

    /// <summary>Adds or replaces a profile by name (case-insensitive) and returns it.</summary>
    public StatuslineProfile Upsert(StatuslineProfile profile)
    {
        var idx = Profiles.FindIndex(p =>
            string.Equals(p.Name, profile.Name, System.StringComparison.OrdinalIgnoreCase));
        if (idx >= 0) Profiles[idx] = profile;
        else Profiles.Add(profile);
        return profile;
    }
}
