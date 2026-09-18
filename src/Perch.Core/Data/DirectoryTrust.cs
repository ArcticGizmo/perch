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
/// round-tripped through <see cref="JsonNode"/> — every other field is preserved — and replaced atomically;
/// a file that exists but does not parse as an object is left untouched rather than clobbered.</para>
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
    /// Best-effort: returns false (without touching the file) if it exists but is not a JSON object.</summary>
    public static bool GrantAt(string claudeJsonPath, string cwd)
    {
        try
        {
            var json = ReadAllText(claudeJsonPath);
            JsonObject root;
            if (string.IsNullOrWhiteSpace(json))
                root = new JsonObject();
            else if (JsonNode.Parse(json) is JsonObject obj)
                root = obj;
            else
                return false;   // unexpected shape — never clobber it

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
            entry["hasTrustDialogAccepted"] = true;

            AtomicWrite(claudeJsonPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsAccepted(JsonObject entry) =>
        entry["hasTrustDialogAccepted"] is JsonValue v && v.TryGetValue(out bool accepted) && accepted;

    private static string? ReadAllText(string path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(fs);
            return reader.ReadToEnd();
        }
        catch { return null; }
    }

    private static void AtomicWrite(string path, string text)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(tmp, text);
        try
        {
            if (File.Exists(path)) File.Replace(tmp, path, null);
            else File.Move(tmp, path);
        }
        catch
        {
            // A locked target (Claude writing concurrently) — fall back to a plain overwrite, then give up
            // leaving the temp behind rather than throwing out of a best-effort write.
            try { File.Move(tmp, path, overwrite: true); }
            catch { TryDelete(tmp); throw; }
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* ignore */ }
    }
}
