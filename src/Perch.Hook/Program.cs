using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
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
//   valet        PreToolUse                       → permission valet: relay the request to the tray's
//                                                   named pipe and echo an explicit user decision back
//                                                   (arg 2 = pipe name, baked in by the registering tray)
//   agentstop    SubagentStop                     → drop agent-{id}.stopped beside the agent transcript
//   teammateidle TeammateIdle                     → drop agent-{id}.idle beside the matching transcript
//   start        SessionStart                     → launch the tray if the user opted into auto-start
//   cleanup      SessionEnd                       → remove this session's sidecars + sweep agent markers
//
// Two invariants keep a stale hook from ever wedging a Claude Code session: it always exits 0, and it
// never volunteers a decision on stdout — the only outputs it ever writes are `valet` relaying a choice a
// user explicitly made in the Perch UI, and `start`'s advisory systemMessage when a normal claude opens a
// session Perch is already controlling ({sid}.perch-lock); every failure path is silent, which Claude Code
// treats as "no opinion" (normal permission flow). Honours CLAUDE_CONFIG_DIR (data view) and PERCH_DEV
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

        // The permission valet (session-control M2): also PreToolUse, kept separate from `mode` so the
        // sidecar write can never be delayed by pipe IO.
        case "valet":
            HandleValet(payload, args.Length > 1 ? args[1] : null);
            break;

        // SessionStart also seeds the initial mode (if present), warns if the id is Perch-controlled, then
        // may launch the tray.
        case "start":
            WriteMode(sessionsDir, f);
            WriteEnvSlug(sessionsDir, f);
            WarnIfPerchControlled(sessionsDir, f);
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
// Which claude-envs environment is running this session.
//
// A scheme that shares `sessions/` across environments by link puts every environment's sidecars in
// one physical directory, so the directory a sidecar sits in no longer says who owns it. The launchers
// export CLAUDE_ENVS_SLUG alongside CLAUDE_CONFIG_DIR for exactly this, and a hook runs *inside* the
// session, so it is the only party that can see it without reading another process's memory.
//
// Written on `start` only - which covers a resume, since SessionStart fires again - so the hot `mode`
// path stays a single write. A bare `claude` sets no slug and gets no sidecar, which is the honest
// answer rather than a guessed one.
static void WriteEnvSlug(string sessionsDir, Dictionary<string, string?> f)
{
    string? sid = f["session_id"];
    string? slug = Environment.GetEnvironmentVariable("CLAUDE_ENVS_SLUG");
    if (string.IsNullOrEmpty(sid) || string.IsNullOrWhiteSpace(slug) || !Directory.Exists(sessionsDir))
        return;

    // Defensive: the slug becomes part of no path here, but it is read back as one downstream.
    foreach (char c in slug)
        if (!char.IsAsciiLetterOrDigit(c) && c != '-' && c != '_')
            return;

    try { File.WriteAllText(Path.Combine(sessionsDir, sid + ".slug"), slug.Trim()); } catch { }
}

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

// SessionStart guard (docs/session-ui-plan.md, collision defence (c)): Perch writes {sid}.perch-lock for
// every session it drives over stream-json. If a *normal* `claude --resume <sid>` opens that id while
// the owning Perch is alive, two processes would append to one transcript — so tell the user, via the
// hook's systemMessage (shown in the terminal, not fed to the model). Perch's own controlled session
// fires this hook too; it carries PERCH_SESSION_OWNER=<tray pid> in its environment, which is how it's
// recognised as the legitimate owner and left silent. A lock whose owner has exited is stale: deleted,
// no warning. Fail-open everywhere — never blocks the session, and the message is advisory only.
static void WarnIfPerchControlled(string sessionsDir, Dictionary<string, string?> f)
{
    try
    {
        string? sid = f["session_id"];
        string? source = f["source"];
        if (string.IsNullOrEmpty(sid)) return;
        if (!string.IsNullOrEmpty(source) && source != "startup" && source != "resume") return;

        string lockPath = Path.Combine(sessionsDir, sid + ".perch-lock");
        if (!File.Exists(lockPath)) return;

        var l = ReadFields(File.ReadAllBytes(lockPath), "pid", "profile");
        string? pid = l["pid"];
        if (string.Equals(pid, Environment.GetEnvironmentVariable("PERCH_SESSION_OWNER"), StringComparison.Ordinal))
            return;   // this IS the Perch-controlled session starting
        if (!IsProcessAlive(pid))
        {
            TryDelete(lockPath);   // stale lock from a tray that exited without cleaning up
            return;
        }

        string who = string.IsNullOrEmpty(l["profile"]) ? "Perch" : l["profile"]!;
        var output = new JsonObject
        {
            ["systemMessage"] =
                $"⚠ Perch: session {sid[..Math.Min(8, sid.Length)]} is currently controlled by {who} (pid {pid}). " +
                "Two writers on one transcript will corrupt it — continue it from the Perch window instead, " +
                "or close it there first.",
        };
        Console.Out.Write(output.ToJsonString());
    }
    catch { /* fail open */ }
}

// True when the recorded owner pid is a live process. Unparseable → treat as dead (stale).
static bool IsProcessAlive(string? pidText)
{
    if (!int.TryParse(pidText, out int pid) || pid <= 0) return false;
    try
    {
        using var p = Process.GetProcessById(pid);
        return !p.HasExited;
    }
    catch { return false; }
}

// SessionEnd: remove this session's sidecars, and sweep any agent stop/idle markers it left behind.
static void HandleCleanup(string sessionsDir, Dictionary<string, string?> f)
{
    string? sid = f["session_id"];
    if (!string.IsNullOrEmpty(sid))
    {
        foreach (string ext in new[] { ".mode", ".notify", ".history", ".slug", ".afk" /* legacy */ })
            TryDelete(Path.Combine(sessionsDir, sid + ext));
        // The ownership lock goes only when it's ours (the controlled session itself ending) or stale — a
        // normal claude that briefly opened a Perch-controlled id must not strip Perch's live ownership.
        try
        {
            string lockPath = Path.Combine(sessionsDir, sid + ".perch-lock");
            if (File.Exists(lockPath))
            {
                string? pid = ReadFields(File.ReadAllBytes(lockPath), "pid")["pid"];
                bool ours = string.Equals(pid, Environment.GetEnvironmentVariable("PERCH_SESSION_OWNER"), StringComparison.Ordinal);
                if (ours || !IsProcessAlive(pid)) TryDelete(lockPath);
            }
        }
        catch { }
    }

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

// The permission valet (session-control M2): forward the raw PreToolUse payload to the tray's named
// pipe and, only when the tray relays an explicit user choice ("allow"/"deny"), echo it to Claude Code
// as hookSpecificOutput JSON. Fail-open at every step — no tray listening (a missing pipe fails the
// connect instantly, so a quit Perch costs ~nothing per tool call), a "pass" reply, a missed deadline,
// or any error → exit silently, leaving the normal permission flow (allowlists, the terminal prompt)
// untouched. The tray replies "pass" immediately unless it is actually showing prompt UI. The pipe name
// is baked into the registration by the tray profile that wrote it (so dev and release trays never
// intercept each other's sessions); the env-derived fallback covers a hand-authored registration.
static void HandleValet(byte[] payload, string? pipeName)
{
    if (string.IsNullOrEmpty(pipeName))
        pipeName = ProfileFolder() == "Perch (Dev)" ? "perch-valet-dev" : "perch-valet";

    try
    {
        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try { pipe.Connect(200); } catch { return; }   // no tray → no opinion

        // Newlines in the compact payload can only be inter-token whitespace (a literal newline inside
        // a JSON string is invalid), so blanking them lets the payload travel as one line.
        for (int i = 0; i < payload.Length; i++)
            if (payload[i] is (byte)'\n' or (byte)'\r')
                payload[i] = (byte)' ';

        pipe.Write(payload, 0, payload.Length);
        pipe.WriteByte((byte)'\n');
        pipe.Flush();

        // The tray auto-passes an unanswered prompt well before this; the deadline is the backstop that
        // keeps a wedged tray from stalling the session for hook-timeout minutes.
        string? reply = ReadLineWithDeadline(pipe, 20_000);
        if (reply is null) return;

        var r = ReadFields(Encoding.UTF8.GetBytes(reply), "decision", "reason");
        string? decision = r["decision"];
        if (decision is not ("allow" or "deny")) return;

        var output = new JsonObject
        {
            ["hookSpecificOutput"] = new JsonObject
            {
                ["hookEventName"] = "PreToolUse",
                ["permissionDecision"] = decision,
                ["permissionDecisionReason"] = r["reason"]
                    ?? (decision == "allow" ? "Approved by the user in Perch." : "Denied by the user in Perch."),
            },
        };
        Console.Out.Write(output.ToJsonString());
    }
    catch { /* fail open */ }
}

// Reads one newline-terminated UTF-8 line from the pipe, or null when the deadline passes or it closes.
static string? ReadLineWithDeadline(NamedPipeClientStream pipe, int deadlineMs)
{
    var ms = new MemoryStream();
    var buf = new byte[4096];
    long deadline = Environment.TickCount64 + deadlineMs;
    while (Environment.TickCount64 < deadline)
    {
        var read = pipe.ReadAsync(buf, 0, buf.Length);
        int remaining = (int)Math.Max(1, deadline - Environment.TickCount64);
        if (!read.Wait(remaining)) return null;
        int n = read.Result;
        if (n <= 0) return null;
        for (int i = 0; i < n; i++)
        {
            if (buf[i] != (byte)'\n') continue;
            ms.Write(buf, 0, i);
            return Encoding.UTF8.GetString(ms.ToArray());
        }
        ms.Write(buf, 0, n);
    }
    return null;
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

// Reads StartMode from the tray's own settings.json for the active profile and reports whether it says
// "launch me when a session opens" (the other modes — Off and OnLogin — are none of this hook's business).
// Mirrors AppProfile: PERCH_DEV (non-empty, not 0/false) selects the "Perch (Dev)" folder, else "Perch".
// Falls back to the legacy AutoStartOnFirstSession bool for a settings file the tray hasn't migrated yet.
static bool AutoStartEnabled()
{
    string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    string path = Path.Combine(appData, ProfileFolder(), "settings.json");
    if (!File.Exists(path)) return false;
    try
    {
        byte[] json = File.ReadAllBytes(path);
        string? mode = ReadFields(json, "StartMode")["StartMode"];
        if (!string.IsNullOrEmpty(mode))
            return string.Equals(mode, "OnSessionStart", StringComparison.OrdinalIgnoreCase);
        return ReadBool(json, "AutoStartOnFirstSession") == true;
    }
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
//
// UseShellExecute = true is load-bearing, not cosmetic: this hook's stdout is a pipe Claude Code reads to
// EOF before it lets the session's first prompt proceed. With UseShellExecute = false the long-lived tray
// would inherit that stdout handle and hold the pipe's write end open for the whole session, so Claude
// Code never sees EOF and the first prompt hangs until the hook times out. ShellExecuteEx launches the
// tray detached, without inheriting our std handles, so the pipe closes the instant this hook exits.
static void LaunchPerch()
{
    try
    {
        if (OperatingSystem.IsMacOS() && TryLaunchMacBundle()) return;

        var psi = new ProcessStartInfo("perch") { UseShellExecute = true };
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
