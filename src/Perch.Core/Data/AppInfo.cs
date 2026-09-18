namespace Perch.Data;

using System.Reflection;

/// <summary>
/// Static, app-wide identity: the running version and the GitHub locations the UI links to.
/// Centralised so the tray menu, settings window, and updater all agree on one source.
/// </summary>
internal static class AppInfo
{
    public const string RepoUrl   = "https://github.com/ArcticGizmo/perch";
    public const string IssuesUrl = RepoUrl + "/issues/new";

    /// <summary>The privacy policy (PRIVACY.md), rendered on GitHub so it opens on any install.</summary>
    public const string PrivacyUrl = RepoUrl + "/blob/main/PRIVACY.md";

    /// <summary>Support/privacy contact, shown in the account-deletion dialog and the privacy policy.</summary>
    public const string SupportEmail = "support@arcticgizmo.dev";

    /// <summary>
    /// The GitHub OAuth app's client id used for Social sign-in. Non-secret — it's public in the OAuth
    /// authorize redirect — so it's safe to compile in. Used only to deep-link a user to revoke Perch's
    /// authorization after they delete their account. Must match the GitHub provider on the Supabase project.
    /// </summary>
    public const string SocialGitHubClientId = "Ov23lipDKYVRDzCU5K9W";

    /// <summary>Deep-link to the user's connection page for the Perch OAuth app — its "Revoke access" button.</summary>
    public const string SocialGitHubRevokeUrl =
        "https://github.com/settings/connections/applications/" + SocialGitHubClientId;

    /// <summary>
    /// Human-readable version (e.g. "0.1.0"). Read from the assembly's informational version,
    /// stripping any "+commit" git metadata the SDK appends; falls back to the numeric version.
    /// </summary>
    public static string Version { get; } = ResolveVersion();

    private static string ResolveVersion()
    {
        var asm  = Assembly.GetEntryAssembly() ?? typeof(AppInfo).Assembly;
        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrEmpty(info))
        {
            var plus = info.IndexOf('+');
            return plus < 0 ? info : info[..plus];
        }
        return asm.GetName().Version?.ToString(3) ?? "0.0.0";
    }
}
