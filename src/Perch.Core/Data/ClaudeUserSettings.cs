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
    // fires perch-hook with the args the binary switches on. We use the exec form (command + args)
    // rather than a single command string so a bin path containing spaces (e.g. the "Perch (Dev)"
    // profile) needs no shell quoting. A retired entry (the parked permission valet's `valet`, removed in
    // review fixes CP10) needs no migration: reconcile strips every Perch-managed entry before re-adding
    // this set, so an old install's leftover disappears on the next launch.
    private static readonly (string Event, string[] Args)[] ManagedHooks =
    {
        ("PreToolUse",   ["mode"]),
        ("PostToolUse",  ["mode"]),
        ("Stop",         ["mode"]),
        ("SubagentStop", ["agentstop"]),
        ("TeammateIdle", ["teammateidle"]),
        ("SessionStart", ["start"]),
        ("SessionEnd",   ["cleanup"]),
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
            if (OpenForWrite(path) is not (JsonObject root, var original)) return false;

            if (root["hooks"] is not JsonObject hooks)
            {
                hooks = new JsonObject();
                root["hooks"] = hooks;
            }

            StripManaged(hooks, OwnedBy(isDev, hookBinaryPath));

            foreach (var (evt, hookArgs) in ManagedHooks)
            {
                if (hooks[evt] is not JsonArray arr)
                {
                    arr = new JsonArray();
                    hooks[evt] = arr;
                }
                arr.Add(NewEntry(hookBinaryPath, hookArgs, version, isDev));
            }

            if (hooks.Count == 0) root.Remove("hooks");

            // Runs on every launch; an already-reconciled file is left untouched (no rewrite, no mtime bump).
            Commit(path, root, original);
            return true;
        }
        catch
        {
            return false;
        }
    }

    // ── Safe read-modify-write (review fixes CP14) ───────────────────────────────────
    // settings.json is Claude Code's own file, written by it, by the hook and by Perch. Opening it for a rewrite
    // must never turn "couldn't read it" into "start from {}": a missing file is the only fresh start; one that
    // exists but is unreadable (locked mid-write), unparseable or not a JSON object is refused. Parse failures
    // throw, and each caller's catch turns them into "no change".

    // The file's JSON object to mutate, plus a deep copy of it as read (null when the file didn't exist). Null
    // when the file exists but couldn't be read or isn't an object — the caller must leave it alone.
    private static (JsonObject Root, JsonObject? Original)? OpenForWrite(string path)
    {
        switch (AtomicFile.TryRead(path, out var text))
        {
            case AtomicFile.ReadResult.Missing: return (new JsonObject(), null);
            case AtomicFile.ReadResult.Failed: return null;
        }
        if (JsonNode.Parse(text, documentOptions: ReadOptions) is not JsonObject root) return null;
        return (root, (JsonObject)root.DeepClone());
    }

    // Writes `root` back atomically — unless it's still deep-equal to what was read, so an idempotent pass never
    // touches the file (and a reformat by Claude Code alone doesn't make every later pass rewrite it). Returns
    // whether it wrote.
    private static bool Commit(string path, JsonObject root, JsonObject? original)
    {
        if (original is not null && JsonNode.DeepEquals(original, root)) return false;
        AtomicFile.Write(path, root.ToJsonString(WriteOptions));
        return true;
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
            if (OpenForWrite(path) is not (JsonObject root, var original) || original is null
                || root["hooks"] is not JsonObject hooks)
                return false;   // no file, unreadable, or no hooks block

            // Only rewrite when we actually removed one of our own entries — so calling this on a directory
            // that has no Perch hooks (e.g. an auto-discovered dir the user left hooks off for) never touches
            // its file, and ReconcileAll can safely strip every not-hooked dir without gratuitous writes.
            if (!StripManaged(hooks, OwnedBy(isDev, devBinaryPath))) return false;
            if (hooks.Count == 0) root.Remove("hooks");

            return Commit(path, root, original);
        }
        catch
        {
            return false;
        }
    }

    // Builds one { "matcher": "", "hooks": [ <command object> ] } entry for a single managed hook. The
    // _perch.dev flag records which profile wrote it, so a dev instance can strip only its own (see
    // OwnedBy); it rides alongside the durable command-path signal in case an external rewrite drops it.
    private static JsonObject NewEntry(string bin, string[] hookArgs, string version, bool isDev) => new()
    {
        ["matcher"] = "",
        ["hooks"] = new JsonArray(new JsonObject
        {
            ["type"]    = "command",
            ["command"] = bin,
            ["args"]    = new JsonArray(hookArgs.Select(a => (JsonNode?)JsonValue.Create(a)).ToArray()),
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
    // as we go. Returns true if it removed anything (so a caller can skip an otherwise no-op rewrite).
    private static bool StripManaged(JsonObject hooks, Func<JsonNode?, bool> owned)
    {
        bool removed = false;
        foreach (var evt in hooks.Select(kv => kv.Key).ToList())
        {
            if (hooks[evt] is not JsonArray entries) continue;

            for (int i = entries.Count - 1; i >= 0; i--)
            {
                if (entries[i] is not JsonObject entry || entry["hooks"] is not JsonArray hookList)
                    continue;

                for (int j = hookList.Count - 1; j >= 0; j--)
                    if (owned(hookList[j]))
                    {
                        hookList.RemoveAt(j);
                        removed = true;
                    }

                if (hookList.Count == 0)
                    entries.RemoveAt(i);
            }

            if (entries.Count == 0)
                hooks.Remove(evt);
        }
        return removed;
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

    /// <summary>
    /// What a new Claude Code session starts with, per <c>~/.claude/settings.json</c>: the <c>model</c>,
    /// <c>permissions.defaultMode</c> and <c>effortLevel</c> keys. Each is null when unset (the CLI then
    /// applies its own built-in default). Read best-effort; a missing or malformed file yields all-null.
    /// </summary>
    public static SessionDefaults ReadSessionDefaults()
    {
        try
        {
            var path = ClaudePaths.UserSettingsFile;
            if (!File.Exists(path)) return SessionDefaults.None;
            var root = JsonNode.Parse(File.ReadAllText(path), documentOptions: ReadOptions) as JsonObject;
            return new SessionDefaults(
                TranscriptJson.AsString(root?["model"]),
                TranscriptJson.AsString((root?["permissions"] as JsonObject)?["defaultMode"]),
                TranscriptJson.AsString(root?["effortLevel"]));
        }
        catch
        {
            return SessionDefaults.None;
        }
    }

    /// <summary>True when <c>env.CLAUDE_CODE_EXPERIMENTAL_AGENT_TEAMS</c> is "1" in the <b>primary</b>
    /// config dir's settings.json.</summary>
    public static bool IsAgentTeamsEnabled() => IsAgentTeamsEnabled(ClaudePaths.UserSettingsFile);

    /// <summary>As <see cref="IsAgentTeamsEnabled()"/>, against a specific config dir's settings file — so a
    /// multi-config-dir setup can read the flag per directory (each dir is a separate settings.json).</summary>
    public static bool IsAgentTeamsEnabled(string settingsPath)
    {
        try
        {
            if (!File.Exists(settingsPath))
                return false;

            var root = JsonNode.Parse(File.ReadAllText(settingsPath), documentOptions: ReadOptions) as JsonObject;
            // ToString() rather than GetValue<string>() so a numeric 1 doesn't throw — either reads "1".
            return (root?["env"] as JsonObject)?[AgentTeamsEnvKey]?.ToString() == "1";
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Sets or clears <c>env.CLAUDE_CODE_EXPERIMENTAL_AGENT_TEAMS</c> in the <b>primary</b> config dir's
    /// settings.json, preserving every other setting. Enabling writes "1"; disabling removes the key (and the
    /// env object if that empties it). Returns true on a successful write.
    /// </summary>
    public static bool SetAgentTeamsEnabled(bool enabled) =>
        SetAgentTeamsEnabled(ClaudePaths.UserSettingsFile, enabled);

    /// <summary>As <see cref="SetAgentTeamsEnabled(bool)"/>, against a specific config dir's settings file —
    /// so a multi-config-dir setup can flip the flag per directory (each dir is a separate settings.json).</summary>
    public static bool SetAgentTeamsEnabled(string settingsPath, bool enabled)
    {
        try
        {
            var path = settingsPath;
            if (OpenForWrite(path) is not (JsonObject root, var original)) return false;

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

            Commit(path, root, original);
            return true;
        }
        catch
        {
            return false;
        }
    }

    // ── statusLine (Perch's statusline designer) ─────────────────────────────────────
    // The root-level "statusLine" block Claude Code invokes on each refresh. Perch owns the profile
    // library elsewhere (Perch.Statusline.StatuslineStore); applying a profile is what writes this block.
    // Read/write preserve every other key, mirroring the env/hook helpers above.

    /// <summary>The current <c>statusLine.command</c> string in <c>~/.claude/settings.json</c>, or null
    /// when there is no status line configured. Used to back up whatever's already there (Perch's or an
    /// external tool's) before switching profiles.</summary>
    public static string? ReadStatusLineCommand() => ReadStatusLineCommand(ClaudePaths.UserSettingsFile);

    /// <summary>As <see cref="ReadStatusLineCommand()"/>, against an explicit settings file (test seam).</summary>
    public static string? ReadStatusLineCommand(string settingsPath)
    {
        try
        {
            if (!File.Exists(settingsPath)) return null;
            var root = JsonNode.Parse(File.ReadAllText(settingsPath), documentOptions: ReadOptions) as JsonObject;
            return TranscriptJson.AsString((root?["statusLine"] as JsonObject)?["command"]);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Writes the <c>statusLine</c> block (<c>type: "command"</c>, the given command and
    /// padding), preserving every other setting. Returns true on a successful write.</summary>
    public static bool SetStatusLine(string command, int padding = 0) =>
        SetStatusLine(ClaudePaths.UserSettingsFile, command, padding);

    /// <summary>As <see cref="SetStatusLine(string,int)"/>, against an explicit settings file (test seam).</summary>
    public static bool SetStatusLine(string settingsPath, string command, int padding = 0)
    {
        try
        {
            if (OpenForWrite(settingsPath) is not (JsonObject root, var original)) return false;

            root["statusLine"] = new JsonObject
            {
                ["type"]    = "command",
                ["command"] = command,
                ["padding"] = padding,
            };

            Commit(settingsPath, root, original);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Removes the <c>statusLine</c> block entirely (revert to no status line), preserving every
    /// other setting. Returns true if the file was rewritten.</summary>
    public static bool ClearStatusLine() => ClearStatusLine(ClaudePaths.UserSettingsFile);

    /// <summary>As <see cref="ClearStatusLine()"/>, against an explicit settings file (test seam).</summary>
    public static bool ClearStatusLine(string settingsPath)
    {
        try
        {
            if (OpenForWrite(settingsPath) is not (JsonObject root, var original) || original is null
                || !root.ContainsKey("statusLine"))
                return false;

            root.Remove("statusLine");
            return Commit(settingsPath, root, original);
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>The user's configured session defaults from <c>settings.json</c> (see
/// <see cref="ClaudeUserSettings.ReadSessionDefaults"/>); null = not set there.</summary>
internal sealed record SessionDefaults(string? Model, string? PermissionMode, string? Effort)
{
    public static readonly SessionDefaults None = new(null, null, null);
}
