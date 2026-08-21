namespace Perch.Plugins;

/// <summary>
/// A parsed, validated <c>perch-plugin.json</c>. This is the whole security surface of a plugin: the
/// host grants only what <see cref="Capabilities"/> declares and lets the plugin do only what
/// <see cref="ExtensionPoints"/> lists. Built exclusively via <see cref="PluginManifestParser"/> — the
/// constructor is internal so an un-validated manifest can't exist. See docs/pluggability-plan.md.
/// </summary>
internal sealed class PluginManifest
{
    /// <summary>The only manifest schema this host understands. A higher value is rejected with a clear
    /// message rather than mis-parsed; bump this (and stay additive) when the wire format grows.</summary>
    public const int SupportedSchema = 1;

    public required int Schema { get; init; }

    /// <summary>Reverse-DNS, globally unique, immutable (used as the on-disk install folder name).</summary>
    public required string Id { get; init; }

    public required string Name { get; init; }
    public required string Version { get; init; }
    public string? Description { get; init; }
    public string? Author { get; init; }
    public string? Homepage { get; init; }

    /// <summary>The lowest Perch version this plugin supports; null = no floor.</summary>
    public string? MinPerch { get; init; }

    public required PluginEntry Entry { get; init; }

    /// <summary>The extension points the plugin contributes to — a subset of
    /// <see cref="PluginExtensionPoints.All"/>. Non-empty (validated).</summary>
    public required IReadOnlyList<string> ExtensionPoints { get; init; }

    public required PluginCapabilities Capabilities { get; init; }

    public bool Declares(string extensionPoint) => ExtensionPoints.Contains(extensionPoint);
}

/// <summary>How Perch launches the plugin. v1 only supports an out-of-process executable/script.</summary>
internal sealed class PluginEntry
{
    /// <summary>The only supported entry kind in v1.</summary>
    public const string ProcessType = "process";

    public required string Type { get; init; }

    /// <summary>The program to run (a launcher on PATH like <c>powershell</c>/<c>node</c>, or a path
    /// relative to the plugin's own directory).</summary>
    public required string Command { get; init; }

    public required IReadOnlyList<string> Args { get; init; }

    /// <summary><see cref="PluginEntryMode.OneShot"/> (spawn per tick, the default — what scripts want) or
    /// <see cref="PluginEntryMode.Persistent"/> (kept warm, host owns the interval).</summary>
    public required PluginEntryMode Mode { get; init; }
}

internal enum PluginEntryMode
{
    OneShot = 0,
    Persistent = 1,
}

/// <summary>
/// The permissions a plugin requests. Absent = denied — the host grants only what is present here, and
/// nothing implicitly. <see cref="Network"/> is a host allowlist (not a bool) so egress intent is explicit;
/// an empty list means "no network". See the security model in docs/pluggability-plan.md.
/// </summary>
internal sealed class PluginCapabilities
{
    /// <summary>The lowest poll interval the host will honour, in seconds. A plugin asking for less is
    /// clamped up to this — a busy-loop plugin can't be requested into existence.</summary>
    public const int MinPollIntervalSec = 15;

    /// <summary>The interval used when a polling plugin omits <c>poll.intervalSec</c>.</summary>
    public const int DefaultPollIntervalSec = 300;

    /// <summary>Hostnames the plugin declares it will talk to. Empty = no network. (v1 uses this for
    /// consent/disclosure; airtight per-plugin egress enforcement is a later sandbox milestone.)</summary>
    public IReadOnlyList<string> Network { get; init; } = [];

    /// <summary>May read <c>~/.claude</c> session/transcript data the host passes in the request context.</summary>
    public bool ReadSessions { get; init; }

    /// <summary>May receive the active session's project directory in the request context.</summary>
    public bool ReadCwd { get; init; }

    /// <summary>May ask the host to raise a Perch notification.</summary>
    public bool Notify { get; init; }

    /// <summary>Requested poll cadence, already clamped to at least <see cref="MinPollIntervalSec"/>.</summary>
    public int PollIntervalSec { get; init; } = DefaultPollIntervalSec;

    public bool RequestsNetwork => Network.Count > 0;
}

/// <summary>The extension points a plugin may contribute to. A manifest listing anything outside this set
/// fails validation (strict — an unknown point could hide intent).</summary>
internal static class PluginExtensionPoints
{
    /// <summary>Contribute a glyph/badge + tooltip to the overlay strip.</summary>
    public const string OverlayGlyph = "overlay.glyph";

    /// <summary>A data source the host polls on an interval.</summary>
    public const string Poll = "poll";

    /// <summary>A tray/context-menu item or quick action the user can invoke.</summary>
    public const string Command = "command";

    /// <summary>Subscribe to session lifecycle events and react.</summary>
    public const string Event = "event";

    public static readonly IReadOnlySet<string> All =
        new HashSet<string>(StringComparer.Ordinal) { OverlayGlyph, Poll, Command, Event };
}

/// <summary>The JSON keys under <c>capabilities</c>. Kept as constants so the parser and any diagnostics
/// agree on spelling.</summary>
internal static class PluginCapabilityKeys
{
    public const string Network = "network";
    public const string ReadSessions = "read.sessions";
    public const string ReadCwd = "read.cwd";
    public const string Notify = "notify";
    public const string PollIntervalSec = "poll.intervalSec";

    /// <summary>Every recognised capability key (used to reject unknown ones).</summary>
    public static readonly IReadOnlySet<string> All =
        new HashSet<string>(StringComparer.Ordinal)
        { Network, ReadSessions, ReadCwd, Notify, PollIntervalSec };
}
