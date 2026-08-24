namespace Perch.Social;

/// <summary>
/// Optional credentials for the developer "puppet" account the social testing tool drives (see the debug
/// window in the app head). They're prefilled into that tool — and, when both email and password are present,
/// used to sign the puppet in automatically on open — so a tester needn't retype them every run. Never shipped
/// and never touched by the real client: only the DEBUG-only testing tool reads this.
///
/// Resolution mirrors <see cref="SupabaseConfig"/>: environment variables first, then the repo-root
/// <c>.env.local</c> (dev checkouts only). Keys:
/// <list type="bullet">
///   <item><c>PERCH_SOCIAL_DEBUG_EMAIL</c> — the puppet user's email.</item>
///   <item><c>PERCH_SOCIAL_DEBUG_PASSWORD</c> — its password.</item>
///   <item><c>PERCH_SOCIAL_DEBUG_HANDLE</c> — (optional) a handle to claim for the puppet on first sign-in.</item>
/// </list>
/// </summary>
public sealed record SocialDebugConfig(string? Email, string? Password, string? Handle)
{
    public static readonly SocialDebugConfig Empty = new(null, null, null);

    /// <summary>True once at least an email and password are available to sign the puppet in.</summary>
    public bool HasCredentials => !string.IsNullOrWhiteSpace(Email) && !string.IsNullOrWhiteSpace(Password);

    public static SocialDebugConfig Resolve()
    {
        var email = Clean(Environment.GetEnvironmentVariable("PERCH_SOCIAL_DEBUG_EMAIL"));
        var password = Clean(Environment.GetEnvironmentVariable("PERCH_SOCIAL_DEBUG_PASSWORD"));
        var handle = Clean(Environment.GetEnvironmentVariable("PERCH_SOCIAL_DEBUG_HANDLE"));

        // Fill any gaps from the repo-root .env.local (found only inside a checkout).
        if (email is null || password is null || handle is null)
        {
            try
            {
                if (DotEnv.FindRepoEnvLocal(AppContext.BaseDirectory) is { } envFile)
                {
                    var map = DotEnv.Parse(File.ReadAllText(envFile));
                    email ??= Clean(map.GetValueOrDefault("PERCH_SOCIAL_DEBUG_EMAIL"));
                    password ??= Clean(map.GetValueOrDefault("PERCH_SOCIAL_DEBUG_PASSWORD"));
                    handle ??= Clean(map.GetValueOrDefault("PERCH_SOCIAL_DEBUG_HANDLE"));
                }
            }
            catch { /* best-effort: whatever came from the environment stands */ }
        }

        return new(email, password, handle);
    }

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
