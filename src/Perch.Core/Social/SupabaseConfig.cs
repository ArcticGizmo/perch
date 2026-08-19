namespace Perch.Social;

/// <summary>
/// Where the Supabase project URL + anon (public) key come from. Both are safe to expose in the client — the
/// anon key is publishable and the data is guarded by row-level security, not by hiding the key — but Perch
/// still resolves them from outside the source tree by default so a public repo needn't carry them.
///
/// Resolution order (first that's fully populated wins):
/// <list type="number">
///   <item><b>Environment</b> — <c>PERCH_SUPABASE_URL</c> / <c>PERCH_SUPABASE_ANON_KEY</c>. The highest
///     override: set them in your shell before <c>dotnet run</c>, or in CI.</item>
///   <item><b>Repo <c>.env.local</c></b> (dev builds) — <c>KEY=VALUE</c> lines in the <c>.env.local</c> beside
///     <c>perch.slnx</c> at the repo root, found by walking up from the running binary (see <see cref="DotEnv"/>).
///     Gitignored; the natural place to keep dev keys in a checkout. Not present in a shipped install.</item>
///   <item><b>Compiled-in defaults</b> — <see cref="SupabaseDefaults"/>, empty unless a release build embeds
///     them. This is the only path that ships to end users, since a distributed desktop app has no server to
///     inject the key at runtime.</item>
/// </list>
/// <see cref="IsConfigured"/> is false when nothing supplied both values — the app then keeps Social inert
/// (the sign-in button explains it needs configuring) rather than throwing.
/// </summary>
public sealed record SupabaseConfig(string Url, string AnonKey)
{
    public bool IsConfigured => !string.IsNullOrWhiteSpace(Url) && !string.IsNullOrWhiteSpace(AnonKey);

    public static SupabaseConfig Resolve()
    {
        // 1) Environment (dev / CI).
        var envUrl = Environment.GetEnvironmentVariable("PERCH_SUPABASE_URL");
        var envKey = Environment.GetEnvironmentVariable("PERCH_SUPABASE_ANON_KEY");
        if (!string.IsNullOrWhiteSpace(envUrl) && !string.IsNullOrWhiteSpace(envKey))
            return new(envUrl.Trim(), envKey.Trim());

        // 2) Repo-root .env.local (dev builds — found only inside a checkout).
        try
        {
            if (DotEnv.FindRepoEnvLocal(AppContext.BaseDirectory) is { } envFile)
            {
                var map = DotEnv.Parse(File.ReadAllText(envFile));
                if (map.TryGetValue("PERCH_SUPABASE_URL", out var u) &&
                    map.TryGetValue("PERCH_SUPABASE_ANON_KEY", out var k) &&
                    !string.IsNullOrWhiteSpace(u) && !string.IsNullOrWhiteSpace(k))
                    return new(u.Trim(), k.Trim());
            }
        }
        catch { /* best-effort: fall through */ }

        // 3) Compiled-in defaults (empty in a plain checkout; filled only for release).
        return new(SupabaseDefaults.Url, SupabaseDefaults.AnonKey);
    }
}
