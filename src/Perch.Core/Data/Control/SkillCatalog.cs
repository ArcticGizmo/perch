namespace Perch.Data.Control;

/// <summary>One discovered Claude Code <em>skill</em> (a user/project/plugin slash command, as opposed to a
/// built-in), ready to drop into the session palette. <see cref="Command"/> is the name with no leading
/// slash: a bare name for a user/project skill (<c>grill-me</c>), or <c>plugin:skill</c> for a plugin one
/// (<c>nexus:e2e-test</c>). <see cref="Description"/> is the front-matter one-liner (may be empty).</summary>
internal sealed record SkillInfo(string Command, string Description, bool IsPlugin);

/// <summary>
/// Discovers Claude Code skills — the user/project/plugin slash commands that aren't built-ins — by scanning
/// the on-disk <c>SKILL.md</c> trees, so the session palette can offer them alongside the built-ins in
/// <see cref="SlashCommandCatalog"/>. Each skill's description is lifted from its YAML front-matter.
///
/// <para>Two sources, treated differently because their availability signals differ:</para>
/// <list type="bullet">
///   <item><b>User + project skills</b> (<c>~/.claude/skills</c> and <c>&lt;cwd&gt;/.claude/skills</c>) are
///   read straight from disk — they're the user's / repo's own and are effectively always available in a
///   session, so a bare name is trusted as-is.</item>
///   <item><b>Plugin skills</b> live under <c>~/.claude/plugins/marketplaces</c>, but that tree holds every
///   <em>installed</em> marketplace while only the <em>enabled</em> plugins are live in any given session.
///   Trusting the disk set would over-report wildly (dozens of marketplace skills the session can't run), so
///   the plugin set is taken from the running session's <em>advertised</em> command list
///   (<see cref="SessionConversation.SlashCommands"/>) and the disk scan only supplies their descriptions.</item>
/// </list>
/// </summary>
internal static class SkillCatalog
{
    /// <summary>
    /// The skills to offer for a session: the user's and the project's own skills (from disk), followed by
    /// the plugin skills the running session advertised (with descriptions filled in from disk where found).
    /// Curated built-ins and internal plumbing are excluded, and a name is never listed twice (a project
    /// skill shadows a same-named user skill; the first advertised plugin entry wins). Best-effort — a
    /// missing or unreadable tree just yields fewer entries, never an exception.
    /// </summary>
    /// <param name="cwd">The session's working directory (for <c>&lt;cwd&gt;/.claude/skills</c>); may be null/empty.</param>
    /// <param name="advertisedCommands">The CLI's advertised <c>slash_commands</c> for this session — the
    /// authoritative source of which plugin skills are actually live (names may carry a leading slash).</param>
    public static IReadOnlyList<SkillInfo> ForSession(string? cwd, IEnumerable<string> advertisedCommands)
    {
        var result = new List<SkillInfo>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            // User + project skills — bare names, straight from disk. Project first so it shadows a
            // same-named user skill. Sorted by name for a stable palette order.
            var bare = new List<SkillInfo>();
            foreach (var root in BareSkillRoots(cwd))
                foreach (var (name, desc) in ScanBareSkills(root))
                {
                    if (SlashCommandCatalog.IsBuiltIn(name) || SlashCommandCatalog.IsInternal(name)) continue;
                    if (!seen.Add(name)) continue;
                    bare.Add(new SkillInfo(name, desc, IsPlugin: false));
                }
            bare.Sort(static (a, b) => string.Compare(a.Command, b.Command, StringComparison.OrdinalIgnoreCase));
            result.AddRange(bare);

            // Plugin skills — availability from the session's advertised list; descriptions from disk.
            var (byCommand, byLeaf) = ScanPluginSkills();
            var plugins = new List<SkillInfo>();
            foreach (var raw in advertisedCommands ?? [])
            {
                var cmd = (raw ?? "").TrimStart('/');
                int colon = cmd.IndexOf(':');
                if (colon <= 0) continue;                                   // plugin skills are namespaced
                if (SlashCommandCatalog.IsInternal(cmd)) continue;
                if (!seen.Add(cmd)) continue;
                var leaf = cmd[(colon + 1)..];
                var desc = byCommand.TryGetValue(cmd, out var d) ? d
                         : byLeaf.TryGetValue(leaf, out var dl) ? dl
                         : "";
                plugins.Add(new SkillInfo(cmd, desc, IsPlugin: true));
            }
            plugins.Sort(static (a, b) => string.Compare(a.Command, b.Command, StringComparison.OrdinalIgnoreCase));
            result.AddRange(plugins);
        }
        catch
        {
            // Discovery is a convenience; never let a filesystem hiccup break the palette.
        }

        return result;
    }

    // The directories that hold bare-named skills, project before user (project wins on a name clash). Only
    // existing directories are returned.
    private static IEnumerable<string> BareSkillRoots(string? cwd)
    {
        if (!string.IsNullOrWhiteSpace(cwd))
        {
            var projectSkills = Path.Combine(cwd, ".claude", "skills");
            if (Directory.Exists(projectSkills)) yield return projectSkills;
        }
        var userSkills = Path.Combine(ClaudePaths.ClaudeDir, "skills");
        if (Directory.Exists(userSkills)) yield return userSkills;
    }

    // Every immediate child directory of <paramref name="root"/> that carries a SKILL.md, as
    // (directoryName, description) pairs.
    private static IEnumerable<(string Name, string Description)> ScanBareSkills(string root)
    {
        foreach (var dir in SafeDirectories(root))
        {
            var file = Path.Combine(dir, "SKILL.md");
            if (!File.Exists(file)) continue;
            yield return (Path.GetFileName(dir), ParseDescription(file));
        }
    }

    // Descriptions for the installed plugins' skills, keyed both by the fully-qualified "<plugin>:<skill>"
    // command and by the bare "<skill>" leaf (a fallback for when the namespacing doesn't line up exactly
    // with what the CLI advertised). Reads the marketplaces tree; the parallel "cache" clones are ignored to
    // avoid double-counting.
    private static (Dictionary<string, string> ByCommand, Dictionary<string, string> ByLeaf) ScanPluginSkills()
    {
        var byCommand = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var byLeaf = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var marketplaces = Path.Combine(ClaudePaths.PluginsDir, "marketplaces");
        if (!Directory.Exists(marketplaces)) return (byCommand, byLeaf);

        // A marketplace keeps its plugins under "plugins/" and/or "external_plugins/"; each plugin owns a
        // "skills/<skill>/SKILL.md". The command Claude Code exposes is "<pluginDir>:<skillDir>".
        foreach (var marketplace in SafeDirectories(marketplaces))
            foreach (var pluginsGroup in new[] { "plugins", "external_plugins" })
            {
                var group = Path.Combine(marketplace, pluginsGroup);
                if (!Directory.Exists(group)) continue;
                foreach (var plugin in SafeDirectories(group))
                {
                    var skillsDir = Path.Combine(plugin, "skills");
                    if (!Directory.Exists(skillsDir)) continue;
                    var pluginName = Path.GetFileName(plugin);
                    foreach (var skill in SafeDirectories(skillsDir))
                    {
                        var file = Path.Combine(skill, "SKILL.md");
                        if (!File.Exists(file)) continue;
                        var leaf = Path.GetFileName(skill);
                        var desc = ParseDescription(file);
                        byCommand[$"{pluginName}:{leaf}"] = desc;
                        byLeaf.TryAdd(leaf, desc);
                    }
                }
            }

        return (byCommand, byLeaf);
    }

    private static string[] SafeDirectories(string path)
    {
        try { return Directory.GetDirectories(path); }
        catch { return []; }
    }

    // Pulls the one-line "description" out of a SKILL.md YAML front-matter block. Handles both the inline
    // form (`description: text`) and a block scalar (`description: |` / `>` followed by indented lines), in
    // which case the first content line is used. Returns "" when there's no front-matter or no description.
    private static string ParseDescription(string file)
    {
        try
        {
            var lines = File.ReadAllLines(file);
            int i = 0;
            // Front-matter must open with a "---" fence (allowing a leading blank line).
            while (i < lines.Length && lines[i].Trim().Length == 0) i++;
            if (i >= lines.Length || lines[i].Trim() != "---") return "";
            i++;

            for (; i < lines.Length; i++)
            {
                var line = lines[i];
                if (line.Trim() == "---") break;                // end of front-matter

                const string key = "description:";
                var trimmed = line.TrimStart();
                if (!trimmed.StartsWith(key, StringComparison.OrdinalIgnoreCase)) continue;

                var value = trimmed[key.Length..].Trim();
                if (value is not ("" or "|" or ">" or "|-" or ">-" or "|+" or ">+"))
                    return value;                              // inline description

                // Block scalar: the description is the following indented lines; take the first content one.
                for (int j = i + 1; j < lines.Length; j++)
                {
                    if (lines[j].Trim() == "---") break;
                    var content = lines[j].Trim();
                    if (content.Length > 0) return content;
                }
                return "";
            }
        }
        catch
        {
            // Unreadable file → no description; the skill still lists by name.
        }
        return "";
    }
}
