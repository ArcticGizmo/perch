namespace Perch.Data;

/// <summary>The severity of the current Claude service status, mirroring the Statuspage
/// <c>status.indicator</c> field ("none" → all-clear, up through "critical", plus "maintenance").</summary>
internal enum StatusLevel
{
    None,
    Minor,
    Major,
    Critical,
    Maintenance,
}

/// <summary>One unresolved incident (or in-progress maintenance) from the status page — its title, the
/// Statuspage impact/lifecycle strings, the latest update body, and a shortlink to its page.</summary>
internal sealed record StatusIncident(string Name, string Impact, string Status, string? LatestUpdate, string Url);

/// <summary>
/// A snapshot of the public Claude service status, as reported by status.claude.com's Statuspage
/// <c>summary.json</c>: the overall <see cref="Level"/> + human <see cref="Description"/>, and any
/// unresolved <see cref="Incidents"/>. Only surfaced in the UI when <see cref="HasIssue"/> is true.
///
/// <see cref="Ok"/> is false when the most recent fetch failed; in that case the fields carry the last
/// successfully-read values (so an ongoing outage keeps showing across a transient network blip, and a
/// resolved one clears on the next good poll).
/// </summary>
internal sealed record StatusInfo(
    StatusLevel Level,
    string Description,
    IReadOnlyList<StatusIncident> Incidents,
    string PageUrl,
    DateTime LastUpdated,
    bool Ok,
    string? Error)
{
    public const string DefaultPageUrl = "https://status.claude.com";

    /// <summary>The all-clear reading — also the seed value before the first fetch, so nothing shows.</summary>
    public static StatusInfo Healthy { get; } =
        new(StatusLevel.None, "All Systems Operational", [], DefaultPageUrl, DateTime.MinValue, true, null);

    /// <summary>Whether there's something worth surfacing (anything other than all-clear). Driven by the
    /// last known <see cref="Level"/> regardless of <see cref="Ok"/>, so a failed poll neither raises a
    /// false alarm nor prematurely clears a real one.</summary>
    public bool HasIssue => Level != StatusLevel.None;
}
