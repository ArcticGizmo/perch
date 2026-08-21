namespace Perch.Plugins;

/// <summary>The session-lifecycle event names delivered to a plugin's <c>event</c> extension point. Kept
/// as constants so the host and plugin authors agree on spelling; append-only (a new event is additive).</summary>
internal static class SessionEvents
{
    /// <summary>A session is waiting for the user (finished, or awaiting input).</summary>
    public const string Attention = "session.attention";

    /// <summary>A session went idle.</summary>
    public const string Idle = "session.idle";

    /// <summary>A session completed its work.</summary>
    public const string Done = "session.done";

    /// <summary>A session hit an API error.</summary>
    public const string Error = "session.error";
}
