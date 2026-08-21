namespace Perch.Plugins;

/// <summary>
/// The host-side orchestrator: discovers plugins, and runs a plugin's one-shot request while enforcing its
/// grants in both directions. Outbound, it puts a context value (e.g. the session cwd) into the request
/// <em>only</em> if the grant allows it; inbound, it drops any message the grant forbids and records the
/// denial. The result is a structured <see cref="PluginPollResult"/> the UI head renders — this class is
/// UI-free (glyphs come back as data; notifications as intents the head routes through <c>INotifier</c>).
/// </summary>
internal sealed class PluginService
{
    private readonly IPluginSandbox _sandbox;
    private readonly string _perchVersion;
    private readonly TimeSpan _timeout;

    public PluginService(IPluginSandbox sandbox, string perchVersion, TimeSpan? timeout = null)
    {
        _sandbox = sandbox;
        _perchVersion = perchVersion;
        _timeout = timeout ?? TimeSpan.FromSeconds(20);
    }

    /// <summary>Runs one poll of a plugin and returns what it contributed. Requires a valid manifest;
    /// callers filter to <see cref="DiscoveredPlugin.Ok"/> plugins first. Never throws for plugin
    /// misbehaviour. <paramref name="grants"/> is the user-consented grant to enforce; when null (a local
    /// sideload / test) it falls back to what the manifest declares.</summary>
    public Task<PluginPollResult> PollAsync(
        DiscoveredPlugin plugin, PluginPollContext context, PluginGrants? grants = null, CancellationToken ct = default) =>
        RunAsync(plugin, PluginRequest.PollType, eventName: null, context, grants, ct);

    /// <summary>Delivers a session lifecycle event to a plugin (its <c>event</c> extension point) and
    /// returns anything it contributed in response. Same enforcement and safety as <see cref="PollAsync"/>.</summary>
    public Task<PluginPollResult> RaiseEventAsync(
        DiscoveredPlugin plugin, string eventName, PluginPollContext context,
        PluginGrants? grants = null, CancellationToken ct = default) =>
        RunAsync(plugin, PluginRequest.EventType, eventName, context, grants, ct);

    private async Task<PluginPollResult> RunAsync(
        DiscoveredPlugin plugin, string requestType, string? eventName,
        PluginPollContext context, PluginGrants? grants, CancellationToken ct)
    {
        var manifest = plugin.Manifest
            ?? throw new ArgumentException("plugin has no valid manifest", nameof(plugin));

        var effective = grants ?? PluginGrants.FromDeclared(manifest.Capabilities);
        var request = new PluginRequest(
            requestType,
            _perchVersion,
            effective.ToWire(),
            BuildContext(context, effective),
            Event: eventName);

        PluginRunResult run;
        try
        {
            var spec = new PluginLaunchSpec(plugin.Directory, manifest.Entry.Command, manifest.Entry.Args);
            using var proc = _sandbox.Launch(spec);
            run = await PluginSession.RunOnceAsync(proc, request, _timeout, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return PluginPollResult.Fault($"failed to launch: {ex.Message}");
        }

        return Interpret(manifest, effective, run);
    }

    // Only hand the plugin the context its grants permit. This is the outbound half of least privilege.
    private static IReadOnlyDictionary<string, string> BuildContext(PluginPollContext ctx, PluginGrants grants)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (grants.ReadCwd && !string.IsNullOrEmpty(ctx.Cwd)) map["cwd"] = ctx.Cwd!;
        if (grants.ReadSessions && !string.IsNullOrEmpty(ctx.SessionId)) map["sessionId"] = ctx.SessionId!;
        return map;
    }

    // Fold the raw messages into a result, dropping anything the grants forbid or the manifest didn't
    // declare (a render from a plugin that never listed overlay.glyph is ignored).
    private static PluginPollResult Interpret(PluginManifest manifest, PluginGrants grants, PluginRunResult run)
    {
        PluginGlyph? glyph = null;
        var notifications = new List<PluginNotifyMessage>();
        var denied = new List<string>();
        bool declaresGlyph = manifest.Declares(PluginExtensionPoints.OverlayGlyph);

        foreach (var msg in run.Messages)
        {
            if (!PluginCapabilityGate.IsAllowed(msg, grants, out var reason))
            {
                denied.Add(reason ?? "denied");
                continue;
            }

            switch (msg)
            {
                case PluginReady { Render: { } r } when declaresGlyph:
                    glyph = Sanitise(r);
                    break;
                case PluginRenderMessage rm when declaresGlyph:
                    glyph = Sanitise(rm.Glyph); // last render wins
                    break;
                case PluginRenderMessage or PluginReady { Render: not null }:
                    denied.Add("emitted a render but did not declare the 'overlay.glyph' extension point.");
                    break;
                case PluginNotifyMessage n:
                    notifications.Add(n);
                    break;
                // logs / ready-without-render / unknown → nothing to surface
            }
        }

        return new PluginPollResult(glyph, notifications, denied, run.TimedOut, run.ExitCode);
    }

    // Untrusted text: clamp lengths and strip control chars so a plugin can't blow out the overlay layout
    // or inject newlines into a tooltip.
    private static PluginGlyph Sanitise(PluginGlyph g) => new(
        Clamp(g.Glyph, 8),
        Clamp(g.Text, 24),
        Clamp(g.Tooltip, 200));

    private static string Clamp(string s, int max)
    {
        var cleaned = new string(s.Where(c => !char.IsControl(c)).ToArray()).Trim();
        return cleaned.Length <= max ? cleaned : cleaned[..max];
    }
}

/// <summary>Candidate context the host <em>can</em> pass a plugin; <see cref="PluginService"/> forwards
/// each value only when the grant allows it.</summary>
internal sealed record PluginPollContext(string? Cwd = null, string? SessionId = null)
{
    public static readonly PluginPollContext Empty = new();
}

/// <summary>The outcome of polling a plugin: the glyph to paint (if any), notifications to raise, actions
/// that were denied (for the audit log), and health signals.</summary>
internal sealed record PluginPollResult(
    PluginGlyph? Glyph,
    IReadOnlyList<PluginNotifyMessage> Notifications,
    IReadOnlyList<string> DeniedActions,
    bool TimedOut,
    int ExitCode)
{
    /// <summary>True when the plugin ran cleanly (exited 0, wasn't killed for a timeout).</summary>
    public bool Ok => !TimedOut && ExitCode == 0;

    public static PluginPollResult Fault(string reason) =>
        new(null, [], [reason], TimedOut: false, ExitCode: -1);
}
