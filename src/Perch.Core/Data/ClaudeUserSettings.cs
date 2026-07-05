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

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    /// <summary>
    /// Reconciles Perch's managed hook block in <c>~/.claude/settings.json</c>: strips every entry we
    /// previously wrote (matched by the <c>_perch.managed</c> marker) then re-adds the current set
    /// pointing at <paramref name="hookBinaryPath"/>. Idempotent — repeated calls converge and never
    /// duplicate — and it only touches our own entries, so user-authored hooks are preserved. Returns
    /// true on a successful write.
    /// </summary>
    public static bool ReconcileHooks(string hookBinaryPath, string version) =>
        ReconcileHooks(ClaudePaths.UserSettingsFile, hookBinaryPath, version);

    /// <summary>As <see cref="ReconcileHooks(string,string)"/>, against an explicit settings file
    /// (test seam).</summary>
    public static bool ReconcileHooks(string settingsPath, string hookBinaryPath, string version)
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

            StripManaged(hooks);

            foreach (var (evt, arg) in ManagedHooks)
            {
                if (hooks[evt] is not JsonArray arr)
                {
                    arr = new JsonArray();
                    hooks[evt] = arr;
                }
                arr.Add(NewEntry(hookBinaryPath, arg, version));
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
    /// leaving user-authored hooks untouched. Returns true if the file was rewritten.
    /// </summary>
    public static bool RemoveManagedHooks() => RemoveManagedHooks(ClaudePaths.UserSettingsFile);

    /// <summary>As <see cref="RemoveManagedHooks()"/>, against an explicit settings file (test seam).</summary>
    public static bool RemoveManagedHooks(string settingsPath)
    {
        try
        {
            var path = settingsPath;
            if (!File.Exists(path)) return false;

            if (JsonNode.Parse(File.ReadAllText(path), documentOptions: ReadOptions) is not JsonObject root
                || root["hooks"] is not JsonObject hooks)
                return false;

            StripManaged(hooks);
            if (hooks.Count == 0) root.Remove("hooks");

            File.WriteAllText(path, root.ToJsonString(WriteOptions));
            return true;
        }
        catch
        {
            return false;
        }
    }

    // Builds one { "matcher": "", "hooks": [ <command object> ] } entry for a single managed hook.
    private static JsonObject NewEntry(string bin, string arg, string version) => new()
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
                ["version"] = version,
                ["note"]    = ManagedNote,
            },
        }),
    };

    // Removes every managed command object from the hooks map, dropping any entry (and any event key)
    // left empty. Snapshots the keys/indices first since we mutate as we go.
    private static void StripManaged(JsonObject hooks)
    {
        foreach (var evt in hooks.Select(kv => kv.Key).ToList())
        {
            if (hooks[evt] is not JsonArray entries) continue;

            for (int i = entries.Count - 1; i >= 0; i--)
            {
                if (entries[i] is not JsonObject entry || entry["hooks"] is not JsonArray hookList)
                    continue;

                for (int j = hookList.Count - 1; j >= 0; j--)
                    if (IsManaged(hookList[j]))
                        hookList.RemoveAt(j);

                if (hookList.Count == 0)
                    entries.RemoveAt(i);
            }

            if (entries.Count == 0)
                hooks.Remove(evt);
        }
    }

    private static bool IsManaged(JsonNode? hook) =>
        hook is JsonObject o && o["_perch"] is JsonObject p && ReadTrue(p["managed"]);

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
