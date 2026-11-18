using System.Text.Json;

namespace Perch.Data.Sprig;

/// <summary>
/// Folds the lines of <c>sprig watch --json</c> into a <see cref="SprigStatus"/>. A snapshot replaces everything;
/// <c>workspace</c>, <c>app</c>, <c>workspace-removed</c>, <c>app-removed</c> and <c>host</c> lines change one part.
/// </summary>
/// <remarks>
/// Tolerant by contract: sprig promises that a consumer may ignore any <c>type</c> or field it doesn't know, and read
/// what it does know from a higher <c>schema</c>. So an unknown type, a malformed or partial line, or a line about a
/// workspace we haven't seen is skipped rather than thrown, and the stream carries on. Not thread-safe; one reader
/// thread owns it.
/// </remarks>
internal sealed class SprigStatusFold
{
    public SprigStatus Current { get; private set; } = SprigStatus.Empty;

    /// <summary>Clears what was read, for a fresh <c>watch</c> process (its first line is a snapshot anyway).</summary>
    public void Reset() => Current = SprigStatus.Empty;

    /// <summary>Applies one line. True when <see cref="Current"/> changed.</summary>
    public bool Apply(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return false;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            return Str(root, "type") switch
            {
                "snapshot" => Snapshot(root),
                "workspace" => Workspace(root),
                "app" => App(root),
                "workspace-removed" => RemoveWorkspace(root),
                "app-removed" => RemoveApp(root),
                "host" => Host(root),
                _ => false,
            };
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private bool Snapshot(JsonElement root)
    {
        bool running = false;
        string? version = null;
        if (root.TryGetProperty("host", out var host) && host.ValueKind == JsonValueKind.Object)
        {
            running = Bool(host, "running");
            version = Str(host, "version");
        }
        var workspaces = new List<SprigWorkspace>();
        if (root.TryGetProperty("workspaces", out var list) && list.ValueKind == JsonValueKind.Array)
            foreach (var w in list.EnumerateArray())
                if (ReadWorkspace(w, apps: null) is { } ws) workspaces.Add(ws);
        Current = new SprigStatus(true, running, version, workspaces);
        return true;
    }

    // A workspace line is the snapshot's shape without apps: its apps come as their own lines, so the ones we hold
    // are kept.
    private bool Workspace(JsonElement root)
    {
        var key = Str(root, "key");
        if (key is null) return false;
        var existing = Current.Find(key);
        if (ReadWorkspace(root, existing?.Apps ?? []) is not { } updated) return false;
        Current = Current with { Workspaces = Replace(Current.Workspaces, key, updated) };
        return true;
    }

    private bool App(JsonElement root)
    {
        var key = Str(root, "workspace");
        if (key is null || Current.Find(key) is not { } ws || ReadApp(root) is not { } app) return false;
        var apps = ws.Apps.ToList();
        int i = apps.FindIndex(a => a.Name == app.Name);
        if (i >= 0) apps[i] = app; else apps.Add(app);
        Current = Current with { Workspaces = Replace(Current.Workspaces, key, ws with { Apps = apps }) };
        return true;
    }

    private bool RemoveWorkspace(JsonElement root)
    {
        var key = Str(root, "key");
        if (key is null || Current.Find(key) is null) return false;
        Current = Current with { Workspaces = Current.Workspaces.Where(w => w.Key != key).ToArray() };
        return true;
    }

    private bool RemoveApp(JsonElement root)
    {
        var key = Str(root, "workspace");
        var name = Str(root, "name");
        if (key is null || name is null || Current.Find(key) is not { } ws || ws.Apps.All(a => a.Name != name)) return false;
        Current = Current with { Workspaces = Replace(Current.Workspaces, key, ws with { Apps = ws.Apps.Where(a => a.Name != name).ToArray() }) };
        return true;
    }

    // Only ever sent when the host has gone; its return comes as a fresh snapshot.
    private bool Host(JsonElement root)
    {
        bool running = Bool(root, "running");
        if (running == Current.HostRunning) return false;
        Current = Current with { HostRunning = running, HostVersion = running ? Str(root, "version") : null };
        return true;
    }

    private static IReadOnlyList<SprigWorkspace> Replace(IReadOnlyList<SprigWorkspace> list, string key, SprigWorkspace updated)
    {
        var copy = list.ToList();
        int i = copy.FindIndex(w => w.Key == key);
        if (i >= 0) copy[i] = updated; else copy.Add(updated);
        return copy;
    }

    /// <param name="apps">The apps to keep, or null to read them from the element (a snapshot).</param>
    private static SprigWorkspace? ReadWorkspace(JsonElement w, IReadOnlyList<SprigApp>? apps)
    {
        if (w.ValueKind != JsonValueKind.Object || Str(w, "key") is not { } key) return null;
        if (apps is null)
        {
            var read = new List<SprigApp>();
            if (w.TryGetProperty("apps", out var list) && list.ValueKind == JsonValueKind.Array)
                foreach (var a in list.EnumerateArray())
                    if (ReadApp(a) is { } app) read.Add(app);
            apps = read;
        }
        var dirs = new List<string>();
        if (w.TryGetProperty("dirs", out var d) && d.ValueKind == JsonValueKind.Array)
            foreach (var e in d.EnumerateArray())
                if (e.ValueKind == JsonValueKind.String && e.GetString() is { Length: > 0 } dir) dirs.Add(dir);

        var stateText = Str(w, "state") ?? "";
        var name = Str(w, "name") ?? "";
        return new SprigWorkspace(
            key,
            Str(w, "orchestration") ?? (key.IndexOf('/') is var slash and > 0 ? key[..slash] : key),
            name,
            Str(w, "label") ?? name,
            Str(w, "branch"),
            ParseState(stateText),
            stateText,
            Bool(w, "paused"),
            Str(w, "error"),
            Str(w, "folder"),
            dirs,
            Str(w, "madeForAgent"),
            apps)
        {
            Readiness = ReadReadiness(w),
        };
    }

    // Absent from a sprig that predates it, and then null, so SprigWorkspace works it out from the apps instead.
    private static SprigReadiness? ReadReadiness(JsonElement w)
    {
        if (!w.TryGetProperty("readiness", out var r) || r.ValueKind != JsonValueKind.Object || Str(r, "health") is not { } health)
            return null;
        var down = new List<string>();
        if (r.TryGetProperty("down", out var d) && d.ValueKind == JsonValueKind.Array)
            foreach (var e in d.EnumerateArray())
                if (e.ValueKind == JsonValueKind.String && e.GetString() is { Length: > 0 } app) down.Add(app);
        return new SprigReadiness(health, Int(r, "ready") ?? 0, Int(r, "started") ?? 0, Int(r, "total") ?? 0, down);
    }

    private static SprigApp? ReadApp(JsonElement a)
    {
        if (a.ValueKind != JsonValueKind.Object || Str(a, "name") is not { } name) return null;
        return new SprigApp(
            name,
            Str(a, "state") ?? SprigApp.Off,
            Int(a, "port"),
            Int(a, "pid"),
            Int(a, "exit"),
            Str(a, "error"),
            Int(a, "restarts") ?? 0,
            Str(a, "doing"),
            a.TryGetProperty("since", out var s) && s.ValueKind == JsonValueKind.String && s.TryGetDateTimeOffset(out var at) ? at : null,
            Str(a, "logFile"));
    }

    internal static SprigWorkspaceState ParseState(string text) => text switch
    {
        "free" => SprigWorkspaceState.Free,
        "starting" => SprigWorkspaceState.Starting,
        "running" => SprigWorkspaceState.Running,
        "paused" => SprigWorkspaceState.Paused,
        "stopped" => SprigWorkspaceState.Stopped,
        "failed" => SprigWorkspaceState.Failed,
        _ => SprigWorkspaceState.Unknown,
    };

    private static string? Str(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool Bool(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static int? Int(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : null;
}
