namespace Perch.Plugins;

/// <summary>
/// Enforces a plugin's grants on the messages it sends back. The rule Perch relies on: a plugin can emit
/// any message it likes, but the host <em>acts</em> only on ones its grants permit — a plugin that never
/// requested <c>notify</c> can print <c>{"type":"notify"}</c> all day and it will be dropped (and audited).
/// This is the host-side half of least privilege; the plugin process itself is untrusted.
/// </summary>
internal static class PluginCapabilityGate
{
    /// <summary>Whether the host should act on <paramref name="message"/> given <paramref name="grants"/>.
    /// <paramref name="reason"/> explains a denial (for the audit log). Messages that request no privileged
    /// host action (render, log, ready, unknown) are always allowed; unknown ones are simply ignored later.</summary>
    public static bool IsAllowed(PluginMessage message, PluginGrants grants, out string? reason)
    {
        reason = null;
        switch (message)
        {
            case PluginNotifyMessage when !grants.Notify:
                reason = "requested a notification without the 'notify' capability.";
                return false;

            // overlay.glyph is gated at load time (the manifest must list the extension point); a render
            // from a plugin that declared it needs no further capability, so allow.
            case PluginRenderMessage:
            case PluginReady:
            case PluginNotifyMessage:
            case PluginLogMessage:
            case PluginUnknownMessage:
                return true;

            default:
                reason = $"unhandled message type {message.GetType().Name}.";
                return false;
        }
    }
}
