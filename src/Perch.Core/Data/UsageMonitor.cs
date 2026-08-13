using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using Perch.Data;
using Perch.Platform;

namespace Perch.Data;

/// <summary>
/// Polls the account-wide rate-limit usage from the same undocumented endpoint Claude Code's
/// /usage command uses (GET https://api.anthropic.com/api/oauth/usage), authenticating with the
/// OAuth access token Claude Code stores in its credentials blob (the file ~/.claude/.credentials.json
/// on Windows/Linux, the login Keychain on macOS — sourced via <see cref="IClaudeCredentials"/>).
///
/// The token is read fresh on every poll and never written back — Claude Code refreshes it during
/// normal use, so an active user keeps it valid. When a fetch fails (network, expired token, shape
/// change) the last successful reading is returned with Ok=false so the UI can show it dimmed.
/// </summary>
internal sealed class UsageMonitor
{
    private const string UsageUrl = "https://api.anthropic.com/api/oauth/usage";
    private const string OAuthBeta = "oauth-2025-04-20";
    private const string FallbackVersion = "2.1.0";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    // Where the OAuth blob lives is OS-specific (file on Windows/Linux, Keychain on macOS) — behind a seam.
    private readonly IClaudeCredentials _credentials;

    // The most recent successful reading, so a failed fetch can still surface last-known values.
    private UsageInfo _last = UsageInfo.Empty;

    public UsageMonitor(IClaudeCredentials credentials) => _credentials = credentials;

    /// <summary>
    /// Fetches the current usage. Always resolves (never throws): on failure the result has
    /// Ok=false and carries the last successful percentages (if any) plus a human reason.
    /// </summary>
    public async Task<UsageInfo> FetchAsync()
    {
        try
        {
            var token = ReadAccessToken();
            if (string.IsNullOrEmpty(token))
                return Fail("Couldn't read Claude credentials — sign in to Claude Code");

            using var req = new HttpRequestMessage(HttpMethod.Get, UsageUrl);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            req.Headers.TryAddWithoutValidation("anthropic-beta", OAuthBeta);
            req.Headers.UserAgent.ParseAdd($"claude-code/{ReadCliVersion()}");
            req.Headers.Accept.ParseAdd("application/json");

            using var resp = await Http.SendAsync(req).ConfigureAwait(false);
            if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                return Fail("Token expired — open Claude Code to refresh");
            if (!resp.IsSuccessStatusCode)
                return Fail($"Usage service returned {(int)resp.StatusCode}");

            var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            var root = JsonNode.Parse(body)?.AsObject();
            if (root is null)
                return Fail("Couldn't parse usage response");

            _last = Parse(root);
            return _last;
        }
        catch (Exception ex)
        {
            return Fail("Couldn't reach usage service: " + ex.Message);
        }
    }

    // Re-tags the last good reading as failed (keeping its percentages for a dimmed display),
    // or returns an empty failed reading if we never succeeded.
    private UsageInfo Fail(string reason) => _last.Ok
        ? _last with { Ok = false, Error = reason }
        : UsageInfo.Empty with { Error = reason };

    /// <summary>
    /// Turns a usage-endpoint response body into a reading. Pure and internal so the parse can be
    /// unit-tested against a captured payload without going near the network.
    /// </summary>
    internal static UsageInfo Parse(JsonObject root)
    {
        var (fivePct, fiveReset) = ReadWindow(root, "five_hour");
        var (sevenPct, sevenReset) = ReadWindow(root, "seven_day");

        return new UsageInfo(fivePct, sevenPct, fiveReset, sevenReset, Clock.Now, true, null)
        {
            Scoped = ReadScoped(root, sevenReset),
            ExtraUsage = ReadExtraUsage(root),
        };
    }

    /// <summary>
    /// Reads the <c>extra_usage</c> block — the monthly overage spend against the account's cap. Both amounts
    /// come from this block in major currency units (<c>used_credits</c> / <c>monthly_limit</c>); the sibling
    /// <c>spend</c> block carries the same figures but with an ambiguous minor-unit scaling, so we take the
    /// self-describing one. Returns null when the block is absent (older shape / no such account feature).
    /// </summary>
    private static ExtraUsageInfo? ReadExtraUsage(JsonObject root)
    {
        if (root["extra_usage"] is not JsonObject e)
            return null;

        int places = Math.Clamp(
            e["decimal_places"] is { } dp && int.TryParse(dp.ToString(), out var n) ? n : 2, 0, 4);

        // Both amounts arrive in the currency's *minor* units (matching the sibling spend block's
        // amount_minor): a $14,000.00 cap is reported as 1_400_000 with decimal_places=2. Shift by
        // decimal_places to get major units. Used and limit share the scale, so the fill ratio is
        // unaffected — only the displayed dollar figures were wrong before this.
        decimal scale = Pow10(places);
        return new ExtraUsageInfo(
            Enabled: AsBool(e["is_enabled"]),
            Used: AsDecimal(e["used_credits"]) / scale,
            Limit: AsDecimal(e["monthly_limit"]) / scale,
            Currency: e["currency"]?.ToString() is { Length: > 0 } c ? c : "USD",
            DecimalPlaces: places,
            LimitReached: AsBool(e["spend_limit_reached"]));
    }

    // 10^n as a decimal, for the minor→major unit shift. n is already clamped to 0–4, so this is a
    // handful of multiplies (and Pow10(0) == 1, i.e. no shift for zero-decimal currencies like JPY).
    private static decimal Pow10(int n)
    {
        decimal r = 1m;
        for (int i = 0; i < n; i++) r *= 10m;
        return r;
    }

    // Defensive JSON scalar reads — the Parse path is unit-tested directly, so these never throw on a
    // missing/oddly-typed node; they fall back to a sane default instead.
    private static bool AsBool(JsonNode? n) => n is not null && bool.TryParse(n.ToString(), out var b) && b;

    private static decimal AsDecimal(JsonNode? n) =>
        n is not null && decimal.TryParse(n.ToString(),
            System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var v)
            ? v : 0m;

    /// <summary>
    /// Walks the response's <c>limits</c> array for <c>weekly_scoped</c> entries — the per-model weekly
    /// buckets. They appear *only* there: the top-level <c>seven_day_opus</c>/<c>seven_day_sonnet</c>
    /// keys are null even when a scoped bucket exists, so <see cref="ReadWindow"/> can't reach them.
    /// Entries without a model display name are skipped (nothing to caption a bar with).
    /// </summary>
    private static IReadOnlyList<ScopedUsage> ReadScoped(JsonObject root, DateTime? weeklyReset)
    {
        if (root["limits"] is not JsonArray limits)
            return [];

        var scoped = new List<ScopedUsage>();
        foreach (var node in limits)
        {
            if (node is not JsonObject entry) continue;
            if (entry["kind"]?.ToString() != "weekly_scoped") continue;

            var label = entry["scope"]?["model"]?["display_name"]?.ToString();
            if (string.IsNullOrWhiteSpace(label)) continue;

            double? percent = entry["percent"] is { } p && double.TryParse(p.ToString(), out var v) ? v : null;

            // Scoped entries carry a null resets_at in practice; weekly windows share a reset boundary,
            // so fall back to the account-wide weekly one to keep the pace marker meaningful.
            DateTime? resetsAt = null;
            if (entry["resets_at"]?.ToString() is { Length: > 0 } iso && DateTimeOffset.TryParse(iso, out var dto))
                resetsAt = dto.LocalDateTime;

            scoped.Add(new ScopedUsage(label, percent, resetsAt ?? weeklyReset));
        }
        return scoped;
    }

    private static (double? percent, DateTime? resetsAt) ReadWindow(JsonObject root, string key)
    {
        if (root[key] is not JsonObject window)
            return (null, null);

        double? percent = window["utilization"] is { } u && double.TryParse(u.ToString(), out var p) ? p : null;

        DateTime? resetsAt = null;
        if (window["resets_at"]?.ToString() is { Length: > 0 } iso &&
            DateTimeOffset.TryParse(iso, out var dto))
            resetsAt = dto.LocalDateTime;

        return (percent, resetsAt);
    }

    private string? ReadAccessToken()
    {
        var json = _credentials.ReadCredentialsJson();
        if (string.IsNullOrEmpty(json))
            return null;

        try
        {
            var root = JsonNode.Parse(json)?.AsObject();
            return root?["claudeAiOauth"]?["accessToken"]?.ToString();
        }
        catch
        {
            return null;
        }
    }

    // Use the version from the most recently-updated session file so our User-Agent matches the
    // installed CLI (the endpoint throttles unknown agents harder). Falls back to a constant.
    private string ReadCliVersion()
    {
        try
        {
            var dir = ClaudePaths.SessionsDir;
            if (!Directory.Exists(dir))
                return FallbackVersion;

            var newest = new DirectoryInfo(dir)
                .EnumerateFiles("*.json")
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .FirstOrDefault();
            if (newest is null)
                return FallbackVersion;

            using var fs = new FileStream(newest.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(fs);
            var version = JsonNode.Parse(reader.ReadToEnd())?["version"]?.ToString();
            return string.IsNullOrEmpty(version) ? FallbackVersion : version;
        }
        catch
        {
            return FallbackVersion;
        }
    }
}
