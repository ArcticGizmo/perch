using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

// perch-hook <event>
//
// A tiny, fast Claude Code hook writer — the self-managed replacement for the installed plugin's
// invoke.ps1. Reads the hook JSON payload on stdin and writes the sidecar files the Perch tray watches.
// Ported 1:1 from plugins/perch/scripts/invoke.ps1, minus the dropped /afk + /history commands.
// NativeAOT-compiled for minimal cold start (the `mode` event fires on every tool call), and
// reflection-free (Utf8JsonReader) so trimming/AOT can't break it.
//
// Events (mapped from Claude Code hooks):
//   mode         PreToolUse / PostToolUse / Stop  → write {sid}.mode (permission mode)
//   agentstop    SubagentStop                     → drop agent-{id}.stopped beside the agent transcript
//   teammateidle TeammateIdle                     → drop agent-{id}.idle beside the matching transcript
//   start        SessionStart                     → launch the tray if the user opted into auto-start
//   cleanup      SessionEnd                       → remove this session's sidecars + sweep agent markers
//
// Two invariants keep a stale hook from ever wedging a Claude Code session: it always exits 0, and it
// never writes a "block" decision to stdout. Honours CLAUDE_CONFIG_DIR (data view) and PERCH_DEV
// (which profile's settings to read) exactly like the app, so dev/hermetic testing works end to end.

string action = args.Length > 0 ? args[0] : "";
try
{
    byte[] payload = ReadStdin();
    var f = ReadFields(payload,
        "session_id", "permission_mode", "transcript_path", "agent_transcript_path",
        "agent_id", "teammate_name", "source");

    string sessionsDir = Path.Combine(ResolveClaudeDir(), "sessions");

    // If Perch was removed without running its uninstaller, our hook entries would linger. Detect that
    // on the infrequent session-lifecycle events (never the per-tool-call `mode` hot path) and strip
    // our own managed entries so a dead command never accumulates.
    if (action is "start" or "cleanup") MaybeSelfHeal();

    switch (action)
    {
        // SubagentStop / TeammateIdle: drop a marker beside the agent's transcript so the tray retires the
        // row at once instead of waiting out its staleness window. Handled before the mode write so a
        // sub-agent's permission_mode never overwrites the parent session's .mode sidecar.
        case "agentstop":
            HandleAgentStop(f);
            break;
        case "teammateidle":
            HandleTeammateIdle(f);
            break;

        // The hot path, fired on every tool call.
        case "mode":
            WriteMode(sessionsDir, f);
            break;

        // SessionStart also seeds the initial mode (if present), then may launch the tray.
        case "start":
            WriteMode(sessionsDir, f);
            HandleStart(f);
            break;

        case "cleanup":
            HandleCleanup(sessionsDir, f);
            break;
    }
}
catch { /* never fail a hook */ }

return 0;

// ── event handlers ────────────────────────────────────────────────────────────────

// Record the session's permission mode so the overlay can badge it.
static void WriteMode(string sessionsDir, Dictionary<string, string?> f)
{
    string? sid = f["session_id"], mode = f["permission_mode"];
    if (!string.IsNullOrEmpty(sid) && !string.IsNullOrEmpty(mode) && Directory.Exists(sessionsDir))
        File.WriteAllText(Path.Combine(sessionsDir, sid + ".mode"), mode);
}

// SubagentStop: a sub-agent finished (or a teammate ended a turn). Drop agent-{id}.stopped beside its
// transcript; the tray reads the marker's mtime as the event time, so the body is just a timestamp.
static void HandleAgentStop(Dictionary<string, string?> f)
{
    string? atp = f["agent_transcript_path"];
    if (string.IsNullOrEmpty(atp))
    {
        // Older builds omit it: rebuild …/subagents/agent-{id}.jsonl from the parent path + agent_id.
        string? sub = SubagentsDir(f["transcript_path"]);
        string? aid = f["agent_id"];
        if (sub is not null && !string.IsNullOrEmpty(aid))
            atp = Path.Combine(sub, $"agent-{aid}.jsonl");
    }
    if (string.IsNullOrEmpty(atp)) return;

    string? d = Path.GetDirectoryName(atp);
    string n = Path.GetFileNameWithoutExtension(atp); // agent-{id}
    if (!string.IsNullOrEmpty(d) && !string.IsNullOrEmpty(n) && Directory.Exists(d))
        File.WriteAllText(Path.Combine(d, n + ".stopped"), Timestamp());
}

// TeammateIdle carries teammate_name (== the agent's type) but no agent_id, so resolve the transcript by
// matching the meta sidecars in the subagents dir, then drop an .idle marker beside it.
static void HandleTeammateIdle(Dictionary<string, string?> f)
{
    string? name = f["teammate_name"];
    string? sub = SubagentsDir(f["transcript_path"]);
    if (string.IsNullOrEmpty(name) || sub is null || !Directory.Exists(sub)) return;

    foreach (string meta in Directory.GetFiles(sub, "agent-*.meta.json"))
    {
        try
        {
            var m = ReadFields(File.ReadAllBytes(meta), "agentType", "name");
            if (m["agentType"] == name || m["name"] == name)
            {
                string @base = meta[..^".meta.json".Length];
                File.WriteAllText(@base + ".idle", Timestamp());
            }
        }
        catch { /* skip a meta file we can't read */ }
    }
}

// SessionStart: if the user opted into auto-start, launch the installed tray when one isn't running. Only
// "startup"/"resume" sources represent a session actually opening ("clear"/"compact" happen mid-session).
static void HandleStart(Dictionary<string, string?> f)
{
    string? source = f["source"];
    if (!string.IsNullOrEmpty(source) && source != "startup" && source != "resume") return;
    if (!AutoStartEnabled()) return;
    if (IsPerchRunning()) return; // the tray's single-instance guard would no-op a second launch anyway
    LaunchPerch();
}

// SessionEnd: remove this session's sidecars, and sweep any agent stop/idle markers it left behind.
static void HandleCleanup(string sessionsDir, Dictionary<string, string?> f)
{
    string? sid = f["session_id"];
    if (!string.IsNullOrEmpty(sid))
        foreach (string ext in new[] { ".mode", ".notify", ".history", ".afk" /* legacy */ })
            TryDelete(Path.Combine(sessionsDir, sid + ext));

    string? sub = SubagentsDir(f["transcript_path"]);
    if (sub is not null && Directory.Exists(sub))
    {
        try
        {
            foreach (string m in Directory.GetFiles(sub, "agent-*.stopped")) TryDelete(m);
            foreach (string m in Directory.GetFiles(sub, "agent-*.idle")) TryDelete(m);
        }
        catch { }
    }
}

// ── helpers ───────────────────────────────────────────────────────────────────────

static byte[] ReadStdin()
{
    using var stdin = Console.OpenStandardInput();
    using var ms = new MemoryStream();
    stdin.CopyTo(ms);
    return ms.ToArray();
}

// Mirrors Perch.Data.ClaudePaths.ResolveClaudeDir: CLAUDE_CONFIG_DIR if set, else ~/.claude.
static string ResolveClaudeDir()
{
    var dir = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
    return string.IsNullOrWhiteSpace(dir)
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude")
        : dir;
}

// {projects}/{enc-cwd}/{session}.jsonl → {projects}/{enc-cwd}/{session}/subagents  (see SubAgentReader).
static string? SubagentsDir(string? transcriptPath)
{
    if (string.IsNullOrEmpty(transcriptPath)) return null;
    string? d = Path.GetDirectoryName(transcriptPath);
    string n = Path.GetFileNameWithoutExtension(transcriptPath); // {session}
    if (string.IsNullOrEmpty(d) || string.IsNullOrEmpty(n)) return null;
    return Path.Combine(d, n, "subagents");
}

// Reads AutoStartOnFirstSession from the tray's own settings.json for the active profile. Mirrors
// AppProfile: PERCH_DEV (non-empty, not 0/false) selects the "Perch (Dev)" folder, else "Perch".
static bool AutoStartEnabled()
{
    string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    string path = Path.Combine(appData, ProfileFolder(), "settings.json");
    if (!File.Exists(path)) return false;
    try { return ReadBool(File.ReadAllBytes(path), "AutoStartOnFirstSession") == true; }
    catch { return false; }
}

static string ProfileFolder()
{
    var env = Environment.GetEnvironmentVariable("PERCH_DEV");
    bool dev = !string.IsNullOrEmpty(env)
        && !(env == "0" || env.Equals("false", StringComparison.OrdinalIgnoreCase));
    return dev ? "Perch (Dev)" : "Perch";
}

// Self-heal: the installer records the tray executable's path in <bin>/perch.path (HookInstaller). If
// that file is gone, Perch was uninstalled without its cleanup running — strip our managed hook block so
// settings.json doesn't keep pointing at a dead binary. Fail-open: no breadcrumb → leave hooks alone.
static void MaybeSelfHeal()
{
    try
    {
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string profile = ProfileFolder();
        string marker = Path.Combine(appData, profile, "bin", "perch.path");
        if (!File.Exists(marker)) return;

        string trayPath = File.ReadAllText(marker).Trim();
        if (string.IsNullOrEmpty(trayPath) || File.Exists(trayPath)) return; // Perch still installed

        // Scope matches ClaudeUserSettings: a release hook clears every Perch entry; a dev hook clears only
        // its own (this running binary's path, or the _perch.dev marker) so it never strips release's hooks.
        bool isDev = profile == "Perch (Dev)";
        string ownBin = Environment.ProcessPath
            ?? Path.Combine(appData, profile, "bin", OperatingSystem.IsWindows() ? "perch-hook.exe" : "perch-hook");
        StripManagedHooks(Path.Combine(ResolveClaudeDir(), "settings.json"), isDev, ownBin);
    }
    catch { /* best-effort */ }
}

// Removes the Perch-managed hook objects this profile owns from settings.json, dropping any entry/event
// left empty, and preserving the user's own hooks. Scope matches ClaudeUserSettings: a release hook
// (isDev == false) clears every Perch entry; a dev hook clears only its own (this binary's path via
// ownBin, or the _perch.dev marker). Mirrors ClaudeUserSettings.StripManaged; duplicated here so
// perch-hook stays a standalone AOT binary with no dependency on Perch.Core.
static void StripManagedHooks(string settingsPath, bool isDev, string ownBin)
{
    if (!File.Exists(settingsPath)) return;

    var opts = new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };
    if (JsonNode.Parse(File.ReadAllText(settingsPath), documentOptions: opts) is not JsonObject root ||
        root["hooks"] is not JsonObject hooks)
        return;

    bool changed = false;
    foreach (var evt in hooks.Select(kv => kv.Key).ToList())
    {
        if (hooks[evt] is not JsonArray entries) continue;

        for (int i = entries.Count - 1; i >= 0; i--)
        {
            if (entries[i] is not JsonObject entry || entry["hooks"] is not JsonArray list) continue;

            for (int j = list.Count - 1; j >= 0; j--)
                if (list[j] is JsonObject h && (isDev ? IsDevOwned(h, ownBin) : IsPerchManaged(h)))
                {
                    list.RemoveAt(j);
                    changed = true;
                }

            if (list.Count == 0) entries.RemoveAt(i);
        }

        if (entries.Count == 0) hooks.Remove(evt);
    }

    if (!changed) return;
    if (hooks.Count == 0) root.Remove("hooks");
    File.WriteAllText(settingsPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
}

// An entry is Perch's if it carries the _perch.managed marker, or — since Claude Code drops our unknown
// _perch field whenever it rewrites settings.json — if its command runs the perch-hook binary. Mirrors
// ClaudeUserSettings.IsManaged.
static bool IsPerchManaged(JsonObject h)
{
    if (h["_perch"] is JsonObject p && p["managed"]?.GetValueKind() == JsonValueKind.True)
        return true;
    return IsPerchCommand(h["command"]);
}

// A dev instance owns an entry only if it wrote it: the _perch.dev marker, or (marker stripped) a command
// equal to this running binary. Mirrors ClaudeUserSettings.IsDevOwned.
static bool IsDevOwned(JsonObject h, string ownBin)
{
    if (h["_perch"] is JsonObject p && p["managed"]?.GetValueKind() == JsonValueKind.True
        && p["dev"]?.GetValueKind() == JsonValueKind.True)
        return true;
    return h["command"] is JsonNode c && c.GetValueKind() == JsonValueKind.String &&
        string.Equals(c.ToString().Replace('\\', '/'), ownBin.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase);
}

// True when the command runs Perch's hook binary, matched by file name so it holds across install dirs,
// path-separator styles ("\" vs "/"), and a ".exe" suffix.
static bool IsPerchCommand(JsonNode? command)
{
    if (command is null || command.GetValueKind() != JsonValueKind.String) return false;
    string leaf = command.ToString();
    int cut = leaf.LastIndexOfAny(new[] { '/', '\\' });
    if (cut >= 0) leaf = leaf[(cut + 1)..];
    if (leaf.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) leaf = leaf[..^4];
    return string.Equals(leaf, "perch-hook", StringComparison.OrdinalIgnoreCase);
}

static bool IsPerchRunning()
{
    Process[] ps;
    try { ps = Process.GetProcessesByName("perch"); }
    catch { return false; }
    try { return ps.Length > 0; }
    finally { foreach (var p in ps) p.Dispose(); }
}

// --autostarted tells the tray it was launched by this hook, so it may auto-close after the last session
// ends. On macOS the tray is a .app launched through LaunchServices (see TryLaunchMacBundle); elsewhere it
// resolves `perch` from PATH (the installer registers it). A dev build run via `dotnet run` won't resolve
// either way and this no-ops.
static void LaunchPerch()
{
    try
    {
        if (OperatingSystem.IsMacOS() && TryLaunchMacBundle()) return;

        var psi = new ProcessStartInfo("perch") { UseShellExecute = false, CreateNoWindow = true };
        psi.ArgumentList.Add("--autostarted");
        Process.Start(psi);
    }
    catch { /* not on PATH (e.g. dev build) → no-op */ }
}

// macOS: `perch` is only a ~/.local/bin symlink, which the hook's PATH usually lacks, and the tray is a
// .app that should launch through LaunchServices — not by exec'ing the inner binary as a child of this
// short-lived hook. Resolve the installed bundle from the installer's perch.path breadcrumb (the same
// marker MaybeSelfHeal reads) and hand it to `open`, which detaches it and registers it as a proper agent
// app. Returns false (fall through to the PATH launch) if the marker or bundle can't be resolved.
static bool TryLaunchMacBundle()
{
    string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    string marker = Path.Combine(appData, ProfileFolder(), "bin", "perch.path");
    if (!File.Exists(marker)) return false;

    string trayPath = File.ReadAllText(marker).Trim(); // …/Perch.app/Contents/MacOS/perch
    if (string.IsNullOrEmpty(trayPath) || !File.Exists(trayPath)) return false;

    string? bundle = FindAppBundle(trayPath);
    if (bundle is null) return false;

    // `open -a <bundle> --args --autostarted` activates a running instance or launches a fresh one; we only
    // reach here when none is running (HandleStart guards on IsPerchRunning), so the args reach the app.
    var psi = new ProcessStartInfo("/usr/bin/open") { UseShellExecute = false, CreateNoWindow = true };
    psi.ArgumentList.Add("-a");
    psi.ArgumentList.Add(bundle);
    psi.ArgumentList.Add("--args");
    psi.ArgumentList.Add("--autostarted");
    Process.Start(psi);
    return true;
}

// Walk up from …/Perch.app/Contents/MacOS/perch to the nearest …/*.app ancestor directory.
static string? FindAppBundle(string execPath)
{
    var dir = new DirectoryInfo(Path.GetDirectoryName(execPath) ?? "");
    while (dir is not null)
    {
        if (dir.Name.EndsWith(".app", StringComparison.OrdinalIgnoreCase)) return dir.FullName;
        dir = dir.Parent;
    }
    return null;
}

static void TryDelete(string path)
{
    try { File.Delete(path); } catch { }
}

static string Timestamp() => DateTime.UtcNow.ToString("o");

// Pulls the named top-level string properties from the payload with a forward-only reader — no reflection,
// so it's AOT/trim-safe and about as fast as JSON parsing gets.
static Dictionary<string, string?> ReadFields(byte[] json, params string[] names)
{
    var result = new Dictionary<string, string?>(StringComparer.Ordinal);
    foreach (var n in names) result[n] = null;
    try
    {
        var reader = new Utf8JsonReader(json);
        int depth = 0;
        string? prop = null;
        while (reader.Read())
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.StartObject:
                case JsonTokenType.StartArray:
                    depth++;
                    break;
                case JsonTokenType.EndObject:
                case JsonTokenType.EndArray:
                    depth--;
                    prop = null;
                    break;
                case JsonTokenType.PropertyName:
                    prop = depth == 1 ? reader.GetString() : null;
                    break;
                case JsonTokenType.String:
                    if (depth == 1 && prop is not null && result.ContainsKey(prop))
                        result[prop] = reader.GetString();
                    prop = null;
                    break;
                default:
                    prop = null;
                    break;
            }
        }
    }
    catch { }
    return result;
}

// Reads a top-level boolean property (true/false), or null if absent/unreadable.
static bool? ReadBool(byte[] json, string name)
{
    try
    {
        var reader = new Utf8JsonReader(json);
        int depth = 0;
        string? prop = null;
        while (reader.Read())
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.StartObject:
                case JsonTokenType.StartArray:
                    depth++;
                    break;
                case JsonTokenType.EndObject:
                case JsonTokenType.EndArray:
                    depth--;
                    prop = null;
                    break;
                case JsonTokenType.PropertyName:
                    prop = depth == 1 ? reader.GetString() : null;
                    break;
                case JsonTokenType.True:
                case JsonTokenType.False:
                    if (depth == 1 && prop == name) return reader.TokenType == JsonTokenType.True;
                    prop = null;
                    break;
                default:
                    prop = null;
                    break;
            }
        }
    }
    catch { }
    return null;
}
