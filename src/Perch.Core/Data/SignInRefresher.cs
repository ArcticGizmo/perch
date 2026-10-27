using System.Diagnostics;
using System.Text.Json.Nodes;
using Perch.Platform;

namespace Perch.Data;

/// <summary>Where one config dir's sign-in is read from: the primary through the OS credential store behind
/// <see cref="IClaudeCredentials"/>, any other dir from its own <c>.credentials.json</c> (the same split the usage poll
/// uses). <see cref="IsPrimary"/> decides whether the refresh run inherits Perch's environment or pins the dir.</summary>
internal sealed record SignInStore(ClaudeConfigDir Dir, bool IsPrimary, Func<string?> Read)
{
    public static SignInStore For(ClaudeConfigDir dir, IClaudeCredentials primary) =>
        dir.Equals(ClaudeConfigSet.Instance.Primary)
            ? new(dir, true, primary.ReadCredentialsJson)
            : new(dir, false, () => UsageMonitor.ReadFile(dir.CredentialsFile));
}

/// <summary>What one refresh pass did for a dir. <see cref="LoginRequired"/> means the sign-in can't be renewed
/// without <c>claude auth login</c>; <see cref="Failed"/> is worth retrying later.</summary>
internal enum RefreshOutcome { NotSignedIn, NotDue, Refreshed, Failed, LoginRequired }

internal sealed record RefreshResult(RefreshOutcome Outcome, string? Reason = null);

/// <summary>
/// The opt-in sign-in refresher: gets a config dir's OAuth access token renewed <see cref="Lead"/> before it expires,
/// so the usage poll keeps working and a session started or restarted later picks up a live token even when no
/// interactive Claude Code is running.
///
/// <para>Perch doesn't talk to the token endpoint itself: that endpoint refuses clients that aren't Claude Code. Instead
/// it runs Claude Code briefly — <see cref="RefreshArgs"/> with empty input under the account's config dir. On startup
/// the CLI renews a token that's near expiry (taking its own refresh lock and writing its own storage, Keychain
/// included), then exits on the empty prompt, so no model call is made and nothing is saved; safe mode keeps hooks,
/// plugins and MCP servers out of it. Perch judges the outcome by re-reading the stored sign-in: a later expiry means
/// renewed; blanked tokens mean Claude Code's refresh was rejected and it signed the account out.</para>
///
/// <para>It never logs in. An expired login, a missing refresh token or a signed-out account is
/// <see cref="RefreshOutcome.LoginRequired"/> with a reason, and the CLI isn't run for it.</para>
/// </summary>
internal sealed class SignInRefresher
{
    /// <summary>How far ahead of expiry the access token is renewed — Claude Code's own refresh point.</summary>
    public static readonly TimeSpan Lead = TimeSpan.FromMinutes(5);

    internal const string RefreshArgs = "-p --safe-mode --no-session-persistence";
    private static readonly TimeSpan RunTimeout = TimeSpan.FromSeconds(60);

    // Runs the CLI for a store; returns why it couldn't run, or null once it has run (its exit code is always a failure,
    // from the empty prompt, so only the stored sign-in says what happened). Swappable for the tests.
    private readonly Func<SignInStore, CancellationToken, Task<string?>> _runClaude;

    public SignInRefresher(Func<SignInStore, CancellationToken, Task<string?>>? runClaude = null) =>
        _runClaude = runClaude ?? RunClaudeAsync;

    /// <summary>The <c>claudeAiOauth</c> fields the refresher needs. Times are Unix epoch milliseconds.</summary>
    internal sealed record OAuthBlob(string? AccessToken, string? RefreshToken, long? ExpiresAtMs, long? RefreshExpiresAtMs)
    {
        public static OAuthBlob? Parse(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try
            {
                if (JsonNode.Parse(json)?["claudeAiOauth"] is not JsonObject o) return null;
                return new OAuthBlob(Str(o["accessToken"]), Str(o["refreshToken"]), Ms(o["expiresAt"]),
                    Ms(o["refreshTokenExpiresAt"]));
            }
            catch
            {
                return null;
            }
        }

        private static string? Str(JsonNode? n) =>
            n is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0 ? s : null;

        private static long? Ms(JsonNode? n) =>
            n is not null && long.TryParse(n.ToString(), out var ms) && ms > 0 ? ms : null;
    }

    internal enum Plan { NotSignedIn, SignedOut, NotDue, Due, LoginExpired, NoRefreshToken }

    /// <summary>What a pass should do with <paramref name="blob"/> at <paramref name="utcNow"/>. Pure, for the tests.</summary>
    internal static Plan PlanFor(OAuthBlob? blob, DateTime utcNow)
    {
        if (blob is null) return Plan.NotSignedIn;
        // The block is there but both tokens are blank: how Claude Code leaves an account after its refresh is rejected.
        if (blob.AccessToken is null && blob.RefreshToken is null) return Plan.SignedOut;
        long now = new DateTimeOffset(utcNow).ToUnixTimeMilliseconds();
        if (blob.RefreshExpiresAtMs is { } loginEnds && loginEnds <= now) return Plan.LoginExpired;
        if (blob.ExpiresAtMs is not { } accessEnds) return Plan.NotDue;
        if (accessEnds - now > (long)Lead.TotalMilliseconds) return Plan.NotDue;
        // Without a refresh token a lapsing access token can't be renewed — but it's only a login problem once it lapses.
        if (blob.RefreshToken is null) return accessEnds <= now ? Plan.NoRefreshToken : Plan.NotDue;
        return Plan.Due;
    }

    /// <summary>Gets <paramref name="store"/>'s sign-in renewed if it's within <see cref="Lead"/> of expiring. File IO and
    /// a short CLI run — call off the UI thread. Never throws.</summary>
    public async Task<RefreshResult> RefreshIfDueAsync(SignInStore store, DateTime utcNow, CancellationToken ct = default)
    {
        try
        {
            var before = OAuthBlob.Parse(store.Read());
            var plan = PlanFor(before, utcNow);
            if (plan != Plan.Due) return Settled(plan, before);

            if (await _runClaude(store, ct).ConfigureAwait(false) is { } error)
                return new(RefreshOutcome.Failed, error);

            var after = OAuthBlob.Parse(store.Read());
            if (after is { AccessToken: null, RefreshToken: null })
                return new(RefreshOutcome.LoginRequired,
                    "Claude Code's refresh of the sign-in was rejected, so it signed this account out.");
            if (after?.ExpiresAtMs > before!.ExpiresAtMs)
                return new(RefreshOutcome.Refreshed);
            return new(RefreshOutcome.Failed, "Claude Code ran but didn't renew the sign-in");
        }
        catch (Exception ex)
        {
            return new(RefreshOutcome.Failed, ex.Message);
        }
    }

    private static RefreshResult Settled(Plan plan, OAuthBlob? blob) => plan switch
    {
        Plan.NotSignedIn => new(RefreshOutcome.NotSignedIn),
        Plan.SignedOut => new(RefreshOutcome.LoginRequired,
            "This account is signed out: Claude Code clears the sign-in when its refresh is rejected, or after a logout."),
        Plan.LoginExpired => new(RefreshOutcome.LoginRequired, LoginExpiredReason(blob!.RefreshExpiresAtMs!.Value)),
        Plan.NoRefreshToken => new(RefreshOutcome.LoginRequired, "The sign-in expired and has no refresh token to renew it with."),
        _ => new(RefreshOutcome.NotDue),
    };

    private static string LoginExpiredReason(long ms)
    {
        var at = DateTimeOffset.FromUnixTimeMilliseconds(ms).LocalDateTime;
        return $"The login expired on {at:ddd d MMM} at {at:t}, so the sign-in can't be renewed.";
    }

    // The config dir itself is the working directory: a folder with no project config for the run to pick up. The
    // primary inherits Perch's environment (pinning ~/.claude would move Claude's .claude.json lookup); any other dir is
    // pinned with CLAUDE_CONFIG_DIR. The output is drained and dropped — it's only the empty-prompt error.
    private static async Task<string?> RunClaudeAsync(SignInStore store, CancellationToken ct)
    {
        try
        {
            var psi = ClaudeCli.CreateStartInfo(RefreshArgs);
            psi.WorkingDirectory = store.Dir.Root;
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardInput = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            foreach (var name in ArtifactPublisher.ScrubbedEnv) psi.Environment.Remove(name);
            if (!store.IsPrimary) psi.Environment["CLAUDE_CONFIG_DIR"] = store.Dir.Root;

            using var proc = Process.Start(psi);
            if (proc is null) return "Couldn't start the claude CLI";
            proc.StandardInput.Close();
            var drain = Task.WhenAll(proc.StandardOutput.ReadToEndAsync(ct), proc.StandardError.ReadToEndAsync(ct));

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(RunTimeout);
            try
            {
                await proc.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                return $"The claude CLI didn't finish within {RunTimeout.TotalSeconds:0} s";
            }
            await drain.ConfigureAwait(false);
            return null;
        }
        catch (Exception ex)
        {
            return "Couldn't run the claude CLI: " + ex.Message;
        }
    }
}
