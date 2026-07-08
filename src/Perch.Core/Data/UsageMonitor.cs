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

            var (fivePct, fiveReset) = ReadWindow(root, "five_hour");
            var (sevenPct, sevenReset) = ReadWindow(root, "seven_day");

            _last = new UsageInfo(fivePct, sevenPct, fiveReset, sevenReset, DateTime.Now, true, null);
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
