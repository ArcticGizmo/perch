namespace Perch.Data.Roost;

/// <summary>
/// How the Roost persists "this session": a <c>pid/sessionId</c> token. A pid alone can't be trusted after a restart
/// (it gets recycled), so a token only ever matches the live pane whose pid and session id both agree. Shared by the
/// roster's closed set and the tabs' cells.
/// </summary>
public static class RoostToken
{
    public static string Format(string pid, string sessionId) => $"{pid}/{sessionId}";

    /// <summary>A token's parts, or null for a malformed one (null, no slash, or nothing either side of it).</summary>
    public static (string Pid, string SessionId)? Parse(string? token)
    {
        if (token is null) return null;
        int slash = token.IndexOf('/');
        return slash <= 0 || slash == token.Length - 1 ? null : (token[..slash], token[(slash + 1)..]);
    }
}
