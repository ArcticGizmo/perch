namespace Perch.Data;

/// <summary>
/// One config dir's usage reading, tagged with the org that dir is signed into — a single set of bars in
/// the overlay's per-org usage strip. <see cref="Org"/> is null when the dir isn't signed in to an org (or
/// its credential can't be read); the <see cref="Header"/> still names the set from the dir's label.
/// </summary>
internal sealed record OrgUsage(ClaudeConfigDir Dir, Org? Org, UsageInfo Usage)
{
    /// <summary>The heading shown above this set's bars: the org's display name when known, else the dir's
    /// own label (custom label, slug, or directory name).</summary>
    public string Header =>
        Org?.DisplayName is { Length: > 0 } n ? n
        : Dir.DisplayLabel is { Length: > 0 } l ? l
        : Dir.Label;
}
