using System.Text.Json;
using System.Text.RegularExpressions;

namespace Perch.Data.Resources;

public enum HookSourceKind { UserSettings, ProjectSettings, Plugin }

/// <summary>One registered hook command (or the status line, as <see cref="HookCatalog.StatusLineEvent"/>), with
/// where it came from. <see cref="Command"/> has the profile folder masked, the same way recorded command lines are,
/// so the two compare directly. <see cref="File"/> is the real local path, for an "open" action; never recorded.</summary>
public sealed record HookDef(
    string Source,
    HookSourceKind Kind,
    string File,
    string Event,
    string Matcher,
    string Command,
    string? PluginId = null)
{
    public bool IsStatusLine => Event == HookCatalog.StatusLineEvent;

    /// <summary>Whether this hook fires for a tool call of <paramref name="tool"/>. Claude Code treats the matcher
    /// as a regex over the tool name, with empty or <c>*</c> meaning every tool.</summary>
    public bool MatchesTool(string tool)
    {
        if (string.IsNullOrEmpty(Matcher) || Matcher == "*") return true;
        try { return Regex.IsMatch(tool, "^(?:" + Matcher + ")$", RegexOptions.None, TimeSpan.FromMilliseconds(50)); }
        catch { return string.Equals(Matcher, tool, StringComparison.Ordinal); }
    }
}

/// <summary>
/// Every hook command that could run in the recorded sessions: the user's settings, each session's project settings,
/// and every installed plugin's <c>hooks/hooks.json</c>, plus the status line. Built by the viewer from the files on
/// disk (best-effort, never throws). Including a plugin that wasn't enabled costs nothing: only hooks whose
/// commands are actually seen running become causes.
/// </summary>
public sealed class HookCatalog
{
    public const string StatusLineEvent = "StatusLine";

    public IReadOnlyList<HookDef> Hooks { get; }

    public HookCatalog(IReadOnlyList<HookDef> hooks) => Hooks = hooks;

    public static readonly HookCatalog Empty = new([]);

    /// <summary>Reads the catalog for the config dir at <paramref name="configRoot"/> and the given project folders.</summary>
    public static HookCatalog Load(string configRoot, IEnumerable<string> projectDirs)
    {
        var hooks = new List<HookDef>();
        foreach (var (name, label) in new[] { ("settings.json", "your settings.json"), ("settings.local.json", "your settings.local.json") })
            hooks.AddRange(FromSettingsFile(Path.Combine(configRoot, name), label, HookSourceKind.UserSettings));

        foreach (var dir in projectDirs.Where(d => !string.IsNullOrWhiteSpace(d)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string leaf = Path.GetFileName(dir.TrimEnd('\\', '/'));
            foreach (var name in new[] { "settings.json", "settings.local.json" })
                hooks.AddRange(FromSettingsFile(Path.Combine(dir, ".claude", name), $"{leaf}/.claude/{name}", HookSourceKind.ProjectSettings));
        }

        var plugins = InstalledPlugins(Path.Combine(configRoot, "plugins", "installed_plugins.json")).ToList();
        // Some plugins run without an installed_plugins.json entry (GitKraken's CLI installs its hooks plugin into the
        // cache directly), so the cache's newest copy of every plugin, and local plugin folders, are read too.
        var known = plugins.Select(p => p.id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        plugins.AddRange(CachedPlugins(Path.Combine(configRoot, "plugins")).Where(p => !known.Contains(p.id)));
        foreach (var (id, installPath) in plugins)
        {
            var file = Path.Combine(installPath, "hooks", "hooks.json");
            string name = id.Split('@')[0];
            hooks.AddRange(FromFile(file, $"the {name} plugin", HookSourceKind.Plugin, id));
        }

        return new HookCatalog(hooks
            .DistinctBy(h => (h.Source, h.Event, h.Matcher, h.Command))
            .ToList());
    }

    private static IEnumerable<HookDef> FromSettingsFile(string file, string label, HookSourceKind kind)
    {
        var json = ReadText(file);
        if (json is null) return [];
        var hooks = Parse(json, label, kind, file).ToList();
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            if (doc.RootElement.TryGetProperty("statusLine", out var sl) && sl.ValueKind == JsonValueKind.Object
                && sl.TryGetProperty("command", out var cmd) && cmd.ValueKind == JsonValueKind.String && cmd.GetString() is { Length: > 0 } c)
                hooks.Add(new HookDef(label, kind, file, StatusLineEvent, "", CommandLineRedactor.Redact(c)));
        }
        catch { }
        return hooks;
    }

    private static IEnumerable<HookDef> FromFile(string file, string label, HookSourceKind kind, string? pluginId) =>
        ReadText(file) is { } json ? Parse(json, label, kind, file, pluginId) : [];

    /// <summary>The hooks in one settings or <c>hooks.json</c> document: <c>{"hooks": {"Event": [{"matcher", "hooks":
    /// [{"type":"command","command"}]}]}}</c>. Malformed parts are skipped.</summary>
    internal static IEnumerable<HookDef> Parse(string json, string label, HookSourceKind kind, string file, string? pluginId = null)
    {
        var result = new List<HookDef>();
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            if (!doc.RootElement.TryGetProperty("hooks", out var events) || events.ValueKind != JsonValueKind.Object) return result;
            foreach (var ev in events.EnumerateObject())
            {
                if (ev.Value.ValueKind != JsonValueKind.Array) continue;
                foreach (var group in ev.Value.EnumerateArray())
                {
                    if (group.ValueKind != JsonValueKind.Object) continue;
                    string matcher = group.TryGetProperty("matcher", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() ?? "" : "";
                    if (!group.TryGetProperty("hooks", out var list) || list.ValueKind != JsonValueKind.Array) continue;
                    foreach (var hook in list.EnumerateArray())
                    {
                        if (hook.ValueKind == JsonValueKind.Object
                            && hook.TryGetProperty("command", out var c) && c.ValueKind == JsonValueKind.String
                            && c.GetString() is { Length: > 0 } command)
                            result.Add(new HookDef(label, kind, file, ev.Name, matcher, CommandLineRedactor.Redact(command), pluginId));
                    }
                }
            }
        }
        catch { }
        return result;
    }

    // installed_plugins.json: {"plugins": {"name@marketplace": [{"installPath": …}]}} (version 2; version 1 had an
    // object instead of the array).
    internal static IEnumerable<(string id, string installPath)> InstalledPlugins(string file)
    {
        var result = new List<(string, string)>();
        if (ReadText(file) is not { } json) return result;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("plugins", out var plugins) || plugins.ValueKind != JsonValueKind.Object) return result;
            foreach (var p in plugins.EnumerateObject())
            {
                var entries = p.Value.ValueKind == JsonValueKind.Array ? p.Value.EnumerateArray().ToList() : [p.Value];
                foreach (var e in entries)
                {
                    if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty("installPath", out var ip)
                        && ip.ValueKind == JsonValueKind.String && ip.GetString() is { Length: > 0 } path)
                        result.Add((p.Name, path));
                }
            }
        }
        catch { }
        return result.Distinct();
    }

    // plugins/cache/<marketplace>/<plugin>/<version>/ (newest version only) and plugins/<plugin>/ (local plugins).
    internal static IEnumerable<(string id, string installPath)> CachedPlugins(string pluginsDir)
    {
        var result = new List<(string, string)>();
        try
        {
            var cache = Path.Combine(pluginsDir, "cache");
            if (Directory.Exists(cache))
            {
                foreach (var market in Directory.EnumerateDirectories(cache))
                {
                    foreach (var plugin in SafeDirectories(market))
                    {
                        // One unreadable folder skips that plugin, not the scan.
                        try
                        {
                            var newest = SafeDirectories(plugin)
                                .Where(v => System.IO.File.Exists(Path.Combine(v, "hooks", "hooks.json")))
                                .OrderByDescending(v => { try { return Directory.GetLastWriteTimeUtc(v); } catch { return DateTime.MinValue; } })
                                .FirstOrDefault();
                            if (newest is not null)
                                result.Add(($"{Path.GetFileName(plugin)}@{Path.GetFileName(market)}", newest));
                        }
                        catch { }
                    }
                }
            }
            if (Directory.Exists(pluginsDir))
            {
                foreach (var local in Directory.EnumerateDirectories(pluginsDir))
                {
                    string name = Path.GetFileName(local);
                    if (name is "cache" or "marketplaces" or "synced") continue;
                    if (System.IO.File.Exists(Path.Combine(local, "hooks", "hooks.json"))) result.Add((name, local));
                }
            }
        }
        catch { }
        return result;
    }

    private static List<string> SafeDirectories(string dir)
    {
        try { return Directory.EnumerateDirectories(dir).ToList(); }
        catch { return []; }
    }

    private static string? ReadText(string file)
    {
        try
        {
            if (!System.IO.File.Exists(file)) return null;
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        catch
        {
            return null;
        }
    }
}
