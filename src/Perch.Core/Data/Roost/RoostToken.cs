namespace Perch.Data.Roost;

/// <summary>
/// How the Roost persists "this session": a <c>pid/sessionId</c> token. A pid alone can't be trusted after a restart
/// (it gets recycled), so a token only ever matches the live pane whose pid and session id both agree. Shared by the
/// roster's closed set and the tabs' cells.
/// <para>A <b>dormant</b> pane (docs/session-recovery-plan.md, R6: a session shown with no process) has no pid. Its key is
/// <see cref="DormantKey"/> (<c>~sessionId</c>, which no pid can equal) and its token the pid-less <c>~/sessionId</c>.</para>
/// </summary>
public static class RoostToken
{
    /// <summary>The pid half of a dormant pane's token.</summary>
    public const string DormantPid = "~";

    public static string Format(string pid, string sessionId) => $"{pid}/{sessionId}";

    /// <summary>A pane's token: <c>pid/sessionId</c>, or <c>~/sessionId</c> for a dormant pane.</summary>
    public static string ForPane(string key, string sessionId) =>
        Format(IsDormantKey(key) ? DormantPid : key, sessionId);

    /// <summary>A token's parts, or null for a malformed one (null, no slash, or nothing either side of it).</summary>
    public static (string Pid, string SessionId)? Parse(string? token)
    {
        if (token is null) return null;
        int slash = token.IndexOf('/');
        return slash <= 0 || slash == token.Length - 1 ? null : (token[..slash], token[(slash + 1)..]);
    }

    /// <summary>The pane key of <paramref name="sessionId"/> shown dormant.</summary>
    public static string DormantKey(string sessionId) => DormantPid + sessionId;

    public static bool IsDormantKey(string? key) => key is { Length: > 1 } && key[0] == DormantPid[0];
}
