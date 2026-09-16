using Perch.Data;

namespace Perch.Avalonia.Services;

/// <summary>
/// UI phrasing for a config dir's live sign-in state (Layer 2), shared by the Config directories
/// settings page and the overlay dir-chip tooltip so the two never diverge. Kept out of
/// <see cref="ClaudeJsonReader"/> because Core carries no UI text.
/// </summary>
internal static class OrgDisplay
{
    /// <summary>A one-line "Signed in: …" descriptor for a sign-in snapshot: the org when org-bound, the
    /// account email (marked "no org") for a personal login, else "Not signed in".</summary>
    public static string SignInText(ClaudeSignIn s) => s.State switch
    {
        SignInState.Org      => "Signed in: " + s.Org!.DisplayName,
        SignInState.Personal => s.Email is { Length: > 0 } e ? $"Signed in: {e} (no org)" : "Signed in (no org)",
        _                    => "Not signed in",
    };
}
