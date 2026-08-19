namespace Perch.Social;

/// <summary>
/// Compiled-in fallback for the Supabase URL + publishable key — the last resort in <see cref="SupabaseConfig"/>,
/// and the <em>only</em> source that ships to end users (a distributed desktop app has no server to inject
/// the key at runtime). Empty in a plain checkout, so a dev build relies on the env vars or the local file.
///
/// A release build fills these in one of two ways (decided per project):
/// <list type="bullet">
///   <item>the values are pasted here and committed — simplest, and acceptable because the publishable key is
///     safe to expose and RLS is the real boundary; or</item>
///   <item>this file is left empty in git and the release workflow rewrites it (or passes
///     <c>/p:DefineConstants</c>) from a CI secret at publish time, keeping the key out of a public repo.</item>
/// </list>
/// Either way the key is only ever the <em>publishable</em> (client) key — never <c>service_role</c>.
/// </summary>
internal static class SupabaseDefaults
{
    public const string Url = "";
    public const string PublishableKey = "";
}
