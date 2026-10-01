using System.Text.Json;
using System.Text.Json.Nodes;

namespace Perch.Data;

/// <summary>
/// The "quick safety check" — Claude Code's <i>"Do you trust the files in this folder?"</i> gate,
/// replicated for Perch-launched sessions.
/// </summary>
/// <remarks>
/// <para>Perch drives sessions headlessly (<c>claude -p --output-format stream-json …</c>), and Claude Code
/// <b>deliberately skips</b> its own trust dialog in that non-interactive mode. So a folder that Claude
/// would have prompted about is entered silently when Perch launches it — this type restores the gate on
/// Perch's side, reading and writing the <b>same</b> store Claude uses so a decision made in either is
/// honoured by the other.</para>
/// <para>Trust lives in <c>.claude.json</c> under
/// <c>projects["&lt;path&gt;"].hasTrustDialogAccepted</c> (a bool). Prompting uses an <b>ancestor walk</b>:
/// trusting a folder trusts its subfolders, so we consider the cwd trusted if it, or any parent directory,
/// carries an accepted flag (matching Claude Code's granting behaviour). The home directory is trusted
/// implicitly (Claude accepts it for the session without persisting).</para>
/// <para>Claude keys the map by a literal, un-normalised path string (a documented quirk). For lookups we
/// are more forgiving — separators and, off Linux, case are normalised via
/// <see cref="ClaudeConfigDir.PathComparer"/> — while writes use the forward-slash absolute form Claude
/// itself emits (e.g. <c>C:/Users/me/proj</c>) so the key we persist is the one Claude will match.</para>
/// <para>All IO is best-effort and never throws: an unreadable file reads as "untrusted" and a failed
/// write returns <c>false</c> (the caller still proceeds for the launch it was granted). The whole file is
/// round-tripped through <see cref="JsonNode"/> — every other field is preserved — and replaced atomically
/// (<see cref="AtomicFile"/>). A file that exists but is unreadable, empty, or not a JSON object is left
/// untouched rather than clobbered: it also holds the user's Claude sign-in (see <see cref="GrantAt"/>).</para>
/// </remarks>
internal static class DirectoryTrust
{
    // ── Pure: normalisation + the ancestor-walk decision ─────────────────────────────

    /// <summary>Whether <paramref name="cwd"/> counts as trusted given the set of accepted project keys —
    /// true if the cwd, any ancestor, or (when supplied) the home dir is present. Pure.</summary>
    public static bool IsTrusted(IReadOnlyCollection<string> trustedKeys, string cwd, string? homeDir = null)
    {
        if (string.IsNullOrWhiteSpace(cwd)) return false;

        var cwdCanon = Canonical(cwd);
        if (homeDir is not null && ClaudeConfigDir.PathComparer.Equals(cwdCanon, Canonical(homeDir)))
            return true;   // Claude Code trusts the home directory itself without a prompt

        if (trustedKeys.Count == 0) return false;
        var trusted = new HashSet<string>(ClaudeConfigDir.PathComparer);
        foreach (var k in trustedKeys) trusted.Add(Canonical(k));

        foreach (var ancestor in AncestorsInclusive(cwd))
            if (trusted.Contains(Canonical(ancestor))) return true;
        return false;
    }

    /// <summary>Whether two paths name the same directory, tolerant of separators/case (null-safe).</summary>
    public static bool SameDir(string? a, string? b) =>
        a is not null && b is not null && ClaudeConfigDir.PathComparer.Equals(Canonical(a), Canonical(b));

    /// <summary>The map key to persist for <paramref name="path"/>: its absolute, forward-slash form with any
    /// trailing separator trimmed (e.g. <c>C:/Users/me/proj</c>) — matching how Claude Code writes it.</summary>
    public static string CanonicalKey(string path)
    {
        var full = SafeFullPath(path) ?? path;
        return Path.TrimEndingDirectorySeparator(full).Replace('\\', '/');
    }

    /// <summary>A path in a form suitable for equality comparison: absolute, native separators, no trailing
    /// separator. Falls back to the input when it can't be resolved.</summary>
    internal static string Canonical(string path)
    {
        var full = SafeFullPath(path);
        return full is null ? path : Path.TrimEndingDirectorySeparator(full);
    }

    /// <summary>The directory and each of its ancestors up to the filesystem root, inclusive.</summary>
    internal static IEnumerable<string> AncestorsInclusive(string path)
    {
        var full = SafeFullPath(path);
        if (full is null) yield break;
        for (var dir = new DirectoryInfo(full); dir is not null; dir = dir.Parent)
            yield return dir.FullName;
    }

    private static string? SafeFullPath(string p)
    {
        try { return string.IsNullOrWhiteSpace(p) ? null : Path.GetFullPath(p); }
        catch { return null; }
    }

    // ── IO: read/write the shared .claude.json trust store ───────────────────────────

    /// <summary>The <c>.claude.json</c> that holds trust for a launch under <paramref name="configDir"/>: the
    /// injected dir's own file when an account is pinned (<c>CLAUDE_CONFIG_DIR</c>), else the file Claude Code
    /// reads when inheriting Perch's environment (the pinned primary's, or the legacy <c>~/.claude.json</c>).</summary>
    public static string ResolveTrustFile(string? configDir)
    {
        if (!string.IsNullOrWhiteSpace(configDir))
            return Path.Combine(configDir!, ".claude.json");
        return ClaudeConfigSet.IsPinned
            ? Path.Combine(ClaudePaths.ClaudeDir, ".claude.json")
            : Path.Combine(ClaudeConfigSet.Home, ".claude.json");
    }

    /// <summary>Whether <paramref name="cwd"/> is already trusted for a launch under
    /// <paramref name="configDir"/>. Best-effort; false on any read failure.</summary>
    public static bool Evaluate(string? configDir, string cwd) =>
        IsTrusted(ReadTrustedKeys(ResolveTrustFile(configDir)), cwd, ClaudeConfigSet.Home);

    /// <summary>Records trust for <paramref name="cwd"/> in the store for a launch under
    /// <paramref name="configDir"/>. Returns whether it was persisted.</summary>
    public static bool Grant(string? configDir, string cwd) => GrantAt(ResolveTrustFile(configDir), cwd);

    /// <summary>The trusted (accepted) project keys in a <c>.claude.json</c> file; empty when missing,
    /// locked, or malformed.</summary>
    public static IReadOnlyList<string> ReadTrustedKeys(string claudeJsonPath)
    {
        var keys = new List<string>();
        var json = ReadAllText(claudeJsonPath);
        if (string.IsNullOrEmpty(json)) return keys;
        try
        {
            if (JsonNode.Parse(json)?["projects"] is not JsonObject projects) return keys;
            foreach (var (key, value) in projects)
                if (value is JsonObject entry && IsAccepted(entry))
                    keys.Add(key);
        }
        catch { /* best-effort: a partial/locked file reads as no trust */ }
        return keys;
    }

    /// <summary>Sets <c>hasTrustDialogAccepted: true</c> for <paramref name="cwd"/> in the given
    /// <c>.claude.json</c>, preserving every other field and reusing an existing key for the same directory.
    /// Returns whether the folder is now recorded as trusted.</summary>
    /// <remarks>
    /// <c>.claude.json</c> also holds the user's Claude sign-in and every project's state, so this never replaces
    /// it with anything but a merge of what's there (review fixes CP14). A fresh object is used <b>only</b> when
    /// the file provably doesn't exist; a read failure (Claude has it locked mid-write), an empty file, or content
    /// that isn't a JSON object all return false with the bytes untouched. The file is re-read just before the
    /// atomic replace, and the merge redone if Claude changed it in between. Already trusted → no write at all.
    /// </remarks>
    public static bool GrantAt(string claudeJsonPath, string cwd)
    {
        try
        {
            for (int attempt = 0; attempt < 3; attempt++)
            {
                var read = AtomicFile.TryRead(claudeJsonPath, out var json);
                if (read == AtomicFile.ReadResult.Failed) return false;
                if (read == AtomicFile.ReadResult.Ok && string.IsNullOrWhiteSpace(json)) return false;   // mid-write, or damaged

                var merged = Merge(read == AtomicFile.ReadResult.Missing ? null : json, cwd);
                if (merged is null) return false;          // not a JSON object — never clobber it
                if (merged.Value.AlreadyTrusted) return true;

                // Claude writes this file often; only replace it if it's still what we merged into.
                var again = AtomicFile.TryRead(claudeJsonPath, out var now);
                if (again != read || (read == AtomicFile.ReadResult.Ok && !string.Equals(now, json, StringComparison.Ordinal)))
                    continue;

                AtomicFile.Write(claudeJsonPath, merged.Value.Text);
                return true;
            }
            return false;   // the file kept changing under us — leave it to the next launch
        }
        catch
        {
            return false;
        }
    }

    // The trust grant merged into `json` (null = no file yet): the new text, or AlreadyTrusted when the folder's
    // entry is already accepted (nothing to write). Null when `json` isn't a JSON object. Throws on unparseable
    // JSON (the caller's catch turns that into "don't touch it").
    private static (string Text, bool AlreadyTrusted)? Merge(string? json, string cwd)
    {
        JsonObject root;
        if (json is null) root = new JsonObject();
        else if (JsonNode.Parse(json) is JsonObject obj) root = obj;
        else return null;

        if (root["projects"] is not JsonObject projects)
        {
            projects = new JsonObject();
            root["projects"] = projects;
        }

        // Reuse an existing entry for this directory (any path spelling) rather than adding a duplicate.
        string? existing = null;
        foreach (var (key, _) in projects)
            if (ClaudeConfigDir.PathComparer.Equals(Canonical(key), Canonical(cwd))) { existing = key; break; }

        var target = existing ?? CanonicalKey(cwd);
        if (projects[target] is not JsonObject entry)
        {
            entry = new JsonObject();
            projects[target] = entry;
        }
        else if (IsAccepted(entry))
        {
            return ("", true);
        }
        entry["hasTrustDialogAccepted"] = true;

        return (root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), false);
    }

    private static bool IsAccepted(JsonObject entry) =>
        entry["hasTrustDialogAccepted"] is JsonValue v && v.TryGetValue(out bool accepted) && accepted;

    // For the read-only lookup, where missing and unreadable both mean "no trust recorded".
    private static string? ReadAllText(string path) =>
        string.IsNullOrEmpty(path) ? null : AtomicFile.ReadShared(path);
}
