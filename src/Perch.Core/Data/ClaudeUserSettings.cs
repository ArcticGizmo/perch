namespace Perch.Data;

using System.Text.Json;
using System.Text.Json.Nodes;

/// <summary>
/// Read/modify/write helper for the user-scope Claude Code settings file
/// (<c>~/.claude/settings.json</c>, see <see cref="ClaudePaths.UserSettingsFile"/>). Used to flip the
/// experimental feature env vars Claude Code reads on launch, and to self-manage Perch's own hook
/// block, while preserving every other key in the file. Best-effort: a missing or unreadable file
/// reads as "unset", and any write failure is swallowed. Comments in the file, if any, are dropped on
/// rewrite.
/// </summary>
internal static class ClaudeUserSettings
{
    // ── Self-managed hooks ───────────────────────────────────────────────────────────
    // Perch writes these hook entries into ~/.claude/settings.json so live session state reaches the
    // tray without a separate marketplace plugin. Each managed hook object carries a "_perch" marker
    // (Claude Code silently ignores unknown fields) so reconcile can find and replace *only ours*.
    //
    // The set mirrors plugins/perch/hooks/hooks.json (minus the dropped UserPromptSubmit): each event
    // fires perch-hook with the arg the binary switches on. We use the exec form (command + args)
    // rather than a single command string so a bin path containing spaces (e.g. the "Perch (Dev)"
    // profile) needs no shell quoting.
    private static readonly (string Event, string Arg)[] ManagedHooks =
    {
        ("PreToolUse",   "mode"),
        ("PostToolUse",  "mode"),
        ("Stop",         "mode"),
        ("SubagentStop", "agentstop"),
        ("TeammateIdle", "teammateidle"),
        ("SessionStart", "start"),
        ("SessionEnd",   "cleanup"),
    };

    private const string ManagedNote = "Added by Perch. Safe to delete if Perch is uninstalled.";

    // The hook binary's file name (no directory, no extension), used to recognise Perch's own entries by
    // command when the "_perch" marker is gone. Mirrors HookInstaller.HookFileName sans the ".exe".
    private const string HookExeName = "perch-hook";

    private static readonly char[] PathSeparators = { '/', '\\' };

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    /// <summary>
    /// Reconciles Perch's managed hook block in <c>~/.claude/settings.json</c>: strips the entries this
    /// profile owns then re-adds the current set pointing at <paramref name="hookBinaryPath"/>. Idempotent
    /// — repeated calls converge and never duplicate — and it never touches user-authored hooks.
    ///
    /// <para><paramref name="isDev"/> sets the scope. A <b>release</b> instance is authoritative: it
    /// strips <em>every</em> Perch-managed entry (any profile's, including a dev instance's leftovers). A
    /// <b>dev</b> instance is polite: it strips only its own entries (the <c>_perch.dev</c> marker, or a
    /// command equal to its own dev binary), leaving an installed release Perch's hooks intact so both can
    /// coexist while you develop. Returns true on a successful write.</para>
    /// </summary>
    public static bool ReconcileHooks(string hookBinaryPath, string version, bool isDev) =>
        ReconcileHooks(ClaudePaths.UserSettingsFile, hookBinaryPath, version, isDev);

    /// <summary>As <see cref="ReconcileHooks(string,string,bool)"/>, against an explicit settings file
    /// (test seam). Defaults to release scope.</summary>
    public static bool ReconcileHooks(string settingsPath, string hookBinaryPath, string version, bool isDev = false)
    {
        try
        {
            var path = settingsPath;
            var root = File.Exists(path)
                ? JsonNode.Parse(File.ReadAllText(path), documentOptions: ReadOptions) as JsonObject ?? new JsonObject()
                : new JsonObject();

            if (root["hooks"] is not JsonObject hooks)
            {
                hooks = new JsonObject();
                root["hooks"] = hooks;
            }

            StripManaged(hooks, OwnedBy(isDev, hookBinaryPath));

            foreach (var (evt, arg) in ManagedHooks)
            {
                if (hooks[evt] is not JsonArray arr)
                {
                    arr = new JsonArray();
                    hooks[evt] = arr;
                }
                arr.Add(NewEntry(hookBinaryPath, arg, version, isDev));
            }

            if (hooks.Count == 0) root.Remove("hooks");

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, root.ToJsonString(WriteOptions));
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Removes Perch's managed hook entries from <c>~/.claude/settings.json</c> (uninstall / self-heal),
    /// leaving user-authored hooks untouched. Scope matches <see cref="ReconcileHooks(string,string,bool)"/>:
    /// a release instance clears every Perch-managed entry; a dev instance clears only its own (pass its
    /// binary path as <paramref name="devBinaryPath"/> so path-matching works after a marker strip).
    /// Returns true if the file was rewritten.
    /// </summary>
    public static bool RemoveManagedHooks(bool isDev = false, string? devBinaryPath = null) =>
        RemoveManagedHooks(ClaudePaths.UserSettingsFile, isDev, devBinaryPath);

    /// <summary>As <see cref="RemoveManagedHooks(bool,string)"/>, against an explicit settings file (test seam).</summary>
    public static bool RemoveManagedHooks(string settingsPath, bool isDev = false, string? devBinaryPath = null)
    {
        try
        {
            var path = settingsPath;
            if (!File.Exists(path)) return false;

            if (JsonNode.Parse(File.ReadAllText(path), documentOptions: ReadOptions) is not JsonObject root
                || root["hooks"] is not JsonObject hooks)
                return false;

            StripManaged(hooks, OwnedBy(isDev, devBinaryPath));
            if (hooks.Count == 0) root.Remove("hooks");

            File.WriteAllText(path, root.ToJsonString(WriteOptions));
            return true;
        }
        catch
        {
            return false;
        }
    }

    // Builds one { "matcher": "", "hooks": [ <command object> ] } entry for a single managed hook. The
    // _perch.dev flag records which profile wrote it, so a dev instance can strip only its own (see
    // OwnedBy); it rides alongside the durable command-path signal in case an external rewrite drops it.
    private static JsonObject NewEntry(string bin, string arg, string version, bool isDev) => new()
    {
        ["matcher"] = "",
        ["hooks"] = new JsonArray(new JsonObject
        {
            ["type"]    = "command",
            ["command"] = bin,
            ["args"]    = new JsonArray(JsonValue.Create(arg)),
            ["_perch"]  = new JsonObject
            {
                ["managed"] = true,
                ["dev"]     = isDev,
                ["version"] = version,
                ["note"]    = ManagedNote,
            },
        }),
    };

    // Removes every command object the caller owns (per <paramref name="owned"/>) from the hooks map,
    // dropping any entry (and any event key) left empty. Snapshots the keys/indices first since we mutate
    // as we go.
    private static void StripManaged(JsonObject hooks, Func<JsonNode?, bool> owned)
    {
        foreach (var evt in hooks.Select(kv => kv.Key).ToList())
        {
            if (hooks[evt] is not JsonArray entries) continue;

            for (int i = entries.Count - 1; i >= 0; i--)
            {
                if (entries[i] is not JsonObject entry || entry["hooks"] is not JsonArray hookList)
                    continue;

                for (int j = hookList.Count - 1; j >= 0; j--)
                    if (owned(hookList[j]))
                        hookList.RemoveAt(j);

                if (hookList.Count == 0)
                    entries.RemoveAt(i);
            }

            if (entries.Count == 0)
                hooks.Remove(evt);
        }
    }

    // The ownership predicate for a strip pass. Release (isDev == false) is authoritative and owns every
    // Perch-managed entry — any profile's — so it also sweeps up a dev instance's leftovers. Dev owns only
    // what it wrote (its _perch.dev marker, or a command equal to its own dev binary), so it never disturbs
    // an installed release Perch's hooks.
    private static Func<JsonNode?, bool> OwnedBy(bool isDev, string? devBinaryPath) =>
        isDev ? hook => IsDevOwned(hook, devBinaryPath) : IsManaged;

    // An entry is "ours" if it carries the _perch.managed marker OR its command runs Perch's hook binary.
    // The marker is the primary signal, but Claude Code re-serialises settings.json through its own hook
    // schema whenever it rewrites the file (a /model or theme change, a plugin toggle, …), dropping our
    // unknown _perch field. Matching the command by binary name as a fallback keeps reconcile idempotent
    // across those rewrites — without it every launch after one would fail to strip the now-unmarked
    // entries and re-add the full set, so the hook block grows by seven each launch.
    private static bool IsManaged(JsonNode? hook) =>
        hook is JsonObject o && (HasMarker(o["_perch"]) || IsPerchCommand(o["command"]));

    // A dev instance owns an entry only if it wrote it: the _perch.dev marker, or (marker stripped) a
    // command equal to this dev instance's own binary path. Never matches a release entry.
    private static bool IsDevOwned(JsonNode? hook, string? devBinaryPath) =>
        hook is JsonObject o && (HasDevMarker(o["_perch"]) || SameCommand(o["command"], devBinaryPath));

    private static bool HasMarker(JsonNode? perch) =>
        perch is JsonObject p && ReadTrue(p["managed"]);

    private static bool HasDevMarker(JsonNode? perch) =>
        perch is JsonObject p && ReadTrue(p["managed"]) && ReadTrue(p["dev"]);

    // True when a hook command runs Perch's hook binary, identified by file name so the match survives a
    // different install dir, a Windows "\" vs POSIX "/" separator (e.g. a Windows-written path seen on a
    // later reconcile), and a ".exe" suffix.
    private static bool IsPerchCommand(JsonNode? command)
    {
        if (command is null || command.GetValueKind() != JsonValueKind.String) return false;
        var leaf = command.ToString();
        int cut = leaf.LastIndexOfAny(PathSeparators);
        if (cut >= 0) leaf = leaf[(cut + 1)..];
        if (leaf.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) leaf = leaf[..^4];
        return string.Equals(leaf, HookExeName, StringComparison.OrdinalIgnoreCase);
    }

    // Whole-path command match, tolerant of "\" vs "/" and case — used to recognise a dev instance's own
    // entries by their exact binary path when the _perch marker has been stripped.
    private static bool SameCommand(JsonNode? command, string? path)
    {
        if (path is null || command is null || command.GetValueKind() != JsonValueKind.String) return false;
        return string.Equals(
            command.ToString().Replace('\\', '/'), path.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase);
    }

    // Tolerant boolean read: accepts a JSON true or the string "true" (a hand-edited file).
    private static bool ReadTrue(JsonNode? n)
    {
        if (n is null) return false;
        try { return n.GetValue<bool>(); }
        catch { return string.Equals(n.ToString(), "true", StringComparison.OrdinalIgnoreCase); }
    }

    // The env var Claude Code reads to turn on the experimental Agent Teams feature. "1" enables it;
    // the key's absence is treated as off, and disabling removes it rather than writing "0".
    private const string AgentTeamsEnvKey = "CLAUDE_CODE_EXPERIMENTAL_AGENT_TEAMS";

    // Tolerant parse: ~/.claude/settings.json is hand-editable, so allow the slips Claude Code itself
    // tolerates (trailing commas, // comments) rather than failing the read.
    private static readonly JsonDocumentOptions ReadOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling     = JsonCommentHandling.Skip,
    };

    /// <summary>True when <c>env.CLAUDE_CODE_EXPERIMENTAL_AGENT_TEAMS</c> is set to "1".</summary>
    public static bool IsAgentTeamsEnabled()
    {
        try
        {
            var path = ClaudePaths.UserSettingsFile;
            if (!File.Exists(path))
                return false;

            var root = JsonNode.Parse(File.ReadAllText(path), documentOptions: ReadOptions) as JsonObject;
            // ToString() rather than GetValue<string>() so a numeric 1 doesn't throw — either reads "1".
            return (root?["env"] as JsonObject)?[AgentTeamsEnvKey]?.ToString() == "1";
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Sets or clears <c>env.CLAUDE_CODE_EXPERIMENTAL_AGENT_TEAMS</c> in <c>~/.claude/settings.json</c>,
    /// preserving every other setting. Enabling writes "1"; disabling removes the key (and the env
    /// object if that empties it). Returns true on a successful write.
    /// </summary>
    public static bool SetAgentTeamsEnabled(bool enabled)
    {
        try
        {
            var path = ClaudePaths.UserSettingsFile;
            var root = File.Exists(path)
                ? JsonNode.Parse(File.ReadAllText(path), documentOptions: ReadOptions) as JsonObject ?? new JsonObject()
                : new JsonObject();

            if (root["env"] is not JsonObject env)
            {
                if (!enabled) return true;   // already absent — nothing to write
                env = new JsonObject();
                root["env"] = env;
            }

            if (enabled)
            {
                env[AgentTeamsEnvKey] = "1";
            }
            else
            {
                env.Remove(AgentTeamsEnvKey);
                if (env.Count == 0) root.Remove("env");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            return true;
        }
        catch
        {
            return false;
        }
    }
}
