using System.Net.Http;
using System.Text.Json.Nodes;

namespace Perch.Data;

/// <summary>
/// Polls the public Claude service status from status.claude.com's Statuspage JSON API
/// (GET https://status.claude.com/api/v2/summary.json — no auth). One call yields the overall status
/// indicator plus any unresolved incidents and in-progress maintenances, which the tray surfaces as an
/// outage footer.
///
/// Like <see cref="UsageMonitor"/> it never throws: on any failure the last successful reading is
/// returned tagged <c>Ok=false</c>, so an ongoing outage keeps showing across a transient blip while a
/// resolved one clears on the next good poll (see <see cref="StatusInfo.HasIssue"/>). The parse is
/// factored into <see cref="Parse"/> so it can be unit-tested without touching the network.
/// </summary>
internal sealed class StatusMonitor
{
    private const string SummaryUrl = "https://status.claude.com/api/v2/summary.json";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    // The most recent successful reading, so a failed fetch can still surface last-known state.
    private StatusInfo _last = StatusInfo.Healthy;

    // The ETag of the last 200 response. Sent back as If-None-Match so an unchanged status returns an
    // empty 304 (Statuspage sits behind a CDN that honours this) instead of re-sending the whole body.
    private string? _etag;

    /// <summary>Fetches the current status. Always resolves (never throws): on failure the result carries
    /// the last successful reading with <c>Ok=false</c> plus a human reason.</summary>
    public async Task<StatusInfo> FetchAsync()
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, SummaryUrl);
            req.Headers.Accept.ParseAdd("application/json");
            req.Headers.UserAgent.ParseAdd("perch");
            if (_etag is { } tag)
                req.Headers.TryAddWithoutValidation("If-None-Match", tag);

            using var resp = await Http.SendAsync(req).ConfigureAwait(false);

            // Unchanged since the last poll — reuse the cached reading (refreshing its timestamp so it
            // doesn't read as stale). Cheap: the 304 body is empty.
            if (resp.StatusCode == System.Net.HttpStatusCode.NotModified)
            {
                _last = _last with { LastUpdated = DateTime.Now, Ok = true, Error = null };
                return _last;
            }
            if (!resp.IsSuccessStatusCode)
                return Fail($"Status service returned {(int)resp.StatusCode}");

            var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            var info = Parse(body);
            if (info is null)
                return Fail("Couldn't parse status response");

            _etag = resp.Headers.ETag?.Tag;
            _last = info;
            return _last;
        }
        catch (Exception ex)
        {
            return Fail("Couldn't reach status service: " + ex.Message);
        }
    }

    // Re-tags the last good reading as failed (keeping its level/incidents), or an empty failed reading.
    private StatusInfo Fail(string reason) => _last with { Ok = false, Error = reason };

    /// <summary>Parses a Statuspage <c>summary.json</c> body into a <see cref="StatusInfo"/>, or null if
    /// the body isn't the expected shape. Public-internal + static so it's unit-testable off the network.</summary>
    internal static StatusInfo? Parse(string json)
    {
        JsonObject? root;
        try { root = JsonNode.Parse(json)?.AsObject(); }
        catch { return null; }
        if (root is null) return null;

        var status = root["status"]?.AsObject();
        var level = MapIndicator(status?["indicator"]?.ToString());
        var description = status?["description"]?.ToString() ?? "";

        var incidents = new List<StatusIncident>();
        string pageUrl = root["page"]?["url"]?.ToString() is { Length: > 0 } url ? url : StatusInfo.DefaultPageUrl;

        // Unresolved incidents (summary.json only carries these) and any in-progress maintenance, both
        // surfaced in the footer's menu so the user sees what's actually going on.
        if (root["incidents"] is JsonArray inc)
            foreach (var node in inc)
                if (ReadIncident(node?.AsObject(), pageUrl) is { } i) incidents.Add(i);

        if (root["scheduled_maintenances"] is JsonArray maint)
            foreach (var node in maint)
                if (node?.AsObject() is { } m && m["status"]?.ToString() == "in_progress"
                    && ReadIncident(m, pageUrl) is { } i)
                    incidents.Add(i);

        return new StatusInfo(level, description, incidents, pageUrl, DateTime.Now, true, null);
    }

    private static StatusIncident? ReadIncident(JsonObject? o, string pageUrl)
    {
        if (o is null) return null;
        string name = o["name"]?.ToString() ?? "Incident";
        string impact = o["impact"]?.ToString() ?? "";
        string state = o["status"]?.ToString() ?? "";
        string url = o["shortlink"]?.ToString() is { Length: > 0 } s ? s : pageUrl;

        // Statuspage lists incident_updates newest-first; the first body is the latest word on it.
        string? latest = null;
        if (o["incident_updates"] is JsonArray ups && ups.Count > 0)
            latest = ups[0]?["body"]?.ToString();

        return new StatusIncident(name, impact, state, latest, url);
    }

    private static StatusLevel MapIndicator(string? indicator) => indicator switch
    {
        "minor"       => StatusLevel.Minor,
        "major"       => StatusLevel.Major,
        "critical"    => StatusLevel.Critical,
        "maintenance" => StatusLevel.Maintenance,
        _             => StatusLevel.None,
    };
}
