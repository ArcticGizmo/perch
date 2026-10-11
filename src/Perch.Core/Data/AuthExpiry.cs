using System.Text.Json.Nodes;
using Perch.Platform;

namespace Perch.Data;

/// <summary>
/// The two clocks on a Claude Code sign-in, read from the <c>claudeAiOauth</c> block of its credentials blob
/// (<see cref="IClaudeCredentials"/>): <paramref name="AccessExpiresAt"/> is when the short-lived (~8h) access
/// token lapses (<c>expiresAt</c>), and <paramref name="RefreshExpiresAt"/> is when the refresh token does
/// (<c>refreshTokenExpiresAt</c>) — past that nothing can mint a new access token, so the account has to
/// <c>/login</c> again. Both arrive as Unix epoch milliseconds and are held as local times, like the usage resets.
/// Either is null when the blob doesn't carry it (not signed in, an older CLI, an API-key setup).
///
/// <para>Perch only <em>reads</em> these. An interactive CLI refreshes the access token on a request made near
/// expiry, but a headless (stream-json) session — every Perch-controlled one — doesn't, and a running process
/// never re-reads the file, so Perch surfaces the clocks rather than refreshing them itself (a refresh rotates the
/// refresh token and would log out any process still holding the old one).</para>
/// </summary>
internal sealed record AuthExpiry(DateTime? AccessExpiresAt, DateTime? RefreshExpiresAt)
{
    public static AuthExpiry None { get; } = new(null, null);

    public bool IsKnown => AccessExpiresAt is not null || RefreshExpiresAt is not null;

    /// <summary>Reads the expiry pair from a credentials blob; <see cref="None"/> when absent/unparseable.</summary>
    internal static AuthExpiry FromJson(string? json)
    {
        if (string.IsNullOrEmpty(json))
            return None;
        try
        {
            if (JsonNode.Parse(json)?["claudeAiOauth"] is not JsonObject oauth)
                return None;
            return new AuthExpiry(EpochMs(oauth["expiresAt"]), EpochMs(oauth["refreshTokenExpiresAt"]));
        }
        catch
        {
            return None;
        }
    }

    /// <summary>The pair for <paramref name="dir"/>: the primary through the OS credential store, any other dir from
    /// its own <c>.credentials.json</c> (as the usage poll reads it). File/Keychain IO — call off the UI thread.</summary>
    internal static AuthExpiry ReadFor(ClaudeConfigDir dir, IClaudeCredentials primary) =>
        FromJson(dir.Equals(ClaudeConfigSet.Instance.Primary)
            ? primary.ReadCredentialsJson()
            : UsageMonitor.ReadFile(dir.CredentialsFile));

    private static DateTime? EpochMs(JsonNode? n)
    {
        if (n is null || !long.TryParse(n.ToString(), out var ms) || ms <= 0)
            return null;
        try { return DateTimeOffset.FromUnixTimeMilliseconds(ms).LocalDateTime; }
        catch (ArgumentOutOfRangeException) { return null; }
    }
}

/// <summary>How loudly an expiry clock should read: <see cref="Normal"/> sits dimmed, <see cref="Soon"/> takes the
/// warning hue, <see cref="Expired"/> the error hue.</summary>
internal enum AuthExpiryLevel { Normal, Soon, Expired }

/// <summary>The thresholds and compact labels for <see cref="AuthExpiry"/>'s clocks, shared by the overlay's usage
/// strip and the session window's banner.</summary>
internal static class AuthExpiryRules
{
    /// <summary>The access token on an <em>account</em> (the overlay strip). Claude Code refreshes it on a request
    /// made within ~5 minutes of expiry, so a warning any earlier would mostly flag something about to fix itself;
    /// this sits just past that point.</summary>
    public static readonly TimeSpan AccountAccessSoon = TimeSpan.FromMinutes(4);

    /// <summary>The access token a Perch-controlled <em>session</em> loaded at spawn. Nothing refreshes it inside a
    /// headless session, so the banner warns well ahead.</summary>
    public static readonly TimeSpan SessionAccessSoon = TimeSpan.FromMinutes(30);

    /// <summary>The refresh token — after this the account must <c>/login</c> again.</summary>
    public static readonly TimeSpan RefreshSoon = TimeSpan.FromDays(2);

    public static AuthExpiryLevel Level(DateTime expiresAt, DateTime now, TimeSpan soon) =>
        expiresAt <= now ? AuthExpiryLevel.Expired
        : expiresAt - now < soon ? AuthExpiryLevel.Soon
        : AuthExpiryLevel.Normal;

    // The labels are a small closed set, so each is built once and reused — the overlay calls Short on every paint.
    private static readonly Dictionary<(char, int), string> Labels = new();
    private static readonly object LabelsLock = new();

    /// <summary>The compact countdown: <c>exp</c> once lapsed, then minutes under an hour (<c>45m</c>), hours under
    /// two days (<c>6h</c>, <c>36h</c>) and whole days beyond (<c>27d</c>). Rounds down, so a label never
    /// overstates the time left.</summary>
    public static string Short(TimeSpan remaining)
    {
        if (remaining <= TimeSpan.Zero) return "exp";
        var (unit, n) = remaining.TotalMinutes < 60 ? ('m', Math.Max(1, (int)remaining.TotalMinutes))
            : remaining.TotalHours < 48 ? ('h', (int)remaining.TotalHours)
            : ('d', (int)remaining.TotalDays);
        lock (LabelsLock)
        {
            if (!Labels.TryGetValue((unit, n), out var s))
                Labels[(unit, n)] = s = $"{n}{unit}";
            return s;
        }
    }

    /// <summary>
    /// The session window's sign-in banner: what to tell the user about a Perch-controlled session's sign-in, or
    /// null text when there's nothing worth a banner. Two clocks, read from different places:
    /// <list type="bullet">
    /// <item>the <b>access token</b> is the one the running <paramref name="process"/> loaded at spawn — nothing
    /// refreshes it inside a headless session, so it warns <see cref="SessionAccessSoon"/> ahead, and once it lapses
    /// only a restart (End, then Resume) picks up a fresh one. A dormant session has no process yet (its wake loads
    /// the current credentials), so it gets no access line.</item>
    /// <item>the <b>login</b> (refresh token) is the <paramref name="account"/>'s, as on disk now — that's what a
    /// restart would load — falling back to the process's when the account clock is unknown.</item>
    /// </list>
    /// The level is the louder of the two lines.
    /// </summary>
    public static (string? Text, AuthExpiryLevel Level) Banner(
        AuthExpiry process, AuthExpiry account, bool running, bool dormant, DateTime now)
    {
        if (!running && !dormant) return (null, AuthExpiryLevel.Normal);
        var lines = new List<string>(2);
        var level = AuthExpiryLevel.Normal;

        if (running && process.AccessExpiresAt is { } access)
        {
            var l = Level(access, now, SessionAccessSoon);
            if (l == AuthExpiryLevel.Expired)
                lines.Add($"This session's sign-in expired at {access:t}, and Perch sessions can't refresh it — prompts will "
                        + "fail until it restarts. End the session, then Resume it to pick up a fresh sign-in.");
            else if (l == AuthExpiryLevel.Soon)
                lines.Add($"This session's sign-in expires in {Long(access - now)} ({access:t}), and Perch sessions can't "
                        + "refresh it. After that, End the session and Resume it to pick up a fresh sign-in.");
            if (l > level) level = l;
        }

        if ((account.RefreshExpiresAt ?? process.RefreshExpiresAt) is { } login)
        {
            var l = Level(login, now, RefreshSoon);
            if (l == AuthExpiryLevel.Expired)
                lines.Add(dormant
                    ? "Your login for this account has expired. Run /login in Claude Code before replying."
                    : "Your login for this account has expired. Run /login in Claude Code, then restart this session.");
            else if (l == AuthExpiryLevel.Soon)
                lines.Add($"Your login for this account expires in {Long(login - now)} ({login:ddd d MMM} {login:t}). "
                        + "Run /login in Claude Code before then.");
            if (l > level) level = l;
        }

        return (lines.Count == 0 ? null : string.Join('\n', lines), level);
    }

    /// <summary>A sentence-friendly span for tooltips and the banner: <c>12 min</c>, <c>5 h 20 min</c>, <c>3 days</c>.</summary>
    public static string Long(TimeSpan remaining)
    {
        if (remaining <= TimeSpan.Zero) return "0 min";
        if (remaining.TotalMinutes < 60) return $"{Math.Max(1, (int)remaining.TotalMinutes)} min";
        if (remaining.TotalHours < 48)
        {
            int h = (int)remaining.TotalHours, m = remaining.Minutes;
            return m == 0 ? $"{h} h" : $"{h} h {m} min";
        }
        int d = (int)remaining.TotalDays;
        return d == 1 ? "1 day" : $"{d} days";
    }
}
