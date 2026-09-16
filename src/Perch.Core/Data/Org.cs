namespace Perch.Data;

/// <summary>
/// A Claude organization identity, as observed from a config dir's <c>.claude.json</c>
/// <c>oauthAccount</c> block. This is a <b>Layer-2</b> type: it is <i>produced by reading</i> a
/// <see cref="ClaudeConfigDir"/>, never stored on one — Layer 1 stays org-free.
/// </summary>
/// <remarks>
/// Identity is <see cref="Uuid"/> (<c>organizationUuid</c>) only: org <b>names</b> duplicate across
/// accounts and can be renamed, so a display-name change must not read as a different org, and two dirs
/// signed into the same org must compare equal. <see cref="Name"/> and <see cref="AccountEmail"/> are
/// display-only and excluded from equality.
/// </remarks>
internal sealed record Org
{
    /// <summary>The stable organization identity — <c>oauthAccount.organizationUuid</c>. The sole key.</summary>
    public string Uuid { get; }

    /// <summary>The display name — <c>oauthAccount.organizationName</c>. May be null/renamed; never the identity.</summary>
    public string? Name { get; init; }

    /// <summary>The signed-in account email, when present in <c>oauthAccount</c>. Display/diagnostic only.</summary>
    public string? AccountEmail { get; init; }

    public Org(string uuid) => Uuid = uuid;

    /// <summary>The name when present, else the uuid — so a readout always has something to show.</summary>
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? Uuid : Name!.Trim();

    public bool Equals(Org? other) =>
        other is not null && string.Equals(Uuid, other.Uuid, StringComparison.Ordinal);

    public override int GetHashCode() => Uuid.GetHashCode(StringComparison.Ordinal);

    public override string ToString() => $"Org({DisplayName}: {Uuid})";
}
