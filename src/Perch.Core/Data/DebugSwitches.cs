namespace Perch.Data;

/// <summary>
/// The opt-in debug logs switched on by environment variable (review fixes CP16). They used to write wherever
/// they liked (<c>PERCH_SESSION_LOG</c> took any path and appended every raw line of a session's stream, prompts
/// and tool output included, uncapped; <c>PERCH_VDM_DEBUG</c> wrote to <c>%TEMP%</c>) and nothing showed they were
/// on. Now:
/// <list type="bullet">
///   <item>the value only switches a log on — any non-empty value other than <c>0</c>/<c>false</c>. It is
///     <b>never</b> used as a path: every log goes to Perch's own per-user <see cref="DiagnosticLog.Dir"/>;</item>
///   <item>each file is size-capped (it starts over past the cap);</item>
///   <item><see cref="ActiveWarning"/> names whatever is on, and the tray shows it, so a switch set by
///     accident (or by something else) is visible.</item>
/// </list>
/// </summary>
public static class DebugSwitches
{
    public const string SessionLogVar = "PERCH_SESSION_LOG";
    public const string VdmDebugVar = "PERCH_VDM_DEBUG";

    public const string SessionLogFile = "session-stream.log";
    public const string VdmLogFile = "vdm.log";

    // A raw session stream is bulky (it's every line), so it gets a larger cap than the one-line-per-event logs.
    public const long SessionLogMaxBytes = 32L * 1024 * 1024;

    public static bool SessionLogOn => IsOn(SessionLogVar);
    public static bool VdmDebugOn => IsOn(VdmDebugVar);

    /// <summary>A one-line warning naming the switches that are on, or null when none is.</summary>
    public static string? ActiveWarning => Warning(SessionLogOn, VdmDebugOn);

    internal static string? Warning(bool sessionLog, bool vdm)
    {
        var on = new List<string>(2);
        if (sessionLog) on.Add($"{SessionLogVar} (records full session output)");
        if (vdm) on.Add(VdmDebugVar);
        return on.Count == 0 ? null : $"Debug logging on: {string.Join(", ", on)} — see {DiagnosticLog.Dir}";
    }

    internal static bool IsOn(string variable) => IsOnValue(Environment.GetEnvironmentVariable(variable));

    internal static bool IsOnValue(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Trim() != "0"
        && !value.Trim().Equals("false", StringComparison.OrdinalIgnoreCase);
}
