namespace Perch.Data.Sprig;

/// <summary>
/// Perch's mirror of what <c>sprig watch --json</c> reports: every workspace sprig knows about and each of its apps.
/// The contract is sprig's (<c>Sprig.Engine.Live.WatchLines</c>, schema 1), so these are plain records holding only
/// what the overlay draws or acts on; <see cref="SprigStatusFold"/> builds them from the stream's lines.
/// </summary>
/// <param name="Seen">A snapshot has arrived. Before the first one the overlay has nothing to show yet.</param>
/// <param name="HostRunning">Sprig's host is up. Without it no app runs, though workspaces still have state.</param>
internal sealed record SprigStatus(bool Seen, bool HostRunning, string? HostVersion, IReadOnlyList<SprigWorkspace> Workspaces)
{
    public static readonly SprigStatus Empty = new(false, false, null, []);

    /// <summary>The workspace with <paramref name="key"/>, or null.</summary>
    public SprigWorkspace? Find(string key)
    {
        foreach (var w in Workspaces)
            if (string.Equals(w.Key, key, StringComparison.Ordinal)) return w;
        return null;
    }
}

/// <summary>Where Perch's connection to sprig stands, which is separate from what sprig reports.</summary>
internal enum SprigFeed
{
    /// <summary>The integration is off: nothing runs.</summary>
    Off,
    /// <summary><c>sprig watch</c> is starting and hasn't sent its first snapshot.</summary>
    Connecting,
    /// <summary>Lines are arriving; what's shown is current.</summary>
    Live,
    /// <summary><c>watch</c> exited and is being started again; what's shown is the last state read.</summary>
    Reconnecting,
    /// <summary>No sprig was found. Looked for again now and then, so installing it is picked up.</summary>
    NotInstalled,
}

/// <summary>A workspace's state as sprig shows it. <see cref="Unknown"/> is a state a newer sprig added.</summary>
internal enum SprigWorkspaceState { Unknown, Free, Starting, Running, Paused, Stopped, Failed }

/// <param name="Key"><c>orchestration/name</c>: how actions address it.</param>
/// <param name="Name">The slot number, or <c>main</c> / a name for one that runs the repos in place.</param>
/// <param name="Label">How people know it: its branch, or "free".</param>
/// <param name="StateText">The state as sprig wrote it, kept for display when <see cref="State"/> is
/// <see cref="SprigWorkspaceState.Unknown"/>.</param>
/// <param name="Dirs">Where each of its repos is checked out, to match a session's folder to it.</param>
/// <param name="MadeForAgent">The Claude Code session id a sprig hook made this slot for, if any.</param>
internal sealed record SprigWorkspace(
    string Key, string Orchestration, string Name, string Label, string? Branch,
    SprigWorkspaceState State, string StateText, bool Paused, string? Error, string? Folder,
    IReadOnlyList<string> Dirs, string? MadeForAgent, IReadOnlyList<SprigApp> Apps)
{
    public bool IsFree => State == SprigWorkspaceState.Free;

    /// <summary>
    /// The state Perch shows. Sprig says <c>starting</c> only while it prepares the workspace (worktrees, files,
    /// infra) and <c>running</c> as soon as any app is alive, which includes apps still building or booting. Read
    /// as "up" that's green for the whole start-up, so a running workspace with apps still on their way up shows as
    /// <see cref="SprigWorkspaceState.Starting"/> until they've settled.
    /// </summary>
    public SprigWorkspaceState Shown
        => State == SprigWorkspaceState.Running && Apps.Any(a => a.IsComingUp) ? SprigWorkspaceState.Starting : State;

    /// <summary>Apps that are up: ready, or a job that finished.</summary>
    public int ReadyCount => Apps.Count(a => a.IsReady);

    /// <summary>Apps that have a runner at all (anything but <c>off</c>).</summary>
    public int ActiveCount => Apps.Count(a => !a.IsOff);

    public bool AnyFailed => Apps.Any(a => a.IsFailed);
}

/// <param name="Name"><c>repo/app</c>.</param>
/// <param name="State">The runner state in camelCase (<c>building</c>, <c>ready</c>, <c>exited</c> …), or
/// <c>off</c> when the plan has the app and the host isn't running it.</param>
internal sealed record SprigApp(
    string Name, string State, int? Port = null, int? Pid = null, int? Exit = null, string? Error = null,
    int Restarts = 0, string? Doing = null, DateTimeOffset? Since = null, string? LogFile = null)
{
    public const string Off = "off";

    public bool IsOff => State == Off;
    public bool IsReady => State is "ready" or "completed";
    public bool IsFailed => State is "exited" or "failed";

    /// <summary>On its way up: building, waiting on what it depends on, starting, restarting, or a job (a
    /// migration, say) still running.</summary>
    public bool IsComingUp => State is "building" or "waiting" or "starting" or "restarting" or "running";

    /// <summary>On its way somewhere: building, waiting on what it depends on, starting, restarting, or stopping.</summary>
    public bool IsBusy => State is "building" or "waiting" or "starting" or "restarting" or "stopping" or "running";

    /// <summary>The part after the repo, which is how people name it.</summary>
    public string ShortName => Name.LastIndexOf('/') is var i and >= 0 ? Name[(i + 1)..] : Name;
}
